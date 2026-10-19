using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Keysharp.Components.Input;

namespace Keysharp.Interception
{
	public sealed partial class InterceptionBackend
	{
		private const int NameListingMilliseconds = 1000;

		private readonly Func<DeviceName[]> getNames;
		// The shortest time from the start of one listing of names to the start of a listing a lookup queues.
		private readonly TimeSpan nameListingInterval;
		private readonly InputDeviceInfo?[] descriptions = new InputDeviceInfo?[LastDevice + 1];
		// Names by kind and hardware ID. An empty name is cached too: Windows gave none, two devices matched, or a listing
		// made after a lookup of the ID did not have it.
		private Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
		// Keys looked up and missing from names, which the next listing answers.
		private HashSet<string> unlisted = new(StringComparer.OrdinalIgnoreCase);
		private long listings, namesListing, listingStarted;
		private bool listingQueued, namesWarmed;

		public InputDeviceInfo[] GetDevices()
		{
			long listing;

			lock (gate)
			{
				ObjectDisposedException.ThrowIf(disposed, this);
				listing = BeginListing();
			}

			// SetupAPI can take longer than a hook decision, so it never owns the driver's lifetime gate.
			var listed = WindowsDeviceNames.UniqueNames(getNames());

			lock (gate)
			{
				ObjectDisposedException.ThrowIf(disposed, this);
				UseListing(listed, listing);
				var devices = new List<InputDeviceInfo>();

				for (var id = 1; id <= LastDevice; id++)
					if (Describe(id, id < FirstMouse ? "Keyboard" : "Mouse", true) is { } device)
						devices.Add(device);

				return devices.ToArray();
			}
		}

		public bool TryGetDeviceInfo(long deviceId, string kind, out InputDeviceInfo device)
		{
			device = null!;

			if (deviceId is <= 0 or > LastDevice || kind != (deviceId < FirstMouse ? "Keyboard" : "Mouse"))
				return false;

			lock (gate)
			{
				ObjectDisposedException.ThrowIf(disposed, this);
				device = Describe((int)deviceId, kind, false)!;
				return device != null;
			}
		}

		// Under the gate. A lookup never waits for SetupAPI: a key the names lack is listed off its path, except in a
		// listing of every slot, which has just read the names.
		private InputDeviceInfo? Describe(int id, string kind, bool listing)
		{
			// Slots move or are reused without a driver notification. Validate only the requested slot on an event lookup.
			if (api.HardwareId(id) is not { Length: > 0 } hardwareId)
				return descriptions[id] = null;

			var key = WindowsDeviceNames.Key(kind, hardwareId);

			if (!names.TryGetValue(key, out var name))
			{
				name = "";

				if (listing)
					names[key] = name;
				else if (unlisted.Add(key))
					QueueNameListing();
			}

			var previous = descriptions[id];

			if (previous != null && previous.HardwareId == hardwareId && previous.Name == name)
				return previous;

			return descriptions[id] = new InputDeviceInfo(id, kind, name, HardwareNumber(hardwareId, "VID_"),
				HardwareNumber(hardwareId, "PID_"), hardwareId);
		}

		// Under the gate: numbers listings in the order they start.
		private long BeginListing()
		{
			listingStarted = Stopwatch.GetTimestamp();
			return ++listings;
		}

		// Under the gate: a listing replaces only an older one.
		private void UseListing(Dictionary<string, string> listed, long listing)
		{
			if (listing > namesListing)
			{
				names = listed;
				namesListing = listing;
			}
		}

		// Under the gate: one listing at a time runs off the hook path, and it starts no sooner than the interval after
		// the previous listing started.
		private void QueueNameListing()
		{
			if (listingQueued || disposed)
				return;

			listingQueued = true;
			var wait = listings == 0 ? TimeSpan.Zero : nameListingInterval - Stopwatch.GetElapsedTime(listingStarted);
			_ = Task.Run(async () =>
			{
				if (wait > TimeSpan.Zero)
					await Task.Delay(wait).ConfigureAwait(false);

				ListQueuedNames();
			});
		}

		private void ListQueuedNames()
		{
			HashSet<string> requested;
			long listing;

			lock (gate)
			{
				if (disposed)
					return;

				requested = unlisted;
				unlisted = new(StringComparer.OrdinalIgnoreCase);
				listing = BeginListing();
			}

			Dictionary<string, string>? listed = null;

			try
			{
				listed = WindowsDeviceNames.UniqueNames(getNames());
			}
			catch (Exception ex)
			{
				Trace.WriteLine($"Interception could not read device names: {ex.Message}");
			}

			lock (gate)
			{
				listingQueued = false;

				if (disposed)
					return;

				if (listed != null)
					UseListing(listed, listing);

				// A key looked up before this listing started, and still missing, stays empty until a later listing has it.
				foreach (var key in requested)
					_ = names.TryAdd(key, "");

				// A key looked up while it ran gets a listing of its own, unless this one had it.
				_ = unlisted.RemoveWhere(names.ContainsKey);

				if (unlisted.Count != 0)
					QueueNameListing();
			}
		}

		private static uint HardwareNumber(ReadOnlySpan<char> hardwareId, ReadOnlySpan<char> token)
		{
			var start = hardwareId.IndexOf(token, StringComparison.OrdinalIgnoreCase);

			if (start <= 0 || hardwareId[start - 1] is not ('\\' or '&'))
				return 0;

			var digits = start + token.Length;
			var end = digits + 4;

			return end <= hardwareId.Length && (end == hardwareId.Length || hardwareId[end] == '&')
				&& uint.TryParse(hardwareId.Slice(digits, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number)
				? number : 0;
		}
	}
}
