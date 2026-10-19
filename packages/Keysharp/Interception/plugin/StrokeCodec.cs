using System;

namespace Keysharp.Interception
{
	/// <summary>
	/// Converts captured Interception strokes to the Windows forms Keysharp's contract uses. It does only what
	/// the device level requires: Pause's E1 sequence becomes one key, a mouse packet becomes one transition per call,
	/// and the E0 Shift packets keyboards wrap around some keys are not reported. Keysharp derives the rest, such as a
	/// key's VK, as Windows would.
	/// </summary>
	internal static class StrokeCodec
	{
		internal const ushort KeyUp = 0x01, KeyE0 = 0x02, KeyE1 = 0x04;
		internal const ushort LeftDown = 0x001, Button4Down = 0x040, Button4Up = 0x080, Button5Down = 0x100, Button5Up = 0x200;
		internal const ushort Wheel = 0x400, HWheel = 0x800;
		internal const ushort MoveAbsolute = 0x001, VirtualDesktop = 0x002, MoveNoCoalesce = 0x008, TermsrvShadow = 0x100;
		internal const int MaxMouseFragments = 14;

		// Packets with other flags, such as attribute changes, are passed on as they are.
		private const ushort SplittableFlags = MoveAbsolute | VirtualDesktop | MoveNoCoalesce | TermsrvShadow;
		private const ushort PauseTailDown = 1, PauseTailUp = 2;
		private const uint LLKHF_EXTENDED = 0x01, LLKHF_UP = 0x80;
		private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
		private const uint VK_PAUSE = 0x13;
		private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;

		/// <summary>Splits a packet into one stroke per transition, movement first, and returns how many there are.</summary>
		internal static int SplitMouse(in NativeStroke stroke, Span<NativeStroke> fragments)
		{
			var mouse = stroke.Mouse;

			if ((mouse.Flags & ~SplittableFlags) != 0)
			{
				fragments[0] = stroke;
				return 1;
			}

			var count = 0;

			if (mouse.Flags != 0 || mouse.X != 0 || mouse.Y != 0 || mouse.State == 0)
				fragments[count++] = Mouse(0, mouse.Flags, 0, mouse.X, mouse.Y, mouse.Information);

			for (ushort bit = LeftDown; bit <= HWheel; bit <<= 1)
				if ((mouse.State & bit) != 0)
					fragments[count++] = Mouse(bit, 0, bit >= Wheel ? mouse.Rolling : (short)0, 0, 0, mouse.Information);

			if ((mouse.State & 0xF000) != 0)
				fragments[count++] = Mouse((ushort)(mouse.State & 0xF000), 0, mouse.Rolling, 0, 0, mouse.Information);

			return count;
		}

		/// <summary>
		/// Reads a key transition in the low-level hook's form, with a VK of 0 so that Keysharp derives it, except for Pause.
		/// False for a packet to pass on unreported; <paramref name="isPauseTail"/> marks the second packet of a Pause, which
		/// goes where the first went. <paramref name="pauseTail"/> is the device's Pause state.
		/// </summary>
		internal static bool TryDecodeKeyboard(in NativeStroke stroke, ref ushort pauseTail, out bool isPauseTail,
			out uint message, out uint vk, out uint scan, out uint flags)
		{
			var key = stroke.Keyboard;
			message = vk = scan = flags = 0;
			isPauseTail = false;
			var expectedTail = pauseTail;
			pauseTail = 0;

			if ((key.State & ~(KeyUp | KeyE0 | KeyE1)) != 0 || key.Code is 0 or > 0xFF || (key.State & (KeyE0 | KeyE1)) == (KeyE0 | KeyE1))
				return false;

			var released = (key.State & KeyUp) != 0;

			if (expectedTail != 0 && key.Code == 0x45 && (key.State & (KeyE0 | KeyE1)) == 0 && released == (expectedTail == PauseTailUp))
			{
				isPauseTail = true;
				return false;
			}

			if ((key.State & KeyE1) != 0)
			{
				// Pause is the only E1 sequence.
				if (key.Code != 0x1D)
					return false;

				vk = VK_PAUSE;
				scan = 0x45;
				pauseTail = released ? PauseTailUp : PauseTailDown;
			}
			else
			{
				// Keysharp would take the fake Shift packets for real Shift transitions.
				if ((key.State & KeyE0) != 0 && key.Code is 0x2A or 0x36)
					return false;

				scan = key.Code;

				if ((key.State & KeyE0) != 0)
					flags = LLKHF_EXTENDED;
			}

			message = released ? WM_KEYUP : WM_KEYDOWN;

			if (released)
				flags |= LLKHF_UP;

			return true;
		}

		/// <summary>
		/// Reads one transition of a split packet in the low-level hook's form. A move has no screen position: its raw
		/// device values are the deltas, which <c>mouseData &amp; 0x8000</c> marks as absolute 0..65535 values.
		/// </summary>
		internal static bool TryDecodeMouse(in NativeStroke stroke, out uint message, out uint mouseData, out int deltaX, out int deltaY)
		{
			var mouse = stroke.Mouse;
			message = mouseData = 0;
			deltaX = deltaY = 0;

			if ((mouse.Flags & ~SplittableFlags) != 0 || (mouse.State & 0xF000) != 0 || (mouse.State & (mouse.State - 1)) != 0)
				return false;

			message = mouse.State switch
			{
				0 => 0x200u,
				0x001 => 0x201u,
				0x002 => 0x202u,
				0x004 => 0x204u,
				0x008 => 0x205u,
				0x010 => 0x207u,
				0x020 => 0x208u,
				Button4Down or Button5Down => 0x20Bu,
				Button4Up or Button5Up => 0x20Cu,
				Wheel => 0x20Au,
				HWheel => 0x20Eu,
				_ => 0u
			};

			switch (mouse.State)
			{
				case 0:
					deltaX = mouse.X;
					deltaY = mouse.Y;

					if ((mouse.Flags & MoveAbsolute) != 0)
						mouseData = MOUSEEVENTF_ABSOLUTE | ((mouse.Flags & VirtualDesktop) != 0 ? MOUSEEVENTF_VIRTUALDESK : 0);

					break;

				case Button4Down or Button4Up:
					mouseData = 1u << 16;
					break;

				case Button5Down or Button5Up:
					mouseData = 2u << 16;
					break;

				case Wheel or HWheel:
					mouseData = (uint)(ushort)mouse.Rolling << 16;
					break;
			}

			return message != 0;
		}

		private static NativeStroke Mouse(ushort state, ushort flags, short rolling, int x, int y, uint information)
			=> new() { Mouse = new() { State = state, Flags = flags, Rolling = rolling, X = x, Y = y, Information = information } };
	}
}
