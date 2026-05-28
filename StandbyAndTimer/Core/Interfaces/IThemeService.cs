using StandbyAndTimer.Core.Models;

namespace StandbyAndTimer.Core.Interfaces;

public interface IThemeService : IDisposable
{
    Theme CurrentTheme    { get; }
    Theme EffectiveTheme  { get; }
    void  SetTheme(Theme theme);
    event EventHandler? ThemeChanged;
}
