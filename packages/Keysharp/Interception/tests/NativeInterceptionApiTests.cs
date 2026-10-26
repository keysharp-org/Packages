using System.ComponentModel;
using System.Runtime.InteropServices;
using static Keysharp.Interception.Tests.Strokes;

namespace Keysharp.Interception.Tests;

[TestFixture]
public class NativeInterceptionApiTests
{
	[Test]
	public void FilterGoesToTheOneDevice()
	{
		var exports = new FakeExports();
		using var api = new NativeInterceptionApi(exports);
		api.SetFilter(14, 0x1FFF);
		Assert.That(exports.Selected, Is.EqualTo(new[] { 14 }));
		Assert.That(exports.Filter, Is.EqualTo(0x1FFF));
		Assert.That(exports.Mistakes, Is.Empty);
	}

	[Test]
	public void FilterFailuresAreReported()
	{
		var exports = new FakeExports { LastError = 5 };
		using var api = new NativeInterceptionApi(exports);
		var error = Assert.Throws<Win32Exception>(() => api.SetFilter(3, 3));
		Assert.That(error!.NativeErrorCode, Is.EqualTo(5));
		exports.LastError = 0;
		exports.CallsPredicate = false;
		Assert.Throws<InvalidOperationException>(() => api.SetFilter(3, 3), "the library never offered the device");
	}

	[Test]
	public void HardwareIdIsTheFirstOfTheList()
	{
		var exports = new FakeExports { HardwareIdBytes = FakeExports.Utf16("HID\\VID_046D&PID_C52B&MI_00\0HID\\VID_046D&PID_C52B\0\0") };
		using var api = new NativeInterceptionApi(exports);
		Assert.That(api.HardwareId(1), Is.EqualTo("HID\\VID_046D&PID_C52B&MI_00"));
		exports.HardwareIdBytes = [];
		Assert.That(api.HardwareId(1), Is.Null, "nothing is connected");
		exports.HardwareIdBytes = FakeExports.Utf16("\0\0");
		Assert.That(api.HardwareId(1), Is.Null);
	}

	[Test]
	public void IdentityCacheReusesTextAndStillObservesChanges()
	{
		var exports = new FakeExports { HardwareIdBytes = FakeExports.Utf16("HID\\First\0GENERIC-A\0\0") };
		using var api = new NativeInterceptionApi(exports);
		var first = api.HardwareId(1);
		Assert.That(api.HardwareId(1), Is.SameAs(first));
		exports.HardwareIdBytes = FakeExports.Utf16("HID\\First\0GENERIC-B\0\0");
		Assert.That(api.HardwareId(1), Is.SameAs(first), "later generic IDs do not identify the slot");
		exports.HardwareIdBytes = FakeExports.Utf16("HID\\Second\0\0");
		Assert.That(api.HardwareId(2), Is.EqualTo("HID\\Second"));
		exports.HardwareIdBytes = FakeExports.Utf16("HID\\First\0\0");
		Assert.That(api.HardwareId(1), Is.SameAs(first), "other slots do not replace this slot's cached text");
		exports.HardwareIdBytes = FakeExports.Utf16("HID\\Changed\0\0");
		var changed = api.HardwareId(1);
		Assert.That(changed, Is.EqualTo("HID\\Changed"));
		exports.HardwareIdBytes = [];
		Assert.That(api.HardwareId(1), Is.Null);
		exports.HardwareIdBytes = FakeExports.Utf16("HID\\Changed\0\0");
		Assert.That(api.HardwareId(1), Is.EqualTo(changed).And.Not.SameAs(changed), "disconnect clears the cached identity");
		Assert.That(exports.HardwareIdCalls, Is.EqualTo(8), "every lookup still queries the driver");
	}

	[Test]
	public void LibraryExchangesStrokesInItsOwnLayout()
	{
		NativeExports.Reset();

		using (var api = new NativeInterceptionApi(new InterceptionLibrary(NativeExports.Find)))
		{
			Assert.That(api.Send(3, Key(0x1D, StrokeCodec.KeyE1, 0xFFC3D44F)), Is.EqualTo(1));
			Assert.That(api.Send(12, Mouse(0x401, 0, short.MaxValue, 5, -7, 9)), Is.EqualTo(1));

			Assert.That(api.TryReceive(4, out var key), Is.True);
			Assert.That((key.Keyboard.Code, key.Keyboard.State, key.Keyboard.Information), Is.EqualTo(((ushort)0x14, (ushort)1, 0xFFC3D44Fu)));
			Assert.That(api.TryReceive(13, out var mouse), Is.True);
			Assert.That((mouse.Mouse.State, mouse.Mouse.Flags, mouse.Mouse.Rolling, mouse.Mouse.X, mouse.Mouse.Y, mouse.Mouse.Information),
				Is.EqualTo(((ushort)0x400, (ushort)1, (short)-120, 13, -13, 0xABCDEF01u)));
		}

		Assert.That(NativeExports.Keys, Is.EqualTo(new[] { (3, (ushort)0x1D, StrokeCodec.KeyE1, 0xFFC3D44Fu) }));
		Assert.That(NativeExports.Mice, Is.EqualTo(new[]
		{
			(12, (ushort)0x401, (ushort)0, short.MaxValue, 5, -7, 9u)
		}));
		Assert.That(NativeExports.Destroyed, Is.EqualTo(1));
	}

	[Test]
	public void ReadErrorsAreReported()
	{
		NativeExports.Reset();
		using var api = new NativeInterceptionApi(new InterceptionLibrary(NativeExports.Find));
		NativeExports.EmptyReceive = true;
		Marshal.SetLastSystemError(5);
		Assert.That(api.Wait(0), Is.Zero, "an ordinary timeout ignores stale errors");
		Marshal.SetLastSystemError(5);
		Assert.That(api.TryReceive(1, out _), Is.False, "an empty queue ignores stale errors");
		NativeExports.LastError = 6;
		Assert.That(Assert.Throws<Win32Exception>(() => api.Wait(0))!.NativeErrorCode, Is.EqualTo(6));
		Assert.That(Assert.Throws<Win32Exception>(() => api.TryReceive(1, out _))!.NativeErrorCode, Is.EqualTo(6));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FailedOpenReleasesLibrary(bool throws)
	{
		var exports = new FakeExports { Context = 0, FailCreate = throws };
		var error = Assert.Throws<InvalidOperationException>(() => new NativeInterceptionApi(exports));
		Assert.That(error!.Message, Does.Contain(throws ? "context failure" : "kpm setup Keysharp/Interception"));
		Assert.That(exports.Disposed, Is.EqualTo(1), "the library is released again");
	}

	[Test]
	public void ContextClosesOnce()
	{
		var exports = new FakeExports();
		var api = new NativeInterceptionApi(exports);
		api.Dispose();
		api.Dispose();
		Assert.That((exports.Destroyed, exports.Disposed), Is.EqualTo((1, 1)));
		Assert.Throws<ObjectDisposedException>(() => api.SetFilter(1, 0));
		Assert.That(exports.Mistakes, Is.Empty);
	}

	[TestCase(true, Architecture.X64, Architecture.X64, null)]
	[TestCase(true, Architecture.Arm64, Architecture.X64, "ARM64")]
	[TestCase(true, Architecture.Arm64, Architecture.Arm64, "ARM64")]
	[TestCase(true, Architecture.X86, Architecture.X86, "x64 Windows only")]
	[TestCase(true, Architecture.X64, Architecture.X86, "x64 Keysharp process")]
	[TestCase(false, Architecture.X64, Architecture.X64, "Windows only")]
	public void RunsOnX64WindowsOnly(bool windows, Architecture os, Architecture process, string? problem)
	{
		var found = NativeInterceptionApi.PlatformProblem(windows, os, process);

		if (problem == null)
			Assert.That(found, Is.Null);
		else
			Assert.That(found, Does.Contain(problem));
	}

	[Test]
	public void LibraryComesFromThePluginDirectoryOnly()
	{
		var directory = Directory.CreateTempSubdirectory("keysharp-interception-");

		try
		{
			var plugin = Path.Combine(directory.FullName, "Keysharp.Interception.dll");
			var library = Path.Combine(directory.FullName, "interception.dll");
			var missing = Assert.Throws<DllNotFoundException>(() => InterceptionLibrary.LibraryPath(plugin));
			Assert.That(missing!.Message, Does.Contain(library));
			File.WriteAllBytes(library, []);
			Assert.That(InterceptionLibrary.LibraryPath(plugin), Is.EqualTo(library));
			Assert.Throws<DllNotFoundException>(() => InterceptionLibrary.LibraryPath(""), "a plugin loaded from bytes has no directory");
		}
		finally
		{
			directory.Delete(true);
		}
	}
}
