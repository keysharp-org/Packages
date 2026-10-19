# Interception

Captures keyboard and mouse input through the
[Interception](https://github.com/oblitum/Interception) driver, below Windows' own input handling.
Hotkeys, hotstrings and InputHook receive captured events through Keysharp's normal processing.
`Send`, `Click` and `MouseMove` use Keysharp's native sender.

Windows x64 only. There is no Interception driver for ARM64 Windows, and the plugin needs an x64
Keysharp process, version 0.0.0.18 or later.

```powershell
kpm add Keysharp/Interception
kpm setup Keysharp/Interception
```

`kpm setup` shows the driver installer's command and asks before running it as administrator.
Restart Windows afterwards.

```ahk
#ErrorStdOut
#Include <KPM/Keysharp/Interception>

F1::Send("Hello")
```

The include registers the plugin with `#Startup`, before hotkey registration. Without the driver,
startup stops with an error; `#ErrorStdOut` sends it to stderr. The include expects KPM's normal
project-local `Lib/KPM/Keysharp/Interception` directory beside the main script.

For a different DLL location, omit the include and register explicitly:

```ahk
#ErrorStdOut
#Warn All, StdOut
#Warn Experimental, Off
#Import Ks { Clr }
#Startup Capture := Clr.Load(A_ScriptDir "\plugins\Keysharp.Interception.dll").InterceptionBackend.Install()
```

Keep `interception.dll` beside `Keysharp.Interception.dll`. Disposing `Capture` removes the plugin
and restores native hooks; ignoring the handle keeps capture registered until script shutdown.
Compiled scripts need these files deployed alongside them at the path their startup expression
uses. The compiler does not embed or copy them. Every target machine also needs the driver installed.

## What changes

- Hotkeys see input from the driver's keyboards and mice. Windows `SendInput` events do not pass
  through the driver; native hooks continue observing injected input for Keysharp's sending logic.
- Hotkeys use capture instead of Windows hotkey registration, as `$` hotkeys do, so
  `A_EventInfo.Device` can describe the originating keyboard or mouse.
- The driver sees Windows keyboard and mouse class devices. Precision touchpads, touchscreens and
  pens normally use other classes: their input reaches neither these hotkeys nor InputHook, and
  this capture cannot block it.
- With NumLock on, keyboards and Windows add fake Shift transitions around some numpad and
  navigation keys. Keysharp does not reproduce them, so Shift with those keys can behave
  differently than with native hooks.
- The driver may deliver input from the lock screen, UAC prompts or the Ctrl+Alt+Delete screen.
  These paths are unverified on hardware; do not rely on Ctrl+Alt+Delete to end BlockInput.
- While another session owns the console or a secure desktop is active, the plugin clears its capture filters and forwards queued input without script callbacks.
  Session changes clear tracked key state before capture resumes, since releases may have been missed.

The simulated tests check packet decoding, suppression, forwarding, device metadata and teardown.
This integration has not been validated against a physical Interception device.

## Device identity

Under the package, `Input.Devices` lists the keyboards and mice the driver sees, and
`A_EventInfo.Device` describes the device behind each event it captures. Keysharp's
[Input reference](https://keysharp-org.github.io/KeysharpDocs/lib/Input.htm) describes both, and
its [`#Startup` reference](https://keysharp-org.github.io/KeysharpDocs/lib/_Startup.htm)
describes early plugin registration. This is what the plugin supplies:

- `Id` is the current Interception slot: 1 to 10 for keyboards, 11 to 20 for mice. Slots change
  when a device is replugged or the PC resumes from sleep.
- `HardwareId` is the driver's first native hardware ID, such as
  `HID\VID_046D&PID_C52B&REV_1211&MI_00` or `ACPI\VEN_LEN&DEV_0071`. It identifies a model or
  interface and normally survives a slot change, but identical devices can have the same value,
  so it cannot distinguish those physical units.
- `VendorId` and `ProductId` are the numbers in complete `VID_` and `PID_` hexadecimal tokens; IDs
  such as ACPI's `VEN_` and `DEV_` do not supply them, so both are 0.
- `Name` is the friendly name or description Windows gives the keyboard or mouse class device
  whose first hardware ID matches, when exactly one present device of that kind matches. For most
  devices that is a generic name such as `HID Keyboard Device`, so match on `HardwareId`. The name
  is empty when Windows supplies none or the match is ambiguous.
- Names are cached. The plugin reads them in the background when capture first starts, and again
  each time the list is read. An event lookup never waits for Windows: a hardware ID missing from
  the cache has an empty name until a background read, at most one a second, adds it, and keeps an
  empty name when Windows does not list it.
- Device-change notifications keep using Keysharp's native device monitoring.

Test the hardware ID to keep a hotkey with one model or interface across slot changes:

```ahk
#ErrorStdOut
#Warn All, StdOut
#Include <KPM/Keysharp/Interception>
global Target := "HID\VID_046D&PID_C52B&REV_1211&MI_00"

#HotIf (A_EventInfo.Device?.HardwareId == Target) ?? false
F1::Send("Hello")
#HotIf
```

The manual check in `tests/hardware/probe-identity.ks` lists the real devices and compares
physical keyboard and mouse events with that list, including after a replug. It sends no input
and opens no window. This check has not been run as part of the simulated tests.

## If input stops

A capturing script stands between your keyboards and mice and Windows. When the script takes too
long to decide about a key, Keysharp passes the key on. Holding Backspace, Escape and Enter together
makes Keysharp let go of the capture until the script next asks for it, as when its hotkeys change
or it starts an InputHook. The chord releases driver capture. If the script enabled Windows'
native `BlockInput`, it still needs the normal `BlockInput "Off"` or an OS escape; the chord does
not release that thread-owned block. Ctrl+Alt+Delete has not been verified with the driver.

While the Keysharp process is stopped, for example at a debugger breakpoint, it cannot forward the
input the driver captures or recognize the chord. Captured keyboards and mice can remain blocked
until the process resumes or ends; input from devices it does not capture is unaffected. Do not
debug a capturing script using only the devices it captures.

## Removing the driver

Run `Lib/KPM/Keysharp/Interception/native/setup/install-interception.exe /uninstall` from an
administrator prompt in the project's directory, then restart Windows. Do this before
`kpm remove`, which deletes the installer along with the package.

## Building and testing

Use the SDK pinned in `global.json` and the matching Input contract in `plugin/ref/`.
Pass the absolute path to a matching installed `Keysharp.Core.dll` or its SDK reference assembly;
the package does not carry a copy of Core. From this directory:

```powershell
dotnet build plugin/Keysharp.Interception.csproj -c Release -p:KeysharpCorePath=C:\Keysharp\Keysharp.Core.dll -p:UpdatePackageBinary=true
dotnet test tests/Keysharp.Interception.Tests.csproj -c Release -p:KeysharpCorePath=C:\Keysharp\Keysharp.Core.dll
```

Only `UpdatePackageBinary=true` updates the packaged plugin. Tests use fake driver exports and
never activate the installed driver. Regenerate the package manifest after changing its payload.

## Licensing

The plugin, `native/Keysharp.Interception.dll`, is BSD-2-Clause licensed (see `LICENSE`). Its source
and tests are in the [registry repository](https://github.com/keysharp-org/Packages/tree/main/packages/Keysharp/Interception).

`native/interception.dll` and `native/setup/install-interception.exe` are from Interception 1.0.1
by Francisco Lopes. They are free for non-commercial use only: the library under the LGPL 3.0, the
driver and its installer under the author's grant to redistribute them. Commercial use needs a
licence from the author. `LICENSE-Interception.txt` has the terms, and `LICENSE-GPL-3.0.txt` the
GNU GPL 3.0 that the LGPL builds on. A deployment that carries the library must retain its licensing terms.
