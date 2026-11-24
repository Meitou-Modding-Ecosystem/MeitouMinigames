// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// The table, laid out as Kenshi's own conversation (the user's pick of three mock-ups, 2026-11-20: "A: conversation"), built from the
// conversation panel's own skins (Kenshi_FloatingPanelSkin, Kenshi_GenericTextBox, Kenshi_GenericTextBoxFlat, Kenshi_ListBoxEmpty):
//   the dealer    his live face, his name, his call on the parchment
//   the table     our cloth: the seven crawlers as a ladder, HIGH at the top (Fish climbs, Roll back goes down) with what is left of each,
//                 the face card, the crawl (the card turned lands on it), the cats on the table
//   you           your face, your money, and your answers as a list: every row is one thing you can do now (a click, or a pad's pointer
//                 and A, picks it at once). HOW TO PLAY and LEDGER on the dealer's panel turn the list into a page (the same button turns back); X closes.
// The cards are dealt here, on the host's thread (they are not world state); their money goes to the game as commands (CrawlSystem), and
// the purse shown is the game's money plus what is still on its way (CrawlCore.Purse), so the table works while the game is paused.
// Pictures: the mod's own PNG files (mod/art, drawn by tools/art/make_cards.py), shown by ImageBoxes as Meitou's "file:" textures.
using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Modding;
using Meitou.Simulation;
using PZA.MeitouUi;
using L = PZA.MeitouUi.PzaLayout;

namespace PZA.CatCrawlers;

public static partial class CrawlWindow
{
    public const string Id = "pza.catcrawlers";
    public static PzaWindow Window = null!;

    enum Anim { None, Deal, Reveal }
    const double DealSeconds = 0.42, RollSeconds = 1.15, FlipSeconds = 0.36;

    static ModContext s_ctx = null!;
    static string s_art = "";
    static readonly Random s_rng = new();
    static Calls s_calls = null!;
    static Table s_table = new();
    static readonly Purse s_purse = new();
    static int s_stake;
    static string s_call = "";
    static Anim s_anim;
    static readonly Stopwatch s_clock = new();
    static Table.Result s_pending;
    static long s_potBefore;
    static int s_world = -1, s_opened;
    static bool s_sitting;
    static CharacterId s_dealer = CharacterId.None, s_player = CharacterId.None;
    static Vector2? s_place;

    public static Table Table => s_table;
    public static string CallText => s_call;

    enum View { Table, Rules, Ledger }
    static View s_view;
    /// <summary>What each row of the answers list does now (filled by Bind, read by the list's select event).</summary>
    static readonly List<Action> s_rows = [];

    static string F(double v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
    static string Box(string type, string skin, string name, double x, double y, double w, double h, string inner = "") =>
        $"<Widget type=\"{type}\" skin=\"{skin}\" position_real=\"{F(x)} {F(y)} {F(w)} {F(h)}\"{(name.Length > 0 ? $" name=\"{name}\"" : "")}>{inner}</Widget>";
    static string Img(string name, double x, double y, double w, double h) => Box("ImageBox", "ImageBox", name, x, y, w, h);
    static string Txt(string name, double x, double y, double w, double h, string align = "Left VCenter", bool wrap = false) =>
        Box("TextBox", "Kenshi_TextboxStandardText", name, x, y, w, h,
            $"<Property key=\"TextAlign\" value=\"{align}\"/>" + (wrap ? "<Property key=\"WordWrap\" value=\"true\"/>" : ""));

    /// <summary>The ladder's rows, top to bottom: the Plank first.</summary>
    public const double RowTop = 0.05, RowStep = 0.129;

    public static string LayoutXml()
    {
        // the dealer (as the conversation panel's NpcPanel)
        string npc = Box("Widget", "Kenshi_FloatingPanelSkin", "NpcPanel", 0, 0, 1, 0.183,
            Img("Dealer", 0.016, 0.08, 0.106, 0.79)
            + Box("TextBox", "Kenshi_GenericTextBoxFlat", "DealerName", 0.138, 0.05, 0.3, 0.22, "<Property key=\"TextAlign\" value=\"Left VCenter\"/>")
            // PZA UI rules: the help page one press away, and a button that closes the window (the user, 2026-11-20: "needs the PZA UI
            // treatment with a button to close and, maybe make it easier to navigate around")
            + Box("Button", "Kenshi_Button1", "BtnRules", 0.585, 0.05, 0.17, 0.22)
            + Box("Button", "Kenshi_Button1", "BtnLedger", 0.762, 0.05, 0.17, 0.22)
            + Box("Button", "Kenshi_CloseButtonSkin", "BtnClose", 0.952, 0.05, 0.031, 0.22)
            + Box("Widget", "Kenshi_GenericTextBox", "", 0.138, 0.31, 0.848, 0.62, Txt("lbCall", 0.02, 0.1, 0.96, 0.8, "Left Top", wrap: true)));

        // the table
        var felt = new System.Text.StringBuilder(Img("Felt", 0.006, 0.014, 0.988, 0.972));
        for (int i = 0; i < Crawl.Ranks; i++)
        {
            int n = Crawl.Ranks - i;   // the Plank on top
            double y = RowTop + i * RowStep;
            felt.Append(Img("G" + n, 0.024, y - 0.012, 0.262, 0.124));
            felt.Append(Img("L" + n, 0.033, y, 0.0265, 0.102));
            felt.Append(Txt("N" + n, 0.07, y - 0.004, 0.17, 0.06));
            felt.Append(Img("P" + n, 0.07, y + 0.062, 0.052, 0.03));
            felt.Append(Img("A" + n, 0.252, y + 0.026, 0.02, 0.05));
        }
        felt.Append(Img("DeckB", 0.63, 0.061, 0.2127, 0.822));
        felt.Append(Img("DeckA", 0.625, 0.068, 0.2127, 0.822));
        felt.Append(Img("CardFace", 0.34, 0.075, 0.2127, 0.822));
        felt.Append(Img("CardDraw", 0.62, 0.075, 0.2127, 0.822));
        felt.Append(Txt("lbFace", 0.32, 0.9, 0.2527, 0.07, "HCenter VCenter"));
        felt.Append(Txt("lbCrawl", 0.6, 0.9, 0.2527, 0.07, "HCenter VCenter"));
        felt.Append(Img("Coins", 0.88, 0.57, 0.062, 0.16));
        felt.Append(Txt("lbPot", 0.85, 0.74, 0.122, 0.075, "HCenter VCenter"));
        felt.Append(Txt("lbPot2", 0.85, 0.81, 0.122, 0.055, "HCenter VCenter"));
        felt.Append(RaidWidgets());
        felt.Append(WrestleWidgets());
        string table = Box("Widget", "Kenshi_FloatingPanelSkin", "TablePanel", 0, 0.196, 1, 0.485, felt.ToString());

        // you (as the conversation panel's PlayerPanel)
        string you = Box("Widget", "Kenshi_FloatingPanelSkin", "PlayerPanel", 0.151, 0.695, 0.849, 0.29,
            Img("Player", 0.019, 0.07, 0.12, 0.474)
            + Box("Widget", "Kenshi_GenericTextBoxFlatSkin", "", 0.019, 0.585, 0.12, 0.126,
                Txt("MoneyLabel", 0.06, 0.15, 0.88, 0.7) + Txt("MoneyAmount", 0.06, 0.15, 0.88, 0.7, "Right VCenter"))
            + Box("TextBox", "Kenshi_GenericTextBoxFlat", "PlayerName", 0.159, 0.045, 0.3, 0.126, "<Property key=\"TextAlign\" value=\"Left VCenter\"/>")
            + Box("Widget", "Kenshi_GenericTextBox", "", 0.159, 0.2, 0.824, 0.74, Box("ListBox", "Kenshi_ListBoxEmpty", "Answers", 0.02, 0.06, 0.96, 0.88)));

        return "<MyGUI type=\"Layout\"><Widget type=\"Widget\" skin=\"PanelEmpty\" position_real=\"0.172 0.115 0.656 0.86\" layer=\"Window\" name=\"Root\">"
            + npc + table + you + "</Widget></MyGUI>";
    }

    public static void Setup(ModContext context)
    {
        s_ctx = context;
        s_art = Path.Combine(context.Folder, "art");
        s_calls = new Calls(Texts, s_rng.Next);
        Window = new PzaWindow(Id, LayoutXml)
        {
            Bind = Bind,
            Event = OnEvent,
            LoadPlace = () => s_place,
            SavePlace = p => s_place = p,
            OpenChanged = open =>
            {
                if (open || !s_sitting) return;
                // closed by its X or Escape: the player stands up
                s_sitting = false;
                Send(new CloseTable(s_calls.Leave()) { Tick = Now });
                Counters.Add("window.stood_up");
            },
        };
        PzaUi.Add(Window);
        int missing = Calls.AllRecords().Count(r => Texts(r).Count == 0);
        Log.Line(missing == 0 ? "calls: every dealer line found in the game's dialogue records" : $"calls: {missing} dealer line record(s) not found: our own lines are used for those");
    }

    /// <summary>The texts of a dialogue line record (text0, text1, ...) in the game's language; none when the record is not there.</summary>
    static IReadOnlyList<string> Texts(string record)
    {
        var r = s_ctx.Data.Find(record);
        if (r is null) return [];
        return [.. r.Strings.Where(kv => kv.Key.StartsWith("text", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value)];
    }

    static long Now => s_ctx.Tick;
    static void Send(SimCommand c) => s_ctx.Enqueue(c);

    /// <summary>The right-click row on a dealer (the menu source's choice): ask the game for a seat.</summary>
    public static void Ask(CharacterId dealer, IReadOnlyList<CharacterId> selected)
    {
        Send(new OpenTable(dealer, [.. selected], s_calls.Invite()) { Tick = Now });
        Counters.Add("menu.chosen");
    }

    static long PurseShown(Published pub) => s_purse.Shown(pub.Money, pub.Applied);

    /// <summary>Once a frame (PzaUi.EachFrame): follow the world (a new game or a load), the game's answer to a seat, and the animation.</summary>
    public static void EachFrame()
    {
        var pub = CrawlSystem.Pub;
        if (pub.World != s_world)
        {
            s_world = pub.World;
            s_table = new Table();
            if (pub.Table is { } saved && !s_table.Load(saved)) Log.Line("the saved table did not add up: an empty table on a fresh crawl");
            s_raid = new Raid();
            s_bout = new WrestleBout();
            s_boutSettled = true;
            s_holdPush = false;
            if (pub.Raid is { } savedRaid && !s_raid.Load(savedRaid)) Log.Line("the saved raid did not add up: an idle table");
            s_purse.Reset(pub.Applied);
            s_opened = pub.Opened;
            s_sitting = false;
            s_anim = Anim.None;
            if (Window.IsOpen) Window.Close();
            Log.Line($"world {pub.World}: table phase {s_table.Phase}, {s_table.Crawl.Total} cards in the crawl");
        }
        if (pub.Open && pub.Opened != s_opened)
        {
            s_opened = pub.Opened;
            s_sitting = true;
            (s_dealer, s_player) = (pub.Dealer, pub.Player);
            s_game = pub.Game;
            s_call = s_game == TableGame.ArmWrestling ? Strings.T("wrestle.call.invite")
                : s_game == TableGame.LootersRights
                ? Strings.T(s_raid.Phase == RaidPhase.Out && s_raid.Player == Raider.Out ? "raid.call.back" : "raid.call.invite")
                : s_table.Phase == Phase.Choose ? s_calls.Face(s_table.Face) : s_calls.Invite();
            Send(new Say(s_dealer, s_call) { Tick = Now });
            s_view = View.Table;
            Window.Open();
            Counters.Add("window.sat_down");
        }
        else if (!pub.Open && s_sitting && pub.Opened == s_opened && pub.Closed.Length > 0)
        {
            // the game closed the table (walked away, the dealer went down)
            s_sitting = false;
            if (Window.IsOpen) Window.Close();
            Counters.Add("window.closed_by_game");
        }
        if (s_game == TableGame.ArmWrestling && Window.IsOpen) WrestleFrame();
        else if (s_game == TableGame.ArmWrestling && s_bout.Phase is WrestlePhase.Countdown or WrestlePhase.Bout) WrestleLeave();   // the window closed mid-bout
        if (s_anim == Anim.Deal && s_clock.Elapsed.TotalSeconds >= DealSeconds) s_anim = Anim.None;
        if (s_anim == Anim.Reveal && s_clock.Elapsed.TotalSeconds >= RollSeconds + FlipSeconds) FinishReveal();
    }

    #region Actions

    static void OnEvent(MyGuiEvent e)
    {
        Counters.Add("event." + e.Action);
        if (WrestleEvent(e.Action)) return;
        switch (e.Action)
        {
            case "BtnClose": Window.Close(); return;
            case "BtnRules": Show(s_view == View.Rules ? View.Table : View.Rules); return;
            case "BtnLedger": Show(s_view == View.Ledger ? View.Table : View.Ledger); return;
        }
        if (e.Action != "select" || e.Widget != "Answers") return;
        Window.Host?.ClearSelection(Id, "Answers");   // a picked row is done: nothing stays marked (the list would grey it)
        int row = (int)MathF.Round(e.Value);
        if (row < 0 || row >= s_rows.Count) return;
        Counters.Add("answers.picked");
        s_rows[row]();
    }

    /// <summary>Turns to a page (the rules, the ledger) or back to the table. Asking for the rules, the dealer says his own line about them.</summary>
    static void Show(View view)
    {
        s_view = view;
        Window.Host?.ClearSelection(Id, "Answers");
        if (view == View.Rules && s_calls.HowTo() is { Length: > 0 } how) s_call = how;
        Counters.Add("view." + view);
    }

    static void PushState() => Send(new TableState(s_table.Save()) { Tick = Now });

    static void Deal(int stakeIndex)
    {
        var pub = CrawlSystem.Pub;
        s_stake = Math.Clamp(stakeIndex, 0, Table.Stakes.Length - 1);
        long stake = Table.Stakes[s_stake];
        var r = s_table.Deal(stake, PurseShown(pub), s_rng.Next);
        if (r.Outcome != Outcome.Dealt) { Counters.Add("deal.refused"); return; }
        Send(new TableEvent(s_purse.Queue(r.Cats), r.Cats, Outcome.Dealt, stake, 0) { Tick = Now });
        PushState();
        string face = s_calls.Face(r.Card);
        s_call = string.Join(" ", new[] { r.FreshCrawl ? Strings.T("call.fresh") : "", r.Burned > 0 ? Strings.TF("call.burned", r.Burned) : "", s_calls.Flourish(), face.TrimStart('.', ' ', '…') }
            .Where(s => s.Length > 0));
        Send(new Say(s_dealer, face) { Tick = Now });
        s_anim = Anim.Deal;
        s_clock.Restart();
        Counters.Add("deal.ok");
    }

    static void Play(Call call)
    {
        if (!s_table.CanCall(call)) return;
        var face = s_table.Face;
        s_potBefore = s_table.Pot;
        s_pending = s_table.Play(call, s_rng.Next);
        if (s_pending.Outcome == Outcome.Refused) return;
        PushState();
        s_call = call == Call.Fish ? s_calls.Fish(face) : s_calls.Roll(face);
        Send(new Say(s_player, call == Call.Fish ? s_calls.SayFish() : s_calls.SayRoll()) { Tick = Now });
        Send(new Say(s_dealer, s_call) { Tick = Now });
        s_anim = Anim.Reveal;
        s_clock.Restart();
        Counters.Add(call == Call.Fish ? "play.fish" : "play.roll");
    }

    static void FinishReveal()
    {
        s_anim = Anim.None;
        var r = s_pending;
        string line = r.Outcome switch
        {
            Outcome.Lick => s_calls.Lick(r.Card, s_table.Pot),
            Outcome.Girl => s_calls.Girl(r.Card, s_table.Pot),
            _ => s_calls.Dry(r.Card),
        };
        string note = "";
        if (s_table.Phase == Phase.Licked && !s_table.CanRide)
            note = s_table.Pot >= Table.HouseLimit ? Strings.T("call.limit") : Strings.TF("call.end", Strings.Name(r.Card));
        s_call = string.Join(" ", new[] { r.FreshCrawl ? Strings.T("call.fresh") : "", line, note }.Where(s => s.Length > 0));
        Send(new TableEvent(s_purse.Queue(0), 0, r.Outcome, s_table.Pot, s_table.Chain) { Tick = Now });
        Send(new Say(s_dealer, line) { Tick = Now });
        Counters.Add("reveal." + r.Outcome);
    }

    static void Take()
    {
        int chain = s_table.Chain;
        var r = s_table.Take();
        if (r.Outcome != Outcome.Took) return;
        Send(new TableEvent(s_purse.Queue(r.Cats), r.Cats, Outcome.Took, r.Cats, chain) { Tick = Now });
        PushState();
        s_call = Strings.TF("call.took", Strings.Cats(r.Cats));
        Counters.Add("take");
    }

    static void Ride()
    {
        var r = s_table.Ride();
        if (r.Outcome != Outcome.Rode) return;
        PushState();
        string face = s_calls.Face(r.Card);
        s_call = Strings.TF("call.ride", Strings.Cats(s_table.Pot), Strings.Name(r.Card)) + " " + face;
        Send(new Say(s_dealer, face) { Tick = Now });
        Counters.Add("ride");
    }

    #endregion

    #region Bind

    // text colours ("r g b"): chalk on the cloth (the parchment boxes keep the game's own ink)
    const string Chalk = "0.88 0.83 0.70", Gold = "0.84 0.67 0.38", Faded = "0.5 0.5 0.44", Red = "0.86 0.32 0.24";

    static string Art(string name) => "file:" + Path.Combine(s_art, name + ".png");
    static void Image(UiBindings b, string widget, string name) => PzaWindow.Props(b, widget, ("ImageTexture", Art(name)));

    /// <summary>The frame of a card turning from <paramref name="from"/> to <paramref name="to"/> at <paramref name="t"/> (0..1): the turn
    /// is eased (a cosine: slow at the start and the end, quick edge-on), and the frame is the drawn width nearest to |cos(pi t)| (art
    /// files: the card, then _f1 .. _f4 at 90 / 70 / 45 / 20 %; tools/art/make_cards.py flip_frames).</summary>
    public static string Flip(string from, string to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        double eased = 0.5 - 0.5 * Math.Cos(Math.PI * t);
        double w = Math.Abs(Math.Cos(Math.PI * eased));
        string side = eased < 0.5 ? from : to;
        double[] widths = [1, 0.9, 0.7, 0.45, 0.2];
        int best = 0;
        for (int i = 1; i < widths.Length; i++) if (Math.Abs(widths[i] - w) < Math.Abs(widths[best] - w)) best = i;
        return best == 0 ? side : $"{side}_f{best}";
    }

    /// <summary>The answers the player has now: caption and what it does.</summary>
    static List<(string Caption, Action Do)> Answers(Table t, long purse, bool busy)
    {
        var rows = new List<(string, Action)>();
        if (s_view != View.Table) return rows;   // a page: the buttons on the dealer's panel turn back
        if (busy) { rows.Add((Strings.T("ans.wait"), () => { })); return rows; }
        if (t.Phase == Phase.Choose)
        {
            if (t.CanCall(Call.Fish)) rows.Add((Strings.TF("ans.fish", Strings.Cats(t.Pays(Call.Fish))), () => Play(Call.Fish)));
            if (t.CanCall(Call.RollBack)) rows.Add((Strings.TF("ans.roll", Strings.Cats(t.Pays(Call.RollBack))), () => Play(Call.RollBack)));
        }
        else if (t.Phase == Phase.Licked)
        {
            rows.Add((Strings.TF("ans.take", Strings.Cats(t.Pot)), Take));
            if (t.CanRide) rows.Add((Strings.TF("ans.chain", Strings.Cats(t.Pot)), Ride));
        }
        else
        {
            for (int i = 0; i < Table.Stakes.Length; i++)
            {
                int ix = i;
                if (purse >= Table.Stakes[i]) rows.Add((Strings.TF("ans.deal", Strings.Cats(Table.Stakes[i])), () => Deal(ix)));
            }
            if (purse < Table.Stakes[0]) rows.Add((Strings.TF("ans.broke", Strings.Cats(Table.Stakes[0])), () => { }));
        }
        rows.Add((Strings.T(t.Phase is Phase.Choose or Phase.Licked ? "ans.leave.hand" : "ans.leave"), () => Window.Close()));
        return rows;
    }

    static void Bind(UiBindings b)
    {
        var pub = CrawlSystem.Pub;
        long purse = PurseShown(pub);
        var t = s_table;
        bool revealing = s_anim == Anim.Reveal;
        double secs = s_clock.Elapsed.TotalSeconds;

        // the page buttons and the close button
        PzaWindow.Enable(b, "BtnRules", "BtnLedger", "BtnClose");
        b.Text["BtnRules"] = Strings.T(s_view == View.Rules ? "btn.table" : "btn.rules");
        b.Text["BtnLedger"] = Strings.T(s_view == View.Ledger ? "btn.table" : "btn.ledger");
        PzaWindow.Tooltip(b, "BtnRules", Strings.T("tip.rules"));
        PzaWindow.Tooltip(b, "BtnLedger", Strings.T("tip.ledger"));
        PzaWindow.Tooltip(b, "BtnClose", Strings.T("tip.close"));

        // the dealer and his call
        if (!s_dealer.IsNone) PzaPortrait.Face(b, "Dealer", s_dealer);
        b.Text["DealerName"] = pub.DealerName;
        b.Text["lbCall"] = s_call;

        // you
        if (!s_player.IsNone) PzaPortrait.Face(b, "Player", s_player);
        b.Text["PlayerName"] = pub.PlayerName;
        b.Text["MoneyLabel"] = Strings.T("money");
        b.Text["MoneyAmount"] = "c." + Strings.Cats(purse);
        if (s_game != TableGame.ArmWrestling) HideWrestleButtons(b);
        if (s_game == TableGame.LootersRights) { BindRaid(b, pub, purse); return; }
        if (s_game == TableGame.ArmWrestling) { BindWrestle(b, pub, purse); return; }

        // the cloth: the ladder (the Plank on top); a card in the air still counts as in the crawl
        Image(b, "Felt", "felt_wide");
        bool choosing = t.Phase == Phase.Choose && s_anim != Anim.Reveal;
        for (int i = 1; i <= Crawl.Ranks; i++)
        {
            var c = (Crawler)i;
            int left = t.Crawl.Left(c) + (revealing && s_pending.Card == c ? 1 : 0);
            bool face = t.Face == c && t.Phase != Phase.Idle;
            Image(b, "L" + i, "c" + i);
            Image(b, "P" + i, "pips" + Math.Clamp(left, 0, 4));
            if (face) Image(b, "G" + i, "glow");
            if (choosing && i != (int)t.Face) Image(b, "A" + i, i > (int)t.Face ? "up" : "down");
            b.Text["N" + i] = Strings.Name(c);
            PzaWindow.Props(b, "N" + i, ("TextColour", face ? Gold : left == 0 ? Faded : Chalk));
            string tip = Strings.TF("tip.ladder", Strings.Name(c), i, left, Crawl.Copies - left);
            PzaWindow.Tooltip(b, "L" + i, tip);
            PzaWindow.Tooltip(b, "P" + i, tip);
        }

        // the face card, the crawl, the card turned
        int crawlShown = t.Crawl.Total + (revealing ? 1 : 0);
        if (t.Face != Crawler.None)
        {
            string face = "c" + (int)t.Face;
            Image(b, "CardFace", s_anim == Anim.Deal ? Flip("back", face, secs / DealSeconds) : face);
            PzaWindow.Tooltip(b, "CardFace", Strings.TF("tip.card.face", Strings.Name(t.Face), (int)t.Face));
            b.Text["lbFace"] = Strings.Name(t.Face);
        }
        if (crawlShown > 2) Image(b, "DeckB", "back");
        if (crawlShown > 1) Image(b, "DeckA", "back");
        if (t.Drawn != Crawler.None && !revealing)
        {
            Image(b, "CardDraw", "c" + (int)t.Drawn);
            PzaWindow.Tooltip(b, "CardDraw", Strings.TF("tip.card.drawn", Strings.Name(t.Drawn), (int)t.Drawn));
            b.Text["lbCrawl"] = Strings.Name(t.Drawn);
        }
        else
        {
            string draw = "back";
            if (revealing && secs > RollSeconds) draw = Flip("back", "c" + (int)s_pending.Card, (secs - RollSeconds) / FlipSeconds);
            else if (revealing) draw = (int)(secs / 0.14) % 2 == 0 ? "back" : "back_f1";   // the dealer riffles: rolling, rolling...
            if (crawlShown > 0 || revealing) Image(b, "CardDraw", draw);
            PzaWindow.Tooltip(b, "CardDraw", Strings.TF("tip.card.crawl", crawlShown));
            b.Text["lbCrawl"] = Strings.TF("crawl.count", crawlShown);
        }
        PzaWindow.Props(b, "lbFace", ("TextColour", Chalk));
        PzaWindow.Props(b, "lbCrawl", ("TextColour", Chalk));

        // the cats on the table
        long pot = revealing ? s_potBefore : t.Pot;
        if (pot > 0)
        {
            Image(b, "Coins", pot >= 1000 ? "coins3" : pot >= 300 ? "coins2" : "coins1");
            b.Text["lbPot"] = "c." + Strings.Cats(pot);
            b.Text["lbPot2"] = t.Chain > 0 && !revealing ? Strings.TF("pot.chain", t.Chain) : Strings.T("pot.table");
        }
        PzaWindow.Props(b, "lbPot", ("TextColour", Gold));
        PzaWindow.Props(b, "lbPot2", ("TextColour", Chalk));

        // your answers
        var rows = Answers(t, purse, s_anim != Anim.None);
        s_rows.Clear();
        var lines = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            lines.Add($"{i + 1}. {rows[i].Caption}");
            s_rows.Add(rows[i].Do);
        }
        if (s_view == View.Rules)
            lines.AddRange(PzaFit.Paragraphs(Window, "Answers", string.Join("\n\n", Enumerable.Range(1, 8).Select(i => Strings.T("rules." + i)))));
        else if (s_view == View.Ledger)
        {
            var led = new Ledger();
            led.Load(pub.Ledger);
            lines.AddRange(Strings.TF("ledger.text", Strings.Cats(led.Hands), Strings.Cats(led.Licks), Strings.Cats(led.Dryouts), Strings.Cats(led.Girls),
                Strings.Cats(led.BestChain), Strings.Cats(led.BiggestPot), Strings.Cats(led.Staked), Strings.Cats(led.Paid), Strings.Cats(led.Net)).Split('\n'));
        }
        PzaWindow.Lines(b, "Answers", lines);
    }

    #endregion
}
