// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// The code mod's entry (meitou.json "entry"). It patches nothing of the game's rules: the table is a window (PZA's Meitou UI), a tick
// system for the money and the save (CrawlSystem), and one row on the right-click menu of a Swamper Casino Dealer (Meitou 0.21+).
using System.Reflection;
using Meitou.Game.Ui;
using Meitou.Localisation;
using Meitou.Modding;
using Meitou.Simulation;
using PZA.MeitouUi;

namespace PZA.CatCrawlers;

public sealed class CatCrawlersMod : IMod
{
    /// <summary>The Shady Dealer's "Deal me in." effect code (PZACatCrawlers.mod).</summary>
    public const int DealMeIn = 1001;
    /// <summary>The Shek challenger's "Lock arms." effect code (PZACatCrawlers.mod).</summary>
    public const int LockArms = 1002;

    public void Initialize(ModContext context)
    {
        Log.Sink = context.Log;
        string version = typeof(CatCrawlersMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        Log.Line($"{version} for Meitou, by PZA: talk to a Swamper Casino Dealer (Shark, Mud Town) and say yes, or hold right-click on him > Play Cat Crawlers");
        foreach (var line in Strings.Init(context.Folder, Loc.Language?.Code ?? "en")) Log.Line(line);

        int art = Directory.Exists(Path.Combine(context.Folder, "art")) ? Directory.GetFiles(Path.Combine(context.Folder, "art"), "*.png").Length : 0;
        Log.Line(art >= 73 ? $"art: {art} pictures" : $"art: only {art} pictures in the mod's art folder (73 expected): cards will show blank");

        Log.Line($"bars: {Hosts.FindBarmen(context.Data)} bar keeper records (a host sits by the nearest)");
        context.AddSystem("pza.catcrawlers.table", b => new CrawlSystem(b.Data, b.Find<Meitou.Simulation.Population.PopulationSystem>(), b.Find<Meitou.Simulation.Production.ProductionSystem>(),
            b.Find<Meitou.Simulation.Aggro.AggroSystem>()?.Relations), SystemAnchor.End);
        // the Shady Dealer's own "Deal me in." (his DIALOG_ACTION in PZACatCrawlers.mod, code 1001): seat the player at his table
        context.AddDialogueEffect(DealMeIn, act =>
        {
            var table = act.World.Characters;
            bool speakerIsPlayer = table.TryResolveNext(act.Speaker, out int s) && table.Cold(s) is { IsPlayer: true };
            var (dealer, player) = speakerIsPlayer ? (act.Target, act.Speaker) : (act.Speaker, act.Target);
            Counters.Add("talk.deal_me_in");
            CrawlSystem.SeatFromTalk(dealer, player);
        });
        // the Shek challenger's own "Lock arms." (code 1002): the same seat, at his table
        context.AddDialogueEffect(LockArms, act =>
        {
            var table = act.World.Characters;
            bool speakerIsPlayer = table.TryResolveNext(act.Speaker, out int s) && table.Cold(s) is { IsPlayer: true };
            var (host, player) = speakerIsPlayer ? (act.Target, act.Speaker) : (act.Speaker, act.Target);
            Counters.Add("talk.lock_arms");
            CrawlSystem.SeatFromTalk(host, player);
        });
        try { Log.Line(DealerTalk.Apply(context.Harmony)); }
        catch (Exception e) { Log.Line($"dealer talk: OFF (patching failed: {e.GetType().Name}: {e.Message})"); }
        try { Log.Line(DealerTalk.ApplyKeys(context.Harmony)); }
        catch (Exception e) { Log.Line($"table keys: OFF (patching failed: {e.GetType().Name}: {e.Message})"); }
        PzaUi.Log = s => Log.Line(s);
        PzaUi.EachFrame = CrawlWindow.EachFrame;
        CrawlWindow.Setup(context);
        PzaUi.Install(context);
        context.AddUi(new DealerMenu());
        context.AddDebugCommand("Cat Crawlers: sit at the nearest dealer's table", c => { CrawlWindow.Ask(CharacterId.None, c.Selected); return "asked"; });
        context.AddDebugCommand("Cat Crawlers: counters to the log", _ => { Log.Line("counters: " + Counters.Dump()); return "logged"; });
    }
}

/// <summary>The right-click row on a dealer. Runs on the host's thread whenever the game builds a character's menu, also every frame a
/// character is under the cursor: it only compares a record id.</summary>
public sealed class DealerMenu : IUiExtension
{
    public string Id => "pza-catcrawlers";

    public void Register(UiRegistration r) => r.AddCharacterMenuVerbs(Rows);

    static IEnumerable<UiMenuVerb> Rows(UiMenuContext c)
    {
        Counters.Add("menu.asked");
        if (c.Target.IsPlayer || c.Selected.Count == 0) return [];
        var game = ShadyDealer.GameOf(c.Target.RecordId);
        if (game == TableGame.None) return [];
        Counters.Add("menu.row_offered");
        var dealer = c.Target.Id;
        var selected = c.Selected;
        return [new UiMenuVerb(Strings.T(game == TableGame.LootersRights ? "menu.raid" : "menu.play"), () => CrawlWindow.Ask(dealer, selected))];
    }
}
