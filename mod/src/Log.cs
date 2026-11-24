// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// Log lines (English, for us) and rule counters (a rule that never counts is a failed test).
using System.Collections.Concurrent;

namespace PZA.CatCrawlers;

public static class Log
{
    public static Action<string> Sink = _ => { };
    static readonly ConcurrentDictionary<string, int> s_once = new();

    public static void Line(string s) => Sink(s);

    /// <summary>At most <paramref name="most"/> lines per key in a session.</summary>
    public static void Few(string key, string s, int most = 3)
    {
        if (s_once.AddOrUpdate(key, 1, (_, n) => n + 1) <= most) Sink(s);
    }
}

public static class Counters
{
    static readonly ConcurrentDictionary<string, long> s_counts = new();
    public static void Add(string name, long n = 1) => s_counts.AddOrUpdate(name, n, (_, v) => v + n);
    public static long Get(string name) => s_counts.GetValueOrDefault(name);
    public static void Clear() => s_counts.Clear();
    public static string Dump() => string.Join(", ", s_counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
}
