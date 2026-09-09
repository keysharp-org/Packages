# LV_Colors

Reimplements just me's familiar `LV_Colors` row, cell and clear interface using Keysharp's native
`Gui.ListView` color methods. Version 0.1.0 publishes a Windows package; rendering on other
platforms has not yet been verified.

```powershell
kpm add Keysharp/LV_Colors
```

```ahk
#Requires Keysharp >=0.0.0.17
#Include <KPM/Keysharp/LV_Colors>

Window := Gui()
List := Window.AddListView("r10 w400", ["Name", "State"])
List.Add(, "Build", "Ready")
Colors := LV_Colors(List)
Colors.Row(1, "E8F5E9", "1B5E20")
Colors.Cell(1, 2, "FFF3E0", "E65100")
```

The supported compatibility surface is:

- `Row(Row, BackColor := "", TextColor := "")`
- `Cell(Row, Column, BackColor := "", TextColor := "")`
- `Clear(AltRows := false, AltCols := false)`

These methods preserve the original background-first argument order and return `true`. `Clear`'s
two compatibility flags are accepted but have no effect. Alternating row or column modes, static
mode and selection coloring are outside this package's surface.

Color assignments use existing one-based row and column indices. Inserting or deleting a row or
column, or sorting the ListView, clears the assignments; reapply them after structural changes.

The interface is derived from
[just me's LV_Colors](https://github.com/AHK-just-me/AHK2_LV_Colors). This package is a small
Keysharp-specific reimplementation and does not copy the original Windows custom-draw machinery.
