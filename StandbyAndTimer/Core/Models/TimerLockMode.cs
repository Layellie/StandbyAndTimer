namespace StandbyAndTimer.Core.Models;

/// <summary>The lock on the Timer card: when the timer turns on by itself.</summary>
public enum TimerLockMode
{
    /// <summary>Never by itself; only the TIMER button.</summary>
    Unlocked,

    /// <summary>At every app start.</summary>
    Locked,

    /// <summary>While a listed game runs: on when one starts, off when the last one exits.</summary>
    InGame,
}
