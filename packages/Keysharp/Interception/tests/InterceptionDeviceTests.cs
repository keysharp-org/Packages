using System.Collections.Concurrent;
using System.Diagnostics;
using Keysharp.Components.Input;
using static Keysharp.Interception.Tests.Strokes;

namespace Keysharp.Interception.Tests;

[TestFixture, Category("Internal")]
public class InterceptionDeviceTests
{
	private const string KeyboardHardwareId = @"HID\VID_046D&PID_C52B&REV_1211&MI_00";
	private const string MouseHardwareId = @"HID\VID_1234&PID_ABCD&MI_01";

	[Test]
	public void DeviceListDescribesConnectedKeyboardAndMouseSlots()
	{
		var api = new RecordingApi();
		api.HardwareIds[3] = KeyboardHardwareId;
		api.HardwareIds[8] = @"ACPI\VEN_LEN&DEV_0071";
		api.HardwareIds[12] = MouseHardwareId;
		using var backend = new InterceptionBackend(api, new Session(), () =>
			[new("Keyboard", KeyboardHardwareId, "USB keyboard"), new("Mouse", MouseHardwareId, "USB mouse")]);
		var devices = backend.GetDevices();
		Assert.That(devices.Select(device => (device.Id, device.Kind, device.Name, device.VendorId, device.ProductId, device.HardwareId)),
			Is.EqualTo(new[]
			{
				(3u, "Keyboard", "USB keyboard", 0x046Du, 0xC52Bu, KeyboardHardwareId),
				(8u, "Keyboard", "", 0u, 0u, @"ACPI\VEN_LEN&DEV_0071"),
				(12u, "Mouse", "USB mouse", 0x1234u, 0xABCDu, MouseHardwareId)
			}));
		Assert.That(api.HardwareIdCalls, Is.EqualTo(20));
		Assert.That(api.FilterWrites, Is.Zero);
		Assert.That(backend.IsReading, Is.False);
	}

	[TestCase(@"HID\VID_0001&PID_0002", 1u, 2u)]
	[TestCase(@"HID\vid_abcd&pid_0123&MI_00", 0xABCDu, 0x0123u)]
	[TestCase(@"ACPI\VEN_LEN&DEV_0071", 0u, 0u)]
	[TestCase(@"HID\VID_12345&PID_1", 0u, 0u)]
	[TestCase(@"HID\VID_G123&PID_ABCDX", 0u, 0u)]
	[TestCase(@"HID\NOTVID_1234&NOTPID_5678", 0u, 0u)]
	public void NumericIdentityUsesOnlyCompleteVendorAndProductTokens(string hardwareId, uint vendorId, uint productId)
	{
		var api = new RecordingApi();
		api.HardwareIds[2] = hardwareId;
		using var backend = new InterceptionBackend(api, new Session());
		Assert.That(backend.TryGetDeviceInfo(2, "Keyboard", out var device), Is.True);
		Assert.That((device.VendorId, device.ProductId), Is.EqualTo((vendorId, productId)));
		Assert.That(device.HardwareId, Is.EqualTo(hardwareId));
		Assert.That(device.Name, Is.Empty);
	}

	[TestCase("HID\\VID_046D&PID_C52B&MI_00\0HID_DEVICE_SYSTEM_KEYBOARD\0HID_DEVICE\0\0", "HID\\VID_046D&PID_C52B&MI_00")]
	[TestCase(@"ACPI\VEN_LEN&DEV_0071", @"ACPI\VEN_LEN&DEV_0071")]
	[TestCase("\0HID_DEVICE\0\0", null)]
	[TestCase("", null)]
	public void NamesKeyOnTheFirstHardwareId(string ids, string? first)
		=> Assert.That(WindowsDeviceNames.FirstHardwareId(ids), Is.EqualTo(first));

	[Test]
	public void EventLookupListsMissingNamesOnceOffItsPath()
	{
		var api = new RecordingApi();
		api.HardwareIds[3] = KeyboardHardwareId;
		var listings = 0;
		var listedOn = 0;
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		using var backend = new InterceptionBackend(api, new Session(), () =>
		{
			listedOn = Environment.CurrentManagedThreadId;
			_ = Interlocked.Increment(ref listings);
			entered.Set();
			_ = release.Wait(WaitLimit);
			return [new("Keyboard", KeyboardHardwareId, "USB keyboard")];
		}, 50);
		Assert.That(backend.TryGetDeviceInfo(3, "Keyboard", out var first), Is.True);
		Assert.That(first.Name, Is.Empty);
		Assert.That(api.HardwareIdCalls, Is.EqualTo(1));
		Assert.That(entered.Wait(WaitLimit), Is.True, "The missing name was not listed.");
		Assert.That(listedOn, Is.Not.EqualTo(Environment.CurrentManagedThreadId));

		// Lookups while the listing runs neither wait for it nor start another.
		for (var i = 0; i < 3; i++)
		{
			Assert.That(backend.TryGetDeviceInfo(3, "Keyboard", out var pending), Is.True);
			Assert.That(pending.Name, Is.Empty);
		}

		Thread.Sleep(200);
		Assert.That(Volatile.Read(ref listings), Is.EqualTo(1));
		release.Set();
		InputDeviceInfo? named = null;
		Assert.That(SpinWait.SpinUntil(() => backend.TryGetDeviceInfo(3, "Keyboard", out named) && named.Name == "USB keyboard", WaitLimit), Is.True);
		Assert.That(backend.TryGetDeviceInfo(3, "Keyboard", out var cached), Is.True);
		Assert.That(cached, Is.SameAs(named));
		Assert.That(first.Name, Is.Empty, "An already returned description is an immutable snapshot.");

		// The listing answered the lookups made while it ran, so nothing more is listed.
		Thread.Sleep(200);
		Assert.That(Volatile.Read(ref listings), Is.EqualTo(1));
		Assert.That(backend.GetDevices().Single(), Is.SameAs(named));
		Assert.That(Volatile.Read(ref listings), Is.EqualTo(2));
	}

	[Test]
	public void UnlistedKeysStayEmptyAndListingsAreSpaced()
	{
		const int interval = 300;
		var api = new RecordingApi();
		api.HardwareIds[3] = KeyboardHardwareId;
		var starts = new ConcurrentQueue<long>();
		using var backend = new InterceptionBackend(api, new Session(), () =>
		{
			starts.Enqueue(Stopwatch.GetTimestamp());
			return [new("Keyboard", KeyboardHardwareId, "USB keyboard")];
		}, interval);
		Assert.That(SpinWait.SpinUntil(() => backend.TryGetDeviceInfo(3, "Keyboard", out var device) && device.Name.Length != 0, WaitLimit), Is.True);

		// Windows names no device with this ID: one more listing, spaced from the first, caches it empty.
		api.HardwareIds[4] = @"ACPI\VEN_LEN&DEV_0071";
		Assert.That(backend.TryGetDeviceInfo(4, "Keyboard", out var unnamed), Is.True);
		Assert.That(unnamed.Name, Is.Empty);
		Assert.That(SpinWait.SpinUntil(() => starts.Count == 2, WaitLimit), Is.True);
		var times = starts.ToArray();
		// Less a timer tick, by which a delay can end early on Windows.
		Assert.That(Stopwatch.GetElapsedTime(times[0], times[1]).TotalMilliseconds, Is.GreaterThanOrEqualTo(interval - 40));

		for (var i = 0; i < 3; i++)
		{
			Assert.That(backend.TryGetDeviceInfo(4, "Keyboard", out var device), Is.True);
			Assert.That(device.Name, Is.Empty);
		}

		Thread.Sleep(2 * interval);
		Assert.That(starts, Has.Count.EqualTo(2), "An ID cached empty was listed again.");
	}

	[Test]
	public void FirstCaptureListsNamesOnceOffItsCaller()
	{
		var api = new RecordingApi();
		api.HardwareIds[3] = KeyboardHardwareId;
		var listings = 0;
		var listedOn = 0;
		using var release = new ManualResetEventSlim();
		using var backend = new InterceptionBackend(api, new Session(), () =>
		{
			listedOn = Environment.CurrentManagedThreadId;
			_ = Interlocked.Increment(ref listings);
			_ = release.Wait(WaitLimit);
			return [new("Keyboard", KeyboardHardwareId, "USB keyboard")];
		}, 50);

		// No discovery is needed until capture starts.
		Assert.That(backend.SetHooks(false, false, null, out _), Is.True);
		Thread.Sleep(200);
		Assert.That(Volatile.Read(ref listings), Is.Zero);

		var host = new Host();
		Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref listings) == 1, WaitLimit), Is.True);
		Assert.That(listedOn, Is.Not.EqualTo(Environment.CurrentManagedThreadId));
		release.Set();
		Assert.That(SpinWait.SpinUntil(() => backend.TryGetDeviceInfo(3, "Keyboard", out var device) && device.Name == "USB keyboard", WaitLimit), Is.True);

		Assert.That(backend.SetHooks(false, false, null, out _), Is.True);
		Assert.That(backend.SetHooks(true, true, host, out _), Is.True);
		Thread.Sleep(200);
		Assert.That(Volatile.Read(ref listings), Is.EqualTo(1), "Only the first capture reads the names.");
	}

	[Test]
	public void ReplugAndSlotReuseReplaceMetadataWithoutInventingNames()
	{
		var api = new RecordingApi();
		api.HardwareIds[3] = KeyboardHardwareId;
		using var backend = new InterceptionBackend(api, new Session(), () => [new("Keyboard", KeyboardHardwareId, "USB keyboard")]);
		var first = backend.GetDevices().Single();
		api.HardwareIds[3] = null;
		api.HardwareIds[7] = KeyboardHardwareId;
		Assert.That(backend.TryGetDeviceInfo(3, "Keyboard", out _), Is.False);
		Assert.That(backend.TryGetDeviceInfo(7, "Keyboard", out var moved), Is.True);
		Assert.That((moved.Id, moved.Name, moved.HardwareId), Is.EqualTo((7u, "USB keyboard", KeyboardHardwareId)));
		api.HardwareIds[7] = @"ACPI\VEN_LEN&DEV_0071";
		Assert.That(backend.TryGetDeviceInfo(7, "Keyboard", out var replacement), Is.True);
		Assert.That((replacement.Id, replacement.Name, replacement.VendorId, replacement.ProductId), Is.EqualTo((7u, "", 0u, 0u)));
		Assert.That(replacement.HardwareId, Is.EqualTo(@"ACPI\VEN_LEN&DEV_0071"));
		Assert.That((first.Id, first.Name, first.HardwareId), Is.EqualTo((3u, "USB keyboard", KeyboardHardwareId)));
		Assert.That(backend.GetDevices().Single().Id, Is.EqualTo(7));
		api.HardwareIds[7] = "";
		Assert.That(backend.GetDevices(), Is.Empty);
	}

	[Test]
	public void NamesRequireOneMatchingPresentDeviceOfTheSameKind()
	{
		var api = new RecordingApi();
		api.HardwareIds[1] = KeyboardHardwareId;
		api.HardwareIds[2] = KeyboardHardwareId;
		api.HardwareIds[11] = KeyboardHardwareId;
		using var backend = new InterceptionBackend(api, new Session(), () =>
			[new("Keyboard", KeyboardHardwareId, "First keyboard"), new("Keyboard", KeyboardHardwareId, "Second keyboard"),
			new("Mouse", KeyboardHardwareId.ToLowerInvariant(), "USB mouse")]);
		var devices = backend.GetDevices();
		Assert.That(devices.Where(device => device.Kind == "Keyboard").Select(device => device.Name), Is.EqualTo(new[] { "", "" }));
		Assert.That(devices.Single(device => device.Kind == "Mouse").Name, Is.EqualTo("USB mouse"));
		Assert.That(devices.Select(device => device.Id), Is.EqualTo(new[] { 1u, 2u, 11u }));
	}

	[Test]
	public void EventLookupRejectsMissingAndWrongKindSlots()
	{
		var api = new RecordingApi(3, 12);
		using var backend = new InterceptionBackend(api, new Session());
		Assert.That(backend.TryGetDeviceInfo(0, "Keyboard", out _), Is.False);
		Assert.That(backend.TryGetDeviceInfo(21, "Mouse", out _), Is.False);
		Assert.That(backend.TryGetDeviceInfo(3, "Mouse", out _), Is.False);
		Assert.That(backend.TryGetDeviceInfo(12, "Keyboard", out _), Is.False);
		Assert.That(backend.TryGetDeviceInfo(3, "Unknown", out _), Is.False);
		Assert.That(api.HardwareIdCalls, Is.Zero);
		Assert.That(backend.TryGetDeviceInfo(4, "Keyboard", out _), Is.False);
		Assert.That(api.HardwareIdCalls, Is.EqualTo(1));
	}

	[Test]
	public void SlowNameDiscoveryAndPendingHostDoNotBlockEventLookup()
	{
		var api = new RecordingApi();
		api.HardwareIds[3] = KeyboardHardwareId;
		using var namesEntered = new ManualResetEventSlim();
		using var namesRelease = new ManualResetEventSlim();
		using var hostEntered = new ManualResetEventSlim();
		using var hostRelease = new ManualResetEventSlim();
		using var backend = new InterceptionBackend(api, new Session(), () =>
		{
			namesEntered.Set();
			_ = namesRelease.Wait(WaitLimit);
			return [new("Keyboard", KeyboardHardwareId, "USB keyboard")];
		});
		var host = new Host { OnKeyboard = key => { hostEntered.Set(); _ = hostRelease.Wait(WaitLimit); return false; } };
		var inventory = Task.Run(backend.GetDevices);

		try
		{
			Assert.That(namesEntered.Wait(WaitLimit), Is.True);
			Assert.That(backend.SetHooks(true, false, host, out _), Is.True);
			api.Enqueue(3, Key(0x1E));
			Assert.That(hostEntered.Wait(WaitLimit), Is.True);
			var lookup = Task.Run(() => backend.TryGetDeviceInfo(3, "Keyboard", out var device) ? device : null);
			Assert.That(lookup.Wait(WaitLimit), Is.True, "Metadata waited for the reader or Windows device enumeration.");
			Assert.That(lookup.Result!.HardwareId, Is.EqualTo(KeyboardHardwareId));
			Assert.That(lookup.Result.Name, Is.Empty);
			Assert.That(inventory.IsCompleted, Is.False);
		}
		finally
		{
			hostRelease.Set();
			namesRelease.Set();
			_ = inventory.Wait(WaitLimit);
		}
	}

	[Test]
	public void CaptureWithoutDeviceReadsQueriesNoHardwareIds()
	{
		var api = new RecordingApi(3, 12);
		using var backend = new InterceptionBackend(api, new Session());
		var host = new Host();
		Assert.That(backend.SetHooks(true, true, host, out _), Is.True);
		api.Enqueue((3, Key(0x1E)), (12, Mouse(1)));
		Settled(api, 2);
		Assert.That(host.Keys.Single().Device, Is.EqualTo(3));
		Assert.That(host.Mice.Single().Device, Is.EqualTo(12));
		Assert.That(api.HardwareIdCalls, Is.Zero);
	}

	[Test]
	public void MetadataCannotUseAReleasedDriverContext()
	{
		var api = new RecordingApi(3);
		var backend = new InterceptionBackend(api, new Session());
		backend.Dispose();
		Assert.Throws<ObjectDisposedException>(() => backend.GetDevices());
		Assert.Throws<ObjectDisposedException>(() => backend.TryGetDeviceInfo(3, "Keyboard", out _));
		Assert.That(api.HardwareIdCalls, Is.Zero);
		Assert.That(api.Violations, Is.Empty);
	}
}
