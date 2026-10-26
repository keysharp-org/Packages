using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Keysharp.Interception
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct NativeKeyboardStroke
	{
		internal ushort Code, State;
		internal uint Information;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct NativeMouseStroke
	{
		internal ushort State, Flags;
		internal short Rolling;
		internal int X, Y;
		internal uint Information;
	}

	/// <summary>interception.h's InterceptionStroke, which holds either kind of stroke.</summary>
	[StructLayout(LayoutKind.Explicit, Size = 20)]
	internal struct NativeStroke
	{
		[FieldOffset(0)] internal NativeKeyboardStroke Keyboard;
		[FieldOffset(0)] internal NativeMouseStroke Mouse;
	}

	/// <summary>An Interception context, as the backend uses it.</summary>
	internal interface IInterceptionApi : IDisposable
	{
		/// <summary>Sets one device's filter. Throws when the driver reports a failure.</summary>
		void SetFilter(int device, ushort filter);

		/// <summary>The device's first hardware id, or null when nothing is connected at that slot.</summary>
		string? HardwareId(int device);

		/// <summary>A device with input queued, or 0 once the timeout has passed.</summary>
		int Wait(int milliseconds);

		bool TryReceive(int device, out NativeStroke stroke);

		/// <summary>Writes one stroke and returns one when the driver accepted it.</summary>
		int Send(int device, in NativeStroke stroke);
	}

	internal interface IInputSession
	{
		/// <summary>Whether this process owns the console's current input desktop.</summary>
		bool IsOwnerSession { get; }
	}

	internal sealed class ConsoleSession : IInputSession
	{
		private readonly uint sessionId;

		internal ConsoleSession()
		{
			using var process = Process.GetCurrentProcess();
			sessionId = unchecked((uint)process.SessionId);
		}

		// Console ownership alone does not exclude the lock screen or another secure desktop.
		public bool IsOwnerSession => WTSGetActiveConsoleSessionId() == sessionId && GetCursorPos(out _);

		[DllImport("kernel32.dll", ExactSpelling = true)]
		private static extern uint WTSGetActiveConsoleSessionId();

		[DllImport("user32.dll", ExactSpelling = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetCursorPos(out System.Drawing.Point point);
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	internal delegate int DevicePredicate(int device);

	/// <summary>The interception.dll exports a context uses, so that tests can stand in for the library.</summary>
	internal abstract class InterceptionExports : IDisposable
	{
		internal abstract nint CreateContext();

		internal abstract void DestroyContext(nint context);

		/// <summary>interception_set_filter, returning the last error it left, since the export reports nothing else.</summary>
		internal abstract int SetFilter(nint context, DevicePredicate predicate, ushort filter);

		internal abstract uint GetHardwareId(nint context, int device, Span<byte> buffer);

		internal abstract int Wait(nint context, uint milliseconds);

		internal abstract int Receive(nint context, int device, ref NativeStroke stroke);

		internal abstract int Send(nint context, int device, in NativeStroke stroke);

		public abstract void Dispose();
	}

	/// <summary>A context of the installed driver.</summary>
	internal sealed class NativeInterceptionApi : IInterceptionApi
	{
		/// <summary>interception.h numbers keyboards 1 to 10 and mice 11 to 20.</summary>
		internal const int FirstMouse = 11, LastDevice = 20;
		private const int HardwareIdBytes = 1024;
		private readonly InterceptionExports exports;
		// The owning backend serializes identity reads with teardown.
		private readonly string?[] hardwareIds = new string?[LastDevice + 1];
		private nint context;

		internal NativeInterceptionApi(InterceptionExports exports)
		{
			this.exports = exports;
			try
			{
				context = exports.CreateContext();

				if (context == 0)
					throw new InvalidOperationException("Cannot open the Interception driver. Install it with kpm setup Keysharp/Interception, then restart Windows.");
			}
			catch
			{
				exports.Dispose();
				throw;
			}
		}

		/// <summary>Opens a context through the interception.dll beside this assembly.</summary>
		internal static NativeInterceptionApi Open()
		{
			if (PlatformProblem(OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture) is { } problem)
				throw new PlatformNotSupportedException(problem);

			return new NativeInterceptionApi(InterceptionLibrary.Load());
		}

		/// <summary>Why the plugin cannot run on this machine, or null when it can. Only an x64 driver and library exist for it.</summary>
		internal static string? PlatformProblem(bool windows, Architecture os, Architecture process) =>
			!windows ? "Interception runs on Windows only."
			: os == Architecture.Arm64 ? "Interception has no driver for ARM64 Windows; it runs on x64 Windows only."
			: os != Architecture.X64 ? "Interception runs on x64 Windows only."
			: process != Architecture.X64 ? $"Interception needs an x64 Keysharp process; this one is {process}."
			: null;

		private nint Context => context != 0 ? context : throw new ObjectDisposedException(nameof(NativeInterceptionApi));

		public void SetFilter(int device, ushort filter)
		{
			var selected = false;
			// Called for every device. It selects the one asked for, and keeps the last error a device's call left.
			var predicate = new DevicePredicate(candidate =>
			{
				var error = Marshal.GetLastSystemError();
				selected |= candidate == device;
				Marshal.SetLastSystemError(error);
				return candidate == device ? 1 : 0;
			});
			var lastError = exports.SetFilter(Context, predicate, filter);
			GC.KeepAlive(predicate);

			if (!selected)
				throw new InvalidOperationException($"Interception has no device {device}.");

			if (lastError != 0)
				throw new Win32Exception(lastError, $"Interception could not set the filter of device {device}.");
		}

		public string? HardwareId(int device)
		{
			Span<byte> buffer = stackalloc byte[HardwareIdBytes];
			var size = exports.GetHardwareId(Context, device, buffer);
			ReadOnlySpan<char> id = size < sizeof(char) || size > buffer.Length ? []
				: WindowsDeviceNames.FirstHardwareIdSpan(MemoryMarshal.Cast<byte, char>(buffer[..(int)(size & ~1u)]));
			ref var cached = ref hardwareIds[device];

			if (id.IsEmpty)
				return cached = null;

			if (!id.SequenceEqual(cached.AsSpan()))
				cached = id.ToString();

			return cached;
		}

		public int Wait(int milliseconds) => exports.Wait(Context, checked((uint)milliseconds));

		public bool TryReceive(int device, out NativeStroke stroke)
		{
			stroke = default;
			return exports.Receive(Context, device, ref stroke) == 1;
		}

		public int Send(int device, in NativeStroke stroke) => exports.Send(Context, device, in stroke);

		public void Dispose()
		{
			if (context == 0)
				return;

			exports.DestroyContext(context);
			context = 0;
			exports.Dispose();
		}
	}

	/// <summary>
	/// The interception.dll beside this assembly. No other copy is ever loaded, so a stray one in the application's or a
	/// system directory cannot stand in for the one the package ships.
	/// </summary>
	internal sealed class InterceptionLibrary : InterceptionExports
	{
		private readonly CreateContextExport createContext;
		private readonly DestroyContextExport destroyContext;
		private readonly SetFilterExport setFilter;
		private readonly GetHardwareIdExport getHardwareId;
		private readonly WaitExport wait;
		private readonly TransferExport receive, send;
		private nint library;

		/// <summary>Binds the exports at the addresses <paramref name="export"/> gives, which tests point at stand-ins.</summary>
		internal InterceptionLibrary(Func<string, nint> export)
		{
			createContext = Export<CreateContextExport>(export, "interception_create_context");
			destroyContext = Export<DestroyContextExport>(export, "interception_destroy_context");
			setFilter = Export<SetFilterExport>(export, "interception_set_filter");
			getHardwareId = Export<GetHardwareIdExport>(export, "interception_get_hardware_id");
			wait = Export<WaitExport>(export, "interception_wait_with_timeout");
			receive = Export<TransferExport>(export, "interception_receive");
			send = Export<TransferExport>(export, "interception_send");
		}

		internal static InterceptionLibrary Load()
		{
			var handle = NativeLibrary.Load(LibraryPath(typeof(InterceptionLibrary).Assembly.Location));

			try
			{
				return new InterceptionLibrary(name => NativeLibrary.GetExport(handle, name)) { library = handle };
			}
			catch
			{
				NativeLibrary.Free(handle);
				throw;
			}
		}

		internal static string LibraryPath(string pluginLocation)
		{
			if (string.IsNullOrEmpty(pluginLocation))
				throw new DllNotFoundException("Keysharp.Interception loads interception.dll from its own directory, so it has to be loaded from a file.");

			var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginLocation))!, "interception.dll");
			return File.Exists(path) ? path : throw new DllNotFoundException($"{path} is missing. Reinstall the Keysharp/Interception package.");
		}

		internal override nint CreateContext() => createContext();

		internal override void DestroyContext(nint context) => destroyContext(context);

		internal override int SetFilter(nint context, DevicePredicate predicate, ushort filter)
		{
			Marshal.SetLastSystemError(0);
			setFilter(context, predicate, filter);
			return Marshal.GetLastPInvokeError();
		}

		internal override unsafe uint GetHardwareId(nint context, int device, Span<byte> buffer)
		{
			fixed (byte* bytes = buffer)
				return getHardwareId(context, device, (nint)bytes, (uint)buffer.Length);
		}

		internal override int Wait(nint context, uint milliseconds)
		{
			Marshal.SetLastSystemError(0);
			var device = wait(context, milliseconds);
			var error = Marshal.GetLastPInvokeError();

			return device != 0 || error == 0 ? device : throw new Win32Exception(error);
		}

		internal override unsafe int Receive(nint context, int device, ref NativeStroke stroke)
		{
			Marshal.SetLastSystemError(0);
			int count;

			fixed (NativeStroke* buffer = &stroke)
				count = receive(context, device, (nint)buffer, 1);

			var error = Marshal.GetLastPInvokeError();
			return count != 0 || error == 0 ? count : throw new Win32Exception(error);
		}

		internal override unsafe int Send(nint context, int device, in NativeStroke stroke)
		{
			// One stroke needs no stride conversion; both native types start at offset zero.
			fixed (NativeStroke* buffer = &stroke)
				return send(context, device, (nint)buffer, 1);
		}

		public override void Dispose()
		{
			if (library == 0)
				return;

			NativeLibrary.Free(library);
			library = 0;
		}

		private static T Export<T>(Func<string, nint> export, string name) where T : Delegate
			=> Marshal.GetDelegateForFunctionPointer<T>(export(name));

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate nint CreateContextExport();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate void DestroyContextExport(nint context);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
		private delegate void SetFilterExport(nint context, DevicePredicate predicate, ushort filter);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate uint GetHardwareIdExport(nint context, int device, nint buffer, uint size);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
		private delegate int WaitExport(nint context, uint milliseconds);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
		private delegate int TransferExport(nint context, int device, nint strokes, uint count);
	}
}
