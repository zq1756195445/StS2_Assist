using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Text.Json;
using static Sts2ClrProbe.ProbeCommon;
using static Sts2ClrProbe.ProbeDump;
using static Sts2ClrProbe.ProbeEvent;
using static Sts2ClrProbe.ProbeReward;
using static Sts2ClrProbe.ProbeShop;
using static Sts2ClrProbe.ProbeTreasure;

bool jsonMode = args.Any(arg => string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase));
bool compactJson = args.Any(arg => string.Equals(arg, "--compact", StringComparison.OrdinalIgnoreCase));
bool includeUiCandidates = args.Any(arg => string.Equals(arg, "--with-ui-candidates", StringComparison.OrdinalIgnoreCase));
string? dumpType = ReadOptionValue(args, "--dump-type");
string? findTypes = ReadOptionValue(args, "--find-types");
int dumpLimit = TryParseIntOption(args, "--dump-limit") ?? 3;
var optionValuesToSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
if (!string.IsNullOrWhiteSpace(dumpType))
{
    optionValuesToSkip.Add(dumpType);
}
if (!string.IsNullOrWhiteSpace(findTypes))
{
    optionValuesToSkip.Add(findTypes);
}
string? dumpLimitValue = ReadOptionValue(args, "--dump-limit");
if (!string.IsNullOrWhiteSpace(dumpLimitValue))
{
    optionValuesToSkip.Add(dumpLimitValue);
}
var positionalArgs = args
    .Where(arg =>
        !string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(arg, "--compact", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(arg, "--with-ui-candidates", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(arg, "--dump-type", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(arg, "--find-types", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(arg, "--dump-limit", StringComparison.OrdinalIgnoreCase)
        && !optionValuesToSkip.Contains(arg))
    .ToArray();

var processName = positionalArgs.Length > 0 ? positionalArgs[0] : "SlayTheSpire2";
if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
{
    processName = Path.GetFileNameWithoutExtension(processName);
}

var cardPattern = positionalArgs.Length > 1 ? positionalArgs[1] : "CARD.";
var process = Process.GetProcessesByName(processName).FirstOrDefault();

if (process is null)
{
    Console.Error.WriteLine($"Process '{processName}' not found.");
    return 1;
}

using DataTarget target = DataTarget.AttachToProcess(process.Id, suspend: false);
ClrInfo clr = target.ClrVersions.Single();
ClrRuntime runtime = clr.CreateRuntime();
ClrHeap heap = runtime.Heap;

if (jsonMode)
{
    var combatRoot = FindCurrentCombatRoot(heap);
    var hand = ReadHandFromHeap(heap, combatRoot);
    var enemies = ReadEnemiesFromHeap(heap, combatRoot);
    var player = ReadPlayerStateFromHeap(heap, combatRoot);
    var currentRooms = ReadCurrentRooms(heap);
    var uiCandidates = ReadUiCandidates(heap);
    var sceneDetection = InferSceneHint(heap, hand, enemies, player, currentRooms, uiCandidates);
    var sceneHint = sceneDetection.Scene;
    var rewardCards = new List<string>();
    var treasureRelics = new List<string>();
    var shopOffers = new List<string>();
    var eventOptions = new List<string>();
    object? eventPage = null;

    switch (sceneHint)
    {
        case "reward":
            rewardCards = ReadRewardCardsFromHeap(heap);
            break;
        case "treasure":
            treasureRelics = ReadTreasureRelicsFromHeap(heap);
            break;
        case "shop":
            shopOffers = ReadShopOffersFromHeap(heap);
            break;
        case "event":
            eventOptions = ReadEventOptionsFromHeap(heap);
            eventPage = ReadEventPageFromHeap(heap);
            break;
    }
    var payload = new Dictionary<string, object?>
    {
        ["process"] = process.ProcessName,
        ["hand"] = hand,
        ["enemies"] = enemies,
        ["player"] = player,
        ["currentRooms"] = currentRooms,
        ["rewardCards"] = rewardCards,
        ["treasureRelics"] = treasureRelics,
        ["shopOffers"] = shopOffers,
        ["eventOptions"] = eventOptions,
        ["eventPage"] = eventPage,
        ["sceneHint"] = sceneHint,
        ["sceneReason"] = sceneDetection.Reason,
        ["sceneSignals"] = sceneDetection.Signals,
        ["status"] = hand.Count > 0 ? "memory(clr-hand)" : "memory(clr-attached)",
    };
    if (includeUiCandidates)
    {
        payload["uiCandidates"] = uiCandidates;
    }
    var jsonOptions = compactJson
        ? new JsonSerializerOptions()
        : new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(payload, jsonOptions));
    return 0;
}

if (!string.IsNullOrWhiteSpace(dumpType))
{
    Console.WriteLine($"Attached to PID {process.Id} ({process.ProcessName})");
    Console.WriteLine($"CLR: {clr.Version}");
    Console.WriteLine();
    Console.WriteLine($"Focused dump for type: {dumpType}");
    DumpFocusedType(heap, dumpType!, Math.Max(1, dumpLimit));
    return 0;
}

if (!string.IsNullOrWhiteSpace(findTypes))
{
    Console.WriteLine($"Attached to PID {process.Id} ({process.ProcessName})");
    Console.WriteLine($"CLR: {clr.Version}");
    Console.WriteLine();

    var patterns = findTypes
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    Console.WriteLine($"Matching types for: {string.Join(", ", patterns)}");
    var matches = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (ClrObject obj in heap.EnumerateObjects())
    {
        if (!obj.IsValid || obj.IsNull || obj.Type is null)
        {
            continue;
        }

        string? typeName = obj.Type.Name;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            continue;
        }

        if (!patterns.Any(pattern => typeName.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        matches.TryGetValue(typeName, out int count);
        matches[typeName] = count + 1;
    }

    foreach ((string typeName, int count) in matches
        .OrderByDescending(entry => entry.Value)
        .ThenBy(entry => entry.Key)
        .Take(200))
    {
        Console.WriteLine($"{count,8}  {typeName}");
    }

    return 0;
}

Console.WriteLine($"Attached to PID {process.Id} ({process.ProcessName})");
Console.WriteLine($"CLR: {clr.Version}");
Console.WriteLine();

var interestingTypes = new Dictionary<string, int>(StringComparer.Ordinal);
var matchedStrings = new List<(ulong Address, string Value)>();
var targetTypeNames = new[]
{
    "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder",
    "MegaCrit.Sts2.Core.Nodes.Cards.NCard",
    "MegaCrit.Sts2.Core.Entities.Cards.CardPile",
    "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState+CardState",
    "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState+PlayerState",
    "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState+CreatureState",
    "MegaCrit.Sts2.Core.Saves.Runs.SerializableCard",
    "MegaCrit.Sts2.Core.Combat.CombatState",
    "MegaCrit.Sts2.Core.Combat.CombatManager",
    "MegaCrit.Sts2.Core.Combat.CombatStateTracker",
    "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
    "MegaCrit.Sts2.Core.Entities.Players.Player",
    "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState",
    "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder",
    "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolderHitbox"
};

foreach (ClrObject obj in heap.EnumerateObjects())
{
    if (!obj.IsValid || obj.IsNull || obj.Type is null)
    {
        continue;
    }

    string? typeName = obj.Type.Name;
    if (string.IsNullOrWhiteSpace(typeName))
    {
        continue;
    }

    if (typeName.Contains("Card", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Hand", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Battle", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Combat", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Intent", StringComparison.OrdinalIgnoreCase))
    {
        interestingTypes.TryGetValue(typeName, out int count);
        interestingTypes[typeName] = count + 1;
    }

    if (obj.Type.IsString)
    {
        try
        {
            string value = obj.AsString();
            if (!string.IsNullOrEmpty(value) && value.Contains(cardPattern, StringComparison.OrdinalIgnoreCase))
            {
                matchedStrings.Add((obj.Address, value));
            }
        }
        catch
        {
            // Skip malformed string objects in inconsistent heap states.
        }
    }
}

Console.WriteLine("Interesting managed types");
foreach ((string type, int count) in interestingTypes
    .OrderByDescending(pair => pair.Value)
    .ThenBy(pair => pair.Key)
    .Take(80))
{
    Console.WriteLine($"{count,8}  {type}");
}

Console.WriteLine();
Console.WriteLine($"Managed strings containing '{cardPattern}'");
foreach ((ulong address, string value) in matchedStrings
    .DistinctBy(entry => entry.Value)
    .OrderBy(entry => entry.Value)
    .Take(120))
{
    Console.WriteLine($"0x{address:X}  {value}");
}

Console.WriteLine();
Console.WriteLine("Sample objects referencing card strings");
int printed = 0;
foreach (ClrObject obj in heap.EnumerateObjects())
{
    if (!obj.IsValid || obj.IsNull || obj.Type is null)
    {
        continue;
    }

    string? typeName = obj.Type.Name;
    if (string.IsNullOrWhiteSpace(typeName))
    {
        continue;
    }

    bool referencesCardString = false;
    foreach (ClrObject child in obj.EnumerateReferences(carefully: true, considerDependantHandles: false))
    {
        if (!child.IsValid || child.IsNull || child.Type is null || !child.Type.IsString)
        {
            continue;
        }

        string childValue;
        try
        {
            childValue = child.AsString();
        }
        catch
        {
            continue;
        }

        if (!string.IsNullOrEmpty(childValue) && childValue.Contains(cardPattern, StringComparison.OrdinalIgnoreCase))
        {
            referencesCardString = true;
            break;
        }
    }

    if (!referencesCardString)
    {
        continue;
    }

    Console.WriteLine($"0x{obj.Address:X}  {typeName}");
    foreach (ClrInstanceField field in obj.Type.Fields.Where(field => !field.IsObjectReference))
    {
        if (field.Type?.ElementType == ClrElementType.String)
        {
            try
            {
                string? fieldValue = field.ReadString(obj.Address, interior: false);
                if (!string.IsNullOrWhiteSpace(fieldValue))
                {
                    Console.WriteLine($"    {field.Name} = {fieldValue}");
                }
            }
            catch
            {
            }
        }
    }

    printed++;
    if (printed >= 25)
    {
        break;
    }
}

Console.WriteLine();
Console.WriteLine("Detailed target type dumps");
foreach (string targetTypeName in targetTypeNames)
{
    var matches = heap.EnumerateObjects()
        .Where(obj => obj.IsValid && !obj.IsNull && obj.Type?.Name == targetTypeName)
        .Take(8)
        .ToList();

    if (matches.Count == 0)
    {
        continue;
    }

    Console.WriteLine();
    Console.WriteLine(targetTypeName);
    foreach (ClrObject obj in matches)
    {
        Console.WriteLine($"  0x{obj.Address:X}");
        DumpObject(obj, indent: "    ");
    }
}

Console.WriteLine();
Console.WriteLine("Focused hand holder scan");
DumpFocusedType(heap, "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder", limit: 8);
DumpFocusedType(heap, "MegaCrit.Sts2.Core.Entities.Cards.CardPile", limit: 8);
DumpFocusedType(heap, "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState+CardState", limit: 12);
DumpFocusedType(heap, "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState+PlayerState", limit: 8);
DumpFocusedType(heap, "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState+CreatureState", limit: 8);
DumpFocusedType(heap, "MegaCrit.Sts2.Core.Entities.Players.Player", limit: 8);
DumpCombatEnemies(heap);
DumpCombatAllies(heap);
DumpUiCandidates(heap);

return 0;

static List<string> ReadHandFromHeap(ClrHeap heap, ClrObject? combatRoot = null)
{
    if (combatRoot is not null && combatRoot.Value.IsValid && !combatRoot.Value.IsNull)
    {
        var fromCurrentPlayerPiles = ReadHandFromCurrentPlayerCombatState(combatRoot.Value);
        if (fromCurrentPlayerPiles.Count > 0)
        {
            return fromCurrentPlayerPiles;
        }

        var fromCurrentHolders = ReadHandFromHoldersForCombatState(heap, combatRoot.Value);
        if (fromCurrentHolders.Count > 0)
        {
            return fromCurrentHolders;
        }
    }

    var fromCardPile = heap.EnumerateObjects()
        .Where(obj => obj.IsValid && !obj.IsNull && obj.Type?.Name == "MegaCrit.Sts2.Core.Entities.Cards.CardPile")
        .Select(TryReadHandFromCardPile)
        .FirstOrDefault(cards => cards is { Count: > 0 });

    if (fromCardPile is { Count: > 0 })
    {
        return fromCardPile;
    }

    return heap.EnumerateObjects()
        .Where(obj => obj.IsValid && !obj.IsNull && obj.Type?.Name == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder")
        .Select(TryReadHandHolderCard)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .ToList();
}

static List<object> ReadEnemiesFromHeap(ClrHeap heap, ClrObject? combatRoot = null)
{
    var combatState = combatRoot ?? FindCurrentCombatRoot(heap);

    if (combatState is null || !combatState.Value.IsValid || combatState.Value.IsNull || combatState.Value.Type is null)
    {
        return new List<object>();
    }

    var root = combatState.Value;

    var enemies = TryReadObjectField(root, "_enemies");
    if (enemies is null)
    {
        return new List<object>();
    }

    int size = TryReadIntField(enemies.Value, "_size") ?? 0;
    if (size <= 0)
    {
        return new List<object>();
    }

    var array = TryReadObjectField(enemies.Value, "_items");
    if (array is null || !array.Value.IsArray)
    {
        return new List<object>();
    }

    List<object> results = new();
    try
    {
        var clrArray = array.Value.AsArray();
        for (int i = 0; i < Math.Min(size, clrArray.Length); i++)
        {
            var enemy = clrArray.GetObjectValue(i);
            if (!enemy.IsValid || enemy.IsNull || enemy.Type is null)
            {
                continue;
            }

            string name = TryReadCreatureName(enemy) ?? "Enemy";
            int hp = TryReadIntField(enemy, "_currentHp") ?? 0;
            int maxHp = TryReadIntField(enemy, "_maxHp") ?? hp;
            int block = TryReadIntField(enemy, "_block")
                ?? TryReadIntField(enemy, "<Block>k__BackingField")
                ?? 0;
            string intent = TryReadCreatureIntent(enemy) ?? (block > 0 ? $"Block {block}" : "Unknown");

            results.Add(new
            {
                name,
                hp,
                maxHp,
                block,
                intent,
            });
        }
    }
    catch
    {
        return new List<object>();
    }

    return results;
}

static object? ReadPlayerStateFromHeap(ClrHeap heap, ClrObject? combatRoot = null)
{
    var combatState = combatRoot ?? FindCurrentCombatRoot(heap);

    if (combatState is null || !combatState.Value.IsValid || combatState.Value.IsNull || combatState.Value.Type is null)
    {
        return null;
    }

    var root = combatState.Value;

    var ally = TryReadPrimaryAlly(root);
    var player = TryReadPlayerObject(root);
    if (player is null && ally is not null)
    {
        player = TryReadObjectFieldByNames(ally.Value, "<Player>k__BackingField", "_player");
    }

    if ((ally is null || !ally.Value.IsValid || ally.Value.IsNull || ally.Value.Type is null)
        && (player is null || !player.Value.IsValid || player.Value.IsNull || player.Value.Type is null))
    {
        return null;
    }

    int? hp = ally is not null
        ? TryReadIntFieldByNames(
            ally.Value,
            "_currentHp", "<CurrentHp>k__BackingField", "_hp", "<Hp>k__BackingField")
        : null;
    int? maxHp = ally is not null
        ? TryReadIntFieldByNames(
            ally.Value,
            "_maxHp", "<MaxHp>k__BackingField", "_maximumHp", "<MaximumHp>k__BackingField")
        : null;

    if (player is not null)
    {
        hp ??= TryReadIntFieldByNames(player.Value,
        "_currentHp", "<CurrentHp>k__BackingField", "_hp", "<Hp>k__BackingField");
        maxHp ??= TryReadIntFieldByNames(player.Value,
        "_maxHp", "<MaxHp>k__BackingField", "_maximumHp", "<MaximumHp>k__BackingField");
    }

    int? energy = player is not null
        ? TryReadIntFieldByNames(player.Value,
            "_currentEnergy", "<CurrentEnergy>k__BackingField", "_energy", "<Energy>k__BackingField",
            "_currentMana", "<CurrentMana>k__BackingField", "_mana", "<Mana>k__BackingField")
        : null;

    if (energy is null && player is not null)
    {
        var combatPlayerState = TryReadObjectFieldByNames(
            player.Value,
            "<PlayerCombatState>k__BackingField",
            "_playerCombatState",
            "_combatState");
        if (combatPlayerState is not null)
        {
            energy = TryReadIntFieldByNames(
                combatPlayerState.Value,
                "_energy",
                "<Energy>k__BackingField",
                "_currentEnergy",
                "<CurrentEnergy>k__BackingField",
                "_mana",
                "<Mana>k__BackingField");
        }
    }

    if (energy is null)
    {
        energy = (player is not null ? TryReadEnergyFromRelatedObjects(player.Value) : null)
            ?? (ally is not null ? TryReadEnergyFromRelatedObjects(ally.Value) : null)
            ?? TryReadEnergyFromRelatedObjects(root);
    }

    if (hp is null && maxHp is null && energy is null)
    {
        return null;
    }

    return new
    {
        hp,
        maxHp,
        energy,
    };
}

static object ReadUiCandidates(ClrHeap heap)
{
    var candidates = ScanUiCandidates(heap);
    return new
    {
        combat = candidates.Where(candidate => candidate.Scene == "combat").Take(8).Select(ToUiCandidatePayload).ToList(),
        reward = candidates.Where(candidate => candidate.Scene == "reward").Take(8).Select(ToUiCandidatePayload).ToList(),
        rest = candidates.Where(candidate => candidate.Scene == "rest").Take(8).Select(ToUiCandidatePayload).ToList(),
        shop = candidates.Where(candidate => candidate.Scene == "shop").Take(8).Select(ToUiCandidatePayload).ToList(),
        eventScene = candidates.Where(candidate => candidate.Scene == "event").Take(8).Select(ToUiCandidatePayload).ToList(),
        map = candidates.Where(candidate => candidate.Scene == "map").Take(8).Select(ToUiCandidatePayload).ToList(),
    };
}

static List<object> ReadCurrentRooms(ClrHeap heap)
{
    foreach (ClrObject obj in heap.EnumerateObjects())
    {
        if (!obj.IsValid || obj.IsNull || obj.Type?.Name != "MegaCrit.Sts2.Core.Runs.RunState")
        {
            continue;
        }

        var currentRooms = TryReadObjectField(obj, "_currentRooms");
        if (currentRooms is null || !currentRooms.Value.IsValid || currentRooms.Value.IsNull)
        {
            return new List<object>();
        }

        return ReadObjectsFromList(currentRooms)
            .Select(room => new
            {
                address = room.Address,
                typeName = room.Type?.Name ?? string.Empty,
            })
            .Cast<object>()
            .ToList();
    }

    return new List<object>();
}

static SceneDetectionResult InferSceneHint(
    ClrHeap heap,
    List<string> hand,
    List<object> enemies,
    object? player,
    List<object> currentRooms,
    object uiCandidates)
{
    var roomsJson = JsonSerializer.SerializeToElement(currentRooms);
    bool IsActiveUiObject(ClrObject obj, bool requireEnabled = false)
    {
        bool? isDisposed = TryReadBoolFieldByNames(
            obj,
            "_disposed",
            "<Disposed>k__BackingField",
            "_isDisposed");
        if (isDisposed == true)
        {
            return false;
        }

        bool? isOpen = TryReadBoolFieldByNames(
            obj,
            "<IsOpen>k__BackingField",
            "_isOpen");
        if (isOpen == false)
        {
            return false;
        }

        if (requireEnabled)
        {
            bool? isEnabled = TryReadBoolFieldByNames(
                obj,
                "_isEnabled",
                "<IsEnabled>k__BackingField",
                "_enabled");
            if (isEnabled == false)
            {
                return false;
            }
        }

        return true;
    }

    bool HasActiveHeapType(string exactTypeName, bool requireEnabled = false)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsNull || obj.Type?.Name != exactTypeName)
            {
                continue;
            }

            if (IsActiveUiObject(obj, requireEnabled))
            {
                return true;
            }
        }

        return false;
    }

    bool hasActiveRewardScreen = HasActiveHeapType("MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen")
        || HasActiveHeapType("MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen")
        || HasActiveHeapType("MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardAlternativeButton", requireEnabled: true)
        || HasActiveHeapType("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton", requireEnabled: true);
    string? currentRoomScene = InferSceneFromCurrentRooms(roomsJson);
    bool hasActiveEventPage = HasActiveEventPage(heap);
    bool hasBattleState = hand.Count > 0 || enemies.Count > 0;
    bool hasPlayerState = player is not null;
    bool hasMapOverlay = HasActiveHeapType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen")
        || HasActiveHeapType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapOverlay");

    var signals = new Dictionary<string, object?>
    {
        ["hasMapOverlay"] = hasMapOverlay,
        ["hasActiveRewardScreen"] = hasActiveRewardScreen,
        ["hasActiveEventPage"] = hasActiveEventPage,
        ["hasBattleState"] = hasBattleState,
        ["hasPlayerState"] = hasPlayerState,
        ["currentRoomScene"] = currentRoomScene,
        ["currentRoomCount"] = currentRooms.Count,
        ["handCount"] = hand.Count,
        ["enemyCount"] = enemies.Count,
    };

    if (hasMapOverlay)
    {
        return new SceneDetectionResult("map-overlay", "map-overlay", signals);
    }

    if (hasActiveRewardScreen)
    {
        return new SceneDetectionResult("reward", "reward-screen", signals);
    }

    if (hasActiveEventPage)
    {
        return new SceneDetectionResult("event", "event-page", signals);
    }

    if (hasBattleState)
    {
        return new SceneDetectionResult("battle", "battle-state", signals);
    }

    if (!string.IsNullOrWhiteSpace(currentRoomScene))
    {
        return new SceneDetectionResult(currentRoomScene!, "current-rooms", signals);
    }

    if (hasPlayerState)
    {
        return new SceneDetectionResult("battle", "player-state", signals);
    }

    return new SceneDetectionResult("unknown", "no-signal", signals);
}

static string? InferSceneFromCurrentRooms(JsonElement roomsJson)
{
    if (roomsJson.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    bool hasCombat = false;
    bool hasReward = false;
    bool hasShop = false;
    bool hasTreasure = false;
    bool hasEvent = false;
    bool hasRest = false;

    foreach (JsonElement item in roomsJson.EnumerateArray())
    {
        if (!item.TryGetProperty("typeName", out JsonElement typeNameElement))
        {
            continue;
        }

        string? typeName = typeNameElement.GetString();
        if (string.IsNullOrWhiteSpace(typeName))
        {
            continue;
        }

        if (typeName.Contains("CombatRoom", StringComparison.OrdinalIgnoreCase))
        {
            hasCombat = true;
            continue;
        }

        if (typeName.Contains("EventRoom", StringComparison.OrdinalIgnoreCase))
        {
            hasEvent = true;
            continue;
        }

        if (typeName.Contains("TreasureRoom", StringComparison.OrdinalIgnoreCase))
        {
            hasTreasure = true;
            continue;
        }

        if (typeName.Contains("Merchant", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Shop", StringComparison.OrdinalIgnoreCase))
        {
            hasShop = true;
            continue;
        }

        if (typeName.Contains("RestSite", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Campfire", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Bonfire", StringComparison.OrdinalIgnoreCase))
        {
            hasRest = true;
            continue;
        }

        if (typeName.Contains("Reward", StringComparison.OrdinalIgnoreCase))
        {
            hasReward = true;
        }
    }

    if (hasCombat)
    {
        return "battle";
    }

    if (hasReward)
    {
        return "reward";
    }

    if (hasShop)
    {
        return "shop";
    }

    if (hasTreasure)
    {
        return "treasure";
    }

    if (hasEvent)
    {
        return "event";
    }

    if (hasRest)
    {
        return "rest";
    }

    return null;
}

static ClrObject? FindCurrentCombatRoot(ClrHeap heap)
{
    Dictionary<ulong, (ClrObject CombatState, int Score)> candidates = new();

    void AddCandidate(ClrObject combatState, int score, string reason)
    {
        if (!combatState.IsValid || combatState.IsNull || combatState.Type?.Name != "MegaCrit.Sts2.Core.Combat.CombatState")
        {
            return;
        }

        if (!candidates.TryGetValue(combatState.Address, out var existing))
        {
            existing = (combatState, 0);
        }

        existing.Score += score;
        candidates[combatState.Address] = existing;
    }

    foreach (ClrObject obj in heap.EnumerateObjects())
    {
        if (!obj.IsValid || obj.IsNull || obj.Type is null)
        {
            continue;
        }

        string? typeName = obj.Type.Name;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            continue;
        }

        if (typeName == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder")
        {
            var handNode = TryReadObjectField(obj, "_hand");
            var combatState = handNode is not null ? TryReadObjectField(handNode.Value, "_combatState") : null;
            if (combatState is not null)
            {
                AddCandidate(combatState.Value, 120, "hand-holder");
            }
        }
        else if (typeName == "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand")
        {
            var combatState = TryReadObjectField(obj, "_combatState");
            if (combatState is not null)
            {
                int mode = TryReadIntField(obj, "_currentMode") ?? 0;
                AddCandidate(combatState.Value, mode == 1 ? 90 : 60, "player-hand");
            }
        }
        else if (typeName == "MegaCrit.Sts2.Core.Nodes.Combat.NDrawPileButton"
            || typeName == "MegaCrit.Sts2.Core.Nodes.Combat.NDiscardPileButton")
        {
            var combatState = TryReadObjectField(obj, "_combatState");
            if (combatState is not null)
            {
                AddCandidate(combatState.Value, 35, typeName.EndsWith("NDrawPileButton", StringComparison.Ordinal) ? "draw-pile" : "discard-pile");
            }
        }
        else if (typeName == "MegaCrit.Sts2.Core.Combat.CombatState")
        {
            AddCandidate(obj, ScoreCombatState(obj), "combat-state");
        }
    }

    return candidates.Values
        .OrderByDescending(candidate => candidate.Score)
        .ThenByDescending(candidate => candidate.CombatState.Address)
        .Select(candidate => (ClrObject?)candidate.CombatState)
        .FirstOrDefault();
}

static int ScoreCombatState(ClrObject combatState)
{
    int score = 0;

    var allies = ReadCreaturesFromList(TryReadObjectField(combatState, "_allies"));
    var enemies = ReadCreaturesFromList(TryReadObjectField(combatState, "_enemies"));
    int liveAllies = allies.Count(creature => (TryReadIntFieldByNames(creature, "_currentHp", "<CurrentHp>k__BackingField", "_hp", "<Hp>k__BackingField") ?? 0) > 0);
    int liveEnemies = enemies.Count(creature => (TryReadIntFieldByNames(creature, "_currentHp", "<CurrentHp>k__BackingField", "_hp", "<Hp>k__BackingField") ?? 0) > 0);

    score += liveAllies * 40;
    score += liveEnemies * 50;
    score += allies.Count * 10;
    score += enemies.Count * 15;

    var player = TryReadPlayerObject(combatState);
    if (player is not null)
    {
        score += 35;
        if (TryReadIntFieldByNames(player.Value, "_currentHp", "<CurrentHp>k__BackingField", "_hp", "<Hp>k__BackingField") is int playerHp && playerHp > 0)
        {
            score += 25;
        }

        var playerCombatState = TryReadObjectFieldByNames(
            player.Value,
            "<PlayerCombatState>k__BackingField",
            "_playerCombatState",
            "_combatState");
        if (playerCombatState is not null && TryReadIntFieldByNames(playerCombatState.Value, "_energy", "<Energy>k__BackingField", "_currentEnergy", "<CurrentEnergy>k__BackingField") is not null)
        {
            score += 20;
        }
    }

    return score;
}

static List<string> ReadHandFromHoldersForCombatState(ClrHeap heap, ClrObject combatState)
{
    List<(int Order, string Name)> cards = new();

    foreach (ClrObject holder in heap.EnumerateObjects())
    {
        if (!holder.IsValid || holder.IsNull || holder.Type?.Name != "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder")
        {
            continue;
        }

        var handNode = TryReadObjectField(holder, "_hand");
        var holderCombatState = handNode is not null ? TryReadObjectField(handNode.Value, "_combatState") : null;
        if (holderCombatState is null || holderCombatState.Value.Address != combatState.Address)
        {
            continue;
        }

        string? cardName = TryReadHandHolderCard(holder);
        if (string.IsNullOrWhiteSpace(cardName))
        {
            continue;
        }

        int order = TryReadIntField(holder, "_holderIndex")
            ?? TryReadIntField(holder, "_handIndex")
            ?? TryReadIntField(holder, "_index")
            ?? cards.Count;
        cards.Add((order, cardName!));
    }

    return cards
        .OrderBy(card => card.Order)
        .ThenBy(card => card.Name, StringComparer.Ordinal)
        .Select(card => card.Name)
        .ToList();
}

static List<string> ReadHandFromCurrentPlayerCombatState(ClrObject combatState)
{
    var player = TryReadPlayerObject(combatState);
    if (player is null || !player.Value.IsValid || player.Value.IsNull)
    {
        return new List<string>();
    }

    var playerCombatState = TryReadObjectFieldByNames(
        player.Value,
        "<PlayerCombatState>k__BackingField",
        "_playerCombatState",
        "_combatState");
    if (playerCombatState is null || !playerCombatState.Value.IsValid || playerCombatState.Value.IsNull)
    {
        return new List<string>();
    }

    var handPile = TryReadObjectFieldByNames(
        playerCombatState.Value,
        "<Hand>k__BackingField",
        "_hand");
    if (handPile is null)
    {
        var pileObjects = ReadObjectsFromList(TryReadObjectField(playerCombatState.Value, "_piles"));
        var pileCandidate = pileObjects
            .FirstOrDefault(pile => TryReadIntField(pile, "<Type>k__BackingField") == 2);
        if (pileCandidate.IsValid && !pileCandidate.IsNull)
        {
            handPile = pileCandidate;
        }
    }

    if (handPile is null || !handPile.Value.IsValid || handPile.Value.IsNull)
    {
        return new List<string>();
    }

    var cards = TryReadHandFromCardPile(handPile.Value);
    return cards ?? new List<string>();
}

static void DumpUiCandidates(ClrHeap heap)
{
    var candidates = ScanUiCandidates(heap);
    if (candidates.Count == 0)
    {
        return;
    }

    Console.WriteLine();
    Console.WriteLine("UI scene candidates");
    foreach (var group in candidates.GroupBy(candidate => candidate.Scene))
    {
        Console.WriteLine($"  {group.Key}");
        foreach (var candidate in group.Take(8))
        {
            string flags = candidate.Flags.Count == 0
                ? "-"
                : string.Join(", ", candidate.Flags.Select(pair => $"{pair.Key}={pair.Value}"));
            Console.WriteLine($"    0x{candidate.Address:X} {candidate.TypeName} [{flags}]");
        }
    }
}

static List<(string Scene, string TypeName, ulong Address, Dictionary<string, string> Flags)> ScanUiCandidates(ClrHeap heap)
{
    var sceneKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["combat"] = new[] { "combat", "battle", "room" },
        ["reward"] = new[] { "reward", "cardreward", "loot", "treasure" },
        ["rest"] = new[] { "rest", "campfire", "bonfire" },
        ["shop"] = new[] { "shop", "merchant", "store" },
        ["event"] = new[] { "event", "ancient", "page", "choice" },
        ["map"] = new[] { "map", "nodepath", "pathselect", "overlay" },
    };
    var results = new List<(string Scene, string TypeName, ulong Address, Dictionary<string, string> Flags)>();
    var seen = new HashSet<string>(StringComparer.Ordinal);

    foreach (ClrObject obj in heap.EnumerateObjects())
    {
        if (!obj.IsValid || obj.IsNull || obj.Type is null)
        {
            continue;
        }

        string? typeName = obj.Type.Name;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            continue;
        }

        string? scene = MatchUiScene(typeName, sceneKeywords);
        if (scene is null)
        {
            continue;
        }

        var flags = ReadUiFlags(obj);
        if (flags.Count == 0
            && !typeName.Contains("Screen", StringComparison.OrdinalIgnoreCase)
            && !typeName.Contains("Overlay", StringComparison.OrdinalIgnoreCase)
            && !typeName.Contains("Room", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        string dedupeKey = $"{scene}|{typeName}|{string.Join("|", flags.Select(pair => $"{pair.Key}:{pair.Value}"))}";
        if (!seen.Add(dedupeKey))
        {
            continue;
        }

        results.Add((scene, typeName, obj.Address, flags));
    }

    return results
        .OrderBy(candidate => candidate.Scene)
        .ThenByDescending(candidate => candidate.Flags.Count)
        .ThenBy(candidate => candidate.TypeName)
        .ToList();
}

static object ToUiCandidatePayload(
    (string Scene, string TypeName, ulong Address, Dictionary<string, string> Flags) candidate)
{
    return new
    {
        scene = candidate.Scene,
        typeName = candidate.TypeName,
        address = candidate.Address,
        flags = candidate.Flags,
    };
}

static string? MatchUiScene(
    string typeName,
    Dictionary<string, string[]> sceneKeywords)
{
    foreach (var (scene, keywords) in sceneKeywords)
    {
        if (keywords.Any(keyword => typeName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return scene;
        }
    }

    return null;
}

static Dictionary<string, string> ReadUiFlags(ClrObject obj)
{
    var interestingFlags = new[]
    {
        "active",
        "visible",
        "open",
        "opened",
        "enabled",
        "show",
        "showing",
        "hidden",
        "closed",
        "current",
        "selected",
        "interactable",
    };
    var flags = new Dictionary<string, string>(StringComparer.Ordinal);
    if (obj.Type is null)
    {
        return flags;
    }

    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type))
    {
        foreach (ClrInstanceField field in type.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                continue;
            }

            string fieldName = field.Name;
            if (!interestingFlags.Any(flag => fieldName.Contains(flag, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                if (field.ElementType == ClrElementType.Boolean)
                {
                    bool value = field.Read<bool>(obj.Address, interior: false);
                    if (value || fieldName.Contains("hidden", StringComparison.OrdinalIgnoreCase))
                    {
                        flags[fieldName] = value.ToString();
                    }
                    continue;
                }

                if (field.ElementType == ClrElementType.Int32)
                {
                    int value = field.Read<int>(obj.Address, interior: false);
                    if (value != 0)
                    {
                        flags[fieldName] = value.ToString();
                    }
                    continue;
                }

                if (field.ElementType == ClrElementType.String)
                {
                    string? value = field.ReadString(obj.Address, interior: false);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        flags[fieldName] = value;
                    }
                }
            }
            catch
            {
                // Best-effort probe only.
            }
        }
    }

    return flags;
}

static string? ReadOptionValue(string[] args, string optionName)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static int? TryParseIntOption(string[] args, string optionName)
{
    string? value = ReadOptionValue(args, optionName);
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return int.TryParse(value, out int parsed) ? parsed : null;
}

internal sealed record SceneDetectionResult(string Scene, string Reason, Dictionary<string, object?> Signals);

