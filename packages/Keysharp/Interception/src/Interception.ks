; Install the driver with kpm setup Keysharp/Interception, then restart Windows.
; Interception is free for non-commercial use only; LICENSE-Interception.txt has its terms.
#Warn Experimental, Off
#Import Ks { Clr }
#Startup Clr.Load(A_ScriptDir "\Lib\KPM\Keysharp\Interception\native\Keysharp.Interception.dll").InterceptionBackend.Install()
#Startup Clr.Load(A_ScriptDir "\Lib\KPM\Keysharp\Interception\native\Keysharp.Interception.dll").InterceptionBackend.InstallSender()
