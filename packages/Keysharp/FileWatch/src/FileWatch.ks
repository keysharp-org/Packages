; FileSystemWatcher subscriptions through Ks.Clr, with callbacks on the owning script thread.
; Call Persistent() in a standalone watcher script and Stop() when finished watching.

class FileWatch {
    __New(Path, Callback, Recursive := false, Filter := "*", Count := -1) {
        #Import Ks { Clr }

        if this.HasOwnProp("__Subscriptions")
            throw ValueError("A FileWatch cannot be initialized more than once.")

        this.__Subscriptions := []
        this.__Watcher := ""
        this.__Stopped := true
        this.__Paused := false
        this.__Remaining := 0
        this.__Callback := ""
        this.__Path := Path

        if !HasMethod(Callback, "Call")
            throw TypeError("FileWatch Callback must be callable.")
        Count := Integer(Count)
        if Count != -1 && Count < 1
            throw ValueError("Count must be -1 (unlimited) or a positive number.")
        Filter := String(Filter)
        if Filter == ""
            Filter := "*"
        if InStr(Filter, "/") || InStr(Filter, "\") || InStr(Filter, Chr(0))
            throw ValueError("Filter must be a filename wildcard without directory separators.")

        try {
            this.__Path := Clr.System.IO.Path.GetFullPath(Path)
            this.__Recursive := !!Recursive
            this.__Filter := Filter
            this.__Callback := Callback
            this.__Remaining := Count
            assembly := Clr.Load("System.IO.FileSystem.Watcher")
            this.__Watcher := assembly.System.IO.FileSystemWatcher()
            this.__Watcher.Path := this.__Path
            this.__Watcher.Filter := Filter
            this.__Watcher.IncludeSubdirectories := this.__Recursive
            this.__Watcher.NotifyFilter := Clr.System.Enum.Parse(assembly.System.IO.NotifyFilters, "FileName, DirectoryName, LastWrite, Size")

            ; Retain the subscriptions so Stop detaches the same CLR delegates that were attached.
            for changeAction in ["Created", "Changed", "Deleted", "Renamed"]
                this.__Subscriptions.Push(this.__Watcher.OnEvent(changeAction, ObjBindMethod(this, "__Change", changeAction)))
            this.__Subscriptions.Push(this.__Watcher.OnEvent("Error", ObjBindMethod(this, "__Error")))
            this.__Stopped := false
            this.__Watcher.EnableRaisingEvents := true
        }
        catch Error as failure {
            this.Stop()
            if failure is ValueError || failure is TypeError || failure is OSError
                throw failure
            throw this.__Failure("could not watch", failure.Message)
        }
    }

    Path => this.__Path
    Recursive => this.__Recursive
    Filter => this.__Filter
    Status => !this.__Live ? "Stopped" : this.__Paused ? "Paused" : "Active"
    IsActive => this.Status == "Active"
    Count => this.__Live ? this.__Remaining : 0

    Paused {
        get => this.__Live && this.__Paused
        set {
            if this.__Live
                this.__Paused := !!value
        }
    }

    Pause(NewState := 1) {
        if !this.__Live
            return false
        this.__Paused := NewState == -1 ? !this.__Paused : !!NewState
        return this.__Paused
    }

    Stop() {
        if !this.HasOwnProp("__Subscriptions")
            return ""

        this.__Stopped := true
        this.__Paused := false
        this.__Remaining := 0
        for subscription in this.__Subscriptions
            subscription.Stop()
        this.__Subscriptions := []
        if IsObject(this.__Watcher)
            this.__Watcher.Dispose()
        this.__Watcher := ""
        this.__Callback := ""
        return ""
    }

    __Delete() => this.Stop()

    __Live => !this.__Stopped && this.__Subscriptions.Length > 0 && this.__Subscriptions[1].IsActive

    __Change(Action, Sender, Change) {
        if !this.IsActive
            return

        userCallback := this.__Callback
        if this.__Remaining > 0 {
            this.__Remaining -= 1
            if this.__Remaining == 0
                this.Stop()
        }

        userCallback.Call(this, Action, Change.Name ?? "", Action == "Renamed" ? Change.OldName ?? "" : "")
    }

    __Error(Sender, Event) {
        if !this.__Live
            return

        sourceFailure := Event.GetException()
        failureMessage := IsObject(sourceFailure) ? sourceFailure.Message : "The file system watcher failed."
        failureNumber := IsObject(sourceFailure) ? sourceFailure.HResult : 0
        this.Stop()
        throw this.__Failure("stopped watching", failureMessage " Events may have been lost; rescan the directory and create a new watcher.", failureNumber)
    }

    __Failure(Action, Message, Number := 0) {
        failure := OSError()
        failure.Message := "FileWatch " Action " '" this.__Path "': " Message
        failure.Number := Number
        failure.What := "FileWatch"
        return failure
    }
}
