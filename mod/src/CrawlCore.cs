// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// The rules of the table, with no game types (the fuzz harness and the offline tests run it alone).
//
// Cat Crawlers is the game Kenshi's Swamper Casino Dealers (the Blackshifters, Shark and Mud Town) already run through dialogue. Its tree
// (dialogue 48362-Dialogue.mod) hides real rules, decoded 2026-11-20 (docs/vanilla-cat-crawlers.md):
//   - seven crawlers in a fixed order: Wolf Tail < Buxom Red < Chubby Nine < Stoneroller < Hungry Gutter < Arrow in the Neck < Plank;
//   - the dealer turns one of the five middle crawlers (never a Wolf Tail or a Plank);
//   - "Fish!" bets that the next crawler is HIGHER, "Roll back!" that it is LOWER;
//   - a win is a "lick" (the dealer pays 200 on the 100 stake), a loss is a "dryout";
//   - the long shot (rolling back from a Buxom Red onto the Wolf Tail, fishing from an Arrow in the Neck onto the Plank) "gets the girl": 600.
// The table keeps all of that and makes it a real card game:
//   - the crawl is a deck of 28 cards, four of each crawler; played cards go to the gutter, face up, so a player who counts has an edge;
//   - a call pays the stake x 6 / (the number of crawlers that win it): 120, 150, 200, 300 or 600 on a 100 stake. The vanilla 200 and 600
//     are exactly the Stoneroller's even call and the long shot;
//   - the same crawler again is a dryout (that is the house's cut: 3 cards in 27 on a fresh crawl);
//   - after a lick: take the cats, or go for a "hot chain" (the dealer's own words): the pot rides and the card just turned is the new face
//     card. A chain ends on a Wolf Tail or a Plank (nothing left to call) and at the house limit.
namespace PZA.CatCrawlers;

/// <summary>The seven crawlers, low to high (the order the vanilla dialogue tree implies).</summary>
public enum Crawler { None = 0, WolfTail = 1, BuxomRed = 2, ChubbyNine = 3, Stoneroller = 4, HungryGutter = 5, ArrowInTheNeck = 6, Plank = 7 }

public enum Call { Fish, RollBack }

/// <summary>Idle: no hand (pick a stake, deal). Choose: a face card is up. Licked: won, the pot waits (take or ride). Dry: lost.</summary>
public enum Phase { Idle, Choose, Licked, Dry }

/// <summary>What the last action did (for the dealer's call and the money).</summary>
public enum Outcome { None, Dealt, Lick, Girl, Dryout, Tie, Took, Rode, Refused, RaidAnte, RaidCarried, RaidBust, WrestleStake, WrestleWon, WrestleRefund }

/// <summary>The deck: how many of each crawler are still in the crawl (index 1..7).</summary>
public sealed class Crawl
{
    public const int Copies = 4, Ranks = 7, Size = Copies * Ranks;
    readonly int[] _left = new int[Ranks + 1];

    public Crawl() => Fresh();

    public int Total { get; private set; }
    public int Left(Crawler c) => c is >= Crawler.WolfTail and <= Crawler.Plank ? _left[(int)c] : 0;
    /// <summary>Cards of <paramref name="c"/> in the gutter (played since the last fresh crawl).</summary>
    public int Gutter(Crawler c) => c is >= Crawler.WolfTail and <= Crawler.Plank ? Copies - _left[(int)c] : 0;

    /// <summary>A fresh crawl: all 28 back, less the cards in <paramref name="keepOut"/> (the face card on the table during a chain).</summary>
    public void Fresh(params Crawler[] keepOut)
    {
        for (int i = 1; i <= Ranks; i++) _left[i] = Copies;
        Total = Size;
        foreach (var c in keepOut)
            if (Left(c) > 0) { _left[(int)c]--; Total--; }
    }

    /// <summary>Draws one card; <paramref name="pick"/> is any number (it is reduced modulo the cards left). None when the crawl is empty.</summary>
    public Crawler Draw(int pick)
    {
        if (Total <= 0) return Crawler.None;
        int n = (int)((uint)pick % (uint)Total);
        for (int i = 1; i <= Ranks; i++)
        {
            if (n < _left[i]) { _left[i]--; Total--; return (Crawler)i; }
            n -= _left[i];
        }
        return Crawler.None;   // unreachable while Total is the sum
    }

    /// <summary>Cards left that are higher (Fish) or lower (RollBack) than <paramref name="face"/>.</summary>
    public int Winning(Crawler face, Call call)
    {
        int n = 0;
        for (int i = 1; i <= Ranks; i++)
            if (call == Call.Fish ? i > (int)face : i < (int)face) n += _left[i];
        return n;
    }

    public int[] Save() => (int[])_left.Clone();

    /// <summary>Restores counts from a save; anything out of range gives a fresh crawl (false).</summary>
    public bool Load(IReadOnlyList<int> left)
    {
        if (left.Count != Ranks + 1 || left.Skip(1).Any(v => v < 0 || v > Copies)) { Fresh(); return false; }
        Total = 0;
        for (int i = 1; i <= Ranks; i++) { _left[i] = left[i]; Total += left[i]; }
        return true;
    }
}

/// <summary>One table: the crawl, the hand, the pot. Money never moves here: each action reports the cats to take from or pay to the player
/// (<see cref="Result.Cats"/>), and the mod queues that for the game.</summary>
public sealed class Table
{
    /// <summary>The stakes on offer. 100 is the vanilla dealer's stake.</summary>
    public static readonly int[] Stakes = [100, 200, 500];
    /// <summary>A pot at or over this is paid out: no more riding (the house's limit).</summary>
    public const long HouseLimit = 6000;
    /// <summary>A hand starts on a fresh crawl when fewer cards than this are left.</summary>
    public const int MinToDeal = 6;

    public readonly record struct Result(Outcome Outcome, long Cats, Crawler Card, bool FreshCrawl, int Burned);

    public Crawl Crawl { get; } = new();
    public Phase Phase { get; private set; } = Phase.Idle;
    public Crawler Face { get; private set; }
    /// <summary>The card turned by the last call (None before a call in this hand).</summary>
    public Crawler Drawn { get; private set; }
    public long Stake { get; private set; }
    /// <summary>The cats on the table: the stake while choosing, the winnings after a lick, 0 after a dryout.</summary>
    public long Pot { get; private set; }
    /// <summary>Licks in a row in this hand.</summary>
    public int Chain { get; private set; }
    public Call LastCall { get; private set; }

    /// <summary>How many of the seven crawlers win <paramref name="call"/> on <paramref name="face"/> (0 on a Wolf Tail rolled back or a Plank fished).</summary>
    public static int WinningRanks(Crawler face, Call call) =>
        face is < Crawler.WolfTail or > Crawler.Plank ? 0 : call == Call.Fish ? Crawl.Ranks - (int)face : (int)face - 1;

    /// <summary>What <paramref name="pot"/> becomes on a won <paramref name="call"/>: pot x 6 / winning ranks, rounded down (0 when the call cannot win).</summary>
    public static long Pays(long pot, Crawler face, Call call)
    {
        int k = WinningRanks(face, call);
        return k <= 0 || pot <= 0 ? 0 : pot * (Crawl.Ranks - 1) / k;
    }

    public long Pays(Call call) => Phase == Phase.Choose ? Pays(Pot, Face, call) : 0;
    public bool CanCall(Call call) => Phase == Phase.Choose && WinningRanks(Face, call) > 0;
    public bool CanDeal => Phase is Phase.Idle or Phase.Dry;
    /// <summary>After a lick: the pot may ride unless the card is an end of the crawl or the pot reached the house limit.</summary>
    public bool CanRide => Phase == Phase.Licked && Drawn is > Crawler.WolfTail and < Crawler.Plank && Pot < HouseLimit;

    /// <summary>Starts a hand: takes the stake, turns a face card (Wolf Tails and Planks are burned into the gutter, as the vanilla dealer
    /// never opens on one). <paramref name="next"/> gives the random picks.</summary>
    public Result Deal(long stake, long purse, Func<int> next)
    {
        if (!CanDeal || stake <= 0 || stake > purse || !Stakes.Contains((int)Math.Min(stake, int.MaxValue))) return new(Outcome.Refused, 0, Crawler.None, false, 0);
        bool fresh = false;
        int burned = 0;
        if (Crawl.Total < MinToDeal) { Crawl.Fresh(); fresh = true; }
        Crawler face;
        while (true)
        {
            face = Crawl.Draw(next());
            if (face == Crawler.None) { Crawl.Fresh(); fresh = true; continue; }   // only ends were left
            if (face is Crawler.WolfTail or Crawler.Plank) { burned++; continue; }
            break;
        }
        Phase = Phase.Choose;
        Face = face;
        Drawn = Crawler.None;
        Stake = Pot = stake;
        Chain = 0;
        return new(Outcome.Dealt, -stake, face, fresh, burned);
    }

    /// <summary>Fish or roll back: turns the next card.</summary>
    public Result Play(Call call, Func<int> next)
    {
        if (!CanCall(call)) return new(Outcome.Refused, 0, Crawler.None, false, 0);
        bool fresh = false;
        if (Crawl.Total == 0) { Crawl.Fresh(Face); fresh = true; }
        long pays = Pays(Pot, Face, call);
        bool longShot = WinningRanks(Face, call) == 1;
        var card = Crawl.Draw(next());
        Drawn = card;
        LastCall = call;
        bool win = call == Call.Fish ? card > Face : card < Face;
        if (win)
        {
            Pot = pays;
            Chain++;
            Phase = Phase.Licked;
            return new(longShot ? Outcome.Girl : Outcome.Lick, 0, card, fresh, 0);
        }
        bool tie = card == Face;
        Pot = 0;
        Chain = 0;
        Phase = Phase.Dry;
        return new(tie ? Outcome.Tie : Outcome.Dryout, 0, card, fresh, 0);
    }

    /// <summary>Takes the pot after a lick.</summary>
    public Result Take()
    {
        if (Phase != Phase.Licked) return new(Outcome.Refused, 0, Crawler.None, false, 0);
        long cats = Pot;
        Pot = 0;
        Phase = Phase.Idle;
        return new(Outcome.Took, cats, Drawn, false, 0);
    }

    /// <summary>The hot chain: the pot rides, the card just turned is the new face card.</summary>
    public Result Ride()
    {
        if (!CanRide) return new(Outcome.Refused, 0, Crawler.None, false, 0);
        Face = Drawn;
        Drawn = Crawler.None;
        Phase = Phase.Choose;
        return new(Outcome.Rode, 0, Face, false, 0);
    }

    #region Save

    /// <summary>The table as numbers (the save and the fuzz harness): version, phase, face, drawn, stake, pot, chain, last call, the crawl's 8 counts.</summary>
    public long[] Save()
    {
        var c = Crawl.Save();
        var a = new long[8 + c.Length];
        (a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]) = (1, (int)Phase, (int)Face, (int)Drawn, Stake, Pot, Chain, (int)LastCall);
        for (int i = 0; i < c.Length; i++) a[8 + i] = c[i];
        return a;
    }

    /// <summary>Restores a saved table. Anything that does not add up (a bad phase, a card outside the crawl, a negative pot, a pot with no
    /// hand) gives an empty table on a fresh crawl and returns false: a broken save never pays out.</summary>
    public bool Load(IReadOnlyList<long> a)
    {
        bool ok = a.Count == 8 + Crawl.Ranks + 1 && a[0] == 1
            && a[1] is >= 0 and <= 3 && a[2] is >= 0 and <= 7 && a[3] is >= 0 and <= 7 && a[4] >= 0 && a[5] >= 0 && a[6] is >= 0 and <= 1000 && a[7] is 0 or 1;
        if (ok)
        {
            var phase = (Phase)a[1];
            var (face, drawn) = ((Crawler)a[2], (Crawler)a[3]);
            bool middle = face is > Crawler.WolfTail and < Crawler.Plank;
            long top = HouseLimit * (Crawl.Ranks - 1);   // no pot can pass one long shot over the limit
            ok = phase switch
            {
                Phase.Idle => a[5] == 0,
                Phase.Choose => middle && a[5] > 0 && a[5] <= top && a[4] > 0,
                Phase.Licked => middle && drawn != Crawler.None && drawn != face && a[5] > 0 && a[5] <= top && a[6] >= 1,
                _ => a[5] == 0,
            };
            ok = ok && Crawl.Load([.. a.Skip(8).Select(v => v is >= 0 and <= Crawl.Copies ? (int)v : -1)]);
            if (ok)
            {
                (Phase, Face, Drawn, Stake, Pot, Chain, LastCall) = (phase, face, drawn, a[4], a[5], (int)a[6], (Call)a[7]);
                return true;
            }
        }
        Crawl.Fresh();
        (Phase, Face, Drawn, Stake, Pot, Chain, LastCall) = (Phase.Idle, Crawler.None, Crawler.None, 0, 0, 0, Call.Fish);
        return false;
    }

    #endregion
}

/// <summary>The ledger the save keeps: what this player has done at the tables.</summary>
public sealed class Ledger
{
    public long Hands, Licks, Dryouts, Girls, BestChain, BiggestPot, Staked, Paid;
    /// <summary>Looter's Rights (version 2): raids, raids busted, the most carried home from one raid.</summary>
    public long Raids, RaidBusts, RaidBest;
    public long Net => Paid - Staked;

    public long[] Save() => [2, Hands, Licks, Dryouts, Girls, BestChain, BiggestPot, Staked, Paid, Raids, RaidBusts, RaidBest];

    /// <summary>Version 1 (9 numbers, Cat Crawlers only) and 2 (12) load; anything else, or a negative number, gives an empty ledger.</summary>
    public bool Load(IReadOnlyList<long> a)
    {
        bool ok = a.Count > 0 && (a[0] == 1 && a.Count == 9 || a[0] == 2 && a.Count == 12) && a.Skip(1).All(v => v >= 0);
        if (!ok) { Hands = Licks = Dryouts = Girls = BestChain = BiggestPot = Staked = Paid = Raids = RaidBusts = RaidBest = 0; return false; }
        (Hands, Licks, Dryouts, Girls, BestChain, BiggestPot, Staked, Paid) = (a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8]);
        (Raids, RaidBusts, RaidBest) = a[0] == 2 ? (a[9], a[10], a[11]) : (0, 0, 0);
        return true;
    }
}

/// <summary>The purse the window shows while commands are still on their way to the game: the game's last published money plus the cats
/// of the commands it has not applied yet.</summary>
public sealed class Purse
{
    readonly List<(long Seq, long Cats)> _pending = [];
    long _seq;

    /// <summary>Notes a change the mod just queued; returns its sequence number (the command carries it).</summary>
    public long Queue(long cats)
    {
        _pending.Add((++_seq, cats));
        return _seq;
    }

    /// <summary>The money to show: the game's <paramref name="published"/> money, with every queued change the game has not applied
    /// (sequence above <paramref name="applied"/>). Never below 0.</summary>
    public long Shown(long published, long applied)
    {
        _pending.RemoveAll(p => p.Seq <= applied);
        long sum = published;
        foreach (var p in _pending) sum += p.Cats;
        return Math.Max(0, sum);
    }

    /// <summary>A new world (a load): nothing queued is coming any more.</summary>
    public void Reset(long seq) { _pending.Clear(); _seq = seq; }
}
