using System.Collections.Concurrent;
using System.Diagnostics;
using static Keysharp.Interception.Tests.Strokes;

namespace Keysharp.Interception.Tests;

[TestFixture]
public class InterceptionBackendTests
{
	private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_MOUSEWHEEL = 0x20A;

	[TestCase(true, false)]
	[TestCase(false, true)]
	[TestCase(true, true)]
	public void CaptureSelection(bool keyboard, bool mouse)
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		Assert.That(backend.SetHooks(keyboard, mouse, host, out var message), Is.True, message);
		Assert.That(api.Filters.Skip(1).Take(10), Is.All.EqualTo(keyboard ? 3 : 0));
		Assert.That(api.Filters.Skip(11), Is.All.EqualTo(mouse ? 0x1FFF : 0));
		Assert.That(api.FilterWrites, Is.EqualTo((keyboard ? 10 : 0) + (mouse ? 10 : 0)), "only devices whose filter changes are set");
		Assert.That(backend.SetHooks(keyboard, mouse, host, out message), Is.True, message);
		Assert.That(api.FilterWrites, Is.EqualTo((keyboard ? 10 : 0) + (mouse ? 10 : 0)));
		Assert.That(backend.SetHooks(false, false, null, out message), Is.True, message);
		Assert.That(api.Filters, Is.All.EqualTo(0));
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void DeclinedChangeKeepsTheCapture()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.FilterFailure = (device, filter) => device == 14 && filter != 0 ? new InvalidOperationException("device 14 refused") : null;
		Assert.That(backend.SetHooks(true, true, host, out var message), Is.False);
		Assert.That(message, Does.Contain("device 14 refused"));
		Assert.That(api.Filters.Skip(1).Take(10), Is.All.EqualTo(3));
		Assert.That(api.Filters.Skip(11), Is.All.EqualTo(0));

		api.Enqueue(1, Key(0x1E));
		Settled(api, 1);
		Assert.That(host.Keys.Count, Is.EqualTo(1));
		Assert.That(host.Losses, Is.Empty);
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void FailedRestoreReleasesTheCapture()
	{
		var api = new RecordingApi(1);
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		// The mice refuse their filter, and keyboard 2 then refuses its own back.
		api.FilterFailure = (device, filter) => device == 12 && filter != 0 || device == 2 && filter == 3
			? new InvalidOperationException($"device {device} refused") : null;
		Assert.That(backend.SetHooks(false, true, host, out var message), Is.False);
		Assert.That(message, Does.Contain("device 12 refused").And.Contain("released"));
		Assert.That(host.Losses.Single(), Is.EqualTo(message));
		Assert.That(api.Filters, Is.All.EqualTo(0));
		Assert.That(api.Disposals, Is.Zero);

		api.FilterFailure = null;
		Assert.That(backend.SetHooks(true, false, host, out message), Is.True, message);
		api.Enqueue(1, Key(0x1E));
		Settled(api, 1);
		Assert.That(host.Keys.Count, Is.EqualTo(1));
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void ForwardOnce()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		host.OnKeyboard = _ =>
		{
			backend.CallNextHook();
			backend.CallNextHook();
			return false;
		};
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.Enqueue(4, Key(0x1E, information: 0x89ABCDEF));
		Settled(api, 1);
		var sent = api.Sent.Single();
		Assert.That(sent.Device, Is.EqualTo(4));
		Assert.That(sent.Stroke.Keyboard.Information, Is.EqualTo(0x89ABCDEF));
		Assert.That(host.Keys.Single().Device, Is.EqualTo(4u));
		Assert.That(host.Keys.Single().ExtraInfo, Is.EqualTo(0x89ABCDEFul));

		// Outside a host call there is nothing to pass on.
		backend.CallNextHook();
		Assert.That(api.Sent.Count, Is.EqualTo(1));
	}

	[Test]
	public void SuppressedTransitionsStay()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host { OnKeyboard = key => key.Scan == 0x1E, OnMouse = move => move.Message == WM_LBUTTONDOWN };
		Assert.That(backend.SetHooks(true, true, host, out _), Is.True);
		api.Enqueue((1, Key(0x1E)), (1, Key(0x30)), (11, Mouse(1)), (11, Mouse(2)));
		Settled(api, 4);
		var sent = api.Sent.ToArray();
		Assert.That(sent.Select(item => item.Device), Is.EqualTo(new[] { 1, 11 }));
		Assert.That(sent[0].Stroke.Keyboard.Code, Is.EqualTo(0x30));
		Assert.That(sent[1].Stroke.Mouse.State, Is.EqualTo(2));
	}

	[Test]
	public void ForwardedOnlyOnceTheDriverTakesIt()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var refusals = 1;
		api.SendResult = _ => Interlocked.Decrement(ref refusals) >= 0 ? 0 : 1;
		var host = new Host();
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.Enqueue(1, Key(0x1E));
		Settled(api, 1);
		Assert.That(api.SendCalls, Is.EqualTo(2), "one refused write, tried once more");
		Assert.That(api.Sent.Count, Is.EqualTo(1));
		Assert.That(host.Losses, Is.Empty);

		// A device that keeps refusing cannot have its input passed on, so the capture lets go of it rather than eat it.
		api.SendResult = _ => 0;
		var thrown = new ConcurrentQueue<Exception>();
		host.OnKeyboard = _ =>
		{
			try
			{
				backend.CallNextHook();
			}
			catch (Exception ex)
			{
				thrown.Enqueue(ex);
			}

			return false;
		};
		api.Enqueue(1, Key(0x30));
		Assert.That(SpinWait.SpinUntil(() => !host.Losses.IsEmpty && !backend.IsReading, WaitLimit), Is.True);
		Assert.That(thrown.Single(), Is.InstanceOf<InvalidOperationException>());
		Assert.That(api.SendCalls, Is.EqualTo(4), "a transition that could not go out is not tried again after its host call");
		Assert.That(api.Filters, Is.All.EqualTo(0));
		Assert.That(api.Disposals, Is.Zero);
	}

	[Test]
	public void MouseFragments()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host { OnMouse = move => move.Message == WM_LBUTTONDOWN };
		Assert.That(backend.SetHooks(false, true, host, out _), Is.True);
		api.Enqueue(13, Mouse(0x401, x: 3, y: -4, rolling: -120, information: 123));
		Settled(api, 1);
		Assert.That(host.Mice.Select(move => move.Message), Is.EqualTo(new[] { WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_MOUSEWHEEL }));
		var sent = api.Sent.Select(item => item.Stroke.Mouse).ToArray();
		Assert.That(sent.Select(mouse => (mouse.State, mouse.X, mouse.Y, mouse.Rolling)),
			Is.EqualTo(new[] { ((ushort)0, 3, -4, (short)0), (StrokeCodec.Wheel, 0, 0, (short)-120) }));
		Assert.That(sent.All(mouse => mouse.Information == 123), Is.True);
	}

	[Test]
	public void MovesCarryRawDeviceValues()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		Assert.That(backend.SetHooks(false, true, host, out _), Is.True);
		api.Enqueue((11, Mouse(0, flags: 3, x: 65535, y: 100)), (12, Mouse(0, x: -3, y: 4)));
		Settled(api, 2);
		Assert.That(host.Mice.Select(move => (move.Message, move.X, move.Y, move.MouseData, move.DeltaX, move.DeltaY, move.Device)),
			Is.EqualTo(new[]
			{
				(WM_MOUSEMOVE, int.MinValue, int.MinValue, 0xC000u, 65535, 100, 11u),
				(WM_MOUSEMOVE, int.MinValue, int.MinValue, 0u, -3, 4, 12u)
			}));
	}

	[TestCase(0x1, 0, 0, false)]
	[TestCase(0x1, 0, 0, true)]
	[TestCase(0x3, 0, 0, false)]
	[TestCase(0x3, 0, 0, true)]
	[TestCase(0x1, 65535, 32767, false)]
	[TestCase(0x1, 65535, 32767, true)]
	[TestCase(0x3, 65535, 32767, false)]
	[TestCase(0x3, 65535, 32767, true)]
	public void AbsoluteMouseFragments(int flags, int x, int y, bool suppressMove)
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host { OnMouse = move => suppressMove && move.Message == WM_MOUSEMOVE };
		Assert.That(backend.SetHooks(false, true, host, out _), Is.True);
		api.Enqueue(13, Mouse(StrokeCodec.LeftDown, (ushort)flags, x: x, y: y, information: 0x89ABCDEF));
		Settled(api, 1);
		Assert.That(host.Mice, Is.EqualTo(new[]
		{
			new MouseEvent(WM_MOUSEMOVE, int.MinValue, int.MinValue, flags == 1 ? 0x8000u : 0xC000u, 0, 0x89ABCDEFul, 13, x, y),
			new MouseEvent(WM_LBUTTONDOWN, int.MinValue, int.MinValue, 0, 0, 0x89ABCDEFul, 13, 0, 0)
		}));
		var movement = ((ushort)0, (ushort)flags, x, y, (short)0, 0x89ABCDEFu);
		var button = (StrokeCodec.LeftDown, (ushort)0, 0, 0, (short)0, 0x89ABCDEFu);
		Assert.That(api.Sent.Select(item => item.Device), Is.All.EqualTo(13));
		Assert.That(api.Sent.Select(item => (item.Stroke.Mouse.State, item.Stroke.Mouse.Flags, item.Stroke.Mouse.X,
			item.Stroke.Mouse.Y, item.Stroke.Mouse.Rolling, item.Stroke.Mouse.Information)),
			Is.EqualTo(suppressMove ? new[] { button } : new[] { movement, button }));
		Assert.That(host.Losses, Is.Empty);
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void KeysLeaveTheirVkToKeysharp()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.Enqueue((1, Key(0x36)), (1, Key(0x1D, 2)), (1, Key(0x45, 1)), (1, Key(0x2A, 2)), (1, Key(0x2A, 3)));
		Settled(api, 5);
		Assert.That(host.Keys.Select(key => (key.Message, key.Vk, key.Scan, key.Flags)), Is.EqualTo(new[]
		{
			(WM_KEYDOWN, 0u, 0x36u, 0u),
			(WM_KEYDOWN, 0u, 0x1Du, 1u),
			(WM_KEYUP, 0u, 0x45u, 0x80u)
		}));
		// The fake Shift packets go out in place, unreported.
		Assert.That(api.Sent.Select(item => (item.Stroke.Keyboard.Code, item.Stroke.Keyboard.State)), Is.EqualTo(new[]
		{
			((ushort)0x36, (ushort)0), ((ushort)0x1D, (ushort)2), ((ushort)0x45, (ushort)1), ((ushort)0x2A, (ushort)2), ((ushort)0x2A, (ushort)3)
		}));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void PauseTail(bool suppressed)
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host { OnKeyboard = _ => suppressed };
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.Enqueue((1, Key(0x1D, StrokeCodec.KeyE1)), (2, Key(0x45)), (1, Key(0x45)));
		Settled(api, 3);
		Assert.That(host.Keys.Select(key => (key.Vk, key.Scan, key.Device)), Is.EqualTo(new[] { (0x13u, 0x45u, 1u), (0u, 0x45u, 2u) }));
		Assert.That(api.Sent.Count, Is.EqualTo(suppressed ? 0 : 3));
	}

	[Test]
	public void PauseTailAfterCaptureStops()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		using var held = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var host = new Host { OnKeyboard = _ => true };
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.BeforeWait = () =>
		{
			if (Volatile.Read(ref api.Receives) != 1)
				return;

			held.Set();
			_ = release.Wait(WaitLimit);
		};
		api.Enqueue((1, Key(0x1D, StrokeCodec.KeyE1)), (1, Key(0x45)));
		Assert.That(held.Wait(WaitLimit), Is.True);
		Assert.That(backend.SetHooks(false, false, null, out _), Is.True);
		api.BeforeWait = null;
		release.Set();
		Assert.That(SpinWait.SpinUntil(() => !backend.IsReading, WaitLimit), Is.True);
		Assert.That(host.Keys.Single().Vk, Is.EqualTo(0x13u));
		Assert.That(api.Sent, Is.Empty, "the second packet of a dropped Pause is dropped too");
	}

	[Test]
	public void HostFailureReleasesTheCapture()
	{
		var api = new RecordingApi(1);
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host { OnKeyboard = _ => throw new InvalidOperationException("failed decision") };
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.Enqueue((1, Key(0x1E)), (1, Key(0x30)));
		Assert.That(SpinWait.SpinUntil(() => !host.Losses.IsEmpty && !backend.IsReading, WaitLimit), Is.True);
		Assert.That(host.Losses.Single(), Does.Contain("failed decision"));
		Assert.That(host.Keys.Count, Is.EqualTo(1));
		Assert.That(api.Sent.Count, Is.EqualTo(2), "both transitions went out");
		Assert.That(api.Filters, Is.All.EqualTo(0));
		Assert.That(api.Disposals, Is.Zero);

		host.OnKeyboard = null;
		Assert.That(backend.SetHooks(true, false, host, out var message), Is.True, message);
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void FiltersFollowTheConsoleSession()
	{
		var api = new RecordingApi(1);
		var session = new Session { Owner = false };
		using var backend = new InterceptionBackend(api, session);
		var host = new Host();
		Assert.That(backend.SetHooks(true, false, host, out var message), Is.True, message);
		Assert.That(api.Filters, Is.All.EqualTo(0), "another user's input is left alone");
		session.Owner = true;
		Assert.That(SpinWait.SpinUntil(() => api.Filters.Skip(1).Take(10).All(filter => filter == 3) && Volatile.Read(ref host.Resets) == 1, WaitLimit), Is.True);
		session.Owner = false;
		Assert.That(SpinWait.SpinUntil(() => api.Filters.All(filter => filter == 0) && Volatile.Read(ref host.Resets) == 2, WaitLimit), Is.True);

		api.Enqueue(1, Key(0x1E));
		Settled(api, 1);
		Assert.That(host.Keys, Is.Empty, "queued input from another desktop is forwarded without script callbacks");
		Assert.That(api.Sent.Count, Is.EqualTo(1));
		var resetsBeforeInput = -1;
		host.OnKeyboard = _ => { resetsBeforeInput = Volatile.Read(ref host.Resets); return false; };
		session.Owner = true;
		api.Enqueue(1, Key(0x30));
		Settled(api, 2);
		Assert.That(resetsBeforeInput, Is.EqualTo(3), "resumed input must follow its capture-gap reset");
	}

	[TestCase(false), TestCase(true)]
	public void SessionChangesBeforeInputDecisions(bool betweenFragments)
	{
		var api = new RecordingApi(1, 11);
		var session = new Session();
		using var backend = new InterceptionBackend(api, session);
		var host = new Host();
		if (betweenFragments) host.OnMouse = _ => { session.Owner = false; return false; };
		else api.BeforeWait = () => { api.BeforeWait = null; session.Owner = false; };
		Assert.That(backend.SetHooks(!betweenFragments, betweenFragments, host, out _), Is.True);
		api.Enqueue(betweenFragments ? 11 : 1, betweenFragments ? Mouse(3, x: 4, y: 5) : Key(0x1E));
		Settled(api, 1);
		Assert.That(host.Keys, Is.Empty);
		Assert.That(host.Mice.Count, Is.EqualTo(betweenFragments ? 1 : 0));
		Assert.That(Volatile.Read(ref host.Resets), Is.EqualTo(1));
		Assert.That(api.Filters, Is.All.EqualTo(0));
		Assert.That(api.Sent.Count, Is.EqualTo(betweenFragments ? 3 : 1));
		if (betweenFragments)
			Assert.That(api.Sent.Select(item => item.Stroke.Mouse.State), Is.EqualTo(new ushort[] { 0, 1, 2 }));
	}

	[Test]
	public void ReceiveFailureReleasesTheCapture()
	{
		var api = new RecordingApi { FailWait = true };
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		Assert.That(SpinWait.SpinUntil(() => !host.Losses.IsEmpty && !backend.IsReading, WaitLimit), Is.True);
		Assert.That(host.Losses.Single(), Does.Contain("wait failed"));
		Assert.That(api.Filters, Is.All.EqualTo(0));
		Assert.That(api.Disposals, Is.Zero);

		api.FailWait = false;
		Assert.That(backend.SetHooks(true, false, host, out var message), Is.True, message);
		Assert.That(backend.IsReading, Is.True);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FailedFilterReleaseWithoutReaderClosesContext(bool readerFailure)
	{
		var api = new RecordingApi
		{
			FailWait = readerFailure,
			FilterFailure = (device, filter) => filter == 0 || (!readerFailure && device == 2)
				? new InvalidOperationException("filter failure") : null
		};
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		api.Enqueue(1, Key(0x1E));
		Assert.That(backend.SetHooks(true, false, host, out _), Is.EqualTo(readerFailure));
		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref api.Disposals) == 1, WaitLimit), Is.True);
		Assert.That(backend.IsReading, Is.False);
		Assert.That(api.Sent.Single().Stroke.Keyboard.Code, Is.EqualTo(0x1E));
		Assert.That(api.Log.Last(), Is.EqualTo("dispose"), "queued input is forwarded before closing the context");
		Assert.Throws<ObjectDisposedException>(() => backend.SetHooks(true, false, host, out _));
		Assert.That(api.Violations, Is.Empty);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void TeardownDropsTailOfUnforwardedPause(bool pending)
	{
		var api = new RecordingApi { SendResult = _ => 0 };
		using var backend = new InterceptionBackend(api, new Session());

		if (pending)
			Assert.That(backend.SetHooks(true, false, new Host { OnKeyboard = _ => { backend.Dispose(); return false; } }, out _), Is.True);

		api.Enqueue((1, Key(0x1D, StrokeCodec.KeyE1)), (1, Key(0x45)));

		if (!pending)
			backend.Dispose();

		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref api.Disposals) == 1, WaitLimit), Is.True);
		Assert.That(api.SendCalls, Is.EqualTo(2), "a failed Pause is retried, but its NumLock-shaped tail must not go out alone");
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void CaptureSelectedFromALossReportIsRead()
	{
		var api = new RecordingApi { FailWait = true };
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		var restored = 0;
		host.OnLost = () =>
		{
			api.FailWait = false;

			if (backend.SetHooks(true, false, host, out _))
				_ = Interlocked.Increment(ref restored);
		};
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref restored) == 1, WaitLimit), Is.True);
		api.Enqueue(1, Key(0x1E));
		Settled(api, 1);
		Assert.That(host.Keys.Count, Is.EqualTo(1), "a reader runs for the capture selected again");
		Assert.That(backend.IsReading, Is.True);
	}

	[Test]
	public void ExitingReaderDoesNotCloseAReplacementDuringDisposal()
	{
		var api = new RecordingApi { FailWait = true };
		var backend = new InterceptionBackend(api, new Session());
		using var restored = new ManualResetEventSlim();
		using var replacementWaiting = new ManualResetEventSlim();
		using var releaseOldReader = new ManualResetEventSlim();
		using var releaseReplacement = new ManualResetEventSlim();
		Thread? oldReader = null, replacementReader = null;
		var host = new Host();
		var captureRestored = false;
		host.OnLost = () =>
		{
			oldReader = Thread.CurrentThread;
			api.FailWait = false;
			captureRestored = backend.SetHooks(true, false, host, out _);
			restored.Set();
			_ = releaseOldReader.Wait(WaitLimit);
		};
		api.BeforeWait = () =>
		{
			if (api.FailWait)
				return;

			replacementReader = Thread.CurrentThread;
			replacementWaiting.Set();
			_ = releaseReplacement.Wait(WaitLimit);
		};

		try
		{
			Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
			Assert.That(restored.Wait(WaitLimit), Is.True);
			Assert.That(captureRestored, Is.True);
			Assert.That(replacementWaiting.Wait(WaitLimit), Is.True);
			var disposal = Task.Run(backend.Dispose);
			Assert.That(disposal.Wait(WaitLimit), Is.True, "Disposal remains bounded while the replacement reader waits.");
			Assert.That(api.Disposals, Is.Zero);

			releaseOldReader.Set();
			Assert.That(oldReader!.Join(WaitLimit), Is.True);
			Assert.That(api.Disposals, Is.Zero, "The old reader cannot close the context while its replacement uses it.");

			releaseReplacement.Set();
			Assert.That(replacementReader!.Join(WaitLimit), Is.True);
			Assert.That(api.Disposals, Is.EqualTo(1));
			Assert.That(api.Violations, Is.Empty);
		}
		finally
		{
			releaseOldReader.Set();
			releaseReplacement.Set();
			backend.Dispose();
			_ = oldReader?.Join(WaitLimit);
			_ = replacementReader?.Join(WaitLimit);
		}
	}

	[Test]
	public void ReaderRunsOnlyWhileNeeded()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		using var held = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var host = new Host();
		Assert.That(backend.IsReading, Is.False);
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		Assert.That(backend.IsReading, Is.True);
		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref api.ReaderPriority) >= 0, WaitLimit), Is.True);
		Assert.That((ThreadPriority)api.ReaderPriority, Is.EqualTo(ThreadPriority.Highest));

		// What is still queued when capture ends goes out unconsulted before the reader stops.
		api.BeforeWait = () =>
		{
			held.Set();
			_ = release.Wait(WaitLimit);
		};
		Assert.That(held.Wait(WaitLimit), Is.True);
		api.Enqueue(1, Key(0x1E));
		Assert.That(backend.SetHooks(false, false, null, out _), Is.True);
		api.BeforeWait = null;
		release.Set();
		Assert.That(SpinWait.SpinUntil(() => !backend.IsReading, WaitLimit), Is.True);
		Assert.That(host.Keys, Is.Empty);
		Assert.That(api.Sent.Count, Is.EqualTo(1));

		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		Assert.That(backend.IsReading, Is.True);
	}

	[Test]
	public void TeardownPassesOnWhatIsHeldInOrder()
	{
		var api = new RecordingApi();
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		host.OnKeyboard = key =>
		{
			entered.Set();
			_ = release.Wait(WaitLimit);
			return true;
		};

		try
		{
			Assert.That(backend.SetHooks(true, true, host, out _), Is.True);
			api.Enqueue((1, Key(0x1E)), (2, Key(0x30)), (11, Mouse(1)));
			Assert.That(entered.Wait(WaitLimit), Is.True);
			api.Log.Clear();
			var elapsed = Stopwatch.StartNew();
			backend.Dispose();
			Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(WaitLimit));
			var log = api.Log.ToArray();
			// The filters first, so that nothing more is queued; then the held transition; then what is queued behind it.
			Assert.That(log.Take(20).Count(entry => entry.StartsWith("filter ") && entry.EndsWith("=0")), Is.EqualTo(20));
			Assert.That(log.Skip(20), Is.EqualTo(new[] { "send 1 -> 1", "receive 2", "send 2 -> 1", "receive 11", "send 11 -> 1" }));
			Assert.That(api.Disposals, Is.Zero, "the reader closes the context once its host call returns");
			Assert.Throws<ObjectDisposedException>(() => backend.SetHooks(true, false, host, out _));
		}
		finally
		{
			release.Set();
			backend.Dispose();
		}

		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref api.Disposals) == 1, WaitLimit), Is.True);
		Assert.That(api.Sent.Count, Is.EqualTo(3), "the held transition went out once, although its host suppressed it after teardown");
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void TeardownPassesOnTheRestOfAPacketInOrder()
	{
		var api = new RecordingApi();
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		host.OnMouse = move =>
		{
			if (move.Message == WM_MOUSEMOVE)
			{
				entered.Set();
				_ = release.Wait(WaitLimit);
			}

			return false;
		};

		try
		{
			Assert.That(backend.SetHooks(false, true, host, out _), Is.True);
			// A move with the left button going down, which the host is asked about as two transitions, then the button's
			// release.
			api.Enqueue(11, Mouse(StrokeCodec.LeftDown, x: 3, y: 4));
			Assert.That(entered.Wait(WaitLimit), Is.True);
			api.Enqueue(11, Mouse(2));
			// Teardown frees the host call once it has passed the move on, and the reader then waits for the lock.
			api.OnSend = (_, _) => release.Set();
			backend.Dispose();
		}
		finally
		{
			release.Set();
			backend.Dispose();
		}

		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref api.Disposals) == 1, WaitLimit), Is.True);
		Assert.That(api.Sent.Select(item => (item.Device, item.Stroke.Mouse.State, item.Stroke.Mouse.X)),
			Is.EqualTo(new[] { (11, (ushort)0, 3), (11, StrokeCodec.LeftDown, 0), (11, (ushort)2, 0) }), "the button goes down before it goes up");
		Assert.That(host.Mice.Single().Message, Is.EqualTo(WM_MOUSEMOVE));
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void SuppressionStandsWhenTheCaptureEndsDuringTheCall()
	{
		var api = new RecordingApi();
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		host.OnKeyboard = key =>
		{
			// As Keysharp's hook thread does when the key it suppresses ends the capture.
			var ended = Task.Run(() => backend.SetHooks(false, false, null, out _));
			return ended.Wait(WaitLimit) && ended.Result;
		};
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.Enqueue(1, Key(0x1C));
		Settled(api, 1);
		Assert.That(host.Keys.Count, Is.EqualTo(1));
		Assert.That(api.Sent, Is.Empty);
		Assert.That(api.Filters, Is.All.EqualTo(0));
		Assert.That(host.Losses, Is.Empty);
	}

	[Test]
	public void DisposalFromCallback()
	{
		var api = new RecordingApi();
		var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		host.OnKeyboard = _ =>
		{
			backend.Dispose();
			return true;
		};
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		api.Enqueue((1, Key(0x1E)), (2, Key(0x30)));
		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref api.Disposals) == 1, WaitLimit), Is.True);
		Assert.That(host.Keys.Count, Is.EqualTo(1));
		Assert.That(api.Sent.Count, Is.EqualTo(2));
		backend.Dispose();
		Assert.That(api.Disposals, Is.EqualTo(1));
		Assert.That(api.Violations, Is.Empty);
	}

	[Test]
	public void TeardownIsBoundedAgainstEndlessInput()
	{
		var api = new RecordingApi();
		var backend = new InterceptionBackend(api, new Session());
		api.OnSend = (device, _) => api.Enqueue(device, Key(0x1E));
		var host = new Host();
		host.OnKeyboard = _ =>
		{
			backend.Dispose();
			return false;
		};

		try
		{
			Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
			api.Enqueue(1, Key(0x1E));
			Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref api.Disposals) == 1, WaitLimit), Is.True);
			Assert.That(host.Keys.Count, Is.EqualTo(1));
			Assert.That(api.Sent.Count, Is.LessThanOrEqualTo(1025));
			Assert.That(api.Violations, Is.Empty);
		}
		finally
		{
			api.OnSend = null;
			backend.Dispose();
		}
	}
}
