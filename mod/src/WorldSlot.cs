// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// Which world's CrawlSystem is the live one, and the hand-off queue that belongs to it (no game types: fuzzed in tests/fuzz "world.slot").
// Meitou's order on a load (MeitouClient Host/WorldSession.cs, 0.21.0 on): the NEW world's systems read their save and are told Ready
// BEFORE the OLD world's systems are disposed. So: Ready makes its owner current and drops what the old world left queued; a Dispose
// only clears anything when its owner is still current (quitting to the title screen), never when a newer world is already ready.
// Items queued from outside a world's tick (DealerTalk's "Deal me in." in the dialogue step) are taken only by the current owner.
namespace PZA.CatCrawlers;

public sealed class WorldSlot<TOwner, TItem> where TOwner : class
{
    readonly object _gate = new();
    readonly Queue<TItem> _queue = new();
    TOwner? _current;

    public TOwner? Current { get { lock (_gate) return _current; } }

    /// <summary>The owner's world is ready (a new game or a load): it is the live one; anything still queued was the old world's and is dropped.
    /// Answers how many were dropped (logged by the caller).</summary>
    public int Ready(TOwner owner)
    {
        lock (_gate)
        {
            _current = owner;
            int dropped = _queue.Count;
            _queue.Clear();
            return dropped;
        }
    }

    /// <summary>The owner's world is being disposed. True when it was still the live one (nothing newer is ready: the title screen or the
    /// game closing): the caller clears what it published. False when a newer world is already ready: nothing changes.</summary>
    public bool Disposed(TOwner owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, owner)) return false;
            _current = null;
            _queue.Clear();
            return true;
        }
    }

    /// <summary>Queue an item for the live world (dropped when no world is live).</summary>
    public bool Enqueue(TItem item)
    {
        lock (_gate)
        {
            if (_current is null) return false;
            _queue.Enqueue(item);
            return true;
        }
    }

    /// <summary>The next queued item, for the live owner only.</summary>
    public bool TryTake(TOwner owner, out TItem item)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, owner) && _queue.Count > 0) { item = _queue.Dequeue(); return true; }
            item = default!;
            return false;
        }
    }
}
