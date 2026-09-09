# FileWatch

Watches a directory through `System.IO.FileSystemWatcher` and Keysharp's CLR event bridge. Version
0.1.0 publishes a Windows package; other platforms have not yet been verified.

```powershell
kpm add Keysharp/FileWatch
```

```ahk
#Requires Keysharp >=0.0.0.17
#Include <KPM/Keysharp/FileWatch>

Watcher := FileWatch(A_ScriptDir, OnChange, true, "*.txt")
Persistent()

OnChange(Hook, Action, Name, OldName) {
    OutputDebugLine(Action " " Name)
}
```

The constructor is `FileWatch(Path, Callback, Recursive := false, Filter := "*", Count := -1)`.
It calls `Callback(Hook, Action, Name, OldName)` on the script scheduler that created the watcher.
Actions are `Created`, `Changed`, `Renamed` and `Deleted`; names are relative to the watched
directory, and `OldName` is empty except on rename.

The watcher exposes `Path`, `Recursive`, `Filter`, `Status`, `IsActive`, `Count` and `Paused`, plus
`Pause()` and `Stop()`. A positive count stops the watcher after that many callbacks begin delivery.
Call `Stop()` when finished so the CLR subscriptions and underlying watcher are disposed.

File-system notifications can be duplicated, delayed or coalesced. A watcher error stops the
watcher and raises an `OSError`; rescan the directory before creating a replacement because events
may have been lost.
