using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Keysharp.Interception.Tests.Strokes;

namespace Keysharp.Interception.Tests;

[TestFixture]
public class StrokeCodecTests
{
	[Test]
	public void LayoutsMatchInterceptionH()
	{
		Assert.That(Marshal.SizeOf<NativeStroke>(), Is.EqualTo(20));
		Assert.That(Marshal.SizeOf<NativeKeyboardStroke>(), Is.EqualTo(8));
		Assert.That(Marshal.SizeOf<NativeMouseStroke>(), Is.EqualTo(20));
		Assert.That(new[] { "Code", "State", "Information" }.Select(field => (int)Marshal.OffsetOf<NativeKeyboardStroke>(field)),
			Is.EqualTo(new[] { 0, 2, 4 }));
		Assert.That(new[] { "State", "Flags", "Rolling", "X", "Y", "Information" }.Select(field => (int)Marshal.OffsetOf<NativeMouseStroke>(field)),
			Is.EqualTo(new[] { 0, 2, 4, 8, 12, 16 }));
		// Both union members are passed to the library at offset zero.
		Assert.That((Unsafe.SizeOf<NativeKeyboardStroke>(), Unsafe.SizeOf<NativeMouseStroke>(), Unsafe.SizeOf<NativeStroke>()), Is.EqualTo((8, 20, 20)));

		var key = MemoryMarshal.Read<NativeStroke>(new byte[] { 0x1D, 0, 3, 0, 0x78, 0x56, 0x34, 0x12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
		ushort tail = 0;
		Assert.That(StrokeCodec.TryDecodeKeyboard(in key, ref tail, out var isTail, out var message, out var vk, out var scan, out var flags), Is.True);
		Assert.That((message, vk, scan, flags, isTail, key.Keyboard.Information), Is.EqualTo((0x101u, 0u, 0x1Du, 0x81u, false, 0x12345678u)));

		var mouse = MemoryMarshal.Read<NativeStroke>(new byte[] { 0, 4, 0, 0, 0x88, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x78, 0x56, 0x34, 0x12 });
		Assert.That(StrokeCodec.TryDecodeMouse(in mouse, out message, out var data, out var deltaX, out var deltaY), Is.True);
		Assert.That((message, data, deltaX, deltaY, mouse.Mouse.Information), Is.EqualTo((0x20Au, 0xFF880000u, 0, 0, 0x12345678u)));
	}

	[Test]
	public void PauseIsOneKey()
	{
		var strokes = new[] { Key(0x1D, 4), Key(0x45), Key(0x1D, 5), Key(0x45, 1) };

		ushort tail = 0;

		for (var i = 0; i < strokes.Length; i++)
		{
			var decoded = StrokeCodec.TryDecodeKeyboard(in strokes[i], ref tail, out var isTail, out var message, out var vk, out var scan, out var flags);
			Assert.That(isTail, Is.EqualTo((i & 1) != 0));
			Assert.That(decoded, Is.EqualTo((i & 1) == 0));

			if (decoded)
				Assert.That((message, vk, scan, flags), Is.EqualTo((i == 0 ? 0x100u : 0x101u, 0x13u, 0x45u, i == 0 ? 0u : 0x80u)));
		}

		Assert.That(tail, Is.Zero);
	}

	[Test]
	public void PauseTailNeedsTheSameDeviceAndReleaseState()
	{
		ushort firstDevice = 0, secondDevice = 0;
		_ = StrokeCodec.TryDecodeKeyboard(Key(0x1D, 4), ref firstDevice, out _, out _, out _, out _, out _);
		Assert.That(StrokeCodec.TryDecodeKeyboard(Key(0x45, 0), ref secondDevice, out var isTail, out _, out var vk, out var scan, out _), Is.True);
		Assert.That((isTail, vk, scan), Is.EqualTo((false, 0u, 0x45u)));
		Assert.That(StrokeCodec.TryDecodeKeyboard(Key(0x45, 1), ref firstDevice, out isTail, out _, out vk, out _, out _), Is.True);
		Assert.That((isTail, vk, firstDevice), Is.EqualTo((false, 0u, (ushort)0)));
	}

	[TestCase(0x20, 4)]
	[TestCase(0x1D, 6)]
	[TestCase(0x1E, 0x20)]
	[TestCase(0x100, 0)]
	[TestCase(0, 0)]
	[TestCase(0x2A, 2)]
	[TestCase(0x36, 3)]
	public void PacketsPassedOnUnreported(int code, int state)
	{
		ushort tail = 0;
		Assert.That(StrokeCodec.TryDecodeKeyboard(Key((ushort)code, (ushort)state), ref tail, out var isTail, out _, out _, out _, out _), Is.False);
		Assert.That(isTail, Is.False);
	}

	[Test]
	public void CompoundPacketSplitsIntoTransitions()
	{
		var original = Mouse(0x505, 0, -120, 13, -9, 0x12345678);
		var fragments = new NativeStroke[StrokeCodec.MaxMouseFragments];
		var split = fragments[..StrokeCodec.SplitMouse(in original, fragments)];
		Assert.That(split.Select(fragment => (fragment.Mouse.State, fragment.Mouse.X, fragment.Mouse.Y, fragment.Mouse.Rolling)), Is.EqualTo(new[]
		{
			((ushort)0, 13, -9, (short)0), ((ushort)1, 0, 0, (short)0), ((ushort)4, 0, 0, (short)0), ((ushort)0x100, 0, 0, (short)0),
			((ushort)0x400, 0, 0, (short)-120)
		}));
		Assert.That(split.All(fragment => fragment.Mouse.Information == 0x12345678), Is.True);
		Assert.That(StrokeCodec.TryDecodeMouse(in split[0], out var message, out var data, out var deltaX, out var deltaY), Is.True);
		Assert.That((message, data, deltaX, deltaY), Is.EqualTo((0x200u, 0u, 13, -9)));
	}

	[Test]
	public void BusiestPacketFitsTheFragmentBuffer()
	{
		var everything = Mouse(0xFFFF, 3, 120, 1, 1);
		var fragments = new NativeStroke[StrokeCodec.MaxMouseFragments];
		Assert.That(StrokeCodec.SplitMouse(in everything, fragments), Is.EqualTo(StrokeCodec.MaxMouseFragments));
	}

	[TestCase(0x001, 0x201, 0)]
	[TestCase(0x002, 0x202, 0)]
	[TestCase(0x004, 0x204, 0)]
	[TestCase(0x008, 0x205, 0)]
	[TestCase(0x010, 0x207, 0)]
	[TestCase(0x020, 0x208, 0)]
	[TestCase(0x040, 0x20B, 1)]
	[TestCase(0x080, 0x20C, 1)]
	[TestCase(0x100, 0x20B, 2)]
	[TestCase(0x200, 0x20C, 2)]
	public void ButtonsUseLowLevelHookMessages(int state, int expected, int button)
	{
		Assert.That(StrokeCodec.TryDecodeMouse(Mouse((ushort)state), out var message, out var data, out var deltaX, out var deltaY), Is.True);
		Assert.That((message, data, deltaX, deltaY), Is.EqualTo(((uint)expected, (uint)button << 16, 0, 0)));
	}

	[TestCase(0x400, -120, 0x20A)]
	[TestCase(0x800, 120, 0x20E)]
	public void WheelMessages(int state, int amount, int expected)
	{
		Assert.That(StrokeCodec.TryDecodeMouse(Mouse((ushort)state, rolling: (short)amount), out var message,
			out var data, out _, out _), Is.True);
		Assert.That((message, unchecked((short)(data >> 16))), Is.EqualTo(((uint)expected, (short)amount)));
	}

	[Test]
	public void AbsoluteMovement()
	{
		var stroke = Mouse(0, 0xB, x: 65535, y: 32767, information: 19);
		Assert.That(StrokeCodec.TryDecodeMouse(in stroke, out var message, out var data, out var deltaX, out var deltaY), Is.True);
		Assert.That((message, data, deltaX, deltaY), Is.EqualTo((0x200u, 0xC000u, 65535, 32767)));
	}

	[Test]
	public void AttributesAndUnknownBitsPassOnUnchanged()
	{
		var fragments = new NativeStroke[StrokeCodec.MaxMouseFragments];

		foreach (var flags in new ushort[] { 4, 0x10 })
		{
			var packet = Mouse(1, flags, 0, 2, 3, 9);
			Assert.That(StrokeCodec.SplitMouse(in packet, fragments), Is.EqualTo(1));
			Assert.That((fragments[0].Mouse.State, fragments[0].Mouse.Flags, fragments[0].Mouse.X, fragments[0].Mouse.Y),
				Is.EqualTo(((ushort)1, flags, 2, 3)));
			Assert.That(StrokeCodec.TryDecodeMouse(in fragments[0], out _, out _, out _, out _), Is.False);
		}

		var unknown = Mouse(0x1001);
		Assert.That(StrokeCodec.SplitMouse(in unknown, fragments), Is.EqualTo(2));
		Assert.That((fragments[0].Mouse.State, fragments[1].Mouse.State), Is.EqualTo(((ushort)1, (ushort)0x1000)));
		Assert.That(StrokeCodec.TryDecodeMouse(in fragments[1], out _, out _, out _, out _), Is.False);
	}
}
