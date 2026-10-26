; Check mouse positioning, physical input and replugging without opening a window.
#ErrorStdOut
#Warn All, StdOut
#SingleInstance Off
#Warn Experimental, Off
#Import Ks { Clr }
#Startup Clr.Load(A_ScriptDir "\..\..\platform\win-x64\native\Keysharp.Interception.dll").InterceptionBackend.Install()
#Import Ks { Input }

global ResultFile := A_Temp "\keysharp-interception-probe-identity.txt"
global KeyboardEvents := 0, MouseEvents := 0, Failures := 0, Finished := false
global MouseMoves := 0, MouseMovesComplete := false
global LastKeyboardHardwareId := "", LastMouseHardwareId := ""
global LastKeyboardId := 0, LastMouseId := 0
OnError(RecordError)
try FileDelete(ResultFile)
Note("started", A_Now)
ListDevices("initial")
CheckMouseMoves()
MouseMovesComplete := true

if A_Args.Length == 1 && A_Args[1] == "--mouse-only"
    Finish("mouse")

Note("instructions", "Press F8 on each keyboard and click the middle button on each mouse. Replug and repeat. Press Ctrl+Alt+F12 to finish; Ctrl+Alt+F11 lists devices again. Automatic timeout is 90 seconds.")
SetTimer(() => Finish("timeout"), -90000)

~$F8::Observe("Keyboard")
~$MButton::Observe("Mouse")
~$^!F11::ListDevices("refresh")
~$^!F12::Finish("done")

CheckMouseMoves()
{
    CoordMode("Mouse", "Screen")
    MouseGetPos(&OriginalX, &OriginalY)
    CenterX := Floor(A_ScreenWidth / 2)
    CenterY := Floor(A_ScreenHeight / 2)

    try
    {
        for Mode in ["Event", "Input"]
        {
            SendMode(Mode)

            for Point in [[0, 0], [A_ScreenWidth - 1, A_ScreenHeight - 1], [CenterX, CenterY], [CenterX, CenterY]]
            {
                MouseMove(Point[1], Point[2], 0)
                CheckPosition(Mode " absolute", Point[1], Point[2])
            }

            MouseMove(12, -9, 0, "R")
            CheckPosition(Mode " relative", CenterX + 12, CenterY - 9)
        }
    }
    finally
    {
        SendMode("Event")
        MouseMove(OriginalX, OriginalY, 0)
        CheckPosition("restore", OriginalX, OriginalY)
    }
}

CheckPosition(Label, ExpectedX, ExpectedY)
{
    global Failures, MouseMoves
    Sleep(50)
    MouseGetPos(&ActualX, &ActualY)
    Good := Abs(ActualX - ExpectedX) <= 1 && Abs(ActualY - ExpectedY) <= 1
    MouseMoves++

    if !Good
        Failures++

    Note("move", (Good ? "ok " : "FAIL ") Label " expected=" ExpectedX "," ExpectedY " actual=" ActualX "," ActualY)
}

Observe(Kind)
{
    global KeyboardEvents, MouseEvents, Failures
    global LastKeyboardHardwareId, LastMouseHardwareId, LastKeyboardId, LastMouseId
    Device := A_EventInfo?.Device

    if !IsSet(Device)
    {
        Failures++
        return Note("event", "FAIL no device for " Kind)
    }

    Matches := Input.Devices.Filter(Current => Current.Id == Device.Id && Current.Kind == Kind)

    if Device.Kind != Kind || Matches.Length != 1
    {
        Failures++
        return Note("event", "FAIL inventory mismatch kind=" Device.Kind " id=" Device.Id " matches=" Matches.Length)
    }

    Current := Matches[1]

    if Device.HardwareId == "" || Device.HardwareId != Current.HardwareId
        || Device.VendorId != Current.VendorId || Device.ProductId != Current.ProductId
    {
        Failures++
        return Note("event", "FAIL metadata mismatch id=" Device.Id)
    }

    Note("event", "ok " Describe(Device))

    if Kind == "Keyboard"
    {
        KeyboardEvents++

        if LastKeyboardHardwareId == Device.HardwareId && LastKeyboardId != Device.Id
            Note("replug", "ok Keyboard same hardware ID, slot " LastKeyboardId " to " Device.Id)

        LastKeyboardHardwareId := Device.HardwareId
        LastKeyboardId := Device.Id
    }
    else
    {
        MouseEvents++

        if LastMouseHardwareId == Device.HardwareId && LastMouseId != Device.Id
            Note("replug", "ok Mouse same hardware ID, slot " LastMouseId " to " Device.Id)

        LastMouseHardwareId := Device.HardwareId
        LastMouseId := Device.Id
    }
}

ListDevices(Reason)
{
    Devices := Input.Devices
    Note("inventory", Reason " count=" Devices.Length)

    for Device in Devices
        Note("device", Describe(Device))
}

Describe(Device) => Device.Kind " id=" Device.Id " name=" Device.Name " vendor=" Device.VendorId " product=" Device.ProductId " hardware=" Device.HardwareId

Note(Key, Value)
{
    FileAppend(Key "=" Value "`n", ResultFile, "UTF-8")
    FileAppend(Key "=" Value "`n", "*")
}

RecordError(Error, Mode)
{
    global Failures
    Failures++
    Note("error", "FAIL " Error.Message " [" Mode "]")
    Finish("error")
    return 1
}

Finish(Reason)
{
    global Finished, Failures

    if Finished || (Reason == "done" && !MouseMovesComplete)
        return

    Finished := true
    ListDevices("final")

    if Reason != "mouse" && (KeyboardEvents == 0 || MouseEvents == 0)
        Failures++

    Note("observed", "keyboard=" KeyboardEvents " mouse=" MouseEvents " moves=" MouseMoves " failures=" Failures)
    Passed := (Reason == "done" || Reason == "mouse") && Failures == 0
    Note("finish", Passed ? "PASS " Reason : "FAIL " Reason)
    ExitApp(Passed ? 0 : 1)
}
