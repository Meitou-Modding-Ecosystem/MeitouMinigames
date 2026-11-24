// PZA's Cat Crawlers for Meitou: fuzz target for the world hand-over (WorldSlot). Author: PZA. Licence: GPLv3 or later. Never shipped.
//   world.slot  random runs of what Meitou does across worlds: new games, loads (the new world is ready BEFORE the old one is disposed,
//               MeitouClient Host/WorldSession.cs), quits to the title screen, a world disposed twice, "Deal me in." from the dialogue step
//               at any moment, and takes by live and stale worlds; checked against a plain model: the live world is the newest ready one
//               not yet disposed, a late Dispose of an old world never unseats the new one, a seat asked for in one world is never taken in
//               another, and nothing queued is lost while its world stays live
using PZA.CatCrawlers;
using PZA.MeitouUi.Fuzz;

static class WorldFuzz
{
    public static PzaFuzz.TargetDef[] Targets =>
    [
        new("world.slot", 6000, Slot, fz =>
        {
            foreach (var rule in new[] { "new_game", "load", "late_dispose", "title", "stale_take", "taken", "dropped_on_ready", "no_world", "double_dispose" })
                fz.Expect(fz.Counted(rule) > 0, $"rule '{rule}' never fired (a rule that never fires is a failed test)");
        }),
    ];

    sealed class Owner(int n) { public readonly int N = n; public override string ToString() => "world " + N; }

    static void Slot(PzaFuzz fz)
    {
        var slot = new WorldSlot<Owner, (int World, int Seat)>();
        var worlds = new List<Owner>();
        Owner? live = null;                       // the model's live world
        var queued = new Queue<(int World, int Seat)>();   // the model's queue (only the live world's items)
        var undisposed = new List<Owner>();
        int seat = 0;
        int steps = fz.Int(4, 59);
        for (int i = 0; i < steps; i++)
        {
            switch (fz.Below(7))
            {
                case 0:   // a new game, or a load: the new world is ready, then the old one is disposed
                {
                    var w = new Owner(worlds.Count + 1);
                    worlds.Add(w);
                    undisposed.Add(w);
                    int dropped = slot.Ready(w);
                    fz.Expect(dropped == queued.Count, $"Ready dropped {dropped}, the model had {queued.Count} queued");
                    if (dropped > 0) fz.Count("dropped_on_ready");
                    queued.Clear();
                    var old = live;
                    live = w;
                    fz.Count(old is null ? "new_game" : "load");
                    if (old is not null && fz.Below(3) > 0)
                    {
                        fz.Expect(!slot.Disposed(old), $"{old} disposed after {w} was ready claimed to be live");
                        undisposed.Remove(old);
                        fz.Count("late_dispose");
                    }
                    break;
                }
                case 1:   // the live world goes to the title screen
                    if (live is null) break;
                    fz.Expect(slot.Disposed(live), $"{live} was live, but its Dispose said it was not");
                    undisposed.Remove(live);
                    live = null;
                    queued.Clear();
                    fz.Count("title");
                    break;
                case 2:   // an old world disposed late, or a world disposed twice
                {
                    var w = worlds.Count > 0 ? worlds[fz.Below(worlds.Count)] : null;
                    if (w is null || ReferenceEquals(w, live)) break;
                    fz.Expect(!slot.Disposed(w), $"{w} is not live, but its Dispose said it was");
                    fz.Count(undisposed.Remove(w) ? "late_dispose" : "double_dispose");
                    break;
                }
                case 3:
                case 4:   // "Deal me in." in the dialogue step
                {
                    var item = (live?.N ?? 0, ++seat);
                    bool ok = slot.Enqueue(item);
                    fz.Expect(ok == (live is not null), $"Enqueue answered {ok} with {(live is null ? "no world" : live)} live");
                    if (ok) queued.Enqueue(item); else fz.Count("no_world");
                    break;
                }
                default:  // a world's Inputs step takes (the live one, or a stale one that still ticks once)
                {
                    var w = fz.Below(4) == 0 && worlds.Count > 0 ? worlds[fz.Below(worlds.Count)] : live;
                    if (w is null) break;
                    bool got = slot.TryTake(w, out var item);
                    if (!ReferenceEquals(w, live))
                    {
                        fz.Expect(!got, $"{w} (not live) took seat {item.Seat} asked for in world {item.World}");
                        fz.Count("stale_take");
                        break;
                    }
                    fz.Expect(got == queued.Count > 0, $"{w} took {(got ? "a seat" : "nothing")}, the model had {queued.Count} queued");
                    if (!got) break;
                    var want = queued.Dequeue();
                    fz.Expect(item == want, $"{w} took seat {item.Seat} of world {item.World}, the model expected seat {want.Seat} of world {want.World}");
                    fz.Expect(item.World == w.N, $"{w} took a seat asked for in world {item.World}");
                    fz.Count("taken");
                    break;
                }
            }
            fz.Expect(ReferenceEquals(slot.Current, live), $"the slot's live world is {slot.Current?.ToString() ?? "none"}, the model's {live?.ToString() ?? "none"}");
        }
    }
}
