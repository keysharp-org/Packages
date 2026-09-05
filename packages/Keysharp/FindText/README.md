# FindText

Finds text or images on screen by pattern matching, and captures patterns interactively through a
GUI. This is a cross-platform Keysharp port of FeiYue's `FindText.ahk` v10.2.

```ahk
#Include <KPM/Keysharp/FindText>

if (ok := FindText(&x, &y, 0, 0, A_ScreenWidth, A_ScreenHeight, 0, 0, text))
    Click(x, y)

FindText().Gui("Show")   ; capture a new pattern
```

## Relationship to the original

The port is `FindText.ahk` copied, with only the Windows-only calls replaced — a deliberately small
and auditable diff, because the library is thousands of lines of behaviour debugged over years
against real screens. Two things changed:

- the matcher shipped as base64 x86/x64 machine code run through `DllCall(VirtualProtect(...))`;
  the C it was compiled from was in the library's own `code()` method, and a `#CSharp` block is now
  a transcription of exactly that C, so Roslyn compiles it into the script's assembly;
- screen capture and pixel access went through GDI, and now go through Keysharp's `Image` class.

The GUI uses Keysharp's `Highlight` overlay for range borders, platform-appropriate temporary
paths, and a monospace pattern preview off Windows. It requests input monitoring, input injection,
and screen capture permissions when initialized. The Test action runs the managed matcher in the
current process. These interaction fixes ship in version 0.2.0.

Because it uses `#CSharp`, this is a Keysharp package and does not run on AutoHotkey.

## Versioning and licensing

Versioned by the registry (`0.x`), not by upstream: the version numbers here describe *this port*,
and reusing FeiYue's `10.2` would claim to be their release. The upstream version the port tracks is
recorded in `port.json`.

The original carries no stated licence — its header names only its author and version — so the
package declares `NOASSERTION` rather than inventing a permission that was never granted. If you
intend to redistribute it, ask the author.

Original author: FeiYue.
[Forum thread](https://www.autohotkey.com/boards/viewtopic.php?f=83&t=116471)
