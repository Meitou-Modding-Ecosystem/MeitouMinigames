// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// Looter's Rights at the Shady Dealer's table: the same conversation-style window (the dealer on top, the cloth, your answers), with the
// Hub's cloth: the trouble line (all four kinds; the ones out this raid lit, the rest dim, with what is left of each), the haul (the loot
// on the table), the last card turned and the dust, the cats the haul is worth, and where the dealer is (out, home with his take, busted).
// Answers: ante up (100 / 200 / 500), stay out, run home (half the haul, or all of it when you are the last one out), leave.
// The rules: RaidCore. The dealer decides each step with DealerMind, without seeing your choice. Money: an ante and each take home go to
// the game as TableEvents (CrawlSystem); the raid itself is saved as numbers (RaidState).
using Meitou.Data.Ui;
using PZA.MeitouUi;

namespace PZA.CatCrawlers;

public static partial class CrawlWindow
{
    static TableGame s_game;
    static Raid s_raid = new();
    static RaidOutcome s_raidLast;

    /// <summary>The Dust King's own line in the game's records ("..., the Dust King always gets looters rights!"), read at run time.</summary>
    const string DustKingLine = "45208-Dialogue.mod";

    public static Raid Raid => s_raid;
    public static TableGame Game => s_game;

    static string RaidArt(Dust d) => d switch
    {
        Dust.Rations => "r_rations", Dust.CatPurse => "r_purse", Dust.Katana => "r_katana", Dust.Relic => "r_relic", Dust.PackBull => "r_bull",
        Dust.HolyPaladins => "r_paladin", Dust.ShekPatrol => "r_shek", Dust.BlackDragon => "r_ninja", Dust.BeakThing => "r_beak",
        Dust.DustKing => "r_king",
        _ => "r_back",
    };

    static string DustName(Dust d) => d == Dust.None ? "" : Strings.T("dust." + (int)d);

    /// <summary>The raid's widgets on the cloth (appended to the table's layout; unused while the Swamp's game is at the table).</summary>
    static string RaidWidgets()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Txt("lbTrouble", 0.02, 0.02, 0.3, 0.06));
        for (int i = 0; i < 4; i++)
        {
            double x = 0.02 + i * 0.075;
            sb.Append(Img("T" + i, x, 0.09, 0.062, 0.24));
            sb.Append(Txt("TN" + i, x - 0.006, 0.335, 0.074, 0.05, "HCenter VCenter"));
        }
        sb.Append(Txt("lbHaul", 0.02, 0.4, 0.3, 0.06));
        for (int i = 0; i < 14; i++)
        {
            double x = 0.02 + (i % 7) * 0.043, y = 0.47 + (i / 7) * 0.255;
            sb.Append(Img("H" + i, x, y, 0.052, 0.2));
        }
        sb.Append(Txt("lbDealerState", 0.84, 0.05, 0.15, 0.45, "HCenter Top", wrap: true));
        return sb.ToString();
    }

    #region Actions

    static void PushRaid() => Send(new RaidState(s_raid.Save()) { Tick = Now });

    static void RaidStart(int stakeIndex)
    {
        var pub = CrawlSystem.Pub;
        s_stake = Math.Clamp(stakeIndex, 0, Raid.Stakes.Length - 1);
        long stake = Raid.Stakes[s_stake];
        var r = s_raid.Start(stake, PurseShown(pub), s_rng.Next);
        if (r.Outcome == RaidOutcome.Refused) { Counters.Add("raid.start_refused"); return; }
        Send(new TableEvent(s_purse.Queue(-stake), -stake, Outcome.RaidAnte, stake, 0) { Tick = Now });
        AfterRaidStep(r, opening: true);
        Counters.Add("raid.start");
    }

    static void RaidChoose(bool run)
    {
        if (!s_raid.CanChoose) return;
        bool dealerRuns = s_raid.Dealer == Raider.Out && DealerMind.Runs(s_raid, alone: false, s_rng.Next());
        var r = s_raid.Step(run, dealerRuns, s_rng.Next);
        if (r.Outcome == RaidOutcome.Refused) return;
        if (run) Send(new Say(s_player, Strings.T("raid.say.run")) { Tick = Now });
        AfterRaidStep(r, opening: false);
        Counters.Add(run ? "raid.run" : "raid.stay");
    }

    /// <summary>After a step: the money, the save, the dealer's line, the card's flip.</summary>
    static void AfterRaidStep(Raid.RaidStep r, bool opening)
    {
        long carried = opening ? r.PlayerCats + s_raid.Stake : r.PlayerCats;
        if (carried > 0) Send(new TableEvent(s_purse.Queue(carried), carried, Outcome.RaidCarried, carried, 0) { Tick = Now });
        if (s_raid.Player == Raider.Busted && s_raidLast != RaidOutcome.Bust && r.Outcome == RaidOutcome.Bust)
            Send(new TableEvent(s_purse.Queue(0), 0, Outcome.RaidBust, 0, 0) { Tick = Now });
        s_raidLast = r.Outcome;
        PushRaid();
        s_call = RaidLine(r, opening);
        Send(new Say(s_dealer, s_call.Length > 160 ? s_call[..160] : s_call) { Tick = Now });
        if (s_raid.LastCard != Dust.None) { s_anim = Anim.Deal; s_clock.Restart(); }
        Counters.Add("raid." + r.Outcome);
    }

    static string RaidLine(Raid.RaidStep r, bool opening)
    {
        var parts = new List<string>();
        if (opening) parts.Add(Strings.TF("raid.call.start", Strings.Cats(s_raid.Stake)));
        if (r.DealerCats > 0 && s_raid.Dealer == Raider.Home && r.Outcome is RaidOutcome.Ran or RaidOutcome.BothRan)
            parts.Add(Strings.TF("raid.call.dealer_ran", Strings.Cats(r.DealerCats)));
        if (r.PlayerCats > 0 && !opening) parts.Add(Strings.TF("raid.call.you_ran", Strings.Cats(r.PlayerCats)));
        var card = s_raid.LastCard;
        switch (s_raid.Phase == RaidPhase.Over && s_raid.Player != Raider.Busted && r.Outcome is RaidOutcome.Ran or RaidOutcome.LastOut ? RaidOutcome.None : TurnOf(r))
        {
            case RaidOutcome.Loot: parts.Add(Strings.TF("raid.call.loot", DustName(card), RaidCards.Value(card))); break;
            case RaidOutcome.Trouble: parts.Add(Strings.TF("raid.call.trouble", DustName(card))); break;
            case RaidOutcome.Bust: parts.Add(Strings.TF(s_raid.Player == Raider.Busted ? "raid.call.bust" : "raid.call.bust_dealer", DustName(card))); break;
            case RaidOutcome.DustKing:
                string king = s_calls.Quote(DustKingLine, () => Strings.T("raid.call.king_fallback"));
                parts.Add(r.Taken == Dust.None ? Strings.TF("raid.call.king_empty", king) : Strings.TF("raid.call.king", king, DustName(r.Taken)));
                break;
            case RaidOutcome.DustDry: parts.Add(Strings.T("raid.call.dry")); break;
        }
        if (s_raid.Phase == RaidPhase.Out && s_raid.Player == Raider.Out)
            parts.Add(Strings.T("raid.tell." + DealerMind.Tell(s_raid)));
        else if (s_raid.Phase == RaidPhase.Over)
            parts.Add(Strings.TF("raid.call.over", Strings.Cats(s_raid.PlayerUnits * s_raid.UnitCats), Strings.Cats(s_raid.DealerUnits * s_raid.UnitCats)));
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    /// <summary>The card part of a step's outcome (a step that ran someone home may still have turned a card).</summary>
    static RaidOutcome TurnOf(Raid.RaidStep r)
    {
        if (r.Outcome is RaidOutcome.Loot or RaidOutcome.Trouble or RaidOutcome.Bust or RaidOutcome.DustKing or RaidOutcome.DustDry) return r.Outcome;
        if (r.Card == Dust.None) return RaidOutcome.None;
        if (s_raid.Phase == RaidPhase.Over && (s_raid.Player == Raider.Busted || s_raid.Dealer == Raider.Busted)) return RaidOutcome.Bust;
        return RaidCards.IsLoot(r.Card) ? RaidOutcome.Loot : RaidCards.IsTrouble(r.Card) ? RaidOutcome.Trouble : r.Card == Dust.DustKing ? RaidOutcome.DustKing : RaidOutcome.None;
    }

    #endregion

    #region Bind

    static List<(string Caption, Action Do)> RaidAnswers(long purse, bool busy)
    {
        var rows = new List<(string, Action)>();
        if (s_view != View.Table) return rows;
        if (busy) { rows.Add((Strings.T("ans.wait"), () => { })); return rows; }
        var r = s_raid;
        if (r.CanChoose)
        {
            bool last = r.Dealer != Raider.Out;
            long take = (last ? r.HaulUnits : r.HaulUnits / 2) * r.UnitCats;
            rows.Add((Strings.T("ans.raid.stay"), () => RaidChoose(run: false)));
            rows.Add((Strings.TF(last ? "ans.raid.run_all" : "ans.raid.run_half", Strings.Cats(take)), () => RaidChoose(run: true)));
        }
        else if (r.CanStart)
        {
            for (int i = 0; i < Raid.Stakes.Length; i++)
            {
                int ix = i;
                if (purse >= Raid.Stakes[i]) rows.Add((Strings.TF("ans.raid.ante", Strings.Cats(Raid.Stakes[i])), () => RaidStart(ix)));
            }
            if (purse < Raid.Stakes[0]) rows.Add((Strings.TF("ans.broke", Strings.Cats(Raid.Stakes[0])), () => { }));
        }
        rows.Add((Strings.T(r.Phase == RaidPhase.Out && r.Player == Raider.Out ? "ans.leave.hand" : "ans.leave"), () => Window.Close()));
        return rows;
    }

    /// <summary>The cloth and the answers for Looter's Rights (the dealer and you are bound by <see cref="Bind"/>).</summary>
    static void BindRaid(UiBindings b, Published pub, long purse)
    {
        var r = s_raid;
        double secs = s_clock.Elapsed.TotalSeconds;
        Image(b, "Felt", "felt_wide");

        b.Text["lbTrouble"] = Strings.T("raid.trouble");
        for (int i = 0; i < 4; i++)
        {
            var kind = RaidCards.Trouble[i];
            bool lit = r.TroubleLine.Contains(kind);
            Image(b, "T" + i, RaidArt(kind) + (lit ? "" : "_dim"));
            b.Text["TN" + i] = Strings.TF("raid.left", r.Deck.Left(kind));
            PzaWindow.Props(b, "TN" + i, ("TextColour", lit ? Gold : Chalk));
            PzaWindow.Tooltip(b, "T" + i, Strings.TF(lit ? "tip.raid.trouble_out" : "tip.raid.trouble", DustName(kind), r.Deck.Left(kind)));
        }
        b.Text["lbHaul"] = Strings.TF("raid.haul", r.HaulUnits);
        for (int i = 0; i < Math.Min(14, r.Haul.Count); i++)
        {
            Image(b, "H" + i, RaidArt(r.Haul[i]));
            PzaWindow.Tooltip(b, "H" + i, Strings.TF("tip.raid.loot", DustName(r.Haul[i]), RaidCards.Value(r.Haul[i])));
        }
        foreach (var w in new[] { "lbTrouble", "lbHaul" }) PzaWindow.Props(b, w, ("TextColour", Chalk));

        // the last card turned (it flips in), and the dust
        if (r.LastCard != Dust.None)
        {
            string art = RaidArt(r.LastCard);
            Image(b, "CardFace", s_anim == Anim.Deal ? Flip("r_back", art, secs / DealSeconds) : art);
            b.Text["lbFace"] = DustName(r.LastCard);
        }
        if (r.Deck.Total > 2) Image(b, "DeckB", "r_back");
        if (r.Deck.Total > 1) Image(b, "DeckA", "r_back");
        if (r.Deck.Total > 0) Image(b, "CardDraw", "r_back");
        b.Text["lbCrawl"] = Strings.TF("raid.dust", r.Deck.Total);
        PzaWindow.Tooltip(b, "CardDraw", Strings.TF("tip.raid.dust", r.Deck.Total, (int)Math.Round(r.BustChance * 100)));
        PzaWindow.Props(b, "lbFace", ("TextColour", Chalk));
        PzaWindow.Props(b, "lbCrawl", ("TextColour", Chalk));

        // what the haul is worth, and where the dealer is
        long worth = r.HaulUnits * r.UnitCats;
        if (worth > 0)
        {
            Image(b, "Coins", worth >= 1000 ? "coins3" : worth >= 300 ? "coins2" : "coins1");
            b.Text["lbPot"] = "c." + Strings.Cats(worth);
            b.Text["lbPot2"] = Strings.T("raid.on_table");
        }
        PzaWindow.Props(b, "lbPot", ("TextColour", Gold));
        PzaWindow.Props(b, "lbPot2", ("TextColour", Chalk));
        b.Text["lbDealerState"] = r.Phase == RaidPhase.Idle ? "" : Strings.TF(r.Dealer switch
        {
            Raider.Out => "raid.dealer.out",
            Raider.Home => "raid.dealer.home",
            _ => "raid.dealer.busted",
        }, Strings.Cats(r.DealerUnits * r.UnitCats)) + (r.Phase == RaidPhase.Out ? "\n" + Strings.TF("raid.bust_chance", (int)Math.Round(r.BustChance * 100)) : "");
        PzaWindow.Props(b, "lbDealerState", ("TextColour", Chalk));

        // your answers, or a page
        var rows = RaidAnswers(purse, s_anim != Anim.None);
        s_rows.Clear();
        var lines = new List<string>();
        for (int i = 0; i < rows.Count; i++) { lines.Add($"{i + 1}. {rows[i].Caption}"); s_rows.Add(rows[i].Do); }
        if (s_view == View.Rules)
            lines.AddRange(PzaFit.Paragraphs(Window, "Answers", string.Join("\n\n", Enumerable.Range(1, 6).Select(i => Strings.T("raid.rules." + i)))));
        else if (s_view == View.Ledger)
        {
            var led = new Ledger();
            led.Load(pub.Ledger);
            lines.AddRange(Strings.TF("raid.ledger", Strings.Cats(led.Raids), Strings.Cats(led.RaidBusts), Strings.Cats(led.RaidBest),
                Strings.Cats(led.Hands), Strings.Cats(led.Staked), Strings.Cats(led.Paid), Strings.Cats(led.Net)).Split('\n'));
        }
        PzaWindow.Lines(b, "Answers", lines);
    }

    #endregion
}
