using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Keysharp.Components.Input;

namespace Keysharp.Interception.Tests;

internal static class Strokes
{
	internal const int WaitLimit = 5000;

	internal static NativeStroke Key(ushort code, ushort state = 0, uint information = 0)
		=> new() { Keyboard = new() { Code = code, State = state, Information = information } };

	internal static NativeStroke Mouse(ushort state, ushort flags = 0, short rolling = 0, int x = 0, int y = 0, uint information = 0)
		=> new() { Mouse = new() { State = state, Flags = flags, Rolling = rolling, X = x, Y = y, Information = information } };

	/// <summary>Waits until the reader has finished with the first <paramref name="count"/> strokes it received.</summary>
	internal static void Settled(RecordingApi api, int count)
		=> Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref api.Settled) >= count, WaitLimit), Is.True, "The reader did not settle the queued input.");
}

internal sealed class Session : IInputSession
{
	internal volatile bool Owner = true;

	public bool IsOwnerSession => Owner;
}

internal readonly record struct KeyEvent(uint Message, uint Vk, uint Scan, uint Flags, ulong ExtraInfo, long Device);

internal readonly record struct MouseEvent(uint Message, int X, int Y, uint MouseData, uint Flags, ulong ExtraInfo, long Device,
	int DeltaX, int DeltaY);

/// <summary>Records what it is told. Callbacks run on the reader thread, so tests assert on the records instead.</summary>
internal sealed class Host : IHookHost
{
	internal readonly ConcurrentQueue<KeyEvent> Keys = new();
	internal readonly ConcurrentQueue<MouseEvent> Mice = new();
	internal readonly ConcurrentQueue<string> Losses = new();
	internal int Resets;
	internal volatile Func<KeyEvent, bool>? OnKeyboard;
	internal volatile Func<MouseEvent, bool>? OnMouse;
	internal volatile Action? OnLost;

	public bool KeyboardEvent(uint message, uint virtualKey, uint scanCode, uint flags, uint time, ulong extraInfo, long deviceId)
	{
		var key = new KeyEvent(message, virtualKey, scanCode, flags, extraInfo, deviceId);
		Keys.Enqueue(key);
		return OnKeyboard?.Invoke(key) ?? false;
	}

	public bool MouseEvent(uint message, int x, int y, uint mouseData, uint flags, uint time, ulong extraInfo, long deviceId,
		int deltaX, int deltaY)
	{
		var move = new MouseEvent(message, x, y, mouseData, flags, extraInfo, deviceId, deltaX, deltaY);
		Mice.Enqueue(move);
		return OnMouse?.Invoke(move) ?? false;
	}

	public void ResetState() => Interlocked.Increment(ref Resets);

	public void CaptureLost(string message)
	{
		Losses.Enqueue(message);
		OnLost?.Invoke();
	}
}

/// <summary>
/// A driver context in memory, with a queue per device. Wait offers the device of the oldest queued stroke, which keeps
/// the order a test sees simple; the driver offers the lowest device with input. Misuse, which happens on the reader
/// thread, is recorded in <see cref="Violations"/> for the test thread to check.
/// </summary>
internal sealed class RecordingApi : IInterceptionApi
{
	internal readonly string?[] HardwareIds = new string?[21];
	internal readonly ushort[] Filters = new ushort[21];
	internal readonly ConcurrentQueue<(int Device, NativeStroke Stroke)> Sent = new();
	internal readonly ConcurrentQueue<string> Log = new();
	internal readonly ConcurrentQueue<string> Violations = new();
	internal int FilterWrites, HardwareIdCalls, SendCalls, Receives, Settled, Disposals;
	internal int ReaderPriority = -1;
	internal volatile Func<int, ushort, Exception?>? FilterFailure;
	internal volatile Func<int, int>? SendResult;
	internal volatile Action<int, NativeStroke>? OnSend;
	internal volatile Action? BeforeWait;
	internal volatile bool FailWait;
	private readonly object incoming = new();
	private readonly Queue<(long Order, NativeStroke Stroke)>[] queues = Enumerable.Range(0, 21).Select(_ => new Queue<(long, NativeStroke)>()).ToArray();
	private readonly AutoResetEvent ready = new(false);
	private long order;

	internal RecordingApi(params int[] connected)
	{
		foreach (var device in connected)
			HardwareIds[device] = $"device-{device}";
	}

	internal void Enqueue(int device, NativeStroke stroke) => Enqueue((device, stroke));

	internal void Enqueue(params (int Device, NativeStroke Stroke)[] strokes)
	{
		lock (incoming)
		{
			foreach (var (device, stroke) in strokes)
				queues[device].Enqueue((order++, stroke));

			_ = ready.Set();
		}
	}

	public void SetFilter(int device, ushort filter)
	{
		Guard(nameof(SetFilter));
		_ = Interlocked.Increment(ref FilterWrites);

		if (FilterFailure?.Invoke(device, filter) is { } failure)
			throw failure;

		Filters[device] = filter;
		Log.Enqueue($"filter {device}={filter}");
	}

	public string? HardwareId(int device)
	{
		Guard(nameof(HardwareId));
		_ = Interlocked.Increment(ref HardwareIdCalls);
		return HardwareIds[device];
	}

	public int Wait(int milliseconds)
	{
		Guard(nameof(Wait));
		_ = Interlocked.CompareExchange(ref ReaderPriority, (int)Thread.CurrentThread.Priority, -1);
		// Everything received before this call has been dealt with.
		Volatile.Write(ref Settled, Volatile.Read(ref Receives));
		BeforeWait?.Invoke();

		if (FailWait)
			throw new InvalidOperationException("wait failed");

		var device = Oldest();

		if (device != 0 || milliseconds == 0)
			return device;

		_ = ready.WaitOne(milliseconds);
		return Oldest();
	}

	public bool TryReceive(int device, out NativeStroke stroke)
	{
		Guard(nameof(TryReceive));

		lock (incoming)
		{
			if (queues[device].TryDequeue(out var item))
			{
				stroke = item.Stroke;
				_ = Interlocked.Increment(ref Receives);
				Log.Enqueue($"receive {device}");
				return true;
			}
		}

		stroke = default;
		return false;
	}

	public int Send(int device, in NativeStroke stroke)
	{
		Guard(nameof(Send));
		_ = Interlocked.Increment(ref SendCalls);
		var accepted = SendResult?.Invoke(device) ?? 1;
		Log.Enqueue($"send {device} -> {accepted}");

		if (accepted > 0)
		{
			Sent.Enqueue((device, stroke));
			OnSend?.Invoke(device, stroke);
		}

		return accepted;
	}

	public void Dispose()
	{
		if (Interlocked.Increment(ref Disposals) > 1)
			Violations.Enqueue("disposed twice");

		Log.Enqueue("dispose");
		_ = ready.Set();
	}

	private int Oldest()
	{
		lock (incoming)
		{
			var device = 0;
			var oldest = long.MaxValue;

			for (var i = 1; i < queues.Length; i++)
				if (queues[i].TryPeek(out var item) && item.Order < oldest)
					(device, oldest) = (i, item.Order);

			return device;
		}
	}

	private void Guard(string call)
	{
		if (Volatile.Read(ref Disposals) != 0)
			Violations.Enqueue($"{call} after disposal");
	}
}

/// <summary>interception.dll's exports in memory, for the native wrapper's own logic.</summary>
internal sealed class FakeExports : InterceptionExports
{
	internal nint Context = 0x1234;
	internal int Destroyed, Disposed, LastError;
	internal bool CallsPredicate = true, FailCreate;
	internal ushort Filter;
	internal readonly List<int> Selected = [];
	internal readonly List<string> Mistakes = [];
	internal byte[] HardwareIdBytes = [];

	internal static byte[] Utf16(string text) => MemoryMarshal.AsBytes(text.AsSpan()).ToArray();

	internal override nint CreateContext() => FailCreate ? throw new InvalidOperationException("context failure") : Context;

	internal override void DestroyContext(nint context)
	{
		if (context != Context)
			Mistakes.Add($"destroyed {context}");

		Destroyed++;
	}

	internal override int SetFilter(nint context, DevicePredicate predicate, ushort filter)
	{
		Filter = filter;

		if (CallsPredicate)
		{
			for (var device = 1; device <= 20; device++)
			{
				// The driver's last error must survive the predicate, which runs after each device's call.
				Marshal.SetLastSystemError(1000 + device);

				if (predicate(device) != 0)
					Selected.Add(device);

				if (Marshal.GetLastSystemError() != 1000 + device)
					Mistakes.Add($"the predicate changed the last error at device {device}");
			}
		}

		return LastError;
	}

	internal override uint GetHardwareId(nint context, int device, Span<byte> buffer)
	{
		HardwareIdBytes.CopyTo(buffer);
		return (uint)HardwareIdBytes.Length;
	}

	internal override int Wait(nint context, uint milliseconds) => 0;

	internal override int Receive(nint context, int device, ref NativeStroke stroke) => 0;
	internal override int Send(nint context, int device, in NativeStroke stroke) => 1;

	public override void Dispose() => Disposed++;
}

/// <summary>
/// Stand-ins for interception.dll's exports, which the library wrapper calls through function pointers as it calls the
/// real ones. They read and write strokes as interception.c does, by the device kind's own stroke type: keyboards 1 to
/// 10, mice 11 to 20. A failure in one would end the test process, so they only record, for the test to check.
/// </summary>
internal static unsafe class NativeExports
{
	internal static readonly List<(int Device, ushort Code, ushort State, uint Information)> Keys = [];
	internal static readonly List<(int Device, ushort State, ushort Flags, short Rolling, int X, int Y, uint Information)> Mice = [];
	internal static int Destroyed, LastError;
	internal static bool EmptyReceive;

	internal static void Reset()
	{
		Keys.Clear();
		Mice.Clear();
		Destroyed = LastError = 0;
		EmptyReceive = false;
	}

	internal static nint Find(string name) => name switch
	{
		"interception_create_context" => (nint)(delegate* unmanaged[Cdecl]<nint>)&CreateContext,
		"interception_destroy_context" => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&DestroyContext,
		"interception_send" => (nint)(delegate* unmanaged[Cdecl]<nint, int, byte*, uint, int>)&Send,
		"interception_receive" => (nint)(delegate* unmanaged[Cdecl]<nint, int, byte*, uint, int>)&Receive,
		"interception_wait_with_timeout" => (nint)(delegate* unmanaged[Cdecl]<nint, uint, int>)&Wait,
		_ => (nint)(delegate* unmanaged[Cdecl]<nint>)&Unused
	};

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static nint CreateContext() => 0x1234;

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static void DestroyContext(nint context) => Destroyed++;

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static nint Unused() => 0;

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static int Wait(nint context, uint milliseconds)
	{
		if (LastError != 0)
			Marshal.SetLastSystemError(LastError);

		return 0;
	}

	// InterceptionKeyStroke is {code, state, information}, 8 bytes; InterceptionMouseStroke is {state, flags, rolling,
	// x, y, information}, 20 bytes.
	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static int Send(nint context, int device, byte* strokes, uint count)
	{
		if (count != 1)
			return 0;

		if (device <= 10)
		{
			Keys.Add((device, *(ushort*)strokes, *(ushort*)(strokes + 2), *(uint*)(strokes + 4)));
		}
		else
		{
			Mice.Add((device, *(ushort*)strokes, *(ushort*)(strokes + 2), *(short*)(strokes + 4), *(int*)(strokes + 8), *(int*)(strokes + 12),
				*(uint*)(strokes + 16)));
		}

		return 1;
	}

	// Writes one stroke made from the device number, so that a test can tell what arrived where.
	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
	private static int Receive(nint context, int device, byte* stroke, uint count)
	{
		if (LastError != 0)
			Marshal.SetLastSystemError(LastError);

		if (LastError != 0 || EmptyReceive)
			return 0;

		if (device <= 10)
		{
			*(ushort*)stroke = (ushort)(0x10 + device);
			*(ushort*)(stroke + 2) = 1;
			*(uint*)(stroke + 4) = 0xFFC3D44F;
		}
		else
		{
			*(ushort*)stroke = 0x400;
			*(ushort*)(stroke + 2) = 1;
			*(short*)(stroke + 4) = -120;
			*(int*)(stroke + 8) = device;
			*(int*)(stroke + 12) = -device;
			*(uint*)(stroke + 16) = 0xABCDEF01;
		}

		return 1;
	}
}
