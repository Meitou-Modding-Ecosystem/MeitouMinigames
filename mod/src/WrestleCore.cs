// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// Shek Arm Wrestling (slice 1, 2026-11-22): a real-time grip contest at a Shek bar.
// No game types here: the fuzz harness and the balance runner (tools/balance-wrestle) use it alone.
//
// A bout: Bet (the stake rows) -> Countdown (1.5 s; leaving refunds the stake) -> Bout (until a pin, hard cap 45 s) -> Pinned.
//   Balance in [-1, +1]: -1 the player is pinned (lost), +1 the Shek is pinned (won). The arm pictures show frame (Balance + 1) * 16.
//   Grit in [0, 1] for each side: the stamina a push spends; a tired arm pushes weaker.
//   PUSH (a click / pad A): an impulse scaled by the player's Strength and grit; at most 8 a second count (no autoclicker gain).
//   BRACE (held): the Shek's push is cut by a Toughness-scaled share and grit comes back faster; no pushing while braced.
//   SEQUENCE: every 3.5-5 s, and when the Shek telegraphs a surge, three prompts (left / up / right) light up; all three in order within
//     the window give a Grit Surge (a big push, grit back, and a surge in progress is broken: the Shek is winded). A wrong prompt is a
//     slip (0.4 s without pushing, grit lost, ground given).
//   The Shek: a steady press, then a telegraphed SURGE (0.8 s warning, 2 s of a hard push), then WINDED (1.5 s, weak, recovering).
// Stat maths (plan section 3): rating R = 0.65 S + 0.35 T; push force F = 0.30 + 0.014 S; grit drain scale 1 / (0.65 + 0.010 T);
// brace mitigation clamp(0.35 + 0.005 T, 0.35, 0.75). The constants below are tuned by tools/balance-wrestle to the plan's win-rate
// envelope (its report: tools/balance-wrestle/solution-*.txt).
namespace PZA.CatCrawlers;

public enum WrestlePhase { Bet, Countdown, Bout, Pinned }
public enum ShekMode { Steady, Telegraph, Surge, Winded }
public enum WrestleEnd { None, Won, Lost, Refunded, Forfeit }
public enum Prompt { Left = 0, Up = 1, Right = 2 }

/// <summary>Strength and Toughness as the bout uses them (each clamped to 1..100; anything not a number counts as 1).</summary>
public readonly record struct WrestleStats(float Strength, float Toughness)
{
    static float C(float v) => float.IsFinite(v) ? Math.Clamp(v, 1f, 100f) : 1f;
    public float S => C(Strength);
    public float T => C(Toughness);
    public float Rating => 0.65f * S + 0.35f * T;
    public float Force => 0.30f + 0.014f * S;
    public float DrainScale => 1f / (0.65f + 0.010f * T);
    public float Mitigation => Math.Clamp(0.35f + 0.005f * T, 0.35f, 0.75f);
}

/// <summary>The challengers (slice 1: Squin's two tiers; the third is the plan's Admag champion, not spawned yet).</summary>
public static class WrestleTiers
{
    public const int Count = 3;
    public static WrestleStats Stats(int tier) => tier switch { 1 => new(22, 22), 2 => new(48, 48), 3 => new(74, 72), _ => new(22, 22) };
    public static readonly int[][] Stakes = [[100, 200, 350], [250, 500, 1000], [500, 1000, 2000]];
    public static int[] StakesOf(int tier) => Stakes[Math.Clamp(tier, 1, Count) - 1];
    /// <summary>Shek Kingdom respect for the first win over each tier (plan section 4).</summary>
    public static int FirstWinRespect(int tier) => tier switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };
}

/// <summary>The tuning (balance runner: tools/balance-wrestle).</summary>
public static class WrestleTuning
{
    public const float Step = 1f / 60f;
    public const float CountdownSeconds = 1.5f, MaxSeconds = 45f, PinnedSeconds = 2f;
    public const int TapCap = 8;                       // taps counted per rolling second
    public const float SlipSeconds = 0.4f;
    public const float TelegraphSeconds = 0.8f, SurgeSeconds = 2f, WindedSeconds = 1.5f;
    public const float SteadyMin = 2.5f, SteadyMax = 4.5f;
    public const float SeqEveryMin = 3.5f, SeqEveryMax = 5f, SeqWindow = 2.5f;

    public static float TapImpulse = 0.05f, TapCost = 0.022f;
    public static float RegenRest = 0.10f, RegenBrace = 0.28f;
    public static float ShekPush = 0.20f, SurgeMult = 2.4f, WindedMult = 0.30f;
    public static float ShekDrainSteady = 0.03f, ShekDrainSurge = 0.20f, ShekRegenWinded = 0.30f;
    public static float SeqBurst = 0.16f, SeqGrit = 0.25f;
    public static float SlipGrit = 0.08f, SlipGround = 0.04f;
    /// <summary>Push grows as force to this power (a stat gap outweighs good play, plan's envelope).</summary>
    public static float ForcePower = 2f;
    /// <summary>The Shek's form for one bout: his push times e^(this x a normal draw), clamped to 0.4..2.5 (a good or a bad day at the
    /// table). Sized from the plan's envelope: 15 rating points move the odds about one standard deviation.</summary>
    public static float FormSigma = 0.47f;
    public static float Pow(float force) => MathF.Pow(force, ForcePower);

    /// <summary>A tired arm: push scales from 35% (empty) to 100% (fresh).</summary>
    public static float GritFactor(float grit) => 0.35f + 0.65f * Math.Clamp(grit, 0f, 1f);
}

/// <summary>One bout. The window calls Tap / Press / Brace as the player acts and Advance once a frame with the real time passed.</summary>
public sealed class WrestleBout
{
    public WrestlePhase Phase { get; private set; } = WrestlePhase.Bet;
    public WrestleEnd End { get; private set; }
    public int Tier { get; private set; } = 1;
    public long Stake { get; private set; }
    public WrestleStats Player { get; private set; }
    public WrestleStats Shek { get; private set; }
    public float Balance { get; private set; }
    public float PlayerGrit { get; private set; } = 1f;
    public float ShekGrit { get; private set; } = 1f;
    public float Clock { get; private set; }            // bout seconds (not the countdown)
    public float CountdownLeft { get; private set; }
    public float PinnedLeft { get; private set; }
    public ShekMode Mode { get; private set; } = ShekMode.Steady;
    public float ModeLeft { get; private set; }
    public bool Braced { get; set; }
    public bool SequenceOpen { get; private set; }
    public float SequenceLeft { get; private set; }
    public int SequenceAt { get; private set; }
    readonly Prompt[] _seq = new Prompt[3];
    public IReadOnlyList<Prompt> Sequence => _seq;
    public float SlipLeft { get; private set; }
    /// <summary>The Shek's form this bout (see FormSigma).</summary>
    public float Form { get; private set; } = 1f;
    float _nextSeq;
    float _accum;
    float _norm = 1f;
    ulong _rng = 1;
    readonly Queue<float> _taps = new();

    // what the bout did (the window's shake and lines, the ledger, the fuzz harness's "every rule fires")
    public int Taps { get; private set; }
    public int TapsCounted { get; private set; }
    public int Surges { get; private set; }
    public int SurgesBroken { get; private set; }
    public int SequencesHit { get; private set; }
    public int Slips { get; private set; }
    public float BracedSeconds { get; private set; }
    /// <summary>Set for one frame when a Grit Surge lands or a slip happens (the window's shake and flash).</summary>
    public bool JustSurged, JustSlipped;

    /// <summary>Frame of the arm pictures, 0 (the player pinned) .. 32 (the Shek pinned).</summary>
    public int Frame => Math.Clamp((int)MathF.Round((Balance + 1f) * 16f), 0, 32);

    /// <summary>Lock arms: the countdown starts (the stake is taken by the caller when this answers true).</summary>
    public bool Start(int tier, long stake, WrestleStats player, ulong seed)
    {
        if (Phase is WrestlePhase.Countdown or WrestlePhase.Bout || stake <= 0 || tier < 1 || tier > WrestleTiers.Count) return false;
        Tier = tier;
        Stake = stake;
        Player = player;
        Shek = WrestleTiers.Stats(tier);
        Balance = 0;
        PlayerGrit = ShekGrit = 1f;
        Clock = 0;
        CountdownLeft = WrestleTuning.CountdownSeconds;
        PinnedLeft = 0;
        Mode = ShekMode.Steady;
        _rng = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        // Box-Muller from the bout's own generator (deterministic for a seed)
        double u1 = Math.Max(1e-9, Between(0, 1)), u2 = Between(0, 1);
        Form = Math.Clamp(MathF.Exp(WrestleTuning.FormSigma * (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2))), 0.4f, 2.5f);
        // the same pace at every tier: both sides' pushes over the mean of the two
        _norm = 2f / (WrestleTuning.Pow(player.Force) + WrestleTuning.Pow(Shek.Force));
        ModeLeft = Between(WrestleTuning.SteadyMin, WrestleTuning.SteadyMax);
        _nextSeq = Between(WrestleTuning.SeqEveryMin, WrestleTuning.SeqEveryMax);
        SequenceOpen = false;
        SlipLeft = 0;
        Braced = false;
        _accum = 0;
        _taps.Clear();
        Taps = TapsCounted = Surges = SurgesBroken = SequencesHit = Slips = 0;
        BracedSeconds = 0;
        End = WrestleEnd.None;
        Phase = WrestlePhase.Countdown;
        return true;
    }

    /// <summary>Leave: during the countdown the stake comes back (Refunded); during the bout it is lost (Forfeit).</summary>
    public WrestleEnd Leave()
    {
        if (Phase == WrestlePhase.Countdown) return Finish(WrestleEnd.Refunded);
        if (Phase == WrestlePhase.Bout) return Finish(WrestleEnd.Forfeit);
        return WrestleEnd.None;
    }

    /// <summary>A PUSH. True when it counted (in the bout, not braced, not slipping, within the 8 a second cap).</summary>
    public bool Tap()
    {
        if (Phase != WrestlePhase.Bout) return false;
        Taps++;
        if (Braced || SlipLeft > 0) return false;
        while (_taps.Count > 0 && Clock - _taps.Peek() >= 1f) _taps.Dequeue();
        if (_taps.Count >= WrestleTuning.TapCap) return false;
        _taps.Enqueue(Clock);
        TapsCounted++;
        Balance += WrestleTuning.TapImpulse * WrestleTuning.Pow(Player.Force) * _norm * WrestleTuning.GritFactor(PlayerGrit);
        PlayerGrit = Math.Max(0f, PlayerGrit - WrestleTuning.TapCost * Player.DrainScale);
        CheckPin();
        return true;
    }

    /// <summary>A sequence prompt. True when it was the right one.</summary>
    public bool Press(Prompt p)
    {
        if (Phase != WrestlePhase.Bout || !SequenceOpen || SlipLeft > 0) return false;
        if (p != _seq[SequenceAt])
        {
            SequenceOpen = false;
            SlipLeft = WrestleTuning.SlipSeconds;
            PlayerGrit = Math.Max(0f, PlayerGrit - WrestleTuning.SlipGrit);
            Balance -= WrestleTuning.SlipGround;
            Slips++;
            JustSlipped = true;
            CheckPin();
            return false;
        }
        if (++SequenceAt < _seq.Length) return true;
        SequenceOpen = false;
        SequencesHit++;
        JustSurged = true;
        Balance += WrestleTuning.SeqBurst * WrestleTuning.Pow(Player.Force) * _norm;
        PlayerGrit = Math.Min(1f, PlayerGrit + WrestleTuning.SeqGrit);
        if (Mode is ShekMode.Telegraph or ShekMode.Surge)
        {
            SurgesBroken++;
            SetMode(ShekMode.Winded);
        }
        CheckPin();
        return true;
    }

    /// <summary>Real time passed (seconds; clamped to a quarter second a frame so a stall never jumps the bout). Answers the end once.</summary>
    public WrestleEnd Advance(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0) return WrestleEnd.None;
        _accum += Math.Min(seconds, 0.25f);
        var end = WrestleEnd.None;
        while (_accum >= WrestleTuning.Step && end == WrestleEnd.None)
        {
            _accum -= WrestleTuning.Step;
            end = StepOnce(WrestleTuning.Step);
        }
        return end;
    }

    WrestleEnd StepOnce(float dt)
    {
        switch (Phase)
        {
            case WrestlePhase.Countdown:
                CountdownLeft -= dt;
                if (CountdownLeft <= 0) { CountdownLeft = 0; Phase = WrestlePhase.Bout; }
                return WrestleEnd.None;
            case WrestlePhase.Pinned:
                PinnedLeft = Math.Max(0, PinnedLeft - dt);
                return WrestleEnd.None;
            case WrestlePhase.Bout:
                break;
            default:
                return WrestleEnd.None;
        }
        Clock += dt;
        if (SlipLeft > 0) SlipLeft = Math.Max(0, SlipLeft - dt);
        if (Braced) BracedSeconds += dt;

        // the Shek's rhythm
        ModeLeft -= dt;
        if (ModeLeft <= 0)
            SetMode(Mode switch
            {
                ShekMode.Steady => ShekMode.Telegraph,
                ShekMode.Telegraph => ShekMode.Surge,
                ShekMode.Surge => ShekMode.Winded,
                _ => ShekMode.Steady,
            });
        float mult = Mode switch { ShekMode.Surge => WrestleTuning.SurgeMult, ShekMode.Winded => WrestleTuning.WindedMult, _ => 1f };
        float push = WrestleTuning.ShekPush * WrestleTuning.Pow(Shek.Force) * _norm * Form * WrestleTuning.GritFactor(ShekGrit) * mult * dt;
        if (Braced) push *= 1f - Player.Mitigation;
        Balance -= push;
        ShekGrit = Mode switch
        {
            ShekMode.Surge => Math.Max(0f, ShekGrit - WrestleTuning.ShekDrainSurge * Shek.DrainScale * dt),
            ShekMode.Winded => Math.Min(1f, ShekGrit + WrestleTuning.ShekRegenWinded / Shek.DrainScale * dt),
            _ => Math.Max(0f, ShekGrit - WrestleTuning.ShekDrainSteady * Shek.DrainScale * dt),
        };

        // the player's grit comes back while not pushing hard (faster braced)
        float regen = (Braced ? WrestleTuning.RegenBrace : WrestleTuning.RegenRest) / Player.DrainScale;
        PlayerGrit = Math.Min(1f, PlayerGrit + regen * dt);

        // sequences
        if (SequenceOpen)
        {
            SequenceLeft -= dt;
            if (SequenceLeft <= 0) SequenceOpen = false;   // let pass: no penalty
        }
        else
        {
            _nextSeq -= dt;
            if (_nextSeq <= 0) OpenSequence();
        }

        var pin = CheckPin();
        if (pin != WrestleEnd.None) return pin;
        if (Clock >= WrestleTuning.MaxSeconds) return Finish(Balance > 0 ? WrestleEnd.Won : WrestleEnd.Lost);
        return WrestleEnd.None;
    }

    void SetMode(ShekMode m)
    {
        Mode = m;
        ModeLeft = m switch
        {
            ShekMode.Telegraph => WrestleTuning.TelegraphSeconds,
            ShekMode.Surge => WrestleTuning.SurgeSeconds,
            ShekMode.Winded => WrestleTuning.WindedSeconds,
            _ => Between(WrestleTuning.SteadyMin, WrestleTuning.SteadyMax),
        };
        if (m == ShekMode.Surge) Surges++;
        if (m == ShekMode.Telegraph && !SequenceOpen) OpenSequence();   // a counter chance at every telegraph
    }

    void OpenSequence()
    {
        for (int i = 0; i < _seq.Length; i++) _seq[i] = (Prompt)(Next() % 3);
        SequenceAt = 0;
        SequenceOpen = true;
        SequenceLeft = WrestleTuning.SeqWindow;
        _nextSeq = Between(WrestleTuning.SeqEveryMin, WrestleTuning.SeqEveryMax);
    }

    WrestleEnd CheckPin()
    {
        if (Phase != WrestlePhase.Bout) return WrestleEnd.None;
        if (Balance >= 1f) return Finish(WrestleEnd.Won);
        if (Balance <= -1f) return Finish(WrestleEnd.Lost);
        return WrestleEnd.None;
    }

    WrestleEnd Finish(WrestleEnd e)
    {
        Balance = Math.Clamp(Balance, -1f, 1f);
        End = e;
        SequenceOpen = false;
        Braced = false;
        PinnedLeft = e is WrestleEnd.Won or WrestleEnd.Lost ? WrestleTuning.PinnedSeconds : 0;
        Phase = e is WrestleEnd.Refunded ? WrestlePhase.Bet : WrestlePhase.Pinned;
        return e;
    }

    /// <summary>Back to the stake rows after a bout.</summary>
    public void Reset() { if (Phase is WrestlePhase.Pinned or WrestlePhase.Bet) { Phase = WrestlePhase.Bet; End = WrestleEnd.None; } }

    ulong Next()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return _rng;
    }

    float Between(float lo, float hi) => lo + (hi - lo) * (float)((Next() >> 11) * (1.0 / (1UL << 53)));
}

/// <summary>What a finished bout is worth in the world (pure; CrawlSystem applies it in its serial step, plan section 4).</summary>
public static class WrestleRewards
{
    public const float HonestSeconds = 4f;
    public const int FullBoutsPerDay = 3, HalfBoutsPerDay = 5;
    public const int RespectCap = 15;
    public const float SprainFloor = 5f;

    /// <summary>The share of XP a bout gives: 1 for the day's first three, 0.5 for the next two, 0 after (per character, per game day).</summary>
    public static float DayShare(int boutsBeforeToday) => boutsBeforeToday < FullBoutsPerDay ? 1f : boutsBeforeToday < HalfBoutsPerDay ? 0.5f : 0f;

    /// <summary>Strength and Toughness for a finished bout, as amounts for the game's own CharacterStats.Gain (its level curve and XP rate
    /// apply on top): nothing for a bout under 4 s or a refund / forfeit; a win or an honest loss scales with the bout's length (full at
    /// 12 s) and the opponent's strength against the player's (capped), times the day's share. Scale: the game trains Toughness 0.075 for a
    /// knock-out and Strength about 0.08 a combat hit (MeitouClient XpService); a full bout is 0.25-0.5 Strength and 0.08-0.2 Toughness,
    /// a quarter of a level at level 10.</summary>
    public const float MaxStrengthXp = 0.5f, MaxToughnessXp = 0.2f;
    public static (float Strength, float Toughness) Xp(WrestleEnd end, float seconds, WrestleStats player, WrestleStats shek, float dayShare)
    {
        if (end is not (WrestleEnd.Won or WrestleEnd.Lost) || !float.IsFinite(seconds) || seconds < HonestSeconds) return (0, 0);
        float len = Math.Clamp(seconds / 12f, 0.33f, 1f);
        float ratio = Math.Clamp(shek.Rating / Math.Max(1f, player.Rating), 0.5f, 1.5f);
        float share = Math.Clamp(float.IsFinite(dayShare) ? dayShare : 0f, 0f, 1f);
        float s = Math.Clamp(0.25f + 0.25f * (ratio - 0.5f), 0.25f, MaxStrengthXp) * len * share;
        float t = Math.Clamp(0.08f + 0.12f * (ratio - 0.5f), 0.08f, MaxToughnessXp) * len * share;
        return (s, t);
    }

    /// <summary>A crushing loss (under 6 s, or lost with grit under 0.15 against a stronger Shek) sprains the right arm: stun only,
    /// clamp(18 - 0.15 T, 4, 18), and never so much that the arm's health (flesh - stun) falls under 5; never worse for an arm already
    /// down. Answers the stun to ADD (0 for none).</summary>
    public static float Sprain(WrestleEnd end, float seconds, float playerGrit, WrestleStats player, WrestleStats shek, float flesh, float stun)
    {
        if (end != WrestleEnd.Lost || !float.IsFinite(seconds) || !float.IsFinite(flesh) || !float.IsFinite(stun)) return 0;
        bool crushing = seconds < 6f || (playerGrit < 0.15f && shek.Rating > player.Rating);
        if (!crushing) return 0;
        float want = Math.Clamp(18f - 0.15f * player.T, 4f, 18f);
        float room = flesh - stun - SprainFloor;
        return room <= 0 ? 0 : Math.Min(want, room);
    }

    /// <summary>Shek Kingdom respect for a win: the first win over a tier gives its amount; never past the cap in all.</summary>
    public static int Respect(bool won, bool firstWinOfTier, int tier, int givenSoFar)
    {
        if (!won || !firstWinOfTier) return 0;
        int room = RespectCap - Math.Max(0, givenSoFar);
        return Math.Clamp(WrestleTiers.FirstWinRespect(tier), 0, Math.Max(0, room));
    }
}

/// <summary>What the save keeps of arm wrestling: first wins per tier, respect given, and each character's bouts per game day
/// (by the game's lasting character id, which the caller assigns in a serial step).</summary>
public sealed class WrestleRecord
{
    public const int MaxCharacters = 256;
    public bool[] FirstWin { get; } = new bool[WrestleTiers.Count];
    public int RespectGiven { get; private set; }
    public long Won { get; private set; }
    public long Lost { get; private set; }
    readonly Dictionary<long, (long Day, int Bouts)> _days = [];

    public int BoutsToday(long character, long day) => _days.TryGetValue(character, out var d) && d.Day == day ? d.Bouts : 0;

    /// <summary>A finished bout: counts it for the day, notes a first win, adds the respect given. Answers whether it was the tier's first win.</summary>
    public bool Note(long character, long day, int tier, bool won, int respect)
    {
        int today = BoutsToday(character, day);
        if (!_days.ContainsKey(character) && _days.Count >= MaxCharacters)
        {
            // the oldest day goes (a save never grows without end)
            long oldest = _days.OrderBy(kv => kv.Value.Day).First().Key;
            _days.Remove(oldest);
        }
        _days[character] = (day, today + 1);
        bool first = false;
        if (won)
        {
            Won++;
            int i = Math.Clamp(tier, 1, WrestleTiers.Count) - 1;
            first = !FirstWin[i];
            FirstWin[i] = true;
        }
        else Lost++;
        RespectGiven = Math.Clamp(RespectGiven + Math.Max(0, respect), 0, WrestleRewards.RespectCap);
        return first;
    }

    public bool IsFirstWin(int tier) => tier >= 1 && tier <= WrestleTiers.Count && !FirstWin[tier - 1];

    public const long Format = 1;

    public long[] Save()
    {
        var a = new List<long> { Format, RespectGiven, Won, Lost };
        a.AddRange(FirstWin.Select(b => b ? 1L : 0L));
        a.Add(_days.Count);
        foreach (var (k, v) in _days) { a.Add(k); a.Add(v.Day); a.Add(v.Bouts); }
        return [.. a];
    }

    /// <summary>Reads a saved record; false (and an empty record) for anything that does not add up.</summary>
    public bool Load(IReadOnlyList<long>? a)
    {
        Clear();
        try
        {
            if (a is null || a.Count < 4 + WrestleTiers.Count + 1 || a[0] != Format) throw new InvalidDataException();
            if (a[1] < 0 || a[1] > WrestleRewards.RespectCap || a[2] < 0 || a[3] < 0) throw new InvalidDataException();
            int at = 4;
            for (int i = 0; i < WrestleTiers.Count; i++)
            {
                if (a[at] is not (0 or 1)) throw new InvalidDataException();
                FirstWin[i] = a[at++] == 1;
            }
            long n = a[at++];
            if (n < 0 || n > MaxCharacters || a.Count != at + 3 * n) throw new InvalidDataException();
            for (long i = 0; i < n; i++)
            {
                long k = a[at], day = a[at + 1], bouts = a[at + 2];
                at += 3;
                if (day < 0 || bouts < 0 || bouts > 1000 || _days.ContainsKey(k)) throw new InvalidDataException();
                _days[k] = (day, (int)bouts);
            }
            RespectGiven = (int)a[1];
            Won = a[2];
            Lost = a[3];
            return true;
        }
        catch (InvalidDataException)
        {
            Clear();
            return false;
        }
    }

    void Clear()
    {
        Array.Clear(FirstWin);
        RespectGiven = 0;
        Won = Lost = 0;
        _days.Clear();
    }
}
