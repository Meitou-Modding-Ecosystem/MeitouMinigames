// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// The table hosts the mod places in the world (records in PZACatCrawlers.mod; nothing in the original game
// spawns them): the Shady Dealer of the Hub (user, 2026-11-20: Dust Bandit kit, Trade Ninjas' faction, Looter's Rights) and the Shek
// Table Challenger of Squin (2026-11-22: Shek Arm Wrestling). One TableHost per host, from a HostSpec. In CrawlSystem's slow serial step:
// if no platoon carries the host's tag, he is spawned once at his town (Meitou's SpawnPlatoon: a ring round the town's centre, kept off
// screen, saved, and found again after a load by its ModTag) and held there (HoldPlatoon: no trips); then brought to the town's bar
// (Tend). A spawn that fails (no town, no record, no walkable point) is logged and tried again a game hour later.
using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace PZA.CatCrawlers;

/// <summary>A host the mod places: Key names his counters and log lines ("shady", "shek").</summary>
public sealed record HostSpec(string Key, string Name, string Template, string Character, string Tag, string Town, string TownName, string Faction,
    string FactionName, TableGame Game);

/// <summary>The hosts (their records live in PZACatCrawlers.mod).</summary>
public static class Hosts
{
    public static readonly HostSpec Shady = new("shady", "shady dealer", "10-PZACatCrawlers.mod", "9-PZACatCrawlers.mod", "pza.catcrawlers:shady-dealer",
        "18919-Newwworld.mod", "the Hub", "40551-rebirth.mod", "Trade Ninjas", TableGame.LootersRights);
    public static readonly HostSpec Shek = new("shek", "shek challenger", "20-PZACatCrawlers.mod", "19-PZACatCrawlers.mod", "pza.catcrawlers:shek-challenger",
        "1079-gamedata.base", "Squin", "11624-Dialogue (10).mod", "Shek Kingdom", TableGame.ArmWrestling);
    public static readonly HostSpec[] All = [Shady, Shek];

    /// <summary>The bar keepers a host sits by: every character record named "...Barman..." or "...Bartender..." (the generic Barman, the
    /// Hub's, Squin's and Admag's own, the Swamp's, Waystations'...), found once in the game's data at load (FindBarmen); the generic
    /// Barman alone until then. A town's own barman is not always the generic one: Squin's is not (headless, 2026-11-22: no_barman).</summary>
    public static HashSet<string> Barmen { get; private set; } = [ShadyDealer.Barman];

    public static int FindBarmen(Meitou.Data.GameDatabase data)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { ShadyDealer.Barman };
        foreach (var r in data.OfType(Meitou.Data.Fcs.FcsRecordType.Character))
            if (r.Name.Contains("Barman", StringComparison.OrdinalIgnoreCase) || r.Name.Contains("Bartender", StringComparison.OrdinalIgnoreCase))
                set.Add(r.StringId);
        Barmen = set;
        return set.Count;
    }
}

/// <summary>The Shady Dealer's names, as the rest of the mod has always used them.</summary>
public static class ShadyDealer
{
    public const string Template = "10-PZACatCrawlers.mod";
    public const string Character = "9-PZACatCrawlers.mod";
    public const string Tag = "pza.catcrawlers:shady-dealer";
    /// <summary>The bars' barman (the generic "Barman": the Hub's "Bar blackmarket", Squin's bars): a host takes the free seat nearest to him.</summary>
    public const string Barman = "1187-gamedata.base";

    /// <summary>Is this character record a host of ours (the Swamper Casino Dealer's, the Shady Dealer's, the Shek challenger's)? Which game.</summary>
    public static TableGame GameOf(string record) =>
        string.Equals(record, Calls.DealerRecord, StringComparison.Ordinal) ? TableGame.CatCrawlers
        : Hosts.All.FirstOrDefault(h => string.Equals(record, h.Character, StringComparison.Ordinal)) is { } h ? h.Game
        : TableGame.None;
}

public sealed class TableHost(HostSpec spec, PopulationData? data, PopulationSystem? population, Meitou.Simulation.Production.ProductionSystem? seats = null)
{
    public HostSpec Spec => spec;
    string K => spec.Key;
    string N => spec.Name;
    /// <summary>The seat's row: our own task number (Meitou's mod codes start at 1000).</summary>
    public const int SitRow = 1717;
    long _nextSeat;
    bool _atBar;
    string _seat = "";
    const float Ring = 60;

    long _nextTry;
    bool _logged, _held;

    /// <summary>The world is ready (CrawlSystem.Ready): the hold on his platoon is not saved (docs/modding.md "Driving NPCs": find it by its
    /// tag and hold again after a load), so hold him again now if he is there; Keep spawns him otherwise.</summary>
    public void Ready(World world)
    {
        if (Find(world) is { } found) Hold(world, found.Id, "after the load");
    }

    void Hold(World world, int platoon, string why)
    {
        bool ok = PopulationSystem.HoldPlatoon(world, platoon, true);
        _held = ok;
        Counters.Add(ok ? $"{K}.held" : $"{K}.hold_refused");
        Log.Few($"{K}.hold", $"{N}: platoon {platoon} held ({why}; {(ok ? "ok" : "refused")})", 4);
    }

    /// <summary>The host's platoon, or null.</summary>
    public Meitou.Simulation.Factions.Platoon? Find(World world) =>
        world.Platoons.All.FirstOrDefault(p => !p.Dead && string.Equals(p.ModTag, spec.Tag, StringComparison.Ordinal));

    /// <summary>Spawns him when he is missing (serial phases only).</summary>
    public void Keep(World world)
    {
        // the main menu's background world (towns, no player: Meitou's NewGameLaunch.HandleMainMenu) is thrown away: no dealer there
        if (!world.Player.Exists) { Counters.Add($"{K}.no_player"); return; }
        if (world.Tick < _nextTry) return;
        _nextTry = world.Tick + (long)MathF.Ceiling(3600f / Math.Max(1e-3f, world.TickSeconds));   // about a game hour (or a real one) later
        if (Find(world) is { } found)
        {
            if (!_held) Hold(world, found.Id, "found by his tag");
            if (!_logged) { _logged = true; Log.Line($"{N}: at {spec.TownName} (found by his tag)"); }
            return;
        }
        if (data is null || population is null) { Log.Few($"{K}.none", $"{N}: OFF (no population system in this world)"); return; }
        var hub = data.Sites.FirstOrDefault(s => string.Equals(s.Record.Id, spec.Town, StringComparison.Ordinal));
        int faction = data.FactionIndex(spec.Faction);
        if (hub is null || faction < 0) { Log.Few($"{K}.nohub", $"{N}: not placed ({spec.TownName} {(hub is null ? "is not in this world" : "is there")}, {spec.FactionName} {(faction < 0 ? "missing" : "found")})"); return; }
        int id = Spawn(population, world, faction, spec.Template, hub.Index, new Vector2(hub.Position.X, hub.Position.Z), 0, Ring, 200);
        if (id < 0 || world.Platoons.Find(id) is not { } platoon)
        {
            Counters.Add($"{K}.spawn_failed");
            Log.Few($"{K}.fail", $"{N}: the spawn at {spec.TownName} failed (template {spec.Template} missing from the data, or no walkable point); trying again later");
            return;
        }
        platoon.ModTag = spec.Tag;
        Hold(world, id, "spawned");
        Counters.Add($"{K}.spawned");
        Log.Line($"{N}: placed at {spec.TownName} (platoon {id}, near {hub.Position.X:0}, {hub.Position.Z:0})");
    }

    // Meitou's SpawnPlatoon gained an optional maxMembers parameter in 0.22.2-nightly.20261124.3 (binary-incompatible with 0.21 / 0.22.1):
    // found by name and shape, filling any trailing optional parameters with their defaults, so one build runs on both.
    static readonly System.Reflection.MethodInfo? s_spawnPlatoon = typeof(PopulationSystem)
        .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .FirstOrDefault(m =>
        {
            if (m.Name != "SpawnPlatoon" || m.ReturnType != typeof(int)) return false;
            var ps = m.GetParameters();
            return ps.Length >= 8
                && ps[0].ParameterType == typeof(World) && ps[1].ParameterType == typeof(int)
                && ps[2].ParameterType == typeof(string) && ps[3].ParameterType == typeof(int)
                && ps[4].ParameterType == typeof(Vector2) && ps[5].ParameterType == typeof(float)
                && ps[6].ParameterType == typeof(float) && ps[7].ParameterType == typeof(long)
                && ps.Skip(8).All(p => p.HasDefaultValue);
        });

    static int Spawn(PopulationSystem population, World world, int faction, string template, int homeSite, Vector2 around, float minRadius, float maxRadius, long money)
    {
        if (s_spawnPlatoon is null) return -1;
        var ps = s_spawnPlatoon.GetParameters();
        var args = new object?[ps.Length];
        args[0] = world; args[1] = faction; args[2] = template; args[3] = homeSite;
        args[4] = around; args[5] = minRadius; args[6] = maxRadius; args[7] = money;
        for (int i = 8; i < ps.Length; i++) args[i] = ps[i].DefaultValue;
        return s_spawnPlatoon.Invoke(population, args) is int id ? id : -1;
    }

    // Meitou's seat API (ProductionSystem.CanSit / SitOn) arrived after 0.22.1: found by name, so the mod still loads on 0.21 / 0.22.1,
    // where he is walked to the barman instead (SendPlatoon, there since 0.21).
    static readonly System.Reflection.MethodInfo? s_canSit = typeof(Meitou.Simulation.Production.ProductionSystem).GetMethod("CanSit");
    static readonly System.Reflection.MethodInfo? s_sitOn = typeof(Meitou.Simulation.Production.ProductionSystem).GetMethod("SitOn");

    /// <summary>Brings him to the bar once he is loaded (every few seconds; serial phases): on a Meitou with the seat API, the free chair or
    /// stool nearest to the Hub's barman (he walks there and sits); otherwise his platoon is sent to stand by the barman. Not while a table
    /// with him is open.</summary>
    public void Tend(World world, bool tableOpen)
    {
        if (tableOpen || world.Tick < _nextSeat) return;
        _nextSeat = world.Tick + (long)MathF.Ceiling(5f / Math.Max(1e-3f, world.TickSeconds));
        var table = world.Characters;
        int me = -1;
        var bars = new List<int>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { } c || c.Medical is not { Dead: false, Incapacitated: false }) continue;
            if (me < 0 && string.Equals(c.RecordId, spec.Character, StringComparison.Ordinal)) me = s;
            else if (Hosts.Barmen.Contains(c.RecordId)) bars.Add(s);
        }
        if (me < 0 || bars.Count == 0) { Counters.Add(me < 0 ? $"{K}.tend.not_loaded" : $"{K}.tend.no_barman"); return; }
        // the barman nearest to him (a town with two bars: the one he is in; several towns loaded: his own)
        var mine = table.Next[me].Position;
        var at = bars.Select(b => table.Next[b].Position).OrderBy(p => Vector3.DistanceSquared(p, mine)).First();
        if (table.Next[me].Task == (byte)Meitou.Simulation.Movement.CharacterTask.Operate) return;   // seated (or on his way to the seat)
        if (TestNoSeat) Log.Few($"{K}.testnoseat", $"{N}: TEST switch PZA_CATCRAWLERS_TEST_NOSEAT is set: no seat, he goes by the standing route");
        if (!TestNoSeat && seats is not null && s_canSit is not null && s_sitOn is not null)
        {
            try
            {
                var task = (Meitou.Data.Gameplay.Ai.TaskType)SitRow;
                var source = Enum.ToObject(s_sitOn.GetParameters()[4].ParameterType, (byte)Meitou.Simulation.Ai.TaskSource.Goal);
                var free = world.Fixtures.SeatSpots
                    // not the barman's own corner: a stool closer than 35 is behind the counter, where the player cannot walk to talk
                    // (headless 2026-11-22, Squin on the 0.22.2 nightly: a seat 17 from the barman, the talk never opened)
                    .Where(sp => Vector3.Distance(sp.Position, at) is >= 35 and < 250)
                    .OrderBy(sp => Vector3.Distance(sp.Position, at))
                    .FirstOrDefault(sp => s_canSit.Invoke(seats, [world, me, sp.Id])?.ToString() == "Seated");
                if (free is not null && s_sitOn.Invoke(seats, [world, me, free.Id, task, source])?.ToString() == "Seated")
                {
                    if (_seat != free.Id) Log.Line($"{N}: takes a seat at the bar ({free.Record}, {Vector3.Distance(free.Position, at):0} from the barman)");
                    _seat = free.Id;
                    Counters.Add($"{K}.seated");
                    return;
                }
                Log.Few($"{K}.noseat", $"{N}: no free seat near the barman; he stands by the bar");
            }
            catch (Exception e) { Log.Few($"{K}.seatthrow", $"{N}: seating failed ({e.GetType().Name}: {e.Message}); he stands by the bar"); }
        }
        // stand by the bar (docs/modding.md "Driving NPCs" / "Moving from the old hooks"). Code-mod API 3 (Meitou nightly 2026-11-21 on)
        // removed CharacterCold.Engaged and AiMind.Engine: there he is ordered to the spot (World.GiveOrder, the player's move command for
        // one character); older games have no GiveOrder and walk him engaged. Which: by name at load, so one build runs on both, and the
        // old route reaches Engaged / AiMind.Engine by name too, so the source also compiles against an API 3 game (the daily watch rebuilds it).
        var spot = new Vector2(at.X + 18, at.Z + 10);
        float far = Vector2.Distance(new Vector2(table.Next[me].Position.X, table.Next[me].Position.Z), spot);
        if (s_giveOrder is not null) OrderToBar(world, me, new Vector3(spot.X, at.Y, spot.Y), far);
        else if (s_engaged is not null) EngagedWalk(world, me, spot, far);
        else Log.Few($"{K}.noroute", $"{N}: this Meitou has neither World.GiveOrder nor CharacterCold.Engaged; he stays where he is");
    }

    /// <summary>Headless tests only: the environment variable PZA_CATCRAWLERS_TEST_NOSEAT=1 (set for crawl_run.py start)
    /// skips the seats, so the standing route (GiveOrder, or the engaged walk) runs even where a seat is free. Logged when set.</summary>
    static readonly bool TestNoSeat = Environment.GetEnvironmentVariable("PZA_CATCRAWLERS_TEST_NOSEAT") == "1";

    /// <summary>World.GiveOrder (code-mod API 3), found by name; null on a game without it (the engaged walk then).</summary>
    static readonly System.Reflection.MethodInfo? s_giveOrder = typeof(World).GetMethod("GiveOrder",
        [typeof(CharacterId), typeof(Meitou.Data.Gameplay.Ai.TaskType), typeof(CharacterId?), typeof(string), typeof(Vector3), typeof(bool)]);

    /// <summary>API 3: the order holds him while he walks; once the queue is empty his package's chooser has him again, so a few seconds
    /// later this step orders him back if he wandered (and never while he is on his way or within 25 of the spot).</summary>
    void OrderToBar(World world, int me, Vector3 spot, float far)
    {
        var table = world.Characters;
        if (far < 25) { if (!_atBar) { _atBar = true; Log.Line($"{N}: stands at the bar"); } return; }
        _atBar = false;
        if (table.Next[me].Task == (byte)Meitou.Simulation.Movement.CharacterTask.GoTo) return;   // on his way
        var cold = table.Cold(me)!;
        if (cold.Fleeing || cold.InCombat || cold.IsImprisoned || !cold.CarriedBy.IsNone || !cold.Carrying.IsNone)
        {
            Counters.Add($"{K}.order.not_calm");
            return;
        }
        string answer;
        try
        {
            answer = s_giveOrder!.Invoke(world, [table.IdOf(me), Meitou.Data.Gameplay.Ai.TaskType.MoveCusOrdered, null, null, spot, false])?.ToString() ?? "null";
        }
        catch (System.Reflection.TargetInvocationException e) { answer = "threw " + e.InnerException?.GetType().Name; }
        // OrderCheck is a record ("OrderCheck { Verdict = Run, ... }"): the counter keeps only the verdict
        int v = answer.IndexOf("Verdict = ", StringComparison.Ordinal);
        string verdict = v < 0 ? answer : answer[(v + 10)..].Split(',', ' ', '}')[0];
        Counters.Add($"{K}.order." + verdict);
        Log.Few($"{K}.order", $"{N}: ordered to the bar ({far:0} away; {answer})", 5);
    }

    /// <summary>CharacterCold.Engaged and AiMind.Engine (gone in code-mod API 3), found by name; null where they are gone.</summary>
    static readonly System.Reflection.PropertyInfo? s_engaged = typeof(CharacterCold).GetProperty("Engaged", typeof(bool));
    static readonly System.Reflection.PropertyInfo? s_engine = typeof(Meitou.Simulation.Ai.AiMind).GetProperty("Engine", typeof(bool));

    static bool EngineMind(CharacterCold cold) => cold.Ai is { } mind && s_engine?.GetValue(mind) is true;

    /// <summary>Before API 3: engage him (keeps the AI off him) and start the engine's own walk. Only called where CharacterCold.Engaged exists.</summary>
    bool EngagedWalk(World world, int me, Vector2 spot, float far)
    {
        var table = world.Characters;
        var cold = table.Cold(me)!;
        if (s_engaged!.GetValue(cold) is not true)
        {
            // the AI's own (docs: engage only one it holds), or one no AI mind runs and nothing else holds (no fight, flight, order, carry)
            // or, the usual case: a member of our held platoon has an engine-run mind (AiMind.Engine: the population's squad, which the AI does
            // not run), and Holds is false for those whatever they do; on the engine's own routine (stand, wander, follow) he is ours to engage
            var task = (Meitou.Simulation.Movement.CharacterTask)table.Next[me].Task;
            bool routine = task is Meitou.Simulation.Movement.CharacterTask.Idle or Meitou.Simulation.Movement.CharacterTask.Wander
                or Meitou.Simulation.Movement.CharacterTask.Follow;
            bool calm = !cold.Fleeing && !cold.InCombat && !cold.IsImprisoned && cold.CarriedBy.IsNone && cold.Carrying.IsNone;
            bool free = Meitou.Simulation.Ai.AiSystem.Holds(table.Next[me], cold)
                || (calm && routine && (cold.Ai is null || EngineMind(cold)));
            if (!free)
            {
                Counters.Add($"{K}.tend.not_free.{task}.{(cold.Ai is null ? "nomind" : EngineMind(cold) ? "engine" : "ai")}");
                return false;
            }
            s_engaged.SetValue(cold, true);
            Counters.Add($"{K}.engaged");
        }
        if (far < 25) { if (!_atBar) { _atBar = true; Log.Line($"{N}: stands at the bar"); } return true; }
        _atBar = false;
        if (table.Next[me].Task == (byte)Meitou.Simulation.Movement.CharacterTask.GoTo) return true;   // on his way
        var started = Meitou.Simulation.Ai.TaskStart.Walk(world, me, spot, Meitou.Simulation.Movement.SpeedMode.Walk,
            (Meitou.Data.Gameplay.Ai.TaskType)SitRow, Meitou.Simulation.Ai.TaskSource.Goal);
        Counters.Add($"{K}.walk." + started);
        Log.Few($"{K}.walk", $"{N}: walks to the bar ({far:0} away; {started})", 5);
        return true;
    }
}

public enum TableGame { None, CatCrawlers, LootersRights, ArmWrestling }
