// PZA's Cat Crawlers for Meitou: fuzz targets for Looter's Rights (RaidCore). Author: PZA. Licence: GPLv3 or later. Never shipped.
//   raid.play   random raids with random choices (and some illegal ones) against a plain model: no card made or lost (deck + haul + trouble
//               line + gone = the deck's copies), the haul is only loot, the trouble line never holds a kind twice, a bust takes everyone
//               still out, units carried home never fall, the money the steps move adds up to what the units are worth, nobody is paid
//               twice, the dealer never runs empty-handed, and a raid always ends
//   raid.save   Save / Load round trips at every point of a random raid; nasty saves load as an idle table that pays nothing
using PZA.CatCrawlers;
using PZA.MeitouUi.Fuzz;

static class RaidFuzz
{
    public static PzaFuzz.TargetDef[] Targets =>
    [
        new("raid.play", 8000, Play, fz =>
        {
            foreach (var rule in new[] { "loot", "trouble", "bust", "dustking", "dustking_empty", "ran", "bothran", "lastout", "dry", "refused", "dealer_ran", "dealer_alone" })
                fz.Expect(fz.Counted(rule) > 0, $"rule '{rule}' never fired (a rule that never fires is a failed test)");
        }),
        new("raid.save", 4000, SaveLoad, fz =>
        {
            fz.Expect(fz.Counted("mid_raid") > 0, "no save was taken in the middle of a raid");
            fz.Expect(fz.Counted("bad_refused") > 0, "no nasty save was refused");
        }),
    ];

    static void Balance(PzaFuzz fz, Raid r, Dust[] gone)
    {
        foreach (var d in RaidCards.All)
        {
            int n = r.Deck.Left(d) + r.Haul.Count(h => h == d) + r.TroubleLine.Count(t => t == d);
            fz.Expect(n <= RaidCards.Copies(d), $"{d}: {n} on the table and face down, only {RaidCards.Copies(d)} exist");
        }
        fz.Expect(r.Haul.All(RaidCards.IsLoot), "a card in the haul is not loot");
        fz.Expect(r.TroubleLine.All(RaidCards.IsTrouble) && r.TroubleLine.Distinct().Count() == r.TroubleLine.Count, "the trouble line holds a kind twice or a non-trouble card");
        fz.Expect(r.Deck.Total == RaidCards.All.Sum(r.Deck.Left), "the deck's total does not add up");
        fz.Expect(r.LooseUnits is >= 0 and < 8, $"loose units {r.LooseUnits}");
    }

    static void Play(PzaFuzz fz)
    {
        // the deck as designed: 34 cards, every trouble kind three times, every loot kind with its copies
        fz.Expect(RaidCards.DeckSize == 34 && RaidCards.Trouble.All(k => RaidCards.Copies(k) == 3) && RaidCards.Loot.All(k => RaidCards.Copies(k) >= 2)
            && RaidCards.Copies(Dust.DustKing) == 2, "the dust is not the designed deck (a kind lost its copies)");
        var r = new Raid();
        int bias = fz.Below(4);
        int Next() => bias switch { 1 => 0, 2 => int.MaxValue, _ => (int)(fz.NextU64() & 0x7fffffff) };
        long purse = fz.Pick(new long[] { 0, 50, 100, 500, 100000 });
        long stake = fz.Chance(0.85) ? fz.Pick(Raid.Stakes) : fz.Pick(new long[] { 0, -5, 99, 1000 });
        if (fz.Chance(0.06))
        {
            // the dust can only run dry from a save (in play a fifth trouble card always busts first): a raid out on a nearly empty deck
            var thin = new Raid();
            thin.Start(100, 1000, () => 0);
            var a = thin.Save().ToList();
            int deckAt = a.Count - RaidCards.All.Length;
            for (int i = deckAt; i < a.Count; i++) a[i] = 0;
            a[deckAt] = Math.Min(fz.Int(0, 2), RaidCards.Copies(RaidCards.All[0]) - thin.Haul.Count(h => h == RaidCards.All[0]));   // a few Rations
            if (r.Load(a))
            {
                long before = r.PlayerUnits;
                while (r.CanChoose) { var st = r.Step(false, false, Next); if (st.Outcome == RaidOutcome.DustDry) fz.Count("dry"); }
                fz.Expect(r.Phase == RaidPhase.Over && r.Player == Raider.Home && r.PlayerUnits >= before, "the dust ran dry but the raid did not end with the player home");
            }
            return;
        }
        var s = r.Start(stake, purse, Next);
        bool legal = Raid.Stakes.Contains((int)Math.Clamp(stake, 0, int.MaxValue)) && stake <= purse;
        fz.Expect((s.Outcome != RaidOutcome.Refused) == legal, $"Start({stake}) with purse {purse}: {s.Outcome}");
        if (s.Outcome == RaidOutcome.Refused) { fz.Count("refused"); fz.Expect(s.PlayerCats == 0 && r.Phase == RaidPhase.Idle, "a refused start moved money"); return; }
        long cats = s.PlayerCats, dcats = s.DealerCats;
        fz.Expect(cats <= -stake + 0 || s.Outcome == RaidOutcome.Bust || cats == -stake, $"the start paid {cats}");
        Count(fz, s);
        int guard = 0;
        while (r.CanChoose && guard++ < 100)
        {
            Balance(fz, r, []);
            long pu = r.PlayerUnits, du = r.DealerUnits, haul = r.HaulUnits;
            bool dealerIn = r.Dealer == Raider.Out;
            bool p = fz.Chance(0.3), d = fz.Chance(0.35);
            if (fz.Chance(0.02)) { var bad = new Raid(); fz.Expect(bad.Step(true, true, Next).Outcome == RaidOutcome.Refused, "a step on an idle table was taken"); fz.Count("refused"); }
            if (d && dealerIn && haul == 0) d = false;   // the dealer's mind never runs empty-handed: the model follows it
            s = r.Step(p, d, Next);
            Count(fz, s);
            if (p && d && dealerIn) fz.Expect(r.PlayerUnits - pu == haul / 2 && r.DealerUnits - du == haul / 2, $"both ran from {haul}: {r.PlayerUnits - pu} / {r.DealerUnits - du}");
            else if (p) fz.Expect(r.PlayerUnits - pu == (dealerIn ? haul / 2 : haul), $"ran from {haul} (dealer in {dealerIn}): took {r.PlayerUnits - pu}");
            fz.Expect(r.PlayerUnits >= pu && r.DealerUnits >= du, "units carried home fell");
            cats += s.PlayerCats; dcats += s.DealerCats;
            if (s.Taken != Dust.None) fz.Expect(r.Haul.All(h => RaidCards.Value(h) <= RaidCards.Value(s.Taken)), $"the Dust King took {s.Taken} and left better loot");
            if (s.Outcome == RaidOutcome.Bust) fz.Expect(r.Player != Raider.Out && r.Dealer != Raider.Out && r.HaulUnits == 0 && r.Phase == RaidPhase.Over, "a bust left someone out or loot on the table");
        }
        fz.Expect(guard < 100, "a raid never ended");
        fz.Expect(!r.CanChoose, "the player can still choose at the end");
        if (r.Phase == RaidPhase.Over) { fz.Expect(r.Player != Raider.Out && r.Dealer != Raider.Out, "the raid is over with someone still out"); fz.Expect(r.HaulUnits == 0, "the raid is over with loot on the table"); }
        fz.Expect(cats == -stake + r.PlayerUnits * r.UnitCats, $"the player's money moved {cats}, units {r.PlayerUnits} at {r.UnitCats} less the ante {stake}");
        fz.Expect(dcats == r.DealerUnits * r.UnitCats, $"the dealer's take {dcats} is not his units {r.DealerUnits} at {r.UnitCats}");
        if (r.Player == Raider.Busted) fz.Expect(cats <= 0 || r.PlayerUnits == 0 || cats == -stake + r.PlayerUnits * r.UnitCats, "busted but paid");
        Balance(fz, r, []);
    }

    static void Count(PzaFuzz fz, Raid.RaidStep s)
    {
        switch (s.Outcome)
        {
            case RaidOutcome.Loot: fz.Count("loot"); break;
            case RaidOutcome.Trouble: fz.Count("trouble"); break;
            case RaidOutcome.Bust: fz.Count("bust"); break;
            case RaidOutcome.DustKing: fz.Count(s.Taken == Dust.None ? "dustking_empty" : "dustking"); break;
            case RaidOutcome.Ran: fz.Count("ran"); if (s.DealerCats > 0 && s.PlayerCats <= 0) fz.Count("dealer_ran"); break;
            case RaidOutcome.BothRan: fz.Count("bothran"); break;
            case RaidOutcome.LastOut: fz.Count("lastout"); break;
            case RaidOutcome.DustDry: fz.Count("dry"); break;
        }
        if (s.Card != Dust.None && s.Outcome is RaidOutcome.Ran && s.DealerCats > 0) fz.Count("dealer_alone");
        if (s.Outcome == RaidOutcome.Ran && s.PlayerCats > 0 && s.DealerCats >= 0) fz.Count("dealer_alone");
    }

    static void SaveLoad(PzaFuzz fz)
    {
        var r = new Raid();
        int Next() => (int)(fz.NextU64() & 0x7fffffff);
        if (fz.Chance(0.9)) r.Start(fz.Pick(Raid.Stakes), 100000, Next);
        for (int i = fz.Int(0, 8); i > 0 && r.CanChoose; i--) r.Step(fz.Chance(0.2), fz.Chance(0.2) && r.HaulUnits > 0, Next);
        if (r.Phase == RaidPhase.Out) fz.Count("mid_raid");
        var saved = r.Save();
        var u = new Raid();
        fz.Expect(u.Load(saved) && u.Save().SequenceEqual(saved), $"a raid saved in {r.Phase} did not load back the same");

        var bad = saved.ToList();
        switch (fz.Below(4))
        {
            case 0: bad.RemoveAt(fz.Below(bad.Count)); break;
            case 1: bad.Add(fz.IntNasty()); break;
            default:
                for (int n = fz.Int(1, 3); n > 0; n--) bad[fz.Below(bad.Count)] = fz.Below(3) switch { 0 => fz.IntNasty(-30, 30), 1 => long.MaxValue - fz.Below(3), _ => -fz.Int(1, 99) };
                break;
        }
        var v = new Raid();
        if (!v.Load(bad))
        {
            fz.Count("bad_refused");
            fz.Expect(v.Phase == RaidPhase.Idle && v.HaulUnits == 0 && !v.CanChoose && v.Deck.Total == RaidCards.DeckSize, "a refused save left something to play or pay");
        }
        else
        {
            Balance(fz, v, []);
            fz.Expect(v.PlayerUnits >= 0 && v.DealerUnits >= 0, "a nasty save loaded negative units");
            if (v.Phase != RaidPhase.Out) fz.Expect(v.HaulUnits == 0, "a nasty save loaded loot on a finished table");
        }
    }
}
