// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// The table's side in the world: a tick system (registered at SystemAnchor.End). Everything that touches the world happens here, in the
// serial steps, from queued commands (MeitouClient docs/modding.md "Determinism contract": no World access outside the tick, outside
// input through Enqueue). The cards themselves are not world state: the window deals them (CrawlWindow), and only their money comes here.
//   OpenTable      a right-click row on a dealer: checked here (a dealer record, awake, one of the selected people close), then published
//   CloseTable     the player stood up
//   TableEvent     cats to take or pay (clamped: the purse never goes below 0) and the hand's outcome for the ledger
//   TableState     the table as numbers, so the save holds the hand in play and the crawl
//   Say            a speech bubble over the dealer or the player (the calls are heard in the world, not only in the window)
// Each slow step it publishes the player's money and, while a table is open, checks the dealer is still awake and someone is still close.
// Saved (IModSaveData, the mod's sidecar; vanilla saves stay byte-exact): the ledger and the table. A save that does not add up loads
// as an empty table (CrawlCore) and never pays out.
using System.Numerics;
using Meitou.Modding;
using Meitou.Simulation;

namespace PZA.CatCrawlers;

/// <summary>TooFar: the line the dealer says when nobody stands close (chosen by the host, so the tick rolls nothing).</summary>
public sealed record OpenTable(CharacterId Dealer, CharacterId[] Players, string TooFar) : SimCommand;
/// <summary>Bye: the dealer's parting line ("" for none).</summary>
public sealed record CloseTable(string Bye) : SimCommand;
public sealed record TableEvent(long Seq, long Cats, Outcome Outcome, long Pot, int Chain) : SimCommand;
public sealed record TableState(long[] Table) : SimCommand;
/// <summary>The Hub's raid as numbers (Raid.Save), so the save holds a raid in play.</summary>
public sealed record RaidState(long[] Raid) : SimCommand;
public sealed record Say(CharacterId Who, string Text) : SimCommand;

/// <summary>What the window reads (immutable; swapped whole).</summary>
public sealed record Published(int World, long Money, long Applied, bool Open, CharacterId Dealer, CharacterId Player, int Opened, string Closed,
    long[]? Table, long[] Ledger, float Distance, string DealerName = "", string PlayerName = "", TableGame Game = TableGame.None, long[]? Raid = null,
    float PlayerStrength = 1, float PlayerToughness = 1, string PlayerRace = "", bool ArmHurt = false, long[]? Wrestle = null);

/// <summary>The tables' side in the world. <paramref name="data"/> / <paramref name="population"/>: the Shady Dealer's spawn at the Hub (null: no spawn).</summary>
public sealed partial class CrawlSystem(Meitou.Simulation.Population.PopulationData? data = null, Meitou.Simulation.Population.PopulationSystem? population = null,
    Meitou.Simulation.Production.ProductionSystem? seats = null, Meitou.Data.Gameplay.FactionRelations? relations = null)
    : ITickSystem, IModSaveData, IWorldReady, IDisposable
{
    /// <summary>How close one of the player's people must stand to the dealer (world units, the agent channel's "m"; two people 20 apart stand a few steps from each other).</summary>
    public const float Range = 50;
    /// <summary>They may drift this far before the table closes.</summary>
    public const float LeaveRange = 100;
    /// <summary>Shek Arm Wrestling's table (slice 1, in work): false until its window is built.</summary>
    public static bool WrestleTableReady = true;

    static int s_worlds;
    /// <summary>What the window reads when no world is live (the title screen): world 0, so the window resets and closes.</summary>
    static Published NoWorld() => new(0, 0, 0, false, CharacterId.None, CharacterId.None, 0, "", null, new Ledger().Save(), 0);
    static volatile Published s_pub = NoWorld();
    public static Published Pub => s_pub;
    /// <summary>The live world's CrawlSystem and its talk hand-off queue (WorldSlot: a load makes the new world live before the old one is
    /// disposed; a seat asked for in the old world is never taken in the new one).</summary>
    static readonly WorldSlot<CrawlSystem, (CharacterId Dealer, CharacterId Player)> s_slot = new();

    /// <summary>The dealer's own "yes" in conversation (DealerTalk, in DialogueSystem's serial input step): seat the player in this same step.</summary>
    public static void SeatFromTalk(CharacterId dealer, CharacterId player)
    {
        if (!s_slot.Enqueue((dealer, player))) Counters.Add("open.from_talk_no_world");
    }

    /// <summary>The world is ready (a new game or a load, before its first tick): this one is live; publish at once, so a load made while
    /// paused shows this world's table, not the last one's; and hold the Shady Dealer again (the hold is not saved).</summary>
    public void Ready(World world, bool fromSave)
    {
        int dropped = s_slot.Ready(this);
        if (dropped > 0) { Counters.Add("world.talk_dropped"); Log.Line($"world {_world}: {dropped} seat(s) asked for in the last world dropped"); }
        foreach (var h in _hosts) h.Ready(world);
        _dirty = true;
        Publish(world);
        Counters.Add(fromSave ? "world.ready_load" : "world.ready_new");
    }

    /// <summary>The world is disposed. On a load the new world is already live and nothing changes; on the way to the title screen this was
    /// the live world: the window is told there is none, so it closes and does not come back with this world's table.</summary>
    public void Dispose()
    {
        if (!s_slot.Disposed(this)) return;
        s_pub = NoWorld();
        Counters.Add("world.disposed_live");
    }

    readonly int _world = Interlocked.Increment(ref s_worlds);
    readonly Ledger _ledger = new();
    long[]? _table, _raid;
    TableGame _game;
    readonly Meitou.Simulation.Population.PopulationData? _data = data;
    readonly Meitou.Data.Gameplay.FactionRelations? _relations = relations;
    readonly TableHost[] _hosts = [.. Hosts.All.Select(h => new TableHost(h, data, population, seats))];
    long _applied;
    bool _open;
    CharacterId _dealer = CharacterId.None;
    CharacterId[] _players = [];
    int _opened;
    string _closed = "";
    bool _dirty = true;
    float _distance;

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        while (s_slot.TryTake(this, out var seat))
        {
            Counters.Add("open.from_talk");
            Open(world, new OpenTable(seat.Dealer, [seat.Player], "") { Tick = world.Tick });
            _dirty = true;
        }
        foreach (var c in commands)
        {
            switch (c)
            {
                case OpenTable o: Open(world, o); break;
                case CloseTable x:
                    if (_open)
                    {
                        if (x.Bye.Length > 0 && Awake(world, _dealer, out _, out _)) world.Bark(_dealer, x.Bye);
                        Close("left");
                    }
                    break;
                case TableEvent e: Apply(world, e); break;
                case TableState s: _table = s.Table; break;
                case RaidState s: _raid = s.Raid; break;
                case WrestleResult r: Settle(world, r); break;
                case Say s:
                    if (s.Text.Length > 0 && world.Characters.TryResolveNext(s.Who, out _)) world.Bark(s.Who, s.Text.Length > 160 ? s.Text[..160] : s.Text);
                    break;
                default: continue;
            }
            _dirty = true;
        }
        Publish(world);
    }

    public void SlowWorld(World world)
    {
        foreach (var h in _hosts)
        {
            h.Keep(world);
            h.Tend(world, _open && _game == h.Spec.Game);
        }
        if (_open)
        {
            if (!Awake(world, _dealer, out var at, out string why)) Close("dealer_" + why);
            else
            {
                _distance = Nearest(world, at, out _);
                if (_distance > LeaveRange) Close("walked_away");
            }
            _dirty = true;
        }
        Publish(world);
    }

    void Publish(World world)
    {
        long money = world.Player.Money;
        var p = s_pub;
        if (!_dirty && p.World == _world && p.Money == money) return;
        _dirty = false;
        s_pub = new Published(_world, money, _applied, _open, _dealer, _players.Length > 0 ? _players[0] : CharacterId.None, _opened, _closed, _table,
            _ledger.Save(), _distance, world.Characters.NameOf(_dealer).Trim(), _players.Length > 0 ? world.Characters.NameOf(_players[0]).Trim() : "", _game, _raid)
        {
            Wrestle = _wrestle.Save(),
        };
        // the one at the table (arm wrestling: his Strength / Toughness for the bout, his race for the arm pictures, a hurt arm)
        if (_players.Length > 0 && world.Characters.TryResolveNext(_players[0], out int ps) && world.Characters.Cold(ps) is { } pc)
        {
            var arm = RightArm(pc);
            s_pub = s_pub with
            {
                PlayerStrength = pc.Stats?.Strength ?? 1,
                PlayerToughness = pc.Stats?.Toughness ?? 1,
                PlayerRace = pc.Race?.Name ?? "",
                ArmHurt = arm is not null && arm.Flesh - arm.Stun < arm.Flesh * 0.75f,
            };
        }
    }

    void Open(World world, OpenTable o)
    {
        Counters.Add("open.asked");
        var table = world.Characters;
        if (o.Dealer.IsNone)
        {
            // the F10 row: the nearest dealer to the first of the selected people
            float best = float.MaxValue;
            var found = CharacterId.None;
            if (o.Players.Length > 0 && table.TryResolveNext(o.Players[0], out int me))
            {
                var here = table.Next[me].Position;
                for (int s = 0; s < table.HighWater; s++)
                {
                    if (table.Cold(s) is not { } c || c.IsPlayer || ShadyDealer.GameOf(c.RecordId) == TableGame.None) continue;
                    float dd = Vector3.Distance(table.Next[s].Position, here);
                    if (dd < best) (best, found) = (dd, table.IdOf(s));
                }
            }
            if (found.IsNone) { Counters.Add("open.refused.no_dealer"); Log.Few("open.none", "table refused: no dealer (Swamper Casino Dealer, Shady Dealer) in the loaded world"); return; }
            o = o with { Dealer = found };
        }
        if (!Awake(world, o.Dealer, out var at, out string why) || table.Cold(Slot(world, o.Dealer)) is not { } cold
            || ShadyDealer.GameOf(cold.RecordId) == TableGame.None || cold.IsPlayer)
        {
            Counters.Add("open.refused.dealer");
            Log.Few("open.dealer", $"table refused: not a dealer who can deal ({why})");
            return;
        }
        _players = [.. o.Players.Where(p => table.TryResolveNext(p, out int s) && table.Cold(s) is { IsPlayer: true })];
        float d = Nearest(world, at, out var who);
        if (_players.Length == 0 || d > Range)
        {
            Counters.Add("open.refused.far");
            Log.Few("open.far", $"table refused: nobody of yours within {Range:0} of the dealer (nearest {d:0})");
            if (o.TooFar.Length > 0) world.Bark(o.Dealer, o.TooFar);
            return;
        }
        if (ShadyDealer.GameOf(table.Cold(Slot(world, o.Dealer))!.RecordId) == TableGame.ArmWrestling && !WrestleTableReady)
        {
            // slice 1 in work: the arm-wrestling window is not built yet; never show the card table for the Shek
            Counters.Add("open.refused.wrestle_not_ready");
            Log.Few("open.wrestle", "table refused: arm wrestling is not playable in this build yet");
            return;
        }
        if (ShadyDealer.GameOf(table.Cold(Slot(world, o.Dealer))!.RecordId) == TableGame.ArmWrestling && !ArmReady(world, who))
        {
            // a stump, a crushed right arm or one that is down: the Shek refuses, and no stake is taken (a prosthetic is welcome)
            Counters.Add("open.refused.arm");
            Log.Line("table refused: the right arm of the one who would sit does not work");
            world.Bark(o.Dealer, Strings.T("wrestle.no_arm"));
            return;
        }
        // the one who sits is the nearest; they are first in the list (their face, their speech bubbles)
        _players = [who, .. _players.Where(p => p != who)];
        _open = true;
        _dealer = o.Dealer;
        _game = ShadyDealer.GameOf(table.Cold(Slot(world, o.Dealer))!.RecordId);
        _distance = d;
        _opened++;
        _closed = "";
        Counters.Add("open.ok");
        Log.Line($"table open: {_game}, dealer slot {o.Dealer.Slot}, player slot {who.Slot}, distance {d:0.0}");
    }

    void Close(string why)
    {
        _open = false;
        _closed = why;
        Counters.Add("close." + why);
        Log.Line($"table closed: {why}");
    }

    void Apply(World world, TableEvent e)
    {
        if (e.Seq <= _applied) { Counters.Add("event.stale"); return; }   // a command queued twice, or from before a load
        _applied = e.Seq;
        long before = world.Player.Money;
        long after = Math.Max(0, before + e.Cats);
        world.Player.Money = after;
        if (after != before + e.Cats) { Counters.Add("event.clamped"); Log.Few("clamp", $"purse clamped at 0 (had {before}, asked {e.Cats})"); }
        switch (e.Outcome)
        {
            case Outcome.Dealt: _ledger.Hands++; _ledger.Staked += before - after; break;
            case Outcome.Lick: _ledger.Licks++; _ledger.BestChain = Math.Max(_ledger.BestChain, e.Chain); break;
            case Outcome.Girl: _ledger.Licks++; _ledger.Girls++; _ledger.BestChain = Math.Max(_ledger.BestChain, e.Chain); break;
            case Outcome.Dryout or Outcome.Tie: _ledger.Dryouts++; break;
            case Outcome.Took: _ledger.Paid += after - before; _ledger.BiggestPot = Math.Max(_ledger.BiggestPot, e.Pot); break;
            case Outcome.RaidAnte: _ledger.Raids++; _ledger.Staked += before - after; break;
            case Outcome.RaidCarried: _ledger.Paid += after - before; _ledger.RaidBest = Math.Max(_ledger.RaidBest, after - before); break;
            case Outcome.RaidBust: _ledger.RaidBusts++; break;
            case Outcome.WrestleStake: _ledger.Staked += before - after; break;
            case Outcome.WrestleWon or Outcome.WrestleRefund: _ledger.Paid += after - before; break;
        }
        Counters.Add("event." + e.Outcome);
    }

    static int Slot(World world, CharacterId id) => world.Characters.TryResolveNext(id, out int slot) ? slot : -1;

    /// <summary>The character exists, is not dead, not out cold, not carried.</summary>
    static bool Awake(World world, CharacterId id, out Vector3 at, out string why)
    {
        at = default;
        var table = world.Characters;
        if (id.IsNone || !table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) { why = "gone"; return false; }
        at = table.Next[slot].Position;
        if (cold.Medical is not { Dead: false, Incapacitated: false }) { why = "down"; return false; }
        if (!cold.CarriedBy.IsNone) { why = "carried"; return false; }
        why = "";
        return true;
    }

    /// <summary>The distance from <paramref name="at"/> to the nearest of the table's players who is awake (float.MaxValue: none).</summary>
    float Nearest(World world, Vector3 at, out CharacterId who)
    {
        float best = float.MaxValue;
        who = CharacterId.None;
        foreach (var p in _players)
        {
            if (!Awake(world, p, out var pos, out _)) continue;
            float d = Vector3.Distance(pos, at);
            if (d < best) (best, who) = (d, p);
        }
        return best;
    }

    #region Save

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(3);   // format 3 (2026-11-22): ledger, table, raid, arm wrestling's record; 2 had no wrestling, 1 no raid
        var ledger = _ledger.Save();
        w.Write(ledger.Length);
        foreach (long v in ledger) w.Write(v);
        var table = _table ?? [];
        w.Write(table.Length);
        foreach (long v in table) w.Write(v);
        var raid = _raid ?? [];
        w.Write(raid.Length);
        foreach (long v in raid) w.Write(v);
        var wrestle = _wrestle.Save();
        w.Write(wrestle.Length);
        foreach (long v in wrestle) w.Write(v);
    }

    public void Read(Stream stream)
    {
        var (ledger, table, raid, wrestle) = Parse(stream);
        if (wrestle is not null && !_wrestle.Load(wrestle)) Log.Line("save: arm wrestling's record did not load (starts empty)");
        if (ledger is null || !_ledger.Load(ledger)) Log.Line("save: the ledger did not load (starts empty)");
        _table = table;
        _raid = raid;
        _dirty = true;
        Log.Line($"save: ledger {_ledger.Hands} hands, net {_ledger.Net}; table {(table is null ? "none" : "kept")}, raid {(raid is null ? "none" : "kept")}; "
            + $"arm wrestling {_wrestle.Won} won / {_wrestle.Lost} lost, respect +{_wrestle.RespectGiven}");
    }

    /// <summary>The save's number lists; nulls for anything unreadable (never throws: a broken sidecar must not stop a load).</summary>
    public static (long[]? Ledger, long[]? Table, long[]? Raid, long[]? Wrestle) Parse(Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int format = r.ReadInt32();
            if (format is not (1 or 2 or 3)) return (null, null, null, null);
            long[]? List(int max)
            {
                int n = r.ReadInt32();
                if (n < 0 || n > max) throw new InvalidDataException();
                var a = new long[n];
                for (int i = 0; i < n; i++) a[i] = r.ReadInt64();
                return a;
            }
            var ledger = List(64);
            var table = List(64);
            var raid = format >= 2 ? List(96) : null;
            var wrestle = format >= 3 ? List(8 + 3 * WrestleRecord.MaxCharacters) : null;
            return (ledger, table is { Length: > 0 } ? table : null, raid is { Length: > 0 } ? raid : null, wrestle is { Length: > 0 } ? wrestle : null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            return (null, null, null, null);
        }
    }

    #endregion
}
