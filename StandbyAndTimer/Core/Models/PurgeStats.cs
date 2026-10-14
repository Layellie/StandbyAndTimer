using System.Globalization;

namespace StandbyAndTimer.Core.Models;

public sealed class PurgeTotal
{
    public int  Count   { get; set; }
    public long FreedMb { get; set; }

    internal void Add(long freedMb) { Count++; FreedMb += freedMb; }
}

/// <summary>
/// The purge history kept across restarts: the last <see cref="RecentMax"/>
/// purges, per-day totals for <see cref="KeepDays"/> days, totals per kind
/// and for all time. Bounded, so the file never grows past a few KB.
/// </summary>
public sealed class PurgeStats
{
    public const int RecentMax = 20;
    public const int KeepDays  = 60;

    /// <summary>Newest first.</summary>
    public List<PurgeResult>              Recent  { get; set; } = [];
    /// <summary>Local date "yyyy-MM-dd" -> that day's purges.</summary>
    public Dictionary<string, PurgeTotal> Days    { get; set; } = [];
    /// <summary>PurgeKind name -> purges of that kind, all time.</summary>
    public Dictionary<string, PurgeTotal> ByKind  { get; set; } = [];
    public PurgeTotal                     AllTime { get; set; } = new();
    /// <summary>When the first purge in these totals ran (or the last reset).</summary>
    public DateTime?                      Since   { get; set; }

    internal static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Add(PurgeResult result)
    {
        Since ??= result.At;
        Recent.Insert(0, result);
        if (Recent.Count > RecentMax) Recent.RemoveRange(RecentMax, Recent.Count - RecentMax);

        string day = DayKey(result.At);
        if (!Days.TryGetValue(day, out var total)) Days[day] = total = new PurgeTotal();
        total.Add(result.FreedMb);

        string kind = result.Kind.ToString();
        if (!ByKind.TryGetValue(kind, out var byKind)) ByKind[kind] = byKind = new PurgeTotal();
        byKind.Add(result.FreedMb);

        AllTime.Add(result.FreedMb);

        string oldest = DayKey(result.At.Date.AddDays(-(KeepDays - 1)));
        foreach (string old in Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList())
            Days.Remove(old);
    }

    /// <summary>Purges of the <paramref name="days"/> days ending with <paramref name="today"/> (1 = today only).</summary>
    public PurgeTotal LastDays(int days, DateTime today)
    {
        var sum = new PurgeTotal();
        for (int i = 0; i < days; i++)
            if (Days.TryGetValue(DayKey(today.Date.AddDays(-i)), out var t))
            {
                sum.Count   += t.Count;
                sum.FreedMb += t.FreedMb;
            }
        return sum;
    }

    public PurgeTotal Of(PurgeKind kind) => ByKind.TryGetValue(kind.ToString(), out var t) ? t : new PurgeTotal();
}
