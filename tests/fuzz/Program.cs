// PZA's Cat Crawlers for Meitou: the fuzz harness. Author: PZA. Licence: GPLv3 or later. Never shipped; it never starts the game.
//   strings.load   the shared PZA's Meitou UI target on Strings.Table
//   core.table     random streams of legal and illegal actions on a Table against a plain model: no card lost or made (every crawler: crawl +
//                  gutter = 4), the face card is never a Wolf Tail or a Plank, a lick pays stake x 6 / winning ranks, the same crawler is a
//                  dryout, a refused action changes nothing, the pot never rides past an end of the crawl or the house limit, and the
//                  player's money never goes below 0 and always equals what the model paid and took
//   core.save      Table.Save / Load round trips at every point of a random game; nasty saves load as an empty table on a full crawl that
//                  pays nothing; Ledger the same
//   core.purse     Purse.Shown against a model with random queueing, applying and resets: never negative, exact once everything is applied
//   calls.lines    Calls on random dialogue texts (nasty strings, word swaps inside word swaps, empty and throwing records): never throws,
//                  never leaves a /TOKEN/ in a line, puts the pot's number into a lick line, falls back to our line for a missing record
//   save.parse     CrawlSystem.Parse on random and mutated bytes: never throws; what the system writes parses back
using PZA.CatCrawlers;
using PZA.MeitouUi.Fuzz;

static class Program
{
    static int Main(string[] a) => PzaFuzz.Run("PZA's Cat Crawlers for Meitou", a,
    [
        PzaFuzzUi.Strings(Strings.Table),
        new("core.table", 6000, TableGame, fz =>
        {
            foreach (var rule in new[] { "deal", "burn", "fresh", "lick", "girl", "dryout", "tie", "take", "ride", "end_of_crawl", "house_limit", "refused", "fresh_mid_chain" })
                fz.Expect(fz.Counted(rule) > 0, $"rule '{rule}' never fired (a rule that never fires is a failed test)");
        }),
        new("core.save", 4000, SaveLoad, fz =>
        {
            fz.Expect(fz.Counted("bad_refused") > 0, "no nasty save was refused");
            fz.Expect(fz.Counted("mid_hand") > 0, "no save was taken in the middle of a hand");
        }),
        new("core.purse", 6000, PurseModel),
        new("calls.lines", 8000, Lines, fz =>
        {
            fz.Expect(Counters.Get("calls.vanilla") > 0, "no line came from a record");
            fz.Expect(Counters.Get("calls.fallback") > 0, "no line fell back to ours");
            fz.Expect(fz.Counted("number") > 0, "no lick line got its number");
        }),
        new("save.parse", 6000, Parse),
        .. RaidFuzz.Targets,
        .. WorldFuzz.Targets,
        .. WrestleFuzz.Targets,
    ]);

    // ---------------------------------------------------------------------------------------------------------------- core.table

    static void TableGame(PzaFuzz fz)
    {
        var t = new Table();
        // some games start on a crawl that is nearly played out (a loaded save), so fresh crawls in mid hand are common
        if (fz.Chance(0.4))
        {
            var thin = new long[16];
            thin[0] = 1;
            for (int c = 1; c <= 7; c++) thin[8 + c] = fz.Chance(0.6) ? 0 : fz.Below(3);
            if (fz.Chance(0.3))
            {
                // ... or in the middle of a hand with the crawl played out: the next call needs a fresh crawl less the face card
                // (some with a pot just under the house limit, so chains reach it on a middle card)
                (thin[1], thin[2], thin[4], thin[5]) = ((int)Phase.Choose, fz.Int(2, 6), 100, fz.Chance(0.5) ? 100 : fz.Int(3000, 5999));
                if (fz.Chance(0.7)) for (int c = 1; c <= 7; c++) thin[8 + c] = 0;
                thin[8 + thin[2]] = Math.Min(thin[8 + thin[2]], 3);
            }
            fz.Expect(t.Load(thin), "a thin crawl did not load");
        }
        long purse = fz.Pick(new long[] { 0, 99, 100, 150, 500, 5000, 1_000_000 }), start = purse;
        long paid = 0, taken = 0;
        // the fuzzer steers the picks: mostly random, sometimes the lowest or highest card, so ends, ties and long chains all happen
        int bias = fz.Below(4);
        int Next() => bias switch { 1 => 0, 2 => int.MaxValue, _ => (int)(fz.NextU64() & 0x7fffffff) };
        int steps = fz.Int(1, 400);
        for (int i = 0; i < steps; i++)
        {
            if (fz.Chance(0.15)) bias = fz.Below(4);
            var before = t.Save();
            var (phase, face, pot, chain, total) = (t.Phase, t.Face, t.Pot, t.Chain, t.Crawl.Total);
            Table.Result r;
            // mostly something the phase allows (so games run long enough to empty a crawl), sometimes anything at all
            int act = fz.Chance(0.2) ? fz.Below(5) : phase switch { Phase.Choose => 1, Phase.Licked => fz.Chance(0.6) && t.CanRide ? 4 : 3, _ => 0 };
            switch (act)
            {
                case 0:
                {
                    long stake = fz.Chance(0.8) ? fz.Pick(Table.Stakes) : fz.Pick(new long[] { 0, -100, 1, 101, 1000, long.MaxValue });
                    bool legal = (phase is Phase.Idle or Phase.Dry) && Table.Stakes.Contains((int)Math.Clamp(stake, 0, int.MaxValue)) && stake <= purse;
                    r = t.Deal(stake, purse, Next);
                    fz.Expect((r.Outcome == Outcome.Dealt) == legal, $"Deal({stake}) with purse {purse} in {phase}: {r.Outcome}");
                    if (r.Outcome == Outcome.Dealt)
                    {
                        fz.Count("deal");
                        if (r.Burned > 0) fz.Count("burn");
                        if (r.FreshCrawl) fz.Count("fresh");
                        fz.Expect(r.Cats == -stake, $"Deal took {r.Cats} for a stake of {stake}");
                        fz.Expect(t.Face is > Crawler.WolfTail and < Crawler.Plank, $"the dealer opened on {t.Face}");
                        fz.Expect(t.Pot == stake && t.Chain == 0 && t.Phase == Phase.Choose && t.Drawn == Crawler.None, "a dealt hand is not a clean hand");
                        fz.Expect(r.FreshCrawl || total >= Table.MinToDeal, $"dealt from a crawl of {total} without a fresh one");
                    }
                    break;
                }
                case 1 or 2:
                {
                    var call = fz.Bool() ? Call.Fish : Call.RollBack;
                    r = t.Play(call, Next);
                    fz.Expect((r.Outcome != Outcome.Refused) == (phase == Phase.Choose), $"Play in {phase}: {r.Outcome}");
                    if (r.Outcome == Outcome.Refused) break;
                    int k = call == Call.Fish ? 7 - (int)face : (int)face - 1;
                    bool win = call == Call.Fish ? r.Card > face : r.Card < face;
                    fz.Expect(r.Card is >= Crawler.WolfTail and <= Crawler.Plank, $"turned {r.Card}");
                    fz.Expect(r.Cats == 0, "a call moved money (only Deal and Take do)");
                    if (r.FreshCrawl) { fz.Count("fresh"); fz.Count("fresh_mid_chain"); fz.Expect(total == 0, $"a fresh crawl in mid hand with {total} cards left"); }
                    if (win)
                    {
                        fz.Expect(r.Outcome == (k == 1 ? Outcome.Girl : Outcome.Lick), $"a win with {k} winning ranks was {r.Outcome}");
                        fz.Expect(t.Pot == pot * 6 / k && t.Pot > pot, $"a lick on pot {pot} with {k} winning ranks left {t.Pot}");
                        fz.Expect(t.Chain == chain + 1 && t.Phase == Phase.Licked, "a lick did not count");
                        fz.Count(k == 1 ? "girl" : "lick");
                        bool end = r.Card is Crawler.WolfTail or Crawler.Plank;
                        if (end) fz.Count("end_of_crawl");
                        if (t.Pot >= Table.HouseLimit) fz.Count("house_limit");
                        fz.Expect(t.CanRide == (!end && t.Pot < Table.HouseLimit), $"CanRide {t.CanRide} on {r.Card} with pot {t.Pot}");
                    }
                    else
                    {
                        fz.Expect(r.Outcome == (r.Card == face ? Outcome.Tie : Outcome.Dryout), $"a loss on {r.Card} against {face} was {r.Outcome}");
                        fz.Expect(t.Pot == 0 && t.Chain == 0 && t.Phase == Phase.Dry, "a dryout left something on the table");
                        fz.Count(r.Card == face ? "tie" : "dryout");
                    }
                    break;
                }
                case 3:
                    r = t.Take();
                    fz.Expect((r.Outcome == Outcome.Took) == (phase == Phase.Licked), $"Take in {phase}: {r.Outcome}");
                    if (r.Outcome == Outcome.Took)
                    {
                        fz.Count("take");
                        fz.Expect(r.Cats == pot && pot > 0 && t.Pot == 0 && t.Phase == Phase.Idle, $"Take paid {r.Cats} of a pot of {pot}");
                    }
                    break;
                default:
                {
                    bool could = t.CanRide;
                    var drawn = t.Drawn;
                    r = t.Ride();
                    fz.Expect((r.Outcome == Outcome.Rode) == could, $"Ride with CanRide {could}: {r.Outcome}");
                    if (r.Outcome == Outcome.Rode)
                    {
                        fz.Count("ride");
                        fz.Expect(t.Face == drawn && t.Pot == pot && t.Chain == chain && t.Phase == Phase.Choose && r.Cats == 0, "a ride changed more than the face card");
                    }
                    break;
                }
            }
            if (r.Outcome == Outcome.Refused)
            {
                fz.Count("refused");
                fz.Expect(t.Save().SequenceEqual(before) && r.Cats == 0, "a refused action changed the table or moved money");
            }
            purse += r.Cats;
            if (r.Cats < 0) taken -= r.Cats; else paid += r.Cats;
            fz.Expect(purse >= 0, $"the purse went to {purse}");
            // the cards: every crawler is in the crawl or in the gutter, the hand's cards among the gutter's
            int sum = 0;
            for (int c = 1; c <= 7; c++)
            {
                int left = t.Crawl.Left((Crawler)c), gut = t.Crawl.Gutter((Crawler)c);
                sum += left;
                fz.Expect(left is >= 0 and <= 4 && left + gut == 4, $"{(Crawler)c}: {left} in the crawl, {gut} in the gutter");
            }
            fz.Expect(sum == t.Crawl.Total, $"the crawl counts {t.Crawl.Total}, its cards sum to {sum}");
            if (t.Phase is Phase.Choose) fz.Expect(t.Crawl.Gutter(t.Face) >= 1, "the face card is still in the crawl");
            if (r.Outcome is Outcome.Lick or Outcome.Girl or Outcome.Dryout or Outcome.Tie && r.FreshCrawl)
                fz.Expect(t.Crawl.Gutter(face) >= 1 && t.Crawl.Total == Crawl.Size - 2 + (r.Card == face ? 0 : 0), $"after a fresh crawl in mid hand: {t.Crawl.Total} cards, face in the gutter {t.Crawl.Gutter(face)}");
            fz.Expect(t.Pot >= 0 && (t.Pot > 0) == (t.Phase is Phase.Choose or Phase.Licked), $"pot {t.Pot} in {t.Phase}");
            fz.Expect(t.Pot <= Table.HouseLimit * 6, $"pot {t.Pot} passed any pot the house limit allows");
        }
        fz.Expect(purse == start - taken + paid, "the purse does not add up");
    }

    // ----------------------------------------------------------------------------------------------------------------- core.save

    static void SaveLoad(PzaFuzz fz)
    {
        var t = new Table();
        int Next() => (int)(fz.NextU64() & 0x7fffffff);
        for (int i = fz.Int(0, 25); i > 0; i--)
            switch (fz.Below(4))
            {
                case 0: t.Deal(fz.Pick(Table.Stakes), 1_000_000, Next); break;
                case 1: t.Play(fz.Bool() ? Call.Fish : Call.RollBack, Next); break;
                case 2: t.Take(); break;
                default: t.Ride(); break;
            }
        var saved = t.Save();
        if (t.Phase is Phase.Choose or Phase.Licked) fz.Count("mid_hand");
        var u = new Table();
        fz.Expect(u.Load(saved) && u.Save().SequenceEqual(saved), $"a table saved in {t.Phase} did not load back the same");
        fz.Expect(u.Phase == t.Phase && u.Face == t.Face && u.Drawn == t.Drawn && u.Pot == t.Pot && u.Chain == t.Chain && u.Crawl.Total == t.Crawl.Total, "the loaded table differs");

        // a nasty save: one or more numbers wrong, or the wrong length
        var bad = saved.ToList();
        int kind = fz.Below(4);
        if (kind == 0) bad.RemoveAt(fz.Below(bad.Count));
        else if (kind == 1) bad.Add(fz.IntNasty());
        else
            for (int n = fz.Int(1, 3); n > 0; n--)
                bad[fz.Below(bad.Count)] = fz.Below(3) switch { 0 => fz.IntNasty(-10, 10), 1 => long.MaxValue - fz.Below(3), _ => -fz.Int(1, 1000) };
        if (fz.Chance(0.15))
        {
            // a hand whose face card is an end of the crawl (no call could be made on it)
            bad = [.. new Table().Save()];
            (bad[1], bad[2], bad[4], bad[5]) = ((int)Phase.Choose, fz.Bool() ? 1 : 7, 100, 100);
            fz.Count("end_face");
        }
        var v = new Table();
        bool ok = v.Load(bad);
        if (!ok)
        {
            fz.Count("bad_refused");
            fz.Expect(v.Phase == Phase.Idle && v.Pot == 0 && v.Crawl.Total == Crawl.Size && v.Take().Outcome == Outcome.Refused, "a refused save left something to pay");
        }
        else
        {
            // accepted: it must still be a table that adds up and cannot pay more than the house limit allows
            fz.Expect(v.Pot >= 0 && v.Pot <= Table.HouseLimit * 6, $"a nasty save loaded with a pot of {v.Pot}");
            fz.Expect((v.Pot > 0) == (v.Phase is Phase.Choose or Phase.Licked), $"a nasty save loaded with pot {v.Pot} in {v.Phase}");
            for (int c = 1; c <= 7; c++) fz.Expect(v.Crawl.Left((Crawler)c) is >= 0 and <= 4, "a nasty save loaded a crawl with impossible counts");
            if (v.Phase != Phase.Idle && v.Phase != Phase.Dry) fz.Expect(v.Face is > Crawler.WolfTail and < Crawler.Plank, $"a nasty save loaded the face card {v.Face}");
        }

        var led = new Ledger { Hands = fz.Below(1000), Licks = fz.Below(1000), Dryouts = fz.Below(1000), Girls = fz.Below(50), BestChain = fz.Below(9), BiggestPot = fz.Below(40000), Staked = fz.Below(1 << 20), Paid = fz.Below(1 << 20) };
        var l2 = new Ledger();
        fz.Expect(l2.Load(led.Save()) && l2.Save().SequenceEqual(led.Save()) && l2.Net == led.Paid - led.Staked, "the ledger did not round trip");
        var lb = led.Save().ToList();
        lb[fz.Below(lb.Count)] = -fz.Int(1, 99);
        var l3 = new Ledger();
        fz.Expect(!l3.Load(lb) && l3.Save().Skip(1).All(x => x == 0), "a ledger with a negative number loaded");
    }

    // ---------------------------------------------------------------------------------------------------------------- core.purse

    static void PurseModel(PzaFuzz fz)
    {
        var p = new Purse();
        long game = fz.Below(5000), applied = 0;
        var queue = new List<(long Seq, long Cats)>();
        for (int i = fz.Int(1, 40); i > 0; i--)
        {
            switch (fz.Below(4))
            {
                case 0 or 1:
                {
                    long cats = fz.Int(-600, 6000);
                    long seq = p.Queue(cats);
                    fz.Expect(queue.Count == 0 || seq > queue[^1].Seq, "sequence numbers do not rise");
                    fz.Expect(seq > applied, "a new command got a sequence the game already applied");
                    queue.Add((seq, cats));
                    break;
                }
                case 2 when queue.Count > 0:
                    // the game applies the oldest (clamped at 0, as CrawlSystem does)
                    game = Math.Max(0, game + queue[0].Cats);
                    applied = queue[0].Seq;
                    queue.RemoveAt(0);
                    break;
                case 3 when fz.Chance(0.1):
                    // a load: nothing queued is coming
                    queue.Clear();
                    applied = fz.Below(3);
                    game = fz.Below(5000);
                    p.Reset(applied);
                    break;
            }
            long shown = p.Shown(game, applied);
            long want = Math.Max(0, game + queue.Sum(q => q.Cats));
            fz.Expect(shown >= 0, $"the purse shows {shown}");
            fz.Expect(shown == want, $"the purse shows {shown}, the model {want} (game {game}, {queue.Count} on the way)");
        }
    }

    // --------------------------------------------------------------------------------------------------------------- calls.lines

    static void Lines(PzaFuzz fz)
    {
        // a random dialogue: some records missing, some empty, some throwing, texts with quotes, word swaps and numbers
        var book = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string[] swaps = ["/GAMBLE/", "/OUTLANDER/", "/HEHAS/", "/WHATEVER/", "/GAMBLE/ and /GAMBLE/"];
        string Text() => fz.Below(6) switch
        {
            0 => "",
            1 => "\"" + fz.Word() + " " + fz.Pick(swaps) + "! " + fz.Below(1000) + " Cats, " + fz.Word() + "\"",
            2 => fz.Text(120),
            3 => "“... " + fz.Pick(swaps) + " " + fz.Word() + "”",
            4 => "  \"\"  ",
            _ => fz.Word() + " 200 " + fz.Pick(swaps),
        };
        foreach (var id in Calls.AllRecords())
            if (fz.Chance(0.75)) book[id] = [.. Enumerable.Range(0, fz.Below(4)).Select(_ => Text())];
        string thrower = fz.Chance(0.2) ? fz.Pick(Calls.AllRecords().ToList()) : "";
        var calls = new Calls(id => id == thrower ? throw new InvalidOperationException("fuzz") : book.TryGetValue(id, out var t) ? t : [], () => (int)(fz.NextU64() & 0x7fffffff));
        var card = (Crawler)fz.Int(-1, 9);
        long cats = fz.Pick(new long[] { 120, 150, 200, 300, 600, 36000, 1 });
        string[] lines =
        [
            calls.Invite(), calls.Flourish(), calls.Leave(), calls.SayFish(), calls.SayRoll(), calls.HowTo(),
            calls.Face(card), calls.Fish(card), calls.Roll(card), calls.Dry(card), calls.Lick(card, cats), calls.Girl(card, cats),
        ];
        foreach (var s in lines)
        {
            fz.Expect(s is not null, "a null line");
            foreach (var known in new[] { "/GAMBLE/", "/OUTLANDER/", "/HEHAS/" }) fz.Expect(!s!.Contains(known, StringComparison.Ordinal), $"'{known}' left in: {PzaFuzzUi.Short(s)}");
            fz.Expect(!s!.Contains('\n') && !s.Contains('\r'), "a line break in a call");
        }
        // a lick line from a record with a number says the pot
        if (card is >= Crawler.WolfTail and <= Crawler.Plank)
        {
            string lick = calls.Lick(card, cats);
            if (System.Text.RegularExpressions.Regex.IsMatch(lick, @"\d"))
            {
                fz.Count("number");
                string first = System.Text.RegularExpressions.Regex.Match(lick, @"\d[\d,\.]*\d|\d").Value;
                // (a word swap may put its own digits first only if the swap text has digits: ours never do; random texts may)
                if (book.Values.All(v => v.All(x => !x.Any(char.IsDigit) || x.Contains("Cats", StringComparison.Ordinal) || x.Contains(" 200 ", StringComparison.Ordinal))))
                    fz.Expect(first == Strings.Cats(cats) || lick.Contains(Strings.Cats(cats), StringComparison.Ordinal), $"a lick of {cats} said: {PzaFuzzUi.Short(lick)}");
            }
        }
        fz.Expect(Calls.Clean("\"  " + lines[0] + " \"") == Calls.Clean(lines[0]), "Clean is not stable");
    }

    // ---------------------------------------------------------------------------------------------------------------- save.parse

    static void Parse(PzaFuzz fz)
    {
        // what the system writes parses back
        var sys = new CrawlSystem();
        using var ms = new MemoryStream();
        sys.Write(ms);
        var good = ms.ToArray();
        var (ledger, table, raid, wrestle) = CrawlSystem.Parse(new MemoryStream(good));
        fz.Expect(ledger is { Length: 12 } && table is null && raid is null, "a new system's save did not parse back as an empty ledger and no table");
        fz.Expect(wrestle is not null && new WrestleRecord().Load(wrestle), "a new system's save did not hold an empty wrestling record");

        byte[] bytes = fz.Below(3) switch
        {
            0 => fz.Mutate(good),
            1 => [.. Enumerable.Range(0, fz.Below(200)).Select(_ => (byte)fz.Below(256))],
            _ => [1, 0, 0, 0, (byte)fz.Below(256), (byte)fz.Below(256), (byte)fz.Below(256), (byte)fz.Below(256), .. good.Skip(8)],
        };
        var (l2, t2, r2, w2) = CrawlSystem.Parse(new MemoryStream(bytes));   // must not throw
        if (w2 is not null) { fz.Expect(w2.Length <= 8 + 3 * WrestleRecord.MaxCharacters, "a wrestling list past the cap"); new WrestleRecord().Load(w2); }
        if (l2 is not null) fz.Expect(l2.Length <= 64, "a ledger list longer than the cap");
        if (t2 is not null) fz.Expect(t2.Length is > 0 and <= 64, "a table list outside the cap");
        // whatever came back, loading it never throws and never leaves a pot without a hand
        var t = new Table();
        if (t2 is not null) t.Load(t2);
        fz.Expect((t.Pot > 0) == (t.Phase is Phase.Choose or Phase.Licked), "a parsed save left a pot without a hand");
        new Ledger().Load(l2 ?? []);
        if (r2 is not null) { var rd = new Raid(); rd.Load(r2); fz.Expect(rd.Phase != RaidPhase.Out || rd.Player == Raider.Out || rd.Dealer == Raider.Out, "a parsed raid is out with nobody out"); }
    }
}
