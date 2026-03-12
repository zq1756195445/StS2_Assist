namespace Sts2EventHook;

internal static class SceneHookManifest
{
    internal static readonly HookCandidate[] All =
    [
        new(
            "MegaCrit.Sts2.Core.Runs.RunState",
            ["SetRoom", "EnterRoom", "LeaveRoom", "AdvanceRoom", "SetCurrentRoom", "Transition"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Nodes.Map.Rooms.NEventRoom",
            ["Open", "Show", "Advance", "Refresh", "Setup"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton",
            ["Click", "Choose", "Select", "Submit", "Handle"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Nodes.Rewards.NRewardsScreen",
            ["Open", "Show", "Refresh", "SetRewards"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Nodes.Rewards.NCardRewardSelectionScreen",
            ["Open", "Show", "Refresh", "SetRewards"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Nodes.Shops.NMerchantInventory",
            ["Open", "Show", "Refresh", "Setup"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Nodes.Map.Rooms.NTreasureRoom",
            ["Open", "Show", "Refresh", "Setup"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.Nodes.Map.Rooms.NRestSiteRoom",
            ["Open", "Show", "Refresh", "Setup"],
            PartialMatch: true),
        new(
            "MegaCrit.Sts2.Core.SceneManagement.NSceneContainer",
            ["SetScene", "OpenScene", "ShowScene", "Transition", "Load"],
            PartialMatch: true),
    ];
}
