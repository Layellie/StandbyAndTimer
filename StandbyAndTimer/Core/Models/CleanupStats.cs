using System.Globalization;

namespace StandbyAndTimer.Core.Models;

public sealed class CleanupTotal
{
    public int  Count      { get; set; }
    public long FreedBytes { get; set; }

    internal void Add(long freedBytes) { Count++; FreedBytes += freedBytes; }
}

/// <summary>
/// The Maintenance history kept across restarts: the last <see cref="RecentMax"/>
/// cleanups, per-day totals for <see cref="KeepDays"/> days, totals per target and
/// for all time. Bounded, so the file stays a few KB. Same shape as PurgeStats.
/// </summary>
public sealed class CleanupStats
{
    public const int RecentMax = 20;
    public const int KeepDays  = 60;

    /// <summary>Newest first.</summary>
    public List<CleanupRecord>              Recent   { get; set; } = [];
    /// <summary>Local date "yyyy-MM-dd" -> that day's cleanups.</summary>
    public Dictionary<string, CleanupTotal> Days     { get; set; } = [];
    /// <summary>Target id -> cleanups of that target, all time.</summary>
    public Dictionary<string, CleanupTotal> ByTarget { get; set; } = [];
    public CleanupTotal                     AllTime  { get; set; } = new();
    /// <summary>When the first cleanup in these totals ran (or the last reset).</summary>
    public DateTime?                        Since    { get; set; }

    internal static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Add(CleanupRecord record)
    {
        Since ??= record.At;
        Recent.Insert(0, record);
        if (Recent.Count > RecentMax) Recent.RemoveRange(RecentMax, Recent.Count - RecentMax);

        string day = DayKey(record.At);
        if (!Days.TryGetValue(day, out var total)) Days[day] = total = new CleanupTotal();
        total.Add(record.FreedBytes);

        if (!ByTarget.TryGetValue(record.TargetId, out var byTarget)) ByTarget[record.TargetId] = byTarget = new CleanupTotal();
        byTarget.Add(record.FreedBytes);

        AllTime.Add(record.FreedBytes);

        string oldest = DayKey(record.At.Date.AddDays(-(KeepDays - 1)));
        foreach (string old in Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList())
            Days.Remove(old);
    }

    /// <summary>Cleanups of the <paramref name="days"/> days ending with <paramref name="today"/> (1 = today only).</summary>
    public CleanupTotal LastDays(int days, DateTime today)
    {
        var sum = new CleanupTotal();
        for (int i = 0; i < days; i++)
            if (Days.TryGetValue(DayKey(today.Date.AddDays(-i)), out var t))
            {
                sum.Count      += t.Count;
                sum.FreedBytes += t.FreedBytes;
            }
        return sum;
    }

    public CleanupTotal Of(string targetId) => ByTarget.TryGetValue(targetId, out var t) ? t : new CleanupTotal();
}
