// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// Shek Arm Wrestling's side in the world (slice 1, 2026-11-22). The bout itself
// is the window's (WrestleCore, host-side, as the cards are); its money comes through TableEvents (WrestleStake / WrestleWon / WrestleRefund),
// and what a finished bout does to the character through one WrestleResult, settled here in the serial input step:
//   training  CharacterStats.Gain on Strength and Toughness (the game's own level curve and XP rate on top), the day's share by character:
//             full for the first three bouts of a game day, half for the next two, none after (WrestleRewards.DayShare)
//   sprain    a crushing loss adds STUN to the right arm only (never flesh: no bleeding, no lost limb), never so much that the arm's health
//             falls under 5, and nothing for an arm already that low (WrestleRewards.Sprain)
//   respect   the first win over each tier raises the Shek Kingdom's relation with the player (+1 / +2, +4 for the champion), never past
//             +15 in all from the tables (WrestleRewards.Respect); losing never costs any
// The character key for the daily share: the game's lasting id where the engine has one (World.LastingIds, Meitou's newer builds, by name),
// else a hash of the character's name (older engines: two characters of one name share a day's count, which only makes the cap stricter).
using System.Reflection;
using Meitou.Simulation;

namespace PZA.CatCrawlers;

/// <summary>A finished bout (or a refund / forfeit) for the world: who, which tier, how it ended, how long it took, the grit left.</summary>
public sealed record WrestleResult(long Seq, CharacterId Player, int Tier, WrestleEnd End, float Seconds, float Grit) : SimCommand;

public sealed partial class CrawlSystem
{
    readonly WrestleRecord _wrestle = new();
    long _wrestleApplied;

    /// <summary>World.LastingIds (Meitou's newer builds), found by name: null on 0.21 / 0.22.1.</summary>
    static readonly PropertyInfo? s_lastingIds = typeof(World).GetProperty("LastingIds");

    /// <summary>The daily-share key of a character: its lasting id, else a hash of its name.</summary>
    static long CharacterKey(World world, CharacterId id)
    {
        try
        {
            if (s_lastingIds?.GetValue(world) is { } ids && ids.GetType().GetMethod("GetOrGive", [typeof(CharacterId)]) is { } give
                && give.Invoke(ids, [id]) is { } lasting && lasting.GetType().GetProperty("Value")?.GetValue(lasting) is ulong v && v != 0)
                return (long)v;
        }
        catch (TargetInvocationException e) { Log.Few("wrestle.lasting", $"wrestle: the lasting id failed ({e.InnerException?.GetType().Name}); the name is the key"); }
        ulong h = 14695981039346656037UL;   // FNV-1a 64 of the name
        foreach (char c in world.Characters.NameOf(id)) { h ^= c; h *= 1099511628211UL; }
        return (long)(h & 0x7FFFFFFFFFFFFFFFUL) | 1L << 62;   // a separate range from the lasting ids' low numbers
    }

    /// <summary>The right arm's part (the one that collapses with the right arm's bit), or null.</summary>
    static Meitou.Simulation.Bodies.HealthPart? RightArm(CharacterCold cold) =>
        cold.Medical?.Parts.FirstOrDefault(p => (p.Template.CollapsePart & 2) != 0 && p.Template.Collapses)
        ?? cold.Medical?.Parts.FirstOrDefault(p => (p.Template.CollapsePart & 2) != 0);

    /// <summary>A player character may wrestle: its right arm works (a fitted prosthetic does; a stump, a crushed arm or one that is down does not).</summary>
    public static bool ArmReady(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) && world.Characters.Cold(slot) is { Medical: { } m } && m.ArmWorks(left: false);

    void Settle(World world, WrestleResult r)
    {
        if (r.Seq <= _wrestleApplied) { Counters.Add("wrestle.stale"); return; }
        _wrestleApplied = r.Seq;
        Counters.Add("wrestle.end." + r.End);
        if (r.End is not (WrestleEnd.Won or WrestleEnd.Lost)) { Log.Line($"wrestle: tier {r.Tier} {r.End} after {r.Seconds:0.0} s"); return; }
        var table = world.Characters;
        if (!table.TryResolveNext(r.Player, out int slot) || table.Cold(slot) is not { } cold)
        {
            Counters.Add("wrestle.player_gone");
            return;
        }
        bool won = r.End == WrestleEnd.Won;
        var shek = WrestleTiers.Stats(r.Tier);
        var p = new WrestleStats(cold.Stats?.Strength ?? 1, cold.Stats?.Toughness ?? 1);
        long key = CharacterKey(world, r.Player);
        long day = (long)Math.Floor(Math.Max(0, world.GameHours) / 24.0);
        int before = _wrestle.BoutsToday(key, day);
        float share = WrestleRewards.DayShare(before);

        // training
        var (xs, xt) = WrestleRewards.Xp(r.End, r.Seconds, p, shek, share);
        float s0 = p.S, t0 = p.T;
        if (cold.Stats is { } stats)
        {
            if (xs > 0) stats.Gain(Meitou.Data.Gameplay.Bodies.StatsEnumerated.Strength, xs);
            if (xt > 0) stats.Gain(Meitou.Data.Gameplay.Bodies.StatsEnumerated.Toughness, xt);
        }
        Counters.Add(xs > 0 ? "wrestle.trained" : share == 0 ? "wrestle.trained.capped" : "wrestle.trained.none");

        // the sprain (stun only)
        float sprain = 0;
        if (!won && RightArm(cold) is { } arm)
        {
            sprain = WrestleRewards.Sprain(r.End, r.Seconds, r.Grit, p, shek, arm.Flesh, arm.Stun);
            if (sprain > 0) { arm.Stun += sprain; Counters.Add("wrestle.sprain"); }
        }

        // respect
        int respect = WrestleRewards.Respect(won, won && _wrestle.IsFirstWin(r.Tier), r.Tier, _wrestle.RespectGiven);
        if (respect > 0)
        {
            int shekFaction = _data?.FactionIndex(Hosts.Shek.Faction) ?? -1;
            int mine = world.Player.Faction;
            if (_relations is null || shekFaction < 0 || mine < 0)
            {
                Counters.Add("wrestle.respect.no_relations");
                respect = 0;   // not counted as given when it could not be given
            }
            else
            {
                var cause = new Meitou.Simulation.Factions.RelationCause(Meitou.Simulation.Factions.RelationCauseType.Dialogue, r.Player);
                Meitou.Simulation.Factions.PlayerRelations.Add(world, _relations, shekFaction, mine, respect, cause, $"{Hosts.Shek.FactionName}: respect for a win at the arm-wrestling table");
                Meitou.Simulation.Factions.PlayerRelations.Add(world, _relations, mine, shekFaction, respect, cause);
                Counters.Add("wrestle.respect");
            }
        }
        _wrestle.Note(key, day, r.Tier, won, respect);
        _dirty = true;
        Log.Line($"wrestle: tier {r.Tier} {r.End} in {r.Seconds:0.0} s; bout {before + 1} today (share {share:0.#}): strength {s0:0.##} -> {cold.Stats?.Strength:0.##}, "
            + $"toughness {t0:0.##} -> {cold.Stats?.Toughness:0.##}; sprain +{sprain:0.#} stun; respect +{respect} ({_wrestle.RespectGiven} given in all)");
    }
}
