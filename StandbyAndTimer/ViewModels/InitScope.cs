namespace StandbyAndTimer.ViewModels;

// Tiny RAII helper that flips a "we're loading from settings, suppress side
// effects" flag on entry and clears it on Dispose. Both MainViewModel and
// SettingsViewModel had the same try/finally pattern around their bulk
// ApplySettings / Initialize blocks; using a `using` scope means the flag
// can't accidentally be left set if an exception escapes the body.
//
// Usage:
//     using (new InitScope(b => _isInitializing = b))
//     {
//         // bulk property assignments here — partial-method change handlers
//         // see _isInitializing == true and skip Persist / SettingsChanged.
//     }
internal readonly struct InitScope : IDisposable
{
    private readonly Action<bool> _set;

    public InitScope(Action<bool> set)
    {
        _set = set;
        _set(true);
    }

    public void Dispose() => _set(false);
}
