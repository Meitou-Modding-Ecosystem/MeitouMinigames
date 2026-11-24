// PZA's Cat Crawlers for Meitou: fuzz targets for Shek Arm Wrestling (WrestleCore). Author: PZA. Licence: GPLv3 or later. Never shipped.
//   wrestle.bout     random bouts with random play (taps, prompts right and wrong, bracing on and off, leaving, frames of any length,
//                    nasty stats): the balance stays in [-1, 1] and the grit in [0, 1], a bout always ends (by a pin, by leaving, or at
//                    the 45 s cap), it ends once, never more than 8 pushes count in a rolling second, a refund only in the countdown, a
//                    forfeit only in the bout, a win is a pin on the Shek or a lead at the cap
//   wrestle.rewards  XP: nothing for a bout under 4 s, a refund or a forfeit, never negative or past the caps, never more on a later bout
//                    of the day; the sprain: stun only, never past 18, never so much the arm falls under 5, nothing on a win; respect:
//                    a first win per tier only, never past 15 in all over any run of bouts
//   wrestle.save     the saved record round-trips; nasty saves load as an empty record
using PZA.CatCrawlers;
using PZA.MeitouUi.Fuzz;

static class WrestleFuzz
{
    public static PzaFuzz.TargetDef[] Targets =>
    [
        new("wrestle.bout", 3000, Bout, fz =>
        {
            foreach (var rule in new[] { "won", "lost", "refunded", "forfeit", "cap", "slip", "sequence", "surge_broken", "tap_capped", "braced", "nasty_stats" })
                fz.Expect(fz.Counted(rule) > 0, $"rule '{rule}' never fired (a rule that never fires is a failed test)");
        }),
        new("wrestle.rewards", 8000, Rewards, fz =>
        {
            foreach (var rule in new[] { "xp", "xp_short", "xp_half", "xp_none_today", "sprain", "sprain_floor", "respect", "respect_capped" })
                fz.Expect(fz.Counted(rule) > 0, $"rule '{rule}' never fired (a rule that never fires is a failed test)");
        }),
        new("wrestle.save", 4000, SaveLoad, fz =>
        {
            fz.Expect(fz.Counted("bad_refused") > 0, "no nasty save was refused");
            fz.Expect(fz.Counted("many") > 0, "no record held many characters");
        }),
    ];

    static WrestleStats Stats(PzaFuzz fz)
    {
        if (fz.Chance(0.1)) { fz.Count("nasty_stats"); return new(fz.Float(), fz.Float()); }
        return new(fz.Int(1, 100), fz.Int(1, 100));
    }

    static void Bout(PzaFuzz fz)
    {
        var b = new WrestleBout();
        int tier = fz.Int(1, WrestleTiers.Count);
        var p = Stats(fz);
        if (!b.Start(tier, fz.Pick(WrestleTiers.StakesOf(tier)), p, fz.NextU64())) { fz.Expect(false, "a fair start was refused"); return; }
        fz.Expect(!b.Start(tier, 100, p, 1), "a second start during the countdown was taken");
        float boutSeconds = 0;
        int ends = 0;
        var end = WrestleEnd.None;
        var counted = new Queue<float>();
        int style = fz.Below(4);             // 0 mash hard, 1 mash + sequences, 2 brace a lot, 3 chaos
        float leaveAt = fz.Chance(0.08) ? fz.Float(0, 30) : float.MaxValue;
        var leftIn = (WrestlePhase?)null;
        for (int frame = 0; frame < 20000 && b.End == WrestleEnd.None; frame++)
        {
            var before = b.Phase;
            float dt = fz.Chance(0.02) ? fz.Float(-1, 5) : fz.Below(3) switch { 0 => 1f / 60, 1 => 1f / 30, _ => 1f / 144 };
            if (b.Phase == WrestlePhase.Bout)
            {
                b.Braced = style == 2 ? b.Mode is ShekMode.Surge or ShekMode.Telegraph : style == 3 && fz.Chance(0.3);
                int taps = style == 0 ? fz.Int(0, 3) : fz.Int(0, 1);
                for (int i = 0; i < taps; i++)
                {
                    if (b.Tap()) counted.Enqueue(b.Clock);
                    else if (!b.Braced && b.SlipLeft <= 0 && b.End == WrestleEnd.None) fz.Count("tap_capped");
                }
                while (counted.Count > 0 && b.Clock - counted.Peek() >= 1f) counted.Dequeue();
                fz.Expect(counted.Count <= WrestleTuning.TapCap, $"{counted.Count} pushes counted within one second (cap {WrestleTuning.TapCap})");
                if (b.Braced) fz.Count("braced");
                if (b.SequenceOpen && b.End == WrestleEnd.None && (style == 1 || style == 3) && fz.Chance(0.3))
                {
                    var want = b.Sequence[b.SequenceAt];
                    bool right = fz.Chance(style == 1 ? 0.9 : 0.5);
                    var mode = b.Mode;
                    int hits = b.SequencesHit, broken = b.SurgesBroken, slips = b.Slips;
                    bool slipping = b.SlipLeft > 0;   // a slip ignores prompts for 0.4 s
                    bool ok = b.Press(right ? want : (Prompt)(((int)want + 1 + fz.Below(2)) % 3));
                    fz.Expect(ok == (right && !slipping), $"a {(right ? "right" : "wrong")} prompt answered {ok}{(slipping ? " while slipping" : "")}");
                    fz.Expect(b.Slips == slips + (right || slipping ? 0 : 1), "a slip counted wrong");
                    if (b.Slips > slips) fz.Count("slip");
                    if (b.SequencesHit > hits) fz.Count("sequence");
                    if (b.SurgesBroken > broken) { fz.Count("surge_broken"); fz.Expect(mode is ShekMode.Telegraph or ShekMode.Surge, "a surge broken outside a surge"); }
                }
                if (b.Clock >= leaveAt && b.End == WrestleEnd.None) { leftIn = b.Phase; b.Leave(); }
            }
            else if (b.Phase == WrestlePhase.Countdown && fz.Chance(0.002)) { leftIn = b.Phase; b.Leave(); }
            if (b.End == WrestleEnd.None)
            {
                float c0 = b.Clock;
                var e = b.Advance(dt);
                if (b.Phase == WrestlePhase.Bout || before == WrestlePhase.Bout) boutSeconds += b.Clock - c0;
                if (e != WrestleEnd.None) { ends++; end = e; }
            }
            fz.Expect(b.Balance is >= -1f and <= 1f, $"balance {b.Balance} out of [-1, 1]");
            fz.Expect(b.PlayerGrit is >= 0f and <= 1f && b.ShekGrit is >= 0f and <= 1f, $"grit {b.PlayerGrit} / {b.ShekGrit} out of [0, 1]");
            fz.Expect(b.Frame is >= 0 and <= 32, $"frame {b.Frame}");
            fz.Expect(b.Clock <= WrestleTuning.MaxSeconds + 0.05f, $"the bout ran {b.Clock} s, past the cap");
        }
        fz.Expect(b.End != WrestleEnd.None, $"a bout never ended (phase {b.Phase}, clock {b.Clock})");
        fz.Expect(ends <= 1, "Advance answered an end twice");
        switch (b.End)
        {
            case WrestleEnd.Won:
                fz.Count("won");
                fz.Expect(b.Balance >= 1f || (b.Clock >= WrestleTuning.MaxSeconds && b.Balance > 0), $"a win at balance {b.Balance}, clock {b.Clock}");
                break;
            case WrestleEnd.Lost:
                fz.Count("lost");
                fz.Expect(b.Balance <= -1f || (b.Clock >= WrestleTuning.MaxSeconds && b.Balance <= 0), $"a loss at balance {b.Balance}, clock {b.Clock}");
                break;
            case WrestleEnd.Refunded:
                fz.Count("refunded");
                fz.Expect(leftIn == WrestlePhase.Countdown && b.Clock == 0, "a refund after the bout started");
                break;
            case WrestleEnd.Forfeit:
                fz.Count("forfeit");
                fz.Expect(leftIn == WrestlePhase.Bout, "a forfeit outside the bout");
                break;
        }
        if (b.Clock >= WrestleTuning.MaxSeconds) fz.Count("cap");
    }

    static void Rewards(PzaFuzz fz)
    {
        var p = Stats(fz);
        int tier = fz.Int(1, WrestleTiers.Count);
        var s = WrestleTiers.Stats(tier);
        var end = fz.Pick(new[] { WrestleEnd.Won, WrestleEnd.Lost, WrestleEnd.Lost, WrestleEnd.Refunded, WrestleEnd.Forfeit, WrestleEnd.None });
        float secs = fz.Chance(0.1) ? fz.Float() : fz.Float(0, 45);
        int before = fz.Int(0, 8);
        float share = WrestleRewards.DayShare(before);
        fz.Expect(share is 0f or 0.5f or 1f, $"day share {share}");
        fz.Expect(share <= WrestleRewards.DayShare(Math.Max(0, before - 1)), "a later bout of the day gave a bigger share");
        if (before >= 3 && before < 5) fz.Count("xp_half");
        if (before >= 5) { fz.Count("xp_none_today"); fz.Expect(share == 0, $"bout {before + 1} of the day still trains (share {share})"); }
        var (xs, xt) = WrestleRewards.Xp(end, secs, p, s, fz.Chance(0.05) ? fz.Float() : share);
        fz.Expect(float.IsFinite(xs) && float.IsFinite(xt) && xs >= 0 && xt >= 0 && xs <= WrestleRewards.MaxStrengthXp && xt <= WrestleRewards.MaxToughnessXp, $"XP {xs} / {xt}");
        bool honest = end is WrestleEnd.Won or WrestleEnd.Lost && float.IsFinite(secs) && secs >= WrestleRewards.HonestSeconds;
        if (!honest) { fz.Expect(xs == 0 && xt == 0, $"XP {xs} / {xt} for a {end} of {secs} s"); if (end is WrestleEnd.Won or WrestleEnd.Lost) fz.Count("xp_short"); }
        else if (xs > 0) fz.Count("xp");

        float flesh = fz.Chance(0.1) ? fz.Float() : fz.Float(0, 100), stun = fz.Chance(0.1) ? fz.Float() : fz.Float(0, 100);
        float grit = fz.Float(0, 1);
        float add = WrestleRewards.Sprain(end, secs, grit, p, s, flesh, stun);
        fz.Expect(float.IsFinite(add) && add >= 0 && add <= 18f, $"sprain {add}");
        if (end != WrestleEnd.Lost) fz.Expect(add == 0, $"a sprain of {add} on a {end}");
        if (add > 0)
        {
            fz.Count("sprain");
            fz.Expect(flesh - stun - add >= WrestleRewards.SprainFloor - 1e-3f, $"a sprain left the arm at {flesh - stun - add} (flesh {flesh}, stun {stun}, +{add})");
        }
        else if (end == WrestleEnd.Lost && float.IsFinite(flesh) && float.IsFinite(stun) && flesh - stun <= WrestleRewards.SprainFloor && secs < 6) fz.Count("sprain_floor");

        // respect over a run of bouts on one record
        var rec = new WrestleRecord();
        for (int i = fz.Int(1, 40); i > 0; i--)
        {
            int t = fz.Int(1, WrestleTiers.Count);
            bool won = fz.Chance(0.6);
            bool first = rec.IsFirstWin(t);
            int r = WrestleRewards.Respect(won, won && first, t, rec.RespectGiven);
            fz.Expect(r >= 0 && r <= WrestleTiers.FirstWinRespect(t), $"respect {r} for tier {t}");
            if (r > 0) fz.Count("respect");
            if (won && first && r < WrestleTiers.FirstWinRespect(t)) fz.Count("respect_capped");
            if (!won || !first) fz.Expect(r == 0, "respect for a loss or a repeat win");
            rec.Note(fz.Int(0, 3), fz.Int(0, 5), t, won, r);
            fz.Expect(rec.RespectGiven is >= 0 and <= WrestleRewards.RespectCap, $"respect given {rec.RespectGiven}");
        }
        // the cap itself: a record near the cap never passes it
        var full = new WrestleRecord();
        full.Load([WrestleRecord.Format, WrestleRewards.RespectCap - fz.Int(0, 2), 0, 0, 0, 0, 0, 0]);
        int last = WrestleRewards.Respect(true, true, 3, full.RespectGiven);
        if (last < WrestleTiers.FirstWinRespect(3)) fz.Count("respect_capped");
        fz.Expect(full.RespectGiven + last <= WrestleRewards.RespectCap, $"respect {full.RespectGiven} + {last} past the cap");
    }

    static void SaveLoad(PzaFuzz fz)
    {
        var rec = new WrestleRecord();
        int chars = fz.Chance(0.1) ? fz.Int(200, 300) : fz.Int(0, 6);
        for (int i = fz.Int(0, chars * 2 + 3); i > 0; i--)
        {
            int t = fz.Int(1, 3);
            bool won = fz.Chance(0.5);
            rec.Note(fz.Int(0, Math.Max(1, chars)), fz.Int(0, 30), t, won, WrestleRewards.Respect(won, rec.IsFirstWin(t), t, rec.RespectGiven));
        }
        var saved = rec.Save();
        if (saved[4 + WrestleTiers.Count] > 100) fz.Count("many");
        fz.Expect(saved[4 + WrestleTiers.Count] <= WrestleRecord.MaxCharacters, "the record grew past its character cap");
        var u = new WrestleRecord();
        fz.Expect(u.Load(saved) && u.Save().SequenceEqual(saved), "a record did not load back the same");

        var bad = saved.ToList();
        switch (fz.Below(4))
        {
            case 0: bad.RemoveAt(fz.Below(bad.Count)); break;
            case 1: bad.Add(fz.IntNasty()); break;
            default:
                for (int n = fz.Int(1, 3); n > 0; n--) bad[fz.Below(bad.Count)] = fz.Below(3) switch { 0 => fz.IntNasty(-30, 30), 1 => long.MaxValue - fz.Below(3), _ => -fz.Int(1, 99) };
                break;
        }
        var v = new WrestleRecord();
        if (!v.Load(bad))
        {
            fz.Count("bad_refused");
            fz.Expect(v.RespectGiven == 0 && v.Won == 0 && v.Lost == 0 && v.FirstWin.All(f => !f), "a refused save left something behind");
        }
        else
        {
            fz.Expect(v.RespectGiven is >= 0 and <= WrestleRewards.RespectCap && v.Won >= 0 && v.Lost >= 0, "a nasty save loaded impossible numbers");
            fz.Expect(v.Save().SequenceEqual(bad), "a save that loaded does not save back the same");
        }
    }
}
