namespace Keysharp.Interception.Tests;

[TestFixture]
public class InterceptionSenderTests
{
	[Test]
	public void KeyboardUsesScanCodesAndPreservesMarkers()
	{
		var api = new RecordingApi(3);
		using var sender = new InterceptionBackend(api, new Session());
		sender.KeybdEvent(0xA3, 0x1D, 0x09, 0xFFFFFFFFFFC3D3E9);
		sender.KeybdEvent(0xA3, 0x1D, 0x0B, 0xFFFFFFFFFFC3D3E9);
		sender.KeybdEvent(0x13, 0x45, 0, 123);
		sender.KeybdEvent(0x13, 0x45, 2, 123);
		var sent = api.Sent.ToArray();
		Assert.That(sent.Select(item => item.Device), Is.All.EqualTo(3));
		Assert.That(sent.Select(item => (item.Stroke.Keyboard.Code, item.Stroke.Keyboard.State)), Is.EqualTo(new[]
		{
			((ushort)0x1D, (ushort)2), ((ushort)0x1D, (ushort)3),
			((ushort)0x1D, (ushort)4), ((ushort)0x45, (ushort)0),
			((ushort)0x1D, (ushort)5), ((ushort)0x45, (ushort)1)
		}));
		Assert.That(sent.Take(2).Select(item => item.Stroke.Keyboard.Information), Is.All.EqualTo(0xFFC3D3E9u));
		Assert.That(sent.Skip(2).Select(item => item.Stroke.Keyboard.Information), Is.All.EqualTo(123u));
		Assert.That(api.FilterWrites, Is.Zero);
		Assert.That(sender.IsReading, Is.False);
	}

	[Test]
	public void DevicesRemainSelectedUntilDisconnected()
	{
		var api = new RecordingApi(3, 13);
		using var sender = new InterceptionBackend(api, new Session());
		sender.KeybdEvent(0x41, 0x1E, 0, 0);
		api.HardwareIds[1] = "new keyboard";
		sender.KeybdEvent(0x42, 0x30, 0, 0);
		sender.KeybdEvent(0x41, 0x1E, 2, 0);
		sender.KeybdEvent(0x42, 0x30, 2, 0);
		sender.KeybdEvent(0x43, 0x2E, 0, 0);
		sender.KeybdEvent(0x43, 0x2E, 2, 0);
		sender.MouseEvent(2, 0, 0, 0, 0);
		api.HardwareIds[11] = "new mouse";
		sender.MouseEvent(1, 4, -3, 0, 0);
		sender.MouseEvent(4, 0, 0, 0, 0);
		sender.MouseEvent(1, -4, 3, 0, 0);
		Assert.That(api.Sent.Select(item => item.Device), Is.EqualTo(new[] { 3, 3, 3, 3, 3, 3, 13, 13, 13, 13 }));
		api.HardwareIds[3] = api.HardwareIds[13] = null;
		sender.KeybdEvent(0x44, 0x20, 0, 0);
		sender.KeybdEvent(0x44, 0x20, 2, 0);
		sender.MouseEvent(1, 3, -4, 0, 0);
		Assert.That(api.Sent.Skip(10).Select(item => item.Device), Is.EqualTo(new[] { 1, 1, 11 }));
	}

	[Test]
	public void CachedTargetsAvoidRepeatedInventoryScans()
	{
		var api = new RecordingApi(10, 20);
		using var sender = new InterceptionBackend(api, new Session());
		sender.KeybdEvent(0x41, 0x1E, 0, 0);
		sender.KeybdEvent(0x41, 0x1E, 2, 0);
		sender.MouseEvent(1, 3, -4, 0, 0);
		var discovered = api.HardwareIdCalls;
		api.HardwareIds[1] = "new keyboard";
		api.HardwareIds[11] = "new mouse";
		sender.KeybdEvent(0x41, 0x1E, 0, 0);
		sender.KeybdEvent(0x41, 0x1E, 2, 0);
		sender.MouseEvent(1, -3, 4, 0, 0);
		Assert.That(api.HardwareIdCalls - discovered, Is.LessThanOrEqualTo(3), "each cached emission needs at most its own target's identity");
		Assert.That(api.Sent.Select(item => item.Device), Is.EqualTo(new[] { 10, 10, 20, 10, 10, 20 }));
	}

	[Test]
	public void HeldInputCannotMoveToAReplacementDevice()
	{
		var api = new RecordingApi(3, 5);
		using var sender = new InterceptionBackend(api, new Session());
		sender.KeybdEvent(0x41, 0x1E, 0, 0);
		api.HardwareIds[3] = "replacement keyboard";
		Assert.Throws<InvalidOperationException>(() => sender.KeybdEvent(0x41, 0x1E, 2, 0));
		Assert.That(api.Sent.Count, Is.EqualTo(1));
	}

	[Test]
	public void MouseCarriesCoordinatesButtonsAndMarkers()
	{
		var api = new RecordingApi(12);
		using var sender = new InterceptionBackend(api, new Session());
		sender.MouseEvent(0xE003, 65535, 32767, 0, 0xFFFFFFFFFFC3D3E9);
		sender.MouseEvent(0x80, 23, 45, 3, 123);
		sender.MouseEvent(0x100, 23, 45, 3, 123);
		sender.MouseEvent(4, 0, 0, 0, 0);
		sender.MouseEvent(1, -3, 4, 0, 0);
		var strokes = api.Sent.Select(item => item.Stroke.Mouse).ToArray();
		Assert.That((strokes[0].State, strokes[0].Flags, strokes[0].X, strokes[0].Y, strokes[0].Information),
			Is.EqualTo(((ushort)1, (ushort)0x0B, 65535, 32767, 0xFFC3D3E9u)));
		Assert.That((strokes[1].State, strokes[1].X, strokes[1].Y), Is.EqualTo(((ushort)0x140, 0, 0)));
		Assert.That(strokes[2].State, Is.EqualTo(0x280));
		Assert.That(strokes[3].State, Is.EqualTo(2));
		Assert.That((strokes[4].Flags, strokes[4].X, strokes[4].Y), Is.EqualTo(((ushort)0, -3, 4)));
	}

	[Test]
	public void LargeWheelsDoNotRepeatMovementOrButtons()
	{
		var api = new RecordingApi(11);
		using var sender = new InterceptionBackend(api, new Session());
		sender.MouseEvent(0x1803, 3, -4, unchecked((uint)-70000), 123);
		var strokes = api.Sent.Select(item => item.Stroke.Mouse).ToArray();
		Assert.That(strokes.Length, Is.EqualTo(3));
		Assert.That(strokes.Sum(stroke => stroke.Rolling), Is.EqualTo(-70000));
		Assert.That((strokes[0].State, strokes[0].X, strokes[0].Y), Is.EqualTo(((ushort)0xC01, 3, -4)));
		Assert.That(strokes.Skip(1).Select(stroke => (stroke.State, stroke.Flags, stroke.X, stroke.Y)),
			Is.All.EqualTo(((ushort)0xC00, (ushort)0, 0, 0)));
		Assert.That(strokes.Select(stroke => stroke.Information), Is.All.EqualTo(123u));
	}

	[Test]
	public void RefusedInputDoesNotPinTheDevice()
	{
		var api = new RecordingApi(3) { SendResult = _ => 0 };
		using var sender = new InterceptionBackend(api, new Session());
		Assert.Throws<InvalidOperationException>(() => sender.KeybdEvent(0x41, 0x1E, 0, 0));
		api.HardwareIds[3] = null;
		api.HardwareIds[1] = "new keyboard";
		api.SendResult = null;
		sender.KeybdEvent(0x41, 0x1E, 0, 0);
		Assert.That(api.Sent.Single().Device, Is.EqualTo(1));
	}

	[TestCase(true)]
	[TestCase(false)]
	public void SessionChangesStopCompoundWritesWithoutRetries(bool keyboard)
	{
		var api = new RecordingApi(1, 11);
		var session = new Session();
		using var sender = new InterceptionBackend(api, session);
		api.OnSend = (_, _) => session.Owner = false;

		if (keyboard)
			Assert.Throws<InvalidOperationException>(() => sender.KeybdEvent(0x13, 0x45, 0, 0));
		else
			Assert.Throws<InvalidOperationException>(() => sender.MouseEvent(0x800, 0, 0, 70000, 0));

		Assert.That(api.SendCalls, Is.EqualTo(1));
		Assert.That(api.Sent.Count, Is.EqualTo(1));
	}

	[Test]
	public void UnavailableSessionDeviceAndDisposedSenderEmitNothing()
	{
		var api = new RecordingApi(1);
		var session = new Session { Owner = false };
		var sender = new InterceptionBackend(api, session);
		Assert.Throws<InvalidOperationException>(() => sender.KeybdEvent(0x41, 0x1E, 0, 0));
		session.Owner = true;
		Assert.Throws<InvalidOperationException>(() => sender.MouseEvent(1, 3, 4, 0, 0));
		sender.Dispose();
		Assert.Throws<ObjectDisposedException>(() => sender.KeybdEvent(0x41, 0x1E, 0, 0));
		Assert.That(api.Sent, Is.Empty);
		Assert.That(api.Violations, Is.Empty);
		Assert.That(api.Disposals, Is.EqualTo(1));
	}

	[Test]
	public void InvalidPacketsAreRejectedBeforeDeviceSelection()
	{
		var api = new RecordingApi(1, 11);
		using var sender = new InterceptionBackend(api, new Session());
		Assert.Throws<ArgumentException>(() => sender.KeybdEvent(0, 0xE9, 4, 0));
		Assert.Throws<ArgumentException>(() => sender.KeybdEvent(0xE8, 0, 0, 0));
		Assert.Throws<ArgumentException>(() => sender.MouseEvent(0x200, 0, 0, 0, 0));
		Assert.Throws<ArgumentException>(() => sender.MouseEvent(0x80, 0, 0, 4, 0));
		Assert.Throws<ArgumentException>(() => sender.MouseEvent(0x880, 0, 0, 1, 0));
		Assert.That(api.HardwareIdCalls, Is.Zero);
		Assert.That(api.Sent, Is.Empty);
	}
}
