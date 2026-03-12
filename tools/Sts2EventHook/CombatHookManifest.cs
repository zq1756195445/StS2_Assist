namespace Sts2EventHook;

internal static class CombatHookManifest
{
    internal static readonly HookCandidate[] All =
    [
        new(
            "MegaCrit.Sts2.Core.Combat.CombatManager",
            ["EndTurn", "TryEndTurn", "PlayCard", "TryPlayCard", "QueueAction", "EnqueueAction"]),
        new(
            "MegaCrit.Sts2.Core.Combat.CombatState",
            ["Apply", "Resolve", "SetTurn", "Advance", "AdvanceTurn", "Draw", "Discard", "Damage"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Combat.CombatStateTracker",
            ["Apply", "Update", "Sync", "Handle"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder",
            ["Add", "Remove", "Insert", "SetCards", "Refresh"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Multiplayer.Game.RunLocationTargetedMessageBuffer",
            ["Handle", "Dispatch"],
            PartialMatch: true),
    ];
}
