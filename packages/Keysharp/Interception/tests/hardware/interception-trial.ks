#ErrorStdOut
#Warn All, StdOut
#Warn Experimental, Off
#SingleInstance Off
#Import Ks { Clr, Input }
#Startup Clr.Load(A_ScriptDir "\..\..\platform\win-x64\native\Keysharp.Interception.dll").InterceptionBackend.Install()
#Startup Clr.Load(A_ScriptDir "\..\..\platform\win-x64\native\Keysharp.Interception.dll").InterceptionBackend.InstallSender()

global LastMouse := ""
Monitor := Gui("+AlwaysOnTop", "Interception SendPlay trial")
Monitor.SetFont("s10", "Consolas")
KeyLine := Monitor.Add("Text", "w680", "Press a key to see its device.")
MouseLine := Monitor.Add("Text", "w680", "Move the mouse to see its device.")
Monitor.Add("Text", "w680 y+12", "F1: send keys to the test box   F2: relative square   F3: absolute square`nCtrl+Alt+L: refresh devices   Ctrl+Alt+X: exit")
TestEdit := Monitor.Add("Edit", "w680 r4")
Monitor.Add("Button", "w210", "Send keys (F1)").OnEvent("Click", (*) => TypeLine())
Monitor.Add("Button", "x+10 w210", "Relative square (F2)").OnEvent("Click", (*) => DrawSquare(false))
Monitor.Add("Button", "x+10 w210", "Absolute square (F3)").OnEvent("Click", (*) => DrawSquare(true))
ResultLine := Monitor.Add("Text", "xm w680 r2", "Interception captures input and sends through the driver in Play mode.")
DeviceList := Monitor.Add("Edit", "w680 r5 ReadOnly")
Monitor.OnEvent("Close", (*) => ExitApp())
Monitor.Show("x20 y20")

Watcher := InputHook("V")
Watcher.KeyOpt("{All}", "N")
Watcher.OnKeyDown := ShowKey
Watcher.OnMouseMove := NoteMouse
Watcher.Start()
SetTimer(ShowMouse, 100)
ListDevices()

Describe(Device) => Format("{} {} '{}' {}:{} {}", Device.Id, Device.Kind, Device.Name, HexId(Device.VendorId), HexId(Device.ProductId), Device.HardwareId)

; A device that reports no vendor or product ID reads as an empty string.
HexId(Value) => IsNumber(Value) ? Format("{:04X}", Value) : "----"

; Only events from a physical device have a Device property; injected ones, such as SendPlay's, do not.
EventDevice() => IsObject(A_EventInfo) && HasProp(A_EventInfo, "Device") ? Describe(A_EventInfo.Device) : "no physical device"

ShowKey(Hook, VirtualKey, ScanCode)
{
    KeyLine.Value := Format("Key {} (vk {:02X}, sc {:03X}) from {}", GetKeyName(Format("vk{:x}sc{:x}", VirtualKey, ScanCode)), VirtualKey, ScanCode, EventDevice())
}

NoteMouse(Hook, DeltaX, DeltaY)
{
    global LastMouse := Format("Mouse moved {}, {} (raw units) from {}", DeltaX, DeltaY, EventDevice())
}

ShowMouse()
{
    if LastMouse != "" && MouseLine.Value != LastMouse
        MouseLine.Value := LastMouse
}

ListDevices()
{
    Text := ""

    for Device in Input.Devices
        Text .= Describe(Device) "`r`n"

    DeviceList.Value := Text == "" ? "No devices listed." : Text
}

TypeLine()
{
    WinActivate("ahk_id " Monitor.Hwnd)

    if !WinWaitActive("ahk_id " Monitor.Hwnd, , 2)
        return ResultLine.Value := "FAIL: test window did not activate."

    TestEdit.Focus()
    TestEdit.Value := ""
    SendPlay("Typed through Interception with SendPlay.{Enter}")
    Sleep(150)
    ResultLine.Value := Trim(TestEdit.Value, "`r`n") == "Typed through Interception with SendPlay." ? "PASS: SendPlay keys reached the test box." : "FAIL: SendPlay text did not reach the test box."
}

DrawSquare(Absolute)
{
    CoordMode("Mouse", "Screen")
    MouseGetPos(&OriginalX, &OriginalY)
    SendMode("Play")
    StartX := Max(0, Min(OriginalX, A_ScreenWidth - 300))
    StartY := Max(0, Min(OriginalY, A_ScreenHeight - 300))
    Failures := 0

    try
    {
        MouseMove(StartX, StartY, 0)
        Sleep(150)
        ExpectedX := StartX, ExpectedY := StartY

        for Step in [[80, 0], [0, 80], [-80, 0], [0, -80]]
        {
            ExpectedX += Step[1], ExpectedY += Step[2]

            if Absolute
                MouseMove(ExpectedX, ExpectedY, 10)
            else
                MouseMove(Step[1], Step[2], 10, "R")

            Sleep(150)
            MouseGetPos(&ActualX, &ActualY)

            if Abs(ActualX - ExpectedX) > 1 || Abs(ActualY - ExpectedY) > 1
                Failures++
        }
    }
    finally
    {
        MouseMove(OriginalX, OriginalY, 0)
        Sleep(150)
    }

    ResultLine.Value := (Failures == 0 ? "PASS: " : "FAIL: ") "SendPlay " (Absolute ? "absolute" : "relative")
        " square (80 pixels per side); " Failures " position mismatches. Pointer restore submitted."
}

F1::TypeLine()
F2::DrawSquare(false)
F3::DrawSquare(true)
^!l::ListDevices()
^!x::ExitApp()
