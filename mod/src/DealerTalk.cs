// PZA's Cat Crawlers for Meitou. Author: PZA. Licence: GPLv3 or later.
// The dealer's own conversation leads to the table (the user, 2026-11-20: the vanilla dialogue "was intuitively the first option I picked").
// When the player answers a Swamper Casino Dealer's offer with his own "Sure, why not." line (DIALOGUE_LINE 48364-Dialogue.mod, the reply
// that pays the vanilla text game's 100 cats), a prefix on Meitou's DialogueSystem.Choose ends the conversation instead, before the
// reply's effects run (so the vanilla 100 cats are not taken), and asks CrawlSystem to seat the player at that dealer's table: the stake is
// then chosen at the table, as with the right-click row. Every other line of every conversation runs as before.
// Runs in DialogueSystem's serial input step; CrawlSystem (registered after it, SystemAnchor.End) takes the seat in the same step.
// Members are found by name; any missing -> the hook stays off with one line, and the conversation stays vanilla.
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation;

namespace PZA.CatCrawlers;

public static class DealerTalk
{
    /// <summary>The dealer's "yes" reply: its four texts are "Sure, why not." / "Sure, okay." / "Hell, what's the harm in it..." / "Okay, let's play."</summary>
    public const string YesReply = "48364-Dialogue.mod";

    static FieldInfo? s_npc, s_player, s_choices, s_ended;

    public static string Apply(Harmony h)
    {
        var system = AccessTools.TypeByName("Meitou.Simulation.Dialogue.DialogueSystem");
        var convo = system?.GetNestedType("Conversation", BindingFlags.NonPublic | BindingFlags.Public);
        var choose = system is null || convo is null ? null : AccessTools.Method(system, "Choose", [typeof(World), convo, typeof(int)]);
        s_npc = convo is null ? null : AccessTools.Field(convo, "Npc");
        s_player = convo is null ? null : AccessTools.Field(convo, "Player");
        s_choices = convo is null ? null : AccessTools.Field(convo, "Choices");
        s_ended = convo is null ? null : AccessTools.Field(convo, "Ended");
        if (choose is null || s_npc is null || s_player is null || s_choices is null || s_ended is null)
            return "dealer talk: OFF (the game lacks DialogueSystem.Choose or its Conversation fields): the dealer's own \"Sure, why not.\" runs the vanilla text game";
        h.Patch(choose, prefix: new HarmonyMethod(AccessTools.Method(typeof(DealerTalk), nameof(ChoosePrefix))));
        return "dealer talk: patched DialogueSystem.Choose prefix (the dealer's \"Sure, why not.\" seats you at the table)";
    }

    /// <summary>Is <paramref name="reply"/> the dealer's "yes"? (Pure; the offline tests call it.)</summary>
    public static bool IsYes(string? replyRecord) => string.Equals(replyRecord, YesReply, StringComparison.Ordinal);

    static bool ChoosePrefix(object __1, int __2)
    {
        try
        {
            if (s_choices!.GetValue(__1) is not List<(DialogueNode? Reply, string Text)> choices || (uint)__2 >= (uint)choices.Count) return true;
            if (!IsYes(choices[__2].Reply?.StringId)) return true;
            var dealer = (CharacterId)s_npc!.GetValue(__1)!;
            var player = (CharacterId)s_player!.GetValue(__1)!;
            s_ended!.SetValue(__1, true);   // the conversation ends here; the reply's effects (the vanilla 100 cats) never run
            CrawlSystem.SeatFromTalk(dealer, player);
            Counters.Add("talk.yes_to_table");
            Log.Few("talk.yes", $"dealer talk: \"{choices[__2].Text}\" -> the table (dealer slot {dealer.Slot}, player slot {player.Slot})", 5);
            return false;
        }
        catch (Exception e)
        {
            Log.Few("talk.throw", $"dealer talk: {e.GetType().Name}: {e.Message} (the conversation goes on as vanilla)");
            return true;
        }
    }

    /// <summary>Patches DialogueWindow.DialogueKey so number keys 1-9 pick answers at every bar-game table and eat the key (no squad switch).</summary>
    public static string ApplyKeys(Harmony h)
    {
        var dw = AccessTools.TypeByName("Meitou.Game.Ui.DialogueWindow");
        var method = dw is null ? null : AccessTools.Method(dw, "DialogueKey", [typeof(Meitou.Game.Host.Input.Key)]);
        if (method is null)
            return "table keys: OFF (DialogueWindow.DialogueKey not found): pick answers with the mouse or pad";
        h.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(DealerTalk), nameof(DialogueKeyPostfix))));
        return "table keys: patched DialogueWindow.DialogueKey postfix (1-9 pick answers at the table)";
    }

    static void DialogueKeyPostfix(Meitou.Game.Host.Input.Key __0, ref bool __result)
    {
        if (__result) return;
        try
        {
            if (CrawlWindow.HandleKey(__0)) __result = true;
        }
        catch (Exception e)
        {
            Log.Few("keys.throw", $"table keys: {e.GetType().Name}: {e.Message}");
        }
    }
}
