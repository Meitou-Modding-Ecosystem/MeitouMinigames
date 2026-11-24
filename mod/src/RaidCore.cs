// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// Looter's Rights: the Hub's game (user, 2026-11-20: "a whole different kind of table game being played at the Hub, with Dust Bandit
// imagery on the cards, and the Dust King"). No game types here: the fuzz harness and the balance runner use it alone.
//
// Two raiders, the player and the Shady Dealer, ante the same stake and go out on one raid. The deck is "the dust" (34 cards):
//   loot     20 cards: Rations x6 (1), Cat Purse x5 (2), Katana x4 (3), Old-World Relic x3 (5), a stolen Pack Bull x2 (8)   [haul units]
//   trouble  12 cards: Holy Paladins, Shek Patrol, Black Dragon Ninjas, Beak Thing, three of each: what the Dust Bandits meet out of
//            the Hub (the Border Zone's spawns: Holy Army and Shek patrols; the Black Dragon Ninjas' Iron Hats; the Bonefields' beaks)
//   the Dust King x2
// A raid goes in steps. The first card turns before anyone may leave. Then, every step, both raiders choose at once (the dealer never
// sees the player's choice): STAY or RUN.
//   - one runs: he carries home HALF of the haul (rounded down; the rest stays for whoever stays)
//   - both run: they split the whole haul (the odd unit is lost in the dust)
//   - the last one left who runs carries home ALL of it
// If anyone is still out, the next card turns:
//   - loot goes onto the haul
//   - trouble goes onto the trouble line; a SECOND card of a kind already there is a bust: everyone still out loses all
//   - the Dust King claims looters' rights: the best card of the haul is gone (on an empty haul he only laughs)
// The dust running out ends the raid: whoever is still out carries the haul home (split if both). In play it cannot happen (a fifth
// trouble card always repeats a kind, so a raid busts first); it guards a raid loaded from a save.
// What a raider carries home is paid at the unit price (stake x UnitPerStake / 100). The antes are gone either way.
namespace PZA.CatCrawlers;

public enum Dust
{
    None = 0,
    Rations = 1, CatPurse = 2, Katana = 3, Relic = 4, PackBull = 5,
    HolyPaladins = 11, ShekPatrol = 12, BlackDragon = 13, BeakThing = 14,
    DustKing = 21,
}

public enum RaidPhase { Idle, Out, Over }

/// <summary>What a raider is doing: still out, gone home with a take, or caught in a bust.</summary>
public enum Raider { Out, Home, Busted }

public enum RaidOutcome { None, Started, Turned, Loot, Trouble, Bust, DustKing, Ran, BothRan, LastOut, DustDry, Refused }

public static class RaidCards
{
    public static readonly Dust[] Loot = [Dust.Rations, Dust.CatPurse, Dust.Katana, Dust.Relic, Dust.PackBull];
    public static readonly Dust[] Trouble = [Dust.HolyPaladins, Dust.ShekPatrol, Dust.BlackDragon, Dust.BeakThing];

    public static int Copies(Dust d) => d switch
    {
        Dust.Rations => 6, Dust.CatPurse => 5, Dust.Katana => 4, Dust.Relic => 3, Dust.PackBull => 2,
        Dust.HolyPaladins or Dust.ShekPatrol or Dust.BlackDragon or Dust.BeakThing => 3,
        Dust.DustKing => 2,
        _ => 0,
    };

    public static int Value(Dust d) => d switch
    {
        Dust.Rations => 1, Dust.CatPurse => 2, Dust.Katana => 3, Dust.Relic => 5, Dust.PackBull => 8, _ => 0,
    };

    public static bool IsLoot(Dust d) => d is >= Dust.Rations and <= Dust.PackBull;
    public static bool IsTrouble(Dust d) => d is >= Dust.HolyPaladins and <= Dust.BeakThing;
    public static readonly Dust[] All = [.. Loot, .. Trouble, Dust.DustKing];
    public static int DeckSize => All.Sum(Copies);
}

/// <summary>The dust: how many of each card are still face down.</summary>
public sealed class DustDeck
{
    readonly Dictionary<Dust, int> _left = [];
    public DustDeck() => Fresh();
    public int Total { get; private set; }
    public int Left(Dust d) => _left.GetValueOrDefault(d);

    public void Fresh()
    {
        Total = 0;
        foreach (var d in RaidCards.All) { _left[d] = RaidCards.Copies(d); Total += RaidCards.Copies(d); }
    }

    public Dust Draw(int pick)
    {
        if (Total <= 0) return Dust.None;
        int n = (int)((uint)pick % (uint)Total);
        foreach (var d in RaidCards.All)
        {
            int k = _left[d];
            if (n < k) { _left[d] = k - 1; Total--; return d; }
            n -= k;
        }
        return Dust.None;
    }

    public long[] Save() => [.. RaidCards.All.Select(d => (long)_left[d])];

    public bool Load(IReadOnlyList<long> a)
    {
        if (a.Count != RaidCards.All.Length || a.Select((v, i) => v < 0 || v > RaidCards.Copies(RaidCards.All[i])).Any(b => b)) { Fresh(); return false; }
        Total = 0;
        for (int i = 0; i < a.Count; i++) { _left[RaidCards.All[i]] = (int)a[i]; Total += (int)a[i]; }
        return true;
    }
}

/// <summary>One raid at the Shady Dealer's table. Money never moves here: <see cref="RaidStep.PlayerCats"/> says what to take or pay.</summary>
public sealed class Raid
{
    public static readonly int[] Stakes = [100, 200, 500];
    /// <summary>A haul unit is worth stake x this / 100 cats (tuned by the balance runner: a careful player gets back a little less than
    /// the ante over many raids; the house's cut is that difference).</summary>
    public const int UnitPerStake = 25;

    public readonly record struct RaidStep(RaidOutcome Outcome, Dust Card, long PlayerCats, long DealerCats, Dust Taken);

    public DustDeck Deck { get; } = new();
    public RaidPhase Phase { get; private set; } = RaidPhase.Idle;
    public long Stake { get; private set; }
    public Raider Player { get; private set; } = Raider.Home;
    public Raider Dealer { get; private set; } = Raider.Home;
    /// <summary>The haul on the table, card by card (loot only), in the order turned.</summary>
    public List<Dust> Haul { get; } = [];
    /// <summary>The trouble line: the kinds turned once this raid.</summary>
    public List<Dust> TroubleLine { get; } = [];
    /// <summary>Units carried home this raid.</summary>
    public long PlayerUnits { get; private set; }
    public long DealerUnits { get; private set; }
    /// <summary>Cards turned this raid (the first must turn before anyone may run).</summary>
    public int Turned { get; private set; }
    public Dust LastCard { get; private set; }
    /// <summary>A part of the haul that is not a whole card any more (half of a card left when a runner took half): units.</summary>
    public long LooseUnits { get; private set; }

    public long HaulUnits => Haul.Sum(d => (long)RaidCards.Value(d)) + LooseUnits;
    public long UnitCats => Stake * UnitPerStake / 100;
    public bool CanStart => Phase is RaidPhase.Idle or RaidPhase.Over;
    public bool CanChoose => Phase == RaidPhase.Out && Turned > 0 && Player == Raider.Out;

    /// <summary>The chance the next card busts the raid (the trouble kinds already out, over the cards left).</summary>
    public double BustChance => Deck.Total == 0 ? 0 : (double)TroubleLine.Sum(Deck.Left) / Deck.Total;

    /// <summary>Starts a raid: both ante <paramref name="stake"/>; a fresh dust; the first card turns.</summary>
    public RaidStep Start(long stake, long purse, Func<int> next)
    {
        if (!CanStart || stake <= 0 || stake > purse || !Stakes.Contains((int)Math.Min(stake, int.MaxValue))) return new(RaidOutcome.Refused, Dust.None, 0, 0, Dust.None);
        Deck.Fresh();
        Haul.Clear();
        TroubleLine.Clear();
        (Stake, Phase, Player, Dealer, PlayerUnits, DealerUnits, Turned, LooseUnits, LastCard) = (stake, RaidPhase.Out, Raider.Out, Raider.Out, 0, 0, 0, 0, Dust.None);
        var first = Turn(next);
        return first with { Outcome = first.Outcome == RaidOutcome.Bust ? RaidOutcome.Bust : RaidOutcome.Started, PlayerCats = -stake + first.PlayerCats };
    }

    /// <summary>One step: the player's and the dealer's choice at once, then (if anyone is still out) the next card.</summary>
    public RaidStep Step(bool playerRuns, bool dealerRuns, Func<int> next)
    {
        if (!CanChoose) return new(RaidOutcome.Refused, Dust.None, 0, 0, Dust.None);
        bool dealerIn = Dealer == Raider.Out;
        dealerRuns &= dealerIn;
        long pay = 0, dpay = 0;
        RaidOutcome outcome = RaidOutcome.None;
        if (playerRuns && dealerRuns)
        {
            long half = HaulUnits / 2;
            (PlayerUnits, DealerUnits) = (PlayerUnits + half, DealerUnits + half);
            ClearHaul();
            (Player, Dealer) = (Raider.Home, Raider.Home);
            pay = half * UnitCats; dpay = half * UnitCats;
            outcome = RaidOutcome.BothRan;
        }
        else if (playerRuns)
        {
            long take = dealerIn ? HaulUnits / 2 : HaulUnits;
            PlayerUnits += take;
            TakeFromHaul(take);
            Player = Raider.Home;
            pay = take * UnitCats;
            outcome = dealerIn ? RaidOutcome.Ran : RaidOutcome.LastOut;
        }
        else if (dealerRuns)
        {
            long take = HaulUnits / 2;   // the player is still out
            DealerUnits += take;
            TakeFromHaul(take);
            Dealer = Raider.Home;
            dpay = take * UnitCats;
            outcome = RaidOutcome.Ran;
        }
        if (Player != Raider.Out && Dealer != Raider.Out)
        {
            Phase = RaidPhase.Over;
            return new(outcome, Dust.None, pay, dpay, Dust.None);
        }
        // whoever is still out: the dealer alone plays on by himself (the house's business; the player only watches the end)
        if (Player != Raider.Out)
        {
            DealerAlone(next, ref dpay);
            return new(outcome, LastCard, pay, dpay, Dust.None);
        }
        var turned = Turn(next);
        return turned with { PlayerCats = pay + turned.PlayerCats, DealerCats = dpay + turned.DealerCats, Outcome = outcome == RaidOutcome.None ? turned.Outcome : outcome };
    }

    /// <summary>The player has gone home: the dealer raids on alone with his own nerve until he runs or busts (so his take is known).</summary>
    void DealerAlone(Func<int> next, ref long dpay)
    {
        int guard = 0;
        while (Dealer == Raider.Out && Phase == RaidPhase.Out && guard++ < 64)
        {
            if (DealerMind.Runs(this, alone: true, next())) { DealerUnits += HaulUnits; dpay += HaulUnits * UnitCats; ClearHaul(); Dealer = Raider.Home; break; }
            var t = Turn(next);
            dpay += t.DealerCats;
        }
        Phase = RaidPhase.Over;
    }

    RaidStep Turn(Func<int> next)
    {
        var card = Deck.Draw(next());
        LastCard = card;
        if (card == Dust.None)
        {
            // the dust ran dry: whoever is out carries the haul home
            long units = HaulUnits;
            bool both = Player == Raider.Out && Dealer == Raider.Out;
            long each = both ? units / 2 : units;
            long pay = 0, dpay = 0;
            if (Player == Raider.Out) { PlayerUnits += each; pay = each * UnitCats; Player = Raider.Home; }
            if (Dealer == Raider.Out) { DealerUnits += each; dpay = each * UnitCats; Dealer = Raider.Home; }
            ClearHaul();
            Phase = RaidPhase.Over;
            return new(RaidOutcome.DustDry, Dust.None, pay, dpay, Dust.None);
        }
        Turned++;
        if (RaidCards.IsLoot(card)) { Haul.Add(card); return new(RaidOutcome.Loot, card, 0, 0, Dust.None); }
        if (card == Dust.DustKing)
        {
            if (Haul.Count == 0) return new(RaidOutcome.DustKing, card, 0, 0, Dust.None);
            var best = Haul.OrderByDescending(RaidCards.Value).First();
            Haul.Remove(best);
            return new(RaidOutcome.DustKing, card, 0, 0, best);
        }
        if (TroubleLine.Contains(card))
        {
            if (Player == Raider.Out) Player = Raider.Busted;
            if (Dealer == Raider.Out) Dealer = Raider.Busted;
            ClearHaul();
            Phase = RaidPhase.Over;
            return new(RaidOutcome.Bust, card, 0, 0, Dust.None);
        }
        TroubleLine.Add(card);
        return new(RaidOutcome.Trouble, card, 0, 0, Dust.None);
    }

    void ClearHaul() { Haul.Clear(); LooseUnits = 0; }

    /// <summary>Takes <paramref name="units"/> off the haul: whole cards from the cheapest up while they fit, the rest as loose units.</summary>
    void TakeFromHaul(long units)
    {
        long total = HaulUnits;
        if (units <= 0) return;
        if (units >= total) { ClearHaul(); return; }
        long left = total - units;
        // keep the most valuable cards that fit in what is left; the remainder becomes loose units
        var keep = new List<Dust>();
        long kept = 0;
        foreach (var d in Haul.OrderByDescending(RaidCards.Value))
            if (kept + RaidCards.Value(d) <= left) { keep.Add(d); kept += RaidCards.Value(d); }
        Haul.Clear();
        Haul.AddRange(keep);
        LooseUnits = left - kept;
    }

    #region Save

    /// <summary>The raid as numbers: version, phase, stake, player, dealer, units x2, turned, last card, loose, haul count, haul..., trouble
    /// count, trouble..., the deck's counts.</summary>
    public long[] Save()
    {
        var a = new List<long> { 1, (int)Phase, Stake, (int)Player, (int)Dealer, PlayerUnits, DealerUnits, Turned, (int)LastCard, LooseUnits, Haul.Count };
        a.AddRange(Haul.Select(d => (long)d));
        a.Add(TroubleLine.Count);
        a.AddRange(TroubleLine.Select(d => (long)d));
        a.AddRange(Deck.Save());
        return [.. a];
    }

    /// <summary>Restores a raid; anything that does not add up gives an idle table that pays nothing (false).</summary>
    public bool Load(IReadOnlyList<long> a)
    {
        try
        {
            if (a.Count < 12 || a[0] != 1) throw new InvalidDataException();
            int i = 1;
            var phase = (RaidPhase)Check(a[i++], 0, 2);
            long stake = a[i++];
            var player = (Raider)Check(a[i++], 0, 2);
            var dealer = (Raider)Check(a[i++], 0, 2);
            long pu = a[i++], du = a[i++];
            int turned = (int)Check(a[i++], 0, RaidCards.DeckSize);
            var last = (Dust)a[i++];
            long loose = a[i++];
            int hn = (int)Check(a[i++], 0, RaidCards.DeckSize);
            if (a.Count < i + hn + 1) throw new InvalidDataException();
            var haul = a.Skip(i).Take(hn).Select(v => (Dust)v).ToList();
            i += hn;
            int tn = (int)Check(a[i++], 0, 4);
            if (a.Count != i + tn + RaidCards.All.Length) throw new InvalidDataException();
            var trouble = a.Skip(i).Take(tn).Select(v => (Dust)v).ToList();
            i += tn;
            var deck = a.Skip(i).ToList();
            bool ok = pu >= 0 && du >= 0 && loose >= 0 && loose < 8 && haul.All(RaidCards.IsLoot) && trouble.All(RaidCards.IsTrouble) && trouble.Distinct().Count() == tn
                && (last == Dust.None || RaidCards.All.Contains(last))
                && phase switch
                {
                    RaidPhase.Out => Stakes.Contains((int)Math.Clamp(stake, 0, int.MaxValue)) && (player == Raider.Out || dealer == Raider.Out) && turned >= 1,
                    _ => haul.Count == 0 && loose == 0,
                }
                && (phase == RaidPhase.Out || (player != Raider.Out && dealer != Raider.Out));
            if (!ok || !Deck.Load(deck)) throw new InvalidDataException();
            // every card is face down, on the table, or gone: no more than its copies
            foreach (var d in RaidCards.All)
                if (Deck.Left(d) + haul.Count(h => h == d) + trouble.Count(t => t == d) > RaidCards.Copies(d)) throw new InvalidDataException();
            (Phase, Stake, Player, Dealer, PlayerUnits, DealerUnits, Turned, LastCard, LooseUnits) = (phase, stake, player, dealer, pu, du, turned, last, loose);
            Haul.Clear(); Haul.AddRange(haul);
            TroubleLine.Clear(); TroubleLine.AddRange(trouble);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidCastException)
        {
            Deck.Fresh(); Haul.Clear(); TroubleLine.Clear();
            (Phase, Stake, Player, Dealer, PlayerUnits, DealerUnits, Turned, LastCard, LooseUnits) = (RaidPhase.Idle, 0, Raider.Home, Raider.Home, 0, 0, 0, Dust.None, 0);
            return false;
        }
    }

    static long Check(long v, long lo, long hi) => v < lo || v > hi ? throw new InvalidDataException() : v;

    #endregion
}

/// <summary>The Shady Dealer's nerve: he runs when the bust chance, weighed against what he would carry home, scares him. A random draw
/// each step (from <paramref name="roll"/>) so he is not a clock; he is braver on an empty haul and greedier when alone.</summary>
public static class DealerMind
{
    public static bool Runs(Raid r, bool alone, int roll)
    {
        if (r.Haul.Count == 0 && r.LooseUnits == 0) return false;   // nothing to carry: never runs empty-handed
        double bust = r.BustChance;
        long units = r.HaulUnits;
        double nerve = 0.14 + ((uint)roll % 1000) / 1000.0 * 0.16;   // 0.14 .. 0.30
        if (alone) nerve += 0.04;                                     // nobody to split with
        if (units >= 14) nerve -= 0.06;                               // a fat haul makes him careful
        if (units <= 3) nerve += 0.05;                                // a thin one makes him push
        return bust > nerve;
    }

    /// <summary>How the dealer looks right now (his line on the table): 0 calm, 1 sweating, 2 about to bolt.</summary>
    public static int Tell(Raid r) => r.BustChance switch { < 0.12 => 0, < 0.24 => 1, _ => 2 };
}
