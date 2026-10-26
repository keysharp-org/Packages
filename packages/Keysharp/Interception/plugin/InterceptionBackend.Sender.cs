using System;
using System.Collections.Generic;
using Keysharp.Runtime;

namespace Keysharp.Interception
{
	public sealed partial class InterceptionBackend
	{
		private const uint KeyExtended = 0x01, KeyReleased = 0x02, KeyScanCode = 0x08;
		private const uint MouseMove = 0x0001, MouseXDown = 0x0080, MouseXUp = 0x0100;
		private const uint MouseWheel = 0x0800, MouseHWheel = 0x1000, MouseNoCoalesce = 0x2000;
		private const uint MouseVirtualDesktop = 0x4000, MouseAbsolute = 0x8000;
		private const uint MouseFlags = 0xFFFF & ~0x0600u;
		private const ushort ButtonDownMask = 0x155, ButtonUpMask = 0x2AA;
		private readonly HashSet<ushort> sentKeys = [];
		private int senderKeyboard, senderMouse;
		private string? senderKeyboardHardwareId, senderMouseHardwareId;
		private ushort sentButtons;

		/// <summary>Registers the driver for SendPlay without changing input capture.</summary>
		[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
		public static IDisposable InstallSender()
		{
			var sender = new InterceptionBackend();

			try { return Script.TheScript.RegisterSenderProvider(sender); }
			catch { sender.Dispose(); throw; }
		}

		public void KeybdEvent(ushort VirtualKey, ushort ScanCode, uint Flags, ulong ExtraInfo)
		{
			if ((Flags & ~(KeyExtended | KeyReleased | KeyScanCode)) != 0 || ScanCode is 0 or > 0xFF)
				throw new ArgumentException("Interception sending needs a physical scan code and does not support Unicode packets.");

			lock (gate)
			{
				var device = SenderDevice(true);
				var up = (Flags & KeyReleased) != 0;
				var state = (ushort)((up ? StrokeCodec.KeyUp : 0) | ((Flags & KeyExtended) != 0 ? StrokeCodec.KeyE0 : 0));
				var information = unchecked((uint)ExtraInfo);
				var pause = VirtualKey == 0x13;
				var key = (ushort)(ScanCode | ((Flags & KeyExtended) != 0 ? 0x100 : 0) | (pause ? 0x200 : 0));

				if (pause)
				{
					WriteSender(device, new NativeStroke
					{
						Keyboard = new() { Code = 0x1D, State = (ushort)(StrokeCodec.KeyE1 | (up ? StrokeCodec.KeyUp : 0)), Information = information }
					});
					WriteSender(device, new NativeStroke
					{
						Keyboard = new() { Code = 0x45, State = (ushort)(up ? StrokeCodec.KeyUp : 0), Information = information }
					});
				}
				else
					WriteSender(device, new NativeStroke { Keyboard = new() { Code = ScanCode, State = state, Information = information } });

				if (up)
					_ = sentKeys.Remove(key);
				else
					_ = sentKeys.Add(key);
			}
		}

		public void MouseEvent(uint Flags, int X, int Y, uint Data, ulong ExtraInfo)
		{
			if ((Flags & ~MouseFlags) != 0 || ((Flags & (MouseXDown | MouseXUp)) != 0 && (Data == 0 || (Data & ~3u) != 0)))
				throw new ArgumentException("Interception received unsupported mouse flags or an invalid X button.");

			if ((Flags & (MouseXDown | MouseXUp)) != 0 && (Flags & (MouseWheel | MouseHWheel)) != 0)
				throw new ArgumentException("Mouse wheel and X button input must be sent separately.");

			lock (gate)
			{
				var device = SenderDevice(false);
				ushort state = (ushort)((Flags >> 1) & 0x3F), flags = 0;

				if ((Flags & MouseXDown) != 0)
					state |= (ushort)(((Data & 1) != 0 ? StrokeCodec.Button4Down : 0) | ((Data & 2) != 0 ? StrokeCodec.Button5Down : 0));

				if ((Flags & MouseXUp) != 0)
					state |= (ushort)(((Data & 1) != 0 ? StrokeCodec.Button4Up : 0) | ((Data & 2) != 0 ? StrokeCodec.Button5Up : 0));

				if ((Flags & MouseWheel) != 0) state |= StrokeCodec.Wheel;
				if ((Flags & MouseHWheel) != 0) state |= StrokeCodec.HWheel;

				if ((Flags & MouseMove) != 0)
				{
					if ((Flags & MouseAbsolute) != 0) flags |= StrokeCodec.MoveAbsolute;
					if ((Flags & MouseVirtualDesktop) != 0) flags |= StrokeCodec.VirtualDesktop;
					if ((Flags & MouseNoCoalesce) != 0) flags |= StrokeCodec.MoveNoCoalesce;
				}

				var rolling = (Flags & (MouseWheel | MouseHWheel)) != 0 ? unchecked((int)Data) : 0;
				var stroke = new NativeStroke
				{
					Mouse = new()
					{
						State = state, Flags = flags, Information = unchecked((uint)ExtraInfo),
						X = (Flags & MouseMove) != 0 ? X : 0, Y = (Flags & MouseMove) != 0 ? Y : 0
					}
				};

				do
				{
					stroke.Mouse.Rolling = (short)Math.Clamp(rolling, short.MinValue, short.MaxValue);
					WriteSender(device, in stroke);
					sentButtons = (ushort)((sentButtons | (stroke.Mouse.State & ButtonDownMask)) & ~((stroke.Mouse.State & ButtonUpMask) >> 1));
					rolling -= stroke.Mouse.Rolling;
					// A split wheel amount must not repeat movement or button transitions.
					stroke.Mouse.State &= StrokeCodec.Wheel | StrokeCodec.HWheel;
					stroke.Mouse.Flags = 0;
					stroke.Mouse.X = stroke.Mouse.Y = 0;
				}
				while (rolling != 0);
			}
		}

		private int SenderDevice(bool isKeyboard)
		{
			ObjectDisposedException.ThrowIf(disposed, this);

			ref var device = ref (isKeyboard ? ref senderKeyboard : ref senderMouse);
			ref var hardwareId = ref (isKeyboard ? ref senderKeyboardHardwareId : ref senderMouseHardwareId);
			var held = isKeyboard ? sentKeys.Count != 0 : sentButtons != 0;

			if (device != 0 && hardwareId == api.HardwareId(device))
				return device;

			if (held)
				throw new InvalidOperationException($"Interception device {device} disconnected while input was held.");

			for (var candidate = isKeyboard ? 1 : FirstMouse; candidate <= (isKeyboard ? FirstMouse - 1 : LastDevice); candidate++)
				if (api.HardwareId(candidate) is { Length: > 0 } current)
				{
					hardwareId = current;
					return device = candidate;
				}

			throw new InvalidOperationException($"Interception has no connected {(isKeyboard ? "keyboard" : "mouse")} for sending.");
		}

		private void WriteSender(int device, in NativeStroke stroke)
		{
			if (!session.IsOwnerSession)
				throw new InvalidOperationException("Interception sending requires the owning console session and input desktop.");

			if (api.Send(device, in stroke) != 1)
				throw new InvalidOperationException($"Interception device {device} did not accept the sent input.");
		}
	}
}
