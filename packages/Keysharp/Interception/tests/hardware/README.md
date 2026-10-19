# Hardware probe

`probe-identity.ks` is a separate manual check for physical input and replugging. It sends no
input, changes no lock keys and opens no window. Run it from this directory with a Keysharp
build that supports `#Startup`, `Script.RegisterHookProvider`, `Input.Devices` and `A_EventInfo.Device`:

```powershell
Keysharp.exe --errorstdout probe-identity.ks
```

It prints and writes the complete initial inventory to
`%TEMP%\keysharp-interception-probe-identity.txt`. Press F24 on a keyboard and click a mouse's
middle button; each event must have the right kind and match the current inventory by slot and
hardware ID, vendor and product. The hotkeys pass physical input on to Windows. If F24 is not
available on the keyboard, choose another key in the script before running it.

Unplug and reconnect a keyboard or mouse, then repeat its event. Compare the listed hardware ID
before and after even when the slot remains the same; a changed slot with the same hardware ID
gets an explicit `replug=ok` line. `Ctrl+Alt+F11` reads the inventory again so removal and
insertion can be checked without an event. `Ctrl+Alt+F12` finishes; the probe also exits after
90 seconds. It passes only when at least one keyboard and one mouse event were checked, no
check failed and it was finished explicitly. The log records which observations were made; a
PASS alone does not establish that every attached device was replugged.

Interception hardware IDs can be shared by identical devices. This probe confirms the ID the
driver reports, not a unique physical unit identifier. Friendly names are optional and can be
empty when a hardware ID matches more than one Windows device. This manual check is not part
of the simulated test run.
