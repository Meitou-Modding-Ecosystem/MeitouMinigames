// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// The dealer's calls. They are Kenshi's own: each call is read at run time from the vanilla dialogue record that says it (the Swamper Casino
// Dealer's game tree, dialogue 48362-Dialogue.mod), in the language the game runs in, so nothing of the game's text ships with the mod and
// a translated game gets translated calls. Only three things are changed in a line: the word swaps (/GAMBLE/ is the dealer's own nonsense
// list, read from its record), the quotes round it, and the number of cats (the vanilla tree always says 200 or 600; the table says what it
// pays). A record that is missing or empty (another mod removed it) falls back to our own line (Strings "call.*").
// No game types here: the mod passes a lookup, the fuzz harness passes its own.
using System.Text.RegularExpressions;

namespace PZA.CatCrawlers;

public sealed class Calls(Func<string, IReadOnlyList<string>> texts, Func<int> pick)
{
    const string Mod = "-Dialogue.mod";
    public const string DealerRecord = "49317" + Mod;          // CHARACTER "Swamper Casino Dealer"
    const string Invite_ = "48363", HowTo_ = "48368", Flourish_ = "48369", Gamble_ = "48371", Leave_ = "51286", SayFish_ = "48377", SayRoll_ = "48378";
    // by the face card (2..6)
    static readonly string[] Face_ = ["", "", "48372", "48373", "48374", "48375", "48376", ""];
    static readonly string[] Fish_ = ["", "", "48415", "48422", "48425", "48430", "48433", ""];
    static readonly string[] Roll_ = ["", "", "48383", "48393", "48402", "48407", "48411", ""];
    // by the card turned (1..7)
    static readonly string[] Dry_ = ["", "48416", "48423", "48382", "48396", "48398", "48399", "48406"];
    static readonly string[] Lick_ = ["", "48394", "48395", "48418", "48419", "48417", "48420", "48421"];
    static readonly string[] Girl_ = ["", "48380", "", "", "", "", "", "48435"];

    /// <summary>Every record id the calls read (the offline check lists the ones the game lacks).</summary>
    public static IEnumerable<string> AllRecords() =>
        new[] { Invite_, HowTo_, Flourish_, Gamble_, Leave_, SayFish_, SayRoll_ }.Concat(Face_).Concat(Fish_).Concat(Roll_).Concat(Dry_).Concat(Lick_).Concat(Girl_)
            .Where(s => s.Length > 0).Distinct().Select(s => s + Mod);

    static readonly Regex Token = new(@"/([A-Z]+)/", RegexOptions.CultureInvariant);
    static readonly Regex Number = new(@"\d[\d,\.]*\d|\d", RegexOptions.CultureInvariant);

    public string Invite() => Line(Invite_, 0, () => Strings.T("call.invite"));
    public string HowTo() => Line(HowTo_, 0, () => "");
    public string Flourish() => Line(Flourish_, 0, () => Strings.T("call.flourish"));
    public string Leave() => Line(Leave_, 0, () => Strings.T("call.leave"));
    public string SayFish() => Line(SayFish_, 0, () => Strings.T("say.fish"));
    public string SayRoll() => Line(SayRoll_, 0, () => Strings.T("say.roll"));
    public string Face(Crawler face) => Line(At(Face_, face), 0, () => Strings.TF("call.face", Strings.Name(face)));
    public string Fish(Crawler face) => Line(At(Fish_, face), 0, () => Strings.TF("call.fish", Strings.Name(face)));
    public string Roll(Crawler face) => Line(At(Roll_, face), 0, () => Strings.TF("call.roll", Strings.Name(face)));
    public string Dry(Crawler card) => Line(At(Dry_, card), 0, () => Strings.TF("call.dry", Strings.Name(card)));
    public string Lick(Crawler card, long cats) => Line(At(Lick_, card), cats, () => Strings.TF("call.lick", Strings.Name(card), Strings.Cats(cats)));
    public string Girl(Crawler card, long cats) =>
        Line(At(Girl_, card), cats, () => At(Lick_, card).Length > 0 && card is not (Crawler.WolfTail or Crawler.Plank)
            ? Lick(card, cats) : Strings.TF("call.girl", Strings.Name(card), Strings.Cats(cats)));

    /// <summary>Any dialogue record's line, cleaned and with its word swaps expanded (the Shady Dealer quoting the Dust King); <paramref
    /// name="fallback"/> when the record is missing.</summary>
    public string Quote(string record, Func<string> fallback)
    {
        string? raw = One(record);
        if (raw is null) { Counters.Add("calls.fallback"); return fallback(); }
        Counters.Add("calls.vanilla");
        return Clean(Expand(raw, 0));
    }

    static string At(string[] ids, Crawler c) => (int)c >= 0 && (int)c < ids.Length ? ids[(int)c] : "";

    string Line(string id, long cats, Func<string> fallback)
    {
        string? raw = id.Length == 0 ? null : One(id + Mod);
        if (raw is null)
        {
            if (id.Length > 0) Counters.Add("calls.fallback");
            return fallback();
        }
        Counters.Add("calls.vanilla");
        string s = Clean(Expand(raw, 0));
        if (cats > 0)
        {
            // the first number in the line is the cats it pays (200 or 600 in the vanilla tree)
            var m = Number.Match(s);
            if (m.Success) s = s[..m.Index] + Strings.Cats(cats) + s[(m.Index + m.Length)..];
        }
        return s;
    }

    /// <summary>One of the record's texts (a line has several variants), or null when it has none.</summary>
    string? One(string record)
    {
        IReadOnlyList<string> all;
        try { all = texts(record); }
        catch (Exception e) { Log.Few("calls.throw", $"calls: reading {record} threw {e.GetType().Name}"); return null; }
        var usable = all.Where(t => !string.IsNullOrWhiteSpace(Clean(t))).ToList();
        return usable.Count == 0 ? null : usable[(int)((uint)pick() % (uint)usable.Count)];
    }

    /// <summary>Word swaps: /GAMBLE/ from the dealer's own list, /OUTLANDER/ and /HEHAS/ from our strings, any other token as its plain word.</summary>
    string Expand(string s, int depth) => Token.Replace(s, m => m.Groups[1].Value switch
    {
        "GAMBLE" when depth < 2 => One(Gamble_ + Mod) is { } g ? Clean(Expand(g, depth + 1)) : "rolling",
        "OUTLANDER" => Strings.T("outlander"),
        "HEHAS" => Strings.T("hehas"),
        "QUICKLAUGH" => Strings.T("quicklaugh"),
        var other => other.ToLowerInvariant(),
    });

    /// <summary>The line without the quotes the dialogue editor's authors typed round it, on one line.</summary>
    public static string Clean(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Length >= 2 && s[0] is '"' or '“' && s[^1] is '"' or '”') s = s[1..^1].Trim();
        return s.Trim('"', '“', '”', ' ');
    }
}
