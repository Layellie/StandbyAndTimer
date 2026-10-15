namespace StandbyAndTimer.Services.Maintenance;

/// <summary>
/// Runs a shell call on its own STA thread: the shell's file and Recycle Bin
/// functions are documented for single-threaded apartments, the thread pool
/// is MTA, and the UI thread must not block on them.
/// </summary>
internal static class StaTask
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "ShellSta" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
