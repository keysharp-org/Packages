# Ax

Inspects and automates macOS accessibility trees through the AXUIElement API — the counterpart to
Acc on Windows and [Keysharp/AtSpi](../AtSpi) on Linux.

```ahk
#Include <KPM/Keysharp/Ax>

el := Ax.ElementFromPoint(100, 200)
MsgBox(el.Title " (" el.RoleName ")")
Ax.Viewer()             ; interactive tree browser
```

Entry points include `GetRootElement`, `GetFocusedElement`, `GetFocusedWindow`, `ElementFromPoint`,
`ElementFromHandle`, `ElementFromPid`, `WindowList`, `Applications` and `Observe` for AXObserver
notifications. Elements expose `Name`, `Title`, `Role`/`Subrole`, `Value`, `Enabled` and the rest of
the AX attribute surface.

## Requirements

macOS only, and it needs permission you have to grant yourself: Accessibility and Input Monitoring
trust for the running application, in System Settings → Privacy & Security. Keysharp requests the
`AccessibilityAutomation` and `InputMonitoring` capabilities, but macOS still asks you.

Sandboxed or privileged applications can expose incomplete trees or deny individual attributes and
actions regardless of trust.

Ships with Keysharp as well; this package exists so a project can pin a version of it.
