using System;
using System.Diagnostics;
using System.Threading;
using Keysharp.Components.Input;
using Keysharp.Runtime;

namespace Keysharp.Interception
{
	/// <summary>Captures keyboard and mouse input below Windows through the Interception driver.</summary>
	public sealed partial class InterceptionBackend : IHookProvider, IInputDeviceProvider
	{
		private const int FirstMouse = NativeInterceptionApi.FirstMouse, LastDevice = NativeInterceptionApi.LastDevice;
		// Every key transition, and every mouse event.
		private const ushort KeyboardFilter = 0x0003, MouseFilter = 0x1FFF;
		private const int WaitMilliseconds = 25, DrainStrokes = 1024, DrainMilliseconds = 50, JoinMilliseconds = 1000;

		private readonly object gate = new();
		private readonly IInterceptionApi api;
		private readonly IInputSession session;
		// Filters last set by this context; unchanged filters need no driver call.
		private readonly ushort[] filters = new ushort[LastDevice + 1];
		private readonly ushort[] pauseTails = new ushort[FirstMouse];
		// Whether a keyboard's last transition was dropped: the second packet of a Pause goes where the first went.
		private readonly bool[] droppedPauseTails = new bool[FirstMouse];
		// The packet the reader received and has not finished with, split when the host is asked about its transitions
		// one at a time. Dispose passes on what is left of it, ahead of what the driver still queues.
		private readonly NativeStroke[] held = new NativeStroke[StrokeCodec.MaxMouseFragments];
		private int heldDevice, heldNext, heldCount;
		private Thread? reader;
		private IHookHost? host;
		private bool keyboard, mouse, disposed, released, ownerSession;
		// The transition of the host call in progress, which CallNextHook passes on.
		private bool hasPending, pendingForwarded;
		private string? pendingFailure;
		private int pendingDevice;
		private NativeStroke pendingStroke;

		/// <summary>Installs capture before script hotkeys and returns its removable registration.</summary>
		[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
		public static IDisposable Install()
		{
			var provider = new InterceptionBackend();

			try { return Script.TheScript.RegisterHookProvider(provider); }
			catch { provider.Dispose(); throw; }
		}

		public InterceptionBackend() : this(new ConsoleSession()) { }

		private InterceptionBackend(IInputSession session)
			: this(NativeInterceptionApi.Open(), session, WindowsDeviceNames.GetDevices) { }

		internal InterceptionBackend(IInterceptionApi api, IInputSession session, Func<DeviceName[]>? getNames = null,
			int nameListingMilliseconds = NameListingMilliseconds)
		{
			this.api = api;
			this.session = session;
			this.getNames = getNames ?? (() => []);
			nameListingInterval = TimeSpan.FromMilliseconds(nameListingMilliseconds);
		}

		/// <summary>Whether the reader runs, which it does while capture is selected or input is still queued.</summary>
		internal bool IsReading
		{
			get
			{
				lock (gate)
					return reader != null;
			}
		}

		private static uint Now => unchecked((uint)Environment.TickCount64);

		private bool Filtering
		{
			get
			{
				foreach (var filter in filters)
					if (filter != 0)
						return true;

				return false;
			}
		}

		public bool SetHooks(bool keyboard, bool mouse, IHookHost? host, out string message)
		{
			IHookHost? lost = null;

			lock (gate)
			{
				ObjectDisposedException.ThrowIf(disposed, this);
				message = "";
				var capture = keyboard || mouse;

				if (capture && host == null)
				{
					message = "Capture needs a hook host.";
					return false;
				}

				// Another session's user owns the console's input: the reader sets the filters once this session owns it again.
				var owner = session.IsOwnerSession;

				if (this.host == null)
					ownerSession = owner;

				try
				{
					ApplyFilters(owner && keyboard, owner && mouse);

					if (capture && reader == null)
						StartReader();

					this.keyboard = keyboard;
					this.mouse = mouse;
					this.host = capture ? host : null;

					// SetupAPI runs off the capture path, so a slow device listing cannot hold up input.
					if (capture && !namesWarmed)
					{
						namesWarmed = true;
						QueueNameListing();
					}

					return true;
				}
				catch (Exception ex)
				{
					message = ex.Message;
				}

				// A declined change keeps the previous selection, unless its filters cannot be restored.
				try
				{
					ApplyFilters(owner && this.keyboard, owner && this.mouse);
				}
				catch (Exception ex)
				{
					lost = this.host;
					DropCapture();
					message += $" The previous capture could not be restored ({ex.Message}), so it was released.";
				}

				// A failed clear cannot leave driver input queued without a reader.
				if (Filtering && reader == null)
					Dispose();
			}

			if (lost != null)
				ReportLoss(lost, message);

			return false;
		}

		public void CallNextHook()
		{
			string? failure;

			lock (gate)
				failure = ForwardPending();

			if (failure != null)
				throw new InvalidOperationException(failure);
		}

		public void Dispose()
		{
			Thread? thread;

			lock (gate)
			{
				if (disposed)
					return;

				disposed = true;
				keyboard = mouse = false;
				host = null;

				// Once the filters are clear nothing more is queued. The transition a host call holds goes out first, then
				// the rest of the reader's packet, then what is queued behind them, so that none is lost or reordered, such
				// as the release of a held key.
				try
				{
					ClearFilters();

					if (ForwardPending() is { } failure)
						Trace.WriteLine($"Interception: {failure}");

					PassOnHeld();
					Drain();
				}
				catch (Exception ex)
				{
					Trace.WriteLine($"Interception teardown failed: {ex.Message}");
				}

				thread = reader;

				if (thread == null)
					ReleaseApi();
			}

			// The reader closes the context once it is out of its host call; a call that outlasts the wait has had its
			// transition passed on above.
			if (thread != null && thread != Thread.CurrentThread)
				_ = thread.Join(JoinMilliseconds);
		}

		private void StartReader()
		{
			var thread = new Thread(ReadLoop)
			{
				IsBackground = true,
				Name = "Keysharp Interception reader",
				// Whatever the driver holds waits on this thread, so it comes before the process's other work.
				Priority = ThreadPriority.Highest
			};
			thread.Start();
			reader = thread;
		}

		private void ReadLoop()
		{
			try
			{
				while (true)
				{
					SyncFilters();
					int timeout;

					lock (gate)
					{
						if (disposed)
							return;

						timeout = keyboard || mouse || Filtering ? WaitMilliseconds : 0;
					}

					var device = api.Wait(timeout);

					if (device is > 0 and <= LastDevice)
						Receive(device);
					else if (timeout == 0)
						lock (gate)
						{
							// Nothing is queued and nothing more will be. SetHooks starts another reader for the next capture.
							if (!disposed && !keyboard && !mouse && !Filtering)
							{
								reader = null;
								return;
							}
						}
				}
			}
			catch (Exception ex)
			{
				IHookHost? lost;

				lock (gate)
				{
					// The reader ends with the capture, so that a SetHooks from here on starts another one.
					if (reader == Thread.CurrentThread)
						reader = null;

					lost = disposed ? null : host;

					if (lost != null)
						DropCapture();

					if (Filtering)
						Dispose();
				}

				if (lost != null)
					ReportLoss(lost, $"Interception stopped reading input: {ex.Message}");
				else
					Trace.WriteLine($"Interception stopped reading input: {ex.Message}");
			}
			finally
			{
				lock (gate)
				{
					if (reader == Thread.CurrentThread)
						reader = null;

					// A loss callback can select a replacement reader before this one finishes.
					if (disposed && reader == null)
						ReleaseApi();
				}
			}
		}

		private void SyncFilters()
		{
			IHookHost? lost = null, reset;
			string? failure = null;

			lock (gate)
			{
				if (disposed) return;
				var owner = session.IsOwnerSession;
				reset = owner != ownerSession ? host : null;
				ownerSession = owner;

				try { ApplyFilters(owner && keyboard, owner && mouse); }
				catch (Exception ex) when (keyboard || mouse)
				{
					lost = host;
					DropCapture();
					failure = $"Interception could not {(owner ? "restore" : "release")} its capture: {ex.Message}";
				}
				catch (Exception ex)
				{
					// Nothing is selected, and the next turn tries the clearing again.
					Trace.WriteLine($"Interception could not clear its filters: {ex.Message}");
				}
			}

			reset?.ResetState();
			if (lost != null) ReportLoss(lost, failure!);
		}

		private void Receive(int device)
		{
			IHookHost? target;

			lock (gate)
			{
				if (disposed || !api.TryReceive(device, out var stroke))
					return;

				target = Captures(device) ? host : null;
				heldDevice = device;
				heldNext = 0;

				// The host is asked about a mouse packet one transition at a time.
				if (device >= FirstMouse && target != null)
					heldCount = StrokeCodec.SplitMouse(in stroke, held);
				else
				{
					held[0] = stroke;
					heldCount = 1;
				}
			}

			while (ProcessNextHeld(device, target))
			{
			}
		}

		// Takes the next transition of the reader's packet, and asks the host about it or passes it on. Taking it and
		// making it the pending transition, or passing it on, share one lock, so that Dispose finds each transition still
		// held, pending or gone out. Returns false once nothing is held.
		private bool ProcessNextHeld(int device, IHookHost? target)
		{
			lock (gate)
				if (heldNext == heldCount) return false;
			// Ownership may change during Wait or between the transitions of one mouse stroke.
			SyncFilters();
			NativeStroke stroke;
			uint message = 0, vk = 0, scan = 0, flags = 0, mouseData = 0;
			int deltaX = 0, deltaY = 0;
			bool consult;
			string? failure = null;

			lock (gate)
			{
				if (heldNext == heldCount)
					return false;

				stroke = held[heldNext++];

				if (device < FirstMouse)
				{
					var decoded = StrokeCodec.TryDecodeKeyboard(in stroke, ref pauseTails[device], out var tail, out message, out vk, out scan, out flags);
					var dropTail = tail && droppedPauseTails[device];
					droppedPauseTails[device] = false;

					if (dropTail)
						return true;

					consult = decoded && target != null;
				}
				else
					consult = target != null && StrokeCodec.TryDecodeMouse(in stroke, out message, out mouseData, out deltaX, out deltaY);

				if (consult && Consults(device, target!))
				{
					pendingDevice = device;
					pendingStroke = stroke;
					pendingForwarded = false;
					pendingFailure = null;
					hasPending = true;
				}
				else
				{
					// Not the host's to decide, or the capture it was read under has ended.
					consult = false;
					failure = released ? null : TrySend(device, in stroke);

					if (device < FirstMouse)
						droppedPauseTails[device] = failure != null;
				}
			}

			if (!consult)
			{
				if (failure != null)
					LoseCapture(failure);

				return true;
			}

			bool suppress;

			try
			{
				suppress = device < FirstMouse
					? target!.KeyboardEvent(message, vk, scan, flags, Now, stroke.Keyboard.Information, device)
					: target!.MouseEvent(message, int.MinValue, int.MinValue, mouseData, 0, Now, stroke.Mouse.Information, device,
						deltaX, deltaY);
			}
			catch (Exception ex)
			{
				// A host that throws decides nothing: the transition goes out, and the capture is released.
				lock (gate)
				{
					_ = ForwardPending();
					EndPending(device);
				}

				LoseCapture($"processing input failed: {ex.Message}");
				return true;
			}

			// The host's answer stands even when the capture has changed meanwhile, as when Keysharp releases it on a key
			// it suppresses.
			lock (gate)
			{
				failure = suppress ? pendingFailure : ForwardPending();
				EndPending(device);
			}

			if (failure != null)
				LoseCapture(failure);

			return true;
		}

		// Under the gate: whether the host still captures the device's kind of input.
		private bool Consults(int device, IHookHost target) => !disposed && ownerSession && Captures(device) && ReferenceEquals(host, target);

		private bool Captures(int device) => device < FirstMouse ? keyboard : mouse;

		// Under the gate: ends the host call's frame.
		private void EndPending(int device)
		{
			if (device < FirstMouse)
				droppedPauseTails[device] = !pendingForwarded;

			hasPending = false;
		}

		// Under the gate: passes on the rest of the reader's packet, which the reader then finds dealt with.
		private void PassOnHeld()
		{
			var next = heldNext;
			heldNext = heldCount;

			for (; next < heldCount; next++)
				PassOnUnconsulted(heldDevice, in held[next]);
		}

		// Under the gate: passes a stroke on without the host, except the second packet of a Pause whose first was dropped.
		private void PassOnUnconsulted(int device, in NativeStroke stroke)
		{
			if (device < FirstMouse)
			{
				_ = StrokeCodec.TryDecodeKeyboard(in stroke, ref pauseTails[device], out var tail, out _, out _, out _, out _);
				var dropTail = tail && droppedPauseTails[device];
				droppedPauseTails[device] = false;

				if (dropTail)
					return;
			}

			var failure = TrySend(device, in stroke);

			if (device < FirstMouse)
				droppedPauseTails[device] = failure != null;

			if (failure != null)
				Trace.WriteLine($"Interception: {failure}");
		}

		// Under the gate. Null once the pending transition is out, or when there is none; otherwise why it could not be
		// passed on, kept so that it is not tried again.
		private string? ForwardPending()
		{
			if (!hasPending || pendingForwarded || released)
				return null;

			if (pendingFailure == null && (pendingFailure = TrySend(pendingDevice, in pendingStroke)) == null)
				pendingForwarded = true;

			if (pendingDevice < FirstMouse)
				droppedPauseTails[pendingDevice] = !pendingForwarded;

			return pendingFailure;
		}

		// Under the gate: passes a stroke on to the device it came from, or says why it could not. A refused write wrote
		// nothing, so a retry cannot duplicate it. A failure is returned rather than thrown, so that the reader goes on
		// with the rest of its packet.
		private string? TrySend(int device, in NativeStroke stroke)
		{
			try
			{
				if (api.Send(device, in stroke) == 1 || api.Send(device, in stroke) == 1)
					return null;
			}
			catch (Exception ex)
			{
				return $"Interception device {device} did not accept input passed on to it: {ex.Message}";
			}

			return $"Interception device {device} did not accept input passed on to it";
		}

		// Input that cannot be passed on would be lost, so the capture is released and Keysharp told. Capture can be selected again
		// without opening another driver context.
		private void LoseCapture(string message)
		{
			IHookHost? target;

			lock (gate)
			{
				target = host;

				if (disposed || target == null)
				{
					Trace.WriteLine($"Interception: {message}");
					return;
				}

				DropCapture();
			}

			ReportLoss(target, message);
		}

		// Ends the selection with no way back to it. Every filter that can be cleared is, so that input flows; the reader
		// passes on what is still queued.
		private void DropCapture()
		{
			keyboard = mouse = false;
			host = null;
			ClearFilters();
		}

		private void ClearFilters()
		{
			for (var device = 1; device <= LastDevice; device++)
			{
				if (filters[device] == 0)
					continue;

				try
				{
					api.SetFilter(device, 0);
					filters[device] = 0;
				}
				catch (Exception ex)
				{
					Trace.WriteLine($"Interception could not clear the filter of device {device}: {ex.Message}");
				}
			}
		}

		// Sets the devices whose filter changes. A failure leaves the devices before it changed, as filters records.
		private void ApplyFilters(bool keyboardOn, bool mouseOn)
		{
			for (var device = 1; device <= LastDevice; device++)
			{
				var filter = device < FirstMouse ? (keyboardOn ? KeyboardFilter : (ushort)0) : (mouseOn ? MouseFilter : (ushort)0);

				if (filters[device] != filter)
				{
					api.SetFilter(device, filter);
					filters[device] = filter;
				}
			}
		}

		// Passes on what the driver still holds, bounded so that a device that keeps sending cannot hold up teardown.
		private void Drain()
		{
			var started = Stopwatch.GetTimestamp();
			var drained = 0;
			bool any;

			do
			{
				any = false;

				for (var device = 1; device <= LastDevice; device++)
				{
					while (api.TryReceive(device, out var stroke))
					{
						any = true;
						PassOnUnconsulted(device, in stroke);

						if (++drained >= DrainStrokes || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= DrainMilliseconds)
							return;
					}
				}
			}
			while (any);
		}

		private void ReleaseApi()
		{
			if (released)
				return;

			released = true;

			try
			{
				api.Dispose();
			}
			catch (Exception ex)
			{
				Trace.WriteLine($"Interception could not close its context: {ex.Message}");
			}
		}

		private static void ReportLoss(IHookHost target, string message)
		{
			try
			{
				target.CaptureLost(message);
			}
			catch (Exception ex)
			{
				Trace.WriteLine($"Interception could not report a lost capture: {ex.Message}");
			}
		}

	}
}
