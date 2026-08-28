# AtSpi

Inspects and automates Linux accessibility trees through AT-SPI — the counterpart to UIA or Acc on
Windows, and to [Keysharp/Ax](../Ax) on macOS.

```ahk
#Include <KPM/Keysharp/AtSpi>

el := AtSpi.ElementFromPoint(100, 200)
MsgBox(el.Name " (" el.RoleName ")")
AtSpi.Viewer()          ; interactive tree browser
```

`AtSpi.Accessible` exposes the usual properties — `Name`, `Description`, `Role`, `States`,
`Attributes`, `Parent`, `Children`, `Location`, `Value` — plus `FindElement`, `WaitElement`,
`Dump`, `Highlight`, `Click`, `Focus`, and the Text and EditableText interface helpers.

## Requirements

Linux only, and it needs more than the package:

- AT-SPI accessibility enabled in the desktop environment. Turning it on sometimes needs a log out
  and back in before the bus appears.
- `libatspi`, `libglib-2.0` and `libgobject` present on the system.
- The AT-SPI bus (`org.a11y.Bus`) running, which it usually is once accessibility is enabled.

Keysharp requests the `AccessibilityAutomation` and `InputMonitoring` capabilities on your behalf.

Ships with Keysharp as well; this package exists so a project can pin a version of it.
