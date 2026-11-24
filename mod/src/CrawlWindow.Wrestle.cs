// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// Shek Arm Wrestling at the Shek challenger's table (slice 1, 2026-11-22): the same conversation-style window (the challenger on top, the table, your answers), with the bout on the table:
//   the arms        a 33-frame picture set (art w_<set>_00 .. w_<set>_32: 00 your hand pinned, 16 locked, 32 his hand pinned), the set by
//                   your race (a Shek: shek_vs_shek; anyone else: human_vs_shek), with a flash over them on a surge, a slip or a pin (the
//                   layout cannot move a picture per frame, so the flash's alpha fades instead of a shake)
//   the meter       the balance (art w_meter_00 .. 32), the Shek's mode under it (SURGE! in red while he telegraphs or surges)
//   the sequence    three prompt buttons (left / up / right): the one to press now lit gold, the ones done dim
//   your grit       art w_grit_00 .. 10, and your Strength / Toughness against his
//   PUSH / BRACE    buttons: click PUSH as fast as you like (8 a second count); BRACE is a switch (click to brace, click again to push);
//                   HOLD PUSH is a switch that pushes steadily for you (5 a second; for anyone who should not mash)
// Answers: the stakes of both tiers (Tier 1, the Retainer's; Tier 2, the Warrior's), leave (before the grip: the stake comes back; after:
// it is lost). The rules: WrestleCore (host-side, on real time, as the cards are). Money: TableEvents; the rest: WrestleResult.
// The pad: every control is a button the pad's pointer and A can hit (PZA's WASD & Controller Support), the answers a list it can walk.
// No keys are read (no key bleeds into the camera or the squad bar).
using Meitou.Data.Ui;
using PZA.MeitouUi;

namespace PZA.CatCrawlers;

public static partial class CrawlWindow
{
    static WrestleBout s_bout = new();
    static bool s_boutSettled = true;
    static long s_wrestleSeq;
    static bool s_holdPush;
    static double s_lastFrame, s_holdNext, s_shakeUntil;
    static readonly System.Diagnostics.Stopwatch s_wclock = System.Diagnostics.Stopwatch.StartNew();
    public const double HoldPushRate = 5;

    public static WrestleBout Bout => s_bout;

    /// <summary>The arm pictures for the player: a Shek's own arm, else the human one (slice 1 has these two sets).</summary>
    static string ArmSet(Published pub) => pub.PlayerRace.Contains("Shek", StringComparison.OrdinalIgnoreCase) ? "ss" : "hs";

    static readonly string[] s_wrestleButtons = ["BtnSeq0", "BtnSeq1", "BtnSeq2", "BtnPush", "BtnBrace", "BtnHold", "BtnLeft"];

    /// <summary>Hides the bout's buttons at the card tables (ImageBoxes / TextBoxes stay blank when unbound, but Kenshi_Button1 still draws).</summary>
    static void HideWrestleButtons(UiBindings b)
    {
        foreach (var id in s_wrestleButtons) b.Hidden.Add(id);
    }

    /// <summary>The bout's widgets on the table (appended to the layout; unused at the other tables).</summary>
    static string WrestleWidgets()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Img("WArms", 0.2, 0.03, 0.6, 0.63));
        sb.Append(Img("WFlash", 0.2, 0.03, 0.6, 0.63));
        sb.Append(Img("WMeter", 0.2, 0.67, 0.6, 0.07));
        sb.Append(Txt("WMode", 0.2, 0.74, 0.6, 0.06, "HCenter VCenter"));
        for (int i = 0; i < 3; i++) sb.Append(Box("Button", "Kenshi_Button1", "BtnSeq" + i, 0.3 + i * 0.14, 0.81, 0.12, 0.13));
        sb.Append(Txt("WYou", 0.015, 0.05, 0.17, 0.3, "Left Top", wrap: true));
        sb.Append(Img("WGrit", 0.015, 0.4, 0.17, 0.06));
        sb.Append(Txt("WGritLb", 0.015, 0.46, 0.17, 0.06));
        sb.Append(Txt("WShek", 0.815, 0.05, 0.17, 0.3, "Left Top", wrap: true));
        sb.Append(Box("Button", "Kenshi_Button1", "BtnPush", 0.815, 0.4, 0.17, 0.17));
        sb.Append(Box("Button", "Kenshi_Button1", "BtnBrace", 0.815, 0.6, 0.17, 0.13));
        sb.Append(Box("Button", "Kenshi_Button1", "BtnHold", 0.815, 0.76, 0.17, 0.13));
        sb.Append(Box("Button", "Kenshi_Button1", "BtnLeft", 0.015, 0.6, 0.17, 0.13));
        return sb.ToString();
    }

    #region Actions

    /// <summary>A button of the bout (true when the event was one).</summary>
    static bool WrestleEvent(string action)
    {
        if (s_game != TableGame.ArmWrestling) return false;
        switch (action)
        {
            case "BtnPush": if (s_bout.Tap()) Counters.Add("wrestle.push"); AfterInput(); return true;
            case "BtnBrace": s_bout.Braced = !s_bout.Braced && s_bout.Phase == WrestlePhase.Bout; Counters.Add("wrestle.brace"); return true;
            case "BtnHold": s_holdPush = !s_holdPush; Counters.Add("wrestle.hold_push." + s_holdPush); return true;
            case "BtnLeft": WrestleLeave(); return true;
            case "BtnSeq0" or "BtnSeq1" or "BtnSeq2":
                s_bout.Press((Prompt)(action[^1] - '0'));
                AfterInput();
                return true;
        }
        return false;
    }

    static void AfterInput()
    {
        if (s_bout.JustSurged || s_bout.JustSlipped) s_shakeUntil = s_wclock.Elapsed.TotalSeconds + 0.25;
        if (s_bout.JustSurged) { Send(new Say(s_player, Strings.T("wrestle.say.surge")) { Tick = Now }); Counters.Add("wrestle.grit_surge"); }
        s_bout.JustSurged = s_bout.JustSlipped = false;
        if (s_bout.End != WrestleEnd.None) Finished(s_bout.End);
    }

    static void WrestleStart(int tier, int stakeIndex)
    {
        var pub = CrawlSystem.Pub;
        long stake = WrestleTiers.StakesOf(tier)[Math.Clamp(stakeIndex, 0, 2)];
        if (PurseShown(pub) < stake) { Counters.Add("wrestle.broke"); return; }
        var me = new WrestleStats(pub.PlayerStrength, pub.PlayerToughness);
        if (!s_bout.Start(tier, stake, me, (ulong)s_rng.NextInt64(1, long.MaxValue))) { Counters.Add("wrestle.start_refused"); return; }
        Send(new TableEvent(s_purse.Queue(-stake), -stake, Outcome.WrestleStake, stake, tier) { Tick = Now });
        s_boutSettled = false;
        s_holdNext = 0;
        s_call = Strings.TF("wrestle.call.grip", Strings.Cats(stake));
        Send(new Say(s_dealer, Strings.T("wrestle.say.grip")) { Tick = Now });
        Counters.Add("wrestle.start.tier" + tier);
    }

    static void WrestleLeave()
    {
        if (s_bout.Phase is WrestlePhase.Countdown or WrestlePhase.Bout)
        {
            var e = s_bout.Leave();
            if (e != WrestleEnd.None) Finished(e);
        }
        else Window.Close();
    }

    /// <summary>Once per frame while the window is open at the Shek's table: the bout on real time, the hold-push switch.</summary>
    static void WrestleFrame()
    {
        double now = s_wclock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - s_lastFrame, 0, 0.25);
        s_lastFrame = now;
        if (s_bout.Phase == WrestlePhase.Bout && s_holdPush && now >= s_holdNext)
        {
            s_holdNext = now + 1.0 / HoldPushRate;
            if (s_bout.Tap()) Counters.Add("wrestle.push.held");
            AfterInput();
        }
        var e = s_bout.Advance(dt);
        if (e != WrestleEnd.None) Finished(e);
        if (s_bout.Phase == WrestlePhase.Bout && s_bout.Mode == ShekMode.Telegraph && s_call != Strings.T("wrestle.call.surge"))
        {
            s_call = Strings.T("wrestle.call.surge");
            Send(new Say(s_dealer, Strings.T("wrestle.say.surge_shek")) { Tick = Now });
        }
    }

    /// <summary>A bout ended (once): the money, the world's side, the challenger's line.</summary>
    static void Finished(WrestleEnd end)
    {
        if (s_boutSettled) return;
        s_boutSettled = true;
        s_holdPush = false;
        long stake = s_bout.Stake;
        if (end == WrestleEnd.Won) Send(new TableEvent(s_purse.Queue(2 * stake), 2 * stake, Outcome.WrestleWon, stake, s_bout.Tier) { Tick = Now });
        if (end == WrestleEnd.Refunded) Send(new TableEvent(s_purse.Queue(stake), stake, Outcome.WrestleRefund, stake, s_bout.Tier) { Tick = Now });
        Send(new WrestleResult(++s_wrestleSeq, s_player, s_bout.Tier, end, s_bout.Clock, s_bout.PlayerGrit) { Tick = Now });
        s_shakeUntil = s_wclock.Elapsed.TotalSeconds + (end is WrestleEnd.Won or WrestleEnd.Lost ? 0.45 : 0);
        string key = end switch
        {
            WrestleEnd.Won => "wrestle.call.won",
            WrestleEnd.Lost => "wrestle.call.lost",
            WrestleEnd.Refunded => "wrestle.call.refunded",
            _ => "wrestle.call.forfeit",
        };
        s_call = Strings.TF(key, Strings.Cats(end == WrestleEnd.Won ? 2 * stake : stake), s_bout.Clock.ToString("0.0"));
        Send(new Say(s_dealer, Strings.T(key.Replace("call", "say"))) { Tick = Now });
        Counters.Add("wrestle.finished." + end);
    }

    #endregion

    #region Bind

    static List<(string Caption, Action Do)> WrestleAnswers(long purse)
    {
        var rows = new List<(string, Action)>();
        if (s_view != View.Table) return rows;
        if (s_bout.Phase is WrestlePhase.Countdown or WrestlePhase.Bout)
        {
            rows.Add((Strings.T(s_bout.Phase == WrestlePhase.Countdown ? "ans.wrestle.back_out" : "ans.wrestle.give_up"), WrestleLeave));
            return rows;
        }
        if (s_bout.Phase == WrestlePhase.Pinned && s_bout.PinnedLeft > 0) { rows.Add((Strings.T("ans.wait"), () => { })); return rows; }
        for (int tier = 1; tier <= 2; tier++)
        {
            var stakes = WrestleTiers.StakesOf(tier);
            for (int i = 0; i < stakes.Length; i++)
            {
                int t = tier, ix = i;
                if (purse >= stakes[i]) rows.Add((Strings.TF("ans.wrestle.stake", Strings.T("wrestle.tier." + t), Strings.Cats(stakes[i])), () => WrestleStart(t, ix)));
            }
        }
        if (purse < WrestleTiers.StakesOf(1)[0]) rows.Add((Strings.TF("ans.broke", Strings.Cats(WrestleTiers.StakesOf(1)[0])), () => { }));
        rows.Add((Strings.T("ans.leave"), () => Window.Close()));
        return rows;
    }

    static void BindWrestle(UiBindings b, Published pub, long purse)
    {
        if (s_bout.Phase == WrestlePhase.Pinned && s_bout.PinnedLeft <= 0) s_bout.Reset();
        var w = s_bout;
        bool live = w.Phase is WrestlePhase.Countdown or WrestlePhase.Bout;
        Image(b, "Felt", "felt_wide");
        string set = ArmSet(pub);
        Image(b, "WArms", $"w_{set}_{w.Frame:00}");
        double now = s_wclock.Elapsed.TotalSeconds;
        if (now < s_shakeUntil)
        {
            Image(b, "WFlash", "w_flash");
            PzaWindow.Props(b, "WFlash", ("Alpha", Math.Clamp((s_shakeUntil - now) / 0.45, 0, 0.8).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        }
        Image(b, "WMeter", $"w_meter_{w.Frame:00}");
        Image(b, "WGrit", $"w_grit_{(int)Math.Round(Math.Clamp(w.PlayerGrit, 0, 1) * 10):00}");
        b.Text["WGritLb"] = Strings.TF("wrestle.grit", (int)Math.Round(w.PlayerGrit * 100));

        var shek = WrestleTiers.Stats(w.Phase == WrestlePhase.Bet ? 1 : w.Tier);
        b.Text["WYou"] = Strings.TF("wrestle.you", (int)pub.PlayerStrength, (int)pub.PlayerToughness) + (pub.ArmHurt ? "\n" + Strings.T("wrestle.arm_hurt") : "");
        b.Text["WShek"] = w.Phase == WrestlePhase.Bet
            ? Strings.TF("wrestle.shek.tiers", (int)WrestleTiers.Stats(1).S, (int)WrestleTiers.Stats(2).S)
            : Strings.TF("wrestle.shek", Strings.T("wrestle.tier." + w.Tier), (int)shek.S, (int)shek.T);
        b.Text["WMode"] = w.Phase switch
        {
            WrestlePhase.Countdown => Strings.TF("wrestle.countdown", Math.Max(1, (int)Math.Ceiling(w.CountdownLeft))),
            WrestlePhase.Bout => Strings.T("wrestle.mode." + w.Mode) + (w.Braced ? "  " + Strings.T("wrestle.braced") : "") + (w.SlipLeft > 0 ? "  " + Strings.T("wrestle.slip") : ""),
            WrestlePhase.Pinned => Strings.T(w.End == WrestleEnd.Won ? "wrestle.pinned.won" : "wrestle.pinned.lost"),
            _ => Strings.T("wrestle.mode.bet"),
        };
        PzaWindow.Props(b, "WMode", ("TextColour", w.Mode is ShekMode.Telegraph or ShekMode.Surge && w.Phase == WrestlePhase.Bout ? Red : Gold));
        foreach (var t in new[] { "WYou", "WShek", "WGritLb" }) PzaWindow.Props(b, t, ("TextColour", Chalk));

        // the sequence: the prompt to press now lit
        for (int i = 0; i < 3; i++)
        {
            string id = "BtnSeq" + i;
            b.Text[id] = Strings.T("wrestle.prompt." + i);
            bool want = w.SequenceOpen && w.Sequence[w.SequenceAt] == (Prompt)i;
            PzaWindow.Props(b, id, ("TextColour", want ? Gold : w.SequenceOpen ? Chalk : Faded));
            PzaWindow.Tooltip(b, id, Strings.T("tip.wrestle.prompt"));
        }
        b.Text["BtnPush"] = Strings.T("wrestle.btn.push");
        b.Text["BtnBrace"] = Strings.T(w.Braced ? "wrestle.btn.unbrace" : "wrestle.btn.brace");
        b.Text["BtnHold"] = Strings.T(s_holdPush ? "wrestle.btn.hold_on" : "wrestle.btn.hold_off");
        b.Text["BtnLeft"] = Strings.T(w.Phase == WrestlePhase.Bout ? "wrestle.btn.give_up" : w.Phase == WrestlePhase.Countdown ? "wrestle.btn.back_out" : "wrestle.btn.stand");
        PzaWindow.Tooltip(b, "BtnPush", Strings.T("tip.wrestle.push"));
        PzaWindow.Tooltip(b, "BtnBrace", Strings.T("tip.wrestle.brace"));
        PzaWindow.Tooltip(b, "BtnHold", Strings.T("tip.wrestle.hold"));
        if (live) PzaWindow.Enable(b, "BtnPush", "BtnBrace", "BtnHold", "BtnLeft", "BtnSeq0", "BtnSeq1", "BtnSeq2");
        else PzaWindow.Enable(b, "BtnLeft");

        // your answers, or a page
        var rows = WrestleAnswers(purse);
        s_rows.Clear();
        var lines = new List<string>();
        for (int i = 0; i < rows.Count; i++) { lines.Add($"{i + 1}. {rows[i].Caption}"); s_rows.Add(rows[i].Do); }
        if (s_view == View.Rules)
            lines.AddRange(PzaFit.Paragraphs(Window, "Answers", string.Join("\n\n", Enumerable.Range(1, 5).Select(i => Strings.T("wrestle.rules." + i)))));
        else if (s_view == View.Ledger)
        {
            var rec = new WrestleRecord();
            rec.Load(pub.Wrestle);
            lines.AddRange(Strings.TF("wrestle.ledger", rec.Won, rec.Lost, rec.FirstWin[0] ? "x" : "-", rec.FirstWin[1] ? "x" : "-", rec.RespectGiven).Split('\n'));
        }
        PzaWindow.Lines(b, "Answers", lines);
    }

    #endregion
}
