; Reimplementation of just me's LV_Colors Row, Cell, and Clear interface using Keysharp's ListView API.
; Interface reference: https://github.com/AHK-just-me/AHK2_LV_Colors
; Colors are cleared by row/column insertion or deletion and sorting; unsorted Add and Modify retain them.

class LV_Colors {
    __New(ListView) {
        if !(ListView is Gui.ListView)
            throw TypeError("LV_Colors requires a Gui.ListView control.")
        this.ListView := ListView
    }

    Row(Row, BackColor := "", TextColor := "") {
        this.ListView.SetRowColor(Row, TextColor, BackColor)
        return true
    }

    Cell(Row, Column, BackColor := "", TextColor := "") {
        this.ListView.SetCellColor(Row, Column, TextColor, BackColor)
        return true
    }

    Clear(AltRows := false, AltCols := false) {
        this.ListView.ClearColors()
        return true
    }
}
