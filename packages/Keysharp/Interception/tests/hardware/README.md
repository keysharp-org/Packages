# Hardware probe

`interception-trial.ks` is the interactive GUI test. It shows physical keyboard and mouse
devices, sends text to its own test box (F1), and draws relative (F2) and absolute (F3) mouse
squares. It registers both capture and the sender provider: text uses `SendPlay`, and mouse
functions use `SendMode("Play")`. Both squares move 80 pixels per side, by absolute and relative
moves, and check all four corners. Both submit a move back to the original pointer position. Buttons perform the same actions.
Close the window or press Ctrl+Alt+X to exit.

```powershell
Keysharp.exe --errorstdout interception-trial.ks
```

`probe-identity.ks` checks mouse positioning, physical input and replugging. It moves the pointer,
restores its starting position, sends no clicks or keys and opens no window. Run it with a Keysharp
build that supports `#Startup`, `Script.RegisterHookProvider`, `Input.Devices` and `A_EventInfo.Device`:

```powershell
Keysharp.exe --errorstdout probe-identity.ks
```

It first checks absolute moves to the primary screen's origin, far corner and centre, including
a repeated destination, in both Event and Input send modes. Each mode also checks a relative
move. Positions must match within one pixel. Keep the mouse still during this brief check.
These moves use Keysharp's native sender while Interception capture is active; they do not
simulate physical absolute-device packets, which the automated plugin tests cover.

For just the unattended movement checks and device inventory, run:

```powershell
Keysharp.exe --errorstdout probe-identity.ks --mouse-only
```

It prints and writes the complete initial inventory to
`%TEMP%\keysharp-interception-probe-identity.txt`. Press F8 on a keyboard and click a mouse's
middle button; each event must have the right kind and match the current inventory by slot and
hardware ID, vendor and product. The hotkeys pass physical input on to Windows. If F8 is not
available on the keyboard, choose another key in the script before running it.

Unplug and reconnect a keyboard or mouse, then repeat its event. Compare the listed hardware ID
before and after even when the slot remains the same; a changed slot with the same hardware ID
gets an explicit `replug=ok` line. `Ctrl+Alt+F11` reads the inventory again so removal and
insertion can be checked without an event. `Ctrl+Alt+F12` finishes; the probe also exits after
90 seconds. The manual phase passes only when at least one keyboard and one mouse event were
checked, no check failed and it was finished explicitly. The log records which observations were made; a
PASS alone does not establish that every attached device was replugged.

Interception hardware IDs can be shared by identical devices. This probe confirms the ID the
driver reports, not a unique physical unit identifier. Friendly names are optional and can be
empty when a hardware ID matches more than one Windows device. This manual check is not part
of the simulated test run.
