using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Keysharp.Interception
{
	internal readonly record struct DeviceName(string Kind, string HardwareId, string Name);

	internal static class WindowsDeviceNames
	{
		private const uint Present = 2, HardwareIdProperty = 1, FriendlyNameProperty = 12, DescriptionProperty = 0;
		private const uint StringProperty = 1, StringListProperty = 7;
		private static readonly Guid KeyboardClass = new("4d36e96b-e325-11ce-bfc1-08002be10318");
		private static readonly Guid MouseClass = new("4d36e96f-e325-11ce-bfc1-08002be10318");

		internal static string Key(string kind, string hardwareId) => kind + '\0' + hardwareId;

		// The driver and SetupAPI give the same NUL-separated list, most specific first. Both key a device on its first
		// ID; the later ones are generic, such as HID_DEVICE, and shared by many devices.
		internal static string? FirstHardwareId(ReadOnlySpan<char> ids)
		{
			var id = FirstHardwareIdSpan(ids);
			return id.IsEmpty ? null : id.ToString();
		}

		internal static ReadOnlySpan<char> FirstHardwareIdSpan(ReadOnlySpan<char> ids)
		{
			var end = ids.IndexOf('\0');
			return end < 0 ? ids : ids[..end];
		}

		internal static Dictionary<string, string> UniqueNames(DeviceName[] devices)
		{
			var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			foreach (var device in devices)
			{
				var key = Key(device.Kind, device.HardwareId);

				// A hardware ID describes a model or interface, so two matching devnodes cannot name a particular slot.
				if (!names.TryAdd(key, device.Name))
					names[key] = "";
			}

			return names;
		}

		internal static DeviceName[] GetDevices()
		{
			if (!OperatingSystem.IsWindows())
				return [];

			var devices = new List<DeviceName>();
			AddClass(devices, KeyboardClass, "Keyboard");
			AddClass(devices, MouseClass, "Mouse");
			return devices.ToArray();
		}

		private static void AddClass(List<DeviceName> devices, Guid classGuid, string kind)
		{
			var set = SetupDiGetClassDevsW(in classGuid, null, 0, Present);

			if (set == -1)
				return;

			try
			{
				var data = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };

				for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
				{
					if (FirstHardwareId(Property(set, ref data, HardwareIdProperty, StringListProperty)) is not { } id)
						continue;

					var name = Property(set, ref data, FriendlyNameProperty, StringProperty);

					if (name.Length == 0)
						name = Property(set, ref data, DescriptionProperty, StringProperty);

					devices.Add(new DeviceName(kind, id, name));
				}
			}
			finally
			{
				_ = SetupDiDestroyDeviceInfoList(set);
			}
		}

		private static string Property(nint set, ref DeviceInfoData data, uint property, uint expectedType)
		{
			_ = SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out var needed);

			if (needed == 0 || needed > 65536 || (needed & 1) != 0)
				return "";

			var bytes = new byte[needed];

			if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out var type, bytes, needed, out var used)
				|| type != expectedType || used > needed || (used & 1) != 0)
				return "";

			var value = new string(MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, (int)used)));
			return expectedType == StringProperty ? value.TrimEnd('\0') : value;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct DeviceInfoData
		{
			internal uint Size;
			internal Guid ClassGuid;
			internal uint Instance;
			internal nint Reserved;
		}

		[DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern nint SetupDiGetClassDevsW(in Guid classGuid, string? enumerator, nint parent, uint flags);

		[DllImport("setupapi.dll", ExactSpelling = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfoData data);

		[DllImport("setupapi.dll", ExactSpelling = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfoData data, uint property,
			out uint type, [Out] byte[]? buffer, uint bufferSize, out uint needed);

		[DllImport("setupapi.dll", ExactSpelling = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool SetupDiDestroyDeviceInfoList(nint set);
	}
}
