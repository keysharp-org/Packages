; Observe physical input and replugging without sending input or opening a window.
#ErrorStdOut
#Warn All, StdOut
#SingleInstance Off
#Warn Experimental, Off
#Import Ks { Clr }
#Startup Clr.Load(A_ScriptDir "\..\..\platform\win-x64\native\Keysharp.Interception.dll").InterceptionBackend.Install()
#Import Ks { Input }

global ResultFile := A_Temp "\keysharp-interception-probe-identity.txt"
global KeyboardEvents := 0, MouseEvents := 0, Failures := 0, Finished := false
global LastKeyboardHardwareId := "", LastMouseHardwareId := ""
global LastKeyboardId := 0, LastMouseId := 0
OnError(RecordError)
try FileDelete(ResultFile)
Note("started", A_Now)
ListDevices("initial")
Note("instructions", "Press F24 on each keyboard and click the middle button on each mouse. Replug and repeat. Press Ctrl+Alt+F12 to finish; Ctrl+Alt+F11 lists devices again. Automatic timeout is 90 seconds.")
SetTimer(() => Finish("timeout"), -90000)

~$F24::Observe("Keyboard")
~$MButton::Observe("Mouse")
~$^!F11::ListDevices("refresh")
~$^!F12::Finish("done")

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

    if Finished
        return

    Finished := true
    ListDevices("final")

    if KeyboardEvents == 0 || MouseEvents == 0
        Failures++

    Note("observed", "keyboard=" KeyboardEvents " mouse=" MouseEvents " failures=" Failures)
    Note("finish", Reason == "done" && Failures == 0 ? "PASS" : "FAIL " Reason)
    ExitApp
}
