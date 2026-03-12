using Microsoft.Diagnostics.Runtime;
using System.Diagnostics;
using System.Text.Json;

bool jsonMode = args.Any(arg => string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase));
var positionalArgs = args
    .Where(arg => !string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
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
    var rewardCards = ReadRewardCardsFromHeap(heap);
    var uiCandidates = ReadUiCandidates(heap);
    var sceneHint = InferSceneHint(hand, enemies, player, uiCandidates);
    var payload = new
    {
        process = process.ProcessName,
        hand,
        enemies,
        player,
        rewardCards,
        sceneHint,
        uiCandidates,
        status = hand.Count > 0 ? "memory(clr-hand)" : "memory(clr-attached)",
    };
    Console.WriteLine(JsonSerializer.Serialize(payload));
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
        shop = candidates.Where(candidate => candidate.Scene == "shop").Take(8).Select(ToUiCandidatePayload).ToList(),
        eventScene = candidates.Where(candidate => candidate.Scene == "event").Take(8).Select(ToUiCandidatePayload).ToList(),
        map = candidates.Where(candidate => candidate.Scene == "map").Take(8).Select(ToUiCandidatePayload).ToList(),
    };
}

static string InferSceneHint(List<string> hand, List<object> enemies, object? player, object uiCandidates)
{
    var uiJson = JsonSerializer.SerializeToElement(uiCandidates);
    bool HasType(string bucket, params string[] typeNames)
    {
        if (!uiJson.TryGetProperty(bucket, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in items.EnumerateArray())
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

            if (typeNames.Any(candidate => typeName.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    bool HasFlag(string bucket, string typeNameFragment, string flagName, string expectedValue)
    {
        if (!uiJson.TryGetProperty(bucket, out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            string? typeName = item.TryGetProperty("typeName", out JsonElement typeNameElement)
                ? typeNameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(typeName)
                || !typeName.Contains(typeNameFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("flags", out JsonElement flags) || flags.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty flag in flags.EnumerateObject())
            {
                if (!string.Equals(flag.Name, flagName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(flag.Value.GetString(), expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    if (hand.Count > 0 || enemies.Count > 0 || player is not null)
    {
        return "battle";
    }

    if (HasFlag("map", "NMapScreen", "<IsOpen>k__BackingField", "True"))
    {
        return "map-overlay";
    }

    if (HasType("shop", "Nodes.Screens.Shops.NMerchant"))
    {
        return "shop";
    }

    if (HasType("reward", "Nodes.Screens.NRewardsScreen", "Nodes.Rewards.NRewardButton"))
    {
        return "reward";
    }

    if (HasType("eventScene", "Nodes.Events.NEventOptionButton", "Nodes.Screens.NAncientBgContainer"))
    {
        return "event";
    }

    return "unknown";
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

static List<ClrObject> ReadCreaturesFromList(ClrObject? listObject)
{
    return ReadObjectsFromList(listObject)
        .Where(LooksLikeCreature)
        .ToList();
}

static List<ClrObject> ReadObjectsFromList(ClrObject? listObject)
{
    if (listObject is null || listObject.Value.Type is null)
    {
        return new List<ClrObject>();
    }

    int size = TryReadIntField(listObject.Value, "_size") ?? 0;
    if (size <= 0)
    {
        return new List<ClrObject>();
    }

    var array = TryReadObjectField(listObject.Value, "_items");
    if (array is null || !array.Value.IsArray)
    {
        return new List<ClrObject>();
    }

    List<ClrObject> results = new();
    try
    {
        var clrArray = array.Value.AsArray();
        for (int i = 0; i < Math.Min(size, clrArray.Length); i++)
        {
            var item = clrArray.GetObjectValue(i);
            if (!item.IsValid || item.IsNull || item.Type is null)
            {
                continue;
            }

            results.Add(item);
        }
    }
    catch
    {
        return new List<ClrObject>();
    }

    return results;
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

static List<string>? TryReadHandFromCardPile(ClrObject pile)
{
    if (pile.Type is null)
    {
        return null;
    }

    int? pileType = TryReadIntField(pile, "<Type>k__BackingField");
    if (pileType != 2)
    {
        return null;
    }

    var cardsList = TryReadObjectField(pile, "_cards");
    if (cardsList is null || cardsList.Value.Type is null)
    {
        return null;
    }

    var array = TryReadObjectField(cardsList.Value, "_items");
    if (array is null || !array.Value.IsArray)
    {
        return null;
    }

    int size = TryReadIntField(cardsList.Value, "_size") ?? 0;
    if (size <= 0)
    {
        return null;
    }

    List<string> cards = new();
    try
    {
        ClrArray clrArray = array.Value.AsArray();
        int count = Math.Min(size, clrArray.Length);
        for (int i = 0; i < count; i++)
        {
            ClrObject item = clrArray.GetObjectValue(i);
            if (!item.IsValid || item.IsNull || item.Type is null)
            {
                continue;
            }

            string? cardName = TryReadCardName(item);
            if (!string.IsNullOrWhiteSpace(cardName))
            {
                cards.Add(cardName);
            }
        }
    }
    catch
    {
        return null;
    }

    return cards;
}

static string? TryReadHandHolderCard(ClrObject holder)
{
    var model = TryReadObjectField(holder, "_model");
    return model is null ? null : TryReadCardName(model.Value);
}

static string? TryReadRewardCardName(ClrObject card)
{
    string? modelEntry = TryReadModelEntry(card);
    if (!string.IsNullOrWhiteSpace(modelEntry)
        && modelEntry.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeCardEntry(modelEntry);
    }

    string? typeName = card.Type?.Name;
    if (string.IsNullOrWhiteSpace(typeName))
    {
        return null;
    }

    if (typeName.Contains(".Models.Cards.", StringComparison.Ordinal)
        || typeName.Contains(".Entities.Cards.", StringComparison.Ordinal))
    {
        return NormalizeCardTypeName(typeName);
    }

    return null;
}

static string? TryReadCardName(ClrObject card)
{
    string? modelEntry = TryReadModelEntry(card);
    if (!string.IsNullOrWhiteSpace(modelEntry))
    {
        return NormalizeCardEntry(modelEntry);
    }

    string? typeName = card.Type?.Name;
    if (string.IsNullOrWhiteSpace(typeName))
    {
        return null;
    }

    return NormalizeCardTypeName(typeName);
}

static string? TryReadCreatureName(ClrObject creature)
{
    var monster = TryReadObjectField(creature, "<Monster>k__BackingField")
        ?? TryReadObjectField(creature, "_monster");
    if (monster is not null)
    {
        string? modelEntry = TryReadModelEntry(monster.Value);
        if (!string.IsNullOrWhiteSpace(modelEntry))
        {
            return NormalizeModelEntry(modelEntry);
        }

        string? typeName = monster.Value.Type?.Name;
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            return NormalizeModelTypeName(typeName);
        }
    }

    string? creatureType = creature.Type?.Name;
    return string.IsNullOrWhiteSpace(creatureType) ? null : NormalizeModelTypeName(creatureType);
}

static string? TryReadCreatureIntent(ClrObject creature)
{
    var monster = TryReadObjectField(creature, "<Monster>k__BackingField")
        ?? TryReadObjectField(creature, "_monster");
    if (monster is null)
    {
        return null;
    }

    var nextMove = TryReadObjectField(monster.Value, "<NextMove>k__BackingField");
    if (nextMove is null)
    {
        var moveStateMachine = TryReadObjectField(monster.Value, "_moveStateMachine");
        if (moveStateMachine is not null)
        {
            nextMove = TryReadObjectField(moveStateMachine.Value, "_currentState")
                ?? TryReadObjectField(moveStateMachine.Value, "_initialState");
        }
    }
    if (nextMove is null)
    {
        return null;
    }

    string? stateId = TryReadStringField(nextMove.Value, "<StateId>k__BackingField");
    var followUpState = TryReadObjectField(nextMove.Value, "<FollowUpState>k__BackingField");
    string? followUpStateId = followUpState is null
        ? null
        : TryReadStringField(followUpState.Value, "<StateId>k__BackingField");
    string? intentKind = TryReadIntentKind(nextMove.Value);

    if (string.IsNullOrWhiteSpace(intentKind) && string.IsNullOrWhiteSpace(stateId))
    {
        return null;
    }

    string label = string.IsNullOrWhiteSpace(intentKind) ? "Unknown" : intentKind!;
    if (!string.IsNullOrWhiteSpace(stateId))
    {
        label = $"{label} ({NormalizeStateId(stateId!)})";
    }

    if (!string.IsNullOrWhiteSpace(followUpStateId))
    {
        label = $"{label} -> {NormalizeStateId(followUpStateId!)}";
    }

    return label;
}

static string? TryReadIntentKind(ClrObject moveState)
{
    var intents = TryReadObjectField(moveState, "<Intents>k__BackingField");
    if (intents is null || !intents.Value.IsArray)
    {
        return null;
    }

    try
    {
        var array = intents.Value.AsArray();
        if (array.Length == 0)
        {
            return null;
        }

        var intent = array.GetObjectValue(0);
        string typeName = intent.Type?.Name ?? string.Empty;
        if (typeName.Contains("SingleAttack", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Attack", StringComparison.OrdinalIgnoreCase))
        {
            return "Attack";
        }

        if (typeName.Contains("Debuff", StringComparison.OrdinalIgnoreCase))
        {
            return "Debuff";
        }

        if (typeName.Contains("Buff", StringComparison.OrdinalIgnoreCase))
        {
            return "Buff";
        }

        if (typeName.Contains("Block", StringComparison.OrdinalIgnoreCase))
        {
            return "Block";
        }

        return NormalizeModelTypeName(typeName).Replace(" Intent", "");
    }
    catch
    {
        return null;
    }
}

static string? TryReadModelEntry(ClrObject obj)
{
    var id = TryReadObjectField(obj, "<Id>k__BackingField") ?? TryReadObjectField(obj, "_id");
    if (id is null)
    {
        return null;
    }

    return TryReadStringField(id.Value, "<Entry>k__BackingField")
        ?? TryReadStringField(id.Value, "Entry");
}

static List<string> ReadRewardCardsFromHeap(ClrHeap heap)
{
    var sources = heap.EnumerateObjects()
        .Where(obj => obj.IsValid && !obj.IsNull && obj.Type?.Name is string typeName
            && (typeName.Contains("CardRewardAlternative", StringComparison.Ordinal)
                || typeName.Contains("NCardRewardSelectionScreen", StringComparison.Ordinal)
                || typeName.Contains("NRewardsScreen", StringComparison.Ordinal)))
        .Take(16)
        .ToList();

    if (sources.Count == 0)
    {
        return new List<string>();
    }

    HashSet<string> cards = new(StringComparer.OrdinalIgnoreCase);
    foreach (ClrObject source in sources)
    {
        CollectCardNamesFromObject(source, cards, depth: 0, maxDepth: 4, new HashSet<ulong>());
        if (cards.Count >= 3)
        {
            break;
        }
    }

    return cards.Take(3).ToList();
}

static void CollectCardNamesFromObject(
    ClrObject obj,
    HashSet<string> cards,
    int depth,
    int maxDepth,
    HashSet<ulong> visited)
{
    if (!obj.IsValid || obj.IsNull || obj.Type is null || !visited.Add(obj.Address))
    {
        return;
    }

    string? cardName = TryReadRewardCardName(obj);
    if (!string.IsNullOrWhiteSpace(cardName))
    {
        cards.Add(cardName!);
        if (cards.Count >= 3)
        {
            return;
        }
    }

    if (depth >= maxDepth)
    {
        return;
    }

    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type))
    {
        foreach (ClrInstanceField field in type.Fields.Where(field => field.IsObjectReference))
        {
            try
            {
                ulong address = field.ReadObject(obj.Address, interior: false);
                if (address == 0)
                {
                    continue;
                }

                ClrObject child = obj.Type.Heap.GetObject(address);
                if (!child.IsValid || child.IsNull)
                {
                    continue;
                }

                if (child.IsArray)
                {
                    TryCollectCardNamesFromArray(child, cards, depth + 1, maxDepth, visited);
                }
                else
                {
                    CollectCardNamesFromObject(child, cards, depth + 1, maxDepth, visited);
                }

                if (cards.Count >= 3)
                {
                    return;
                }
            }
            catch
            {
                // Best-effort traversal only.
            }
        }
    }
}

static void TryCollectCardNamesFromArray(
    ClrObject obj,
    HashSet<string> cards,
    int depth,
    int maxDepth,
    HashSet<ulong> visited)
{
    try
    {
        ClrArray array = obj.AsArray();
        int count = Math.Min(array.Length, 12);
        for (int i = 0; i < count; i++)
        {
            ClrObject child = array.GetObjectValue(i);
            if (!child.IsValid || child.IsNull)
            {
                continue;
            }

            CollectCardNamesFromObject(child, cards, depth, maxDepth, visited);
            if (cards.Count >= 3)
            {
                return;
            }
        }
    }
    catch
    {
        // Ignore inconsistent heap arrays.
    }
}

static ClrObject? TryReadObjectField(ClrObject obj, string fieldName)
{
    if (obj.Type is null)
    {
        return null;
    }

    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type))
    {
        ClrInstanceField? field = type.Fields.FirstOrDefault(field => field.Name == fieldName && field.IsObjectReference);
        if (field is null)
        {
            continue;
        }

        ulong address = field.ReadObject(obj.Address, interior: false);
        if (address == 0)
        {
            return null;
        }

        return obj.Type.Heap.GetObject(address);
    }

    return null;
}

static ClrObject? TryReadObjectFieldByNames(ClrObject obj, params string[] fieldNames)
{
    foreach (string fieldName in fieldNames)
    {
        var value = TryReadObjectField(obj, fieldName);
        if (value is not null)
        {
            return value;
        }
    }

    return null;
}

static string? TryReadStringField(ClrObject obj, string fieldName)
{
    if (obj.Type is null)
    {
        return null;
    }

    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type))
    {
        ClrInstanceField? field = type.Fields.FirstOrDefault(field =>
            field.Name == fieldName && field.ElementType == ClrElementType.String);
        if (field is null)
        {
            continue;
        }

        return field.ReadString(obj.Address, interior: false);
    }

    return null;
}

static int? TryReadIntField(ClrObject obj, string fieldName)
{
    if (obj.Type is null)
    {
        return null;
    }

    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type))
    {
        ClrInstanceField? field = type.Fields.FirstOrDefault(field => field.Name == fieldName);
        if (field is null || field.ElementType != ClrElementType.Int32)
        {
            continue;
        }

        return field.Read<int>(obj.Address, interior: false);
    }

    return null;
}

static int? TryReadIntFieldByNames(ClrObject obj, params string[] fieldNames)
{
    foreach (string fieldName in fieldNames)
    {
        int? value = TryReadIntField(obj, fieldName);
        if (value is not null)
        {
            return value;
        }
    }

    return null;
}

static ClrObject? TryReadPlayerObject(ClrObject combatState)
{
    var primaryAlly = TryReadPrimaryAlly(combatState);
    if (primaryAlly is not null)
    {
        var playerFromAlly = TryReadObjectFieldByNames(primaryAlly.Value, "<Player>k__BackingField", "_player");
        if (playerFromAlly is not null)
        {
            return playerFromAlly;
        }
    }

    var direct = TryReadObjectFieldByNames(
        combatState,
        "_player",
        "_mainPlayer",
        "_localPlayer",
        "_playerEntity",
        "_hero",
        "_character",
        "<Player>k__BackingField",
        "<Character>k__BackingField");
    if (direct is not null)
    {
        return direct;
    }

    if (combatState.Type is null)
    {
        return null;
    }

    foreach (ClrType type in EnumerateTypeHierarchy(combatState.Type))
    {
        foreach (ClrInstanceField field in type.Fields.Where(field => field.IsObjectReference))
        {
            string name = field.Name;
            if (!name.Contains("player", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("hero", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("character", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = TryReadObjectField(combatState, name);
            if (candidate is null || !candidate.Value.IsValid || candidate.Value.IsNull)
            {
                continue;
            }

            if (LooksLikeCreature(candidate.Value))
            {
                return candidate;
            }
        }
    }

    return null;
}

static ClrObject? TryReadPrimaryAlly(ClrObject combatState)
{
    var allies = TryReadObjectField(combatState, "_allies");
    if (allies is null || allies.Value.Type is null)
    {
        return null;
    }

    int size = TryReadIntField(allies.Value, "_size") ?? 0;
    if (size <= 0)
    {
        return null;
    }

    var array = TryReadObjectField(allies.Value, "_items");
    if (array is null || !array.Value.IsArray)
    {
        return null;
    }

    try
    {
        var clrArray = array.Value.AsArray();
        for (int i = 0; i < Math.Min(size, clrArray.Length); i++)
        {
            var ally = clrArray.GetObjectValue(i);
            if (!ally.IsValid || ally.IsNull || ally.Type is null)
            {
                continue;
            }

            if (LooksLikeCreature(ally))
            {
                return ally;
            }
        }
    }
    catch
    {
        return null;
    }

    return null;
}

static int? TryReadEnergyFromRelatedObjects(ClrObject obj)
{
    if (obj.Type is null)
    {
        return null;
    }

    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type))
    {
        foreach (ClrInstanceField field in type.Fields.Where(field => field.IsObjectReference))
        {
            string name = field.Name;
            if (!name.Contains("energy", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("mana", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("resource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var related = TryReadObjectField(obj, name);
            if (related is null || !related.Value.IsValid || related.Value.IsNull)
            {
                continue;
            }

            int? value = TryReadIntFieldByNames(
                related.Value,
                "_currentEnergy",
                "<CurrentEnergy>k__BackingField",
                "_energy",
                "<Energy>k__BackingField",
                "_currentMana",
                "<CurrentMana>k__BackingField",
                "_mana",
                "<Mana>k__BackingField",
                "_value",
                "<Value>k__BackingField",
                "_current",
                "<Current>k__BackingField");
            if (value is not null)
            {
                return value;
            }
        }
    }

    return null;
}

static bool LooksLikeCreature(ClrObject obj)
{
    return TryReadIntFieldByNames(
            obj,
            "_currentHp",
            "<CurrentHp>k__BackingField",
            "_hp",
            "<Hp>k__BackingField")
        is not null;
}

static string NormalizeCardEntry(string entry)
{
    string token = entry.Trim();
    if (token.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase))
    {
        token = token["CARD.".Length..];
    }

    token = token
        .Replace("_IRONCLAD", "", StringComparison.OrdinalIgnoreCase)
        .Replace("_SILENT", "", StringComparison.OrdinalIgnoreCase)
        .Replace("_DEFECT", "", StringComparison.OrdinalIgnoreCase)
        .Replace("_WATCHER", "", StringComparison.OrdinalIgnoreCase);

    return ToTitleWords(token);
}

static string NormalizeCardTypeName(string typeName)
{
    string token = typeName[(typeName.LastIndexOf('.') + 1)..];
    foreach (string suffix in new[] { "Ironclad", "Silent", "Defect", "Watcher" })
    {
        if (token.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            token = token[..^suffix.Length];
            break;
        }
    }

    List<char> chars = new();
    for (int i = 0; i < token.Length; i++)
    {
        char current = token[i];
        if (i > 0 && char.IsUpper(current) && char.IsLower(token[i - 1]))
        {
            chars.Add(' ');
        }
        chars.Add(current);
    }

    return new string(chars.ToArray()).Trim();
}

static string NormalizeModelEntry(string entry)
{
    string token = entry.Trim();
    int dotIndex = token.LastIndexOf('.');
    if (dotIndex >= 0 && dotIndex < token.Length - 1)
    {
        token = token[(dotIndex + 1)..];
    }

    return ToTitleWords(token);
}

static string NormalizeModelTypeName(string typeName)
{
    string token = typeName[(typeName.LastIndexOf('.') + 1)..];
    return NormalizeCardTypeName(token);
}

static string NormalizeStateId(string stateId)
{
    string token = stateId.Trim();
    if (token.EndsWith("_MOVE", StringComparison.OrdinalIgnoreCase))
    {
        token = token[..^"_MOVE".Length];
    }

    return ToTitleWords(token);
}

static string ToTitleWords(string token)
{
    return string.Join(" ",
        token.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
}

static void DumpObject(ClrObject obj, string indent)
{
    if (obj.Type is null)
    {
        return;
    }

    HashSet<ulong> visited = new();
    DumpObjectRecursive(obj, indent, 0, visited);
}

static void DumpObjectRecursive(ClrObject obj, string indent, int depth, HashSet<ulong> visited)
{
    if (obj.Type is null || !visited.Add(obj.Address))
    {
        return;
    }

    HashSet<string> printedFields = new(StringComparer.Ordinal);
    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type).Reverse())
    {
        foreach (ClrInstanceField field in type.Fields)
        {
            if (!printedFields.Add(field.Name))
            {
                continue;
            }

            try
            {
                string fieldType = field.Type?.Name ?? field.ElementType.ToString();
                if (field.ElementType == ClrElementType.String)
                {
                    string? value = field.ReadString(obj.Address, interior: false);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        Console.WriteLine($"{indent}{field.Name} [{fieldType}] = {value}");
                    }
                    continue;
                }

                if (field.IsObjectReference)
                {
                    ulong address = field.ReadObject(obj.Address, interior: false);
                    if (address == 0)
                    {
                        continue;
                    }

                    ClrObject child = obj.Type.Heap.GetObject(address);
                    string childType = child.Type?.Name ?? "<unknown>";
                    Console.WriteLine($"{indent}{field.Name} [{fieldType}] -> 0x{address:X} ({childType})");

                    if (child.IsArray)
                    {
                        DumpArray(child, indent + "  ", maxElements: 8);
                    }

                    if (depth < 1 && ShouldRecurseInto(childType))
                    {
                        DumpObjectRecursive(child, indent + "  ", depth + 1, visited);
                    }
                    continue;
                }

                string? scalar = ReadScalar(field, obj.Address);
                if (!string.IsNullOrWhiteSpace(scalar))
                {
                    Console.WriteLine($"{indent}{field.Name} [{fieldType}] = {scalar}");
                }
            }
            catch
            {
                // Skip fields we cannot decode generically.
            }
        }
    }
}

static IEnumerable<ClrType> EnumerateTypeHierarchy(ClrType type)
{
    for (ClrType? current = type; current is not null; current = current.BaseType)
    {
        yield return current;
    }
}

static void DumpFocusedType(ClrHeap heap, string targetTypeName, int limit)
{
    var matches = heap.EnumerateObjects()
        .Where(obj => obj.IsValid && !obj.IsNull && obj.Type?.Name == targetTypeName)
        .Take(limit)
        .ToList();

    if (matches.Count == 0)
    {
        return;
    }

    Console.WriteLine();
    Console.WriteLine(targetTypeName);
    foreach (ClrObject obj in matches)
    {
        Console.WriteLine($"  0x{obj.Address:X}");
        DumpFocusedObject(obj, indent: "    ", depth: 0, maxDepth: 2, new HashSet<ulong>());
    }
}

static void DumpFocusedObject(ClrObject obj, string indent, int depth, int maxDepth, HashSet<ulong> visited)
{
    if (obj.Type is null || !visited.Add(obj.Address))
    {
        return;
    }

    if (obj.IsArray)
    {
        DumpArray(obj, indent, maxElements: 8);
        return;
    }

    HashSet<string> printedFields = new(StringComparer.Ordinal);
    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type).Reverse())
    {
        foreach (ClrInstanceField field in type.Fields)
        {
            if (!printedFields.Add(field.Name))
            {
                continue;
            }

            try
            {
                string fieldType = field.Type?.Name ?? field.ElementType.ToString();
                if (field.ElementType == ClrElementType.String)
                {
                    string? value = field.ReadString(obj.Address, interior: false);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        Console.WriteLine($"{indent}{field.Name} [{fieldType}] = {value}");
                    }

                    continue;
                }

                if (field.IsObjectReference)
                {
                    ulong address = field.ReadObject(obj.Address, interior: false);
                    if (address == 0)
                    {
                        continue;
                    }

                    ClrObject child = obj.Type.Heap.GetObject(address);
                    string childType = child.Type?.Name ?? "<unknown>";
                    Console.WriteLine($"{indent}{field.Name} [{fieldType}] -> 0x{address:X} ({childType})");

                    if (child.IsArray)
                    {
                        DumpArray(child, indent + "  ", maxElements: 8);
                    }

                    if (depth < maxDepth && ShouldFocusRecurseInto(childType))
                    {
                        DumpFocusedObject(child, indent + "  ", depth + 1, maxDepth, visited);
                    }

                    continue;
                }

                string? scalar = ReadScalar(field, obj.Address);
                if (!string.IsNullOrWhiteSpace(scalar) && scalar != "0" && scalar != "False")
                {
                    Console.WriteLine($"{indent}{field.Name} [{fieldType}] = {scalar}");
                }
            }
            catch
            {
                // Skip fields we cannot decode generically.
            }
        }
    }
}

static void DumpArray(ClrObject obj, string indent, int maxElements)
{
    try
    {
        ClrArray array = obj.AsArray();
        Console.WriteLine($"{indent}<array length={array.Length}>");
        int count = Math.Min(array.Length, maxElements);
        for (int i = 0; i < count; i++)
        {
            ClrObject item = array.GetObjectValue(i);
            if (!item.IsValid || item.IsNull)
            {
                continue;
            }

            string itemType = item.Type?.Name ?? "<unknown>";
            if (item.Type?.IsString == true)
            {
                string value = item.AsString();
                Console.WriteLine($"{indent}  [{i}] \"{value}\"");
            }
            else
            {
                Console.WriteLine($"{indent}  [{i}] 0x{item.Address:X} ({itemType})");
            }
        }
    }
    catch
    {
        // Not all references can be decoded as arrays safely.
    }
}

static void DumpCombatEnemies(ClrHeap heap)
{
    var combatState = heap.EnumerateObjects()
        .FirstOrDefault(obj => obj.IsValid && !obj.IsNull && obj.Type?.Name == "MegaCrit.Sts2.Core.Combat.CombatState");

    if (!combatState.IsValid || combatState.IsNull || combatState.Type is null)
    {
        return;
    }

    var enemies = TryReadObjectField(combatState, "_enemies");
    if (enemies is null)
    {
        return;
    }

    Console.WriteLine();
    Console.WriteLine("Combat enemies");
    Console.WriteLine($"  list: 0x{enemies.Value.Address:X} ({enemies.Value.Type?.Name ?? "<unknown>"})");

    int size = TryReadIntField(enemies.Value, "_size") ?? 0;
    Console.WriteLine($"  size: {size}");

    var array = TryReadObjectField(enemies.Value, "_items");
    if (array is null || !array.Value.IsArray)
    {
        return;
    }

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

            Console.WriteLine($"  [{i}] 0x{enemy.Address:X} {enemy.Type.Name}");
            DumpFocusedObject(enemy, "    ", depth: 0, maxDepth: 2, new HashSet<ulong>());

            var monster = TryReadObjectField(enemy, "<Monster>k__BackingField")
                ?? TryReadObjectField(enemy, "_monster");
            if (monster is not null)
            {
                Console.WriteLine($"    Monster model: 0x{monster.Value.Address:X} {monster.Value.Type?.Name}");
                DumpIntentFields(monster.Value, "      ");
                DumpFocusedObject(monster.Value, "      ", depth: 0, maxDepth: 2, new HashSet<ulong>());
            }
        }
    }
    catch
    {
        // Best-effort diagnostic only.
    }
}

static void DumpCombatAllies(ClrHeap heap)
{
    var combatState = heap.EnumerateObjects()
        .FirstOrDefault(obj => obj.IsValid && !obj.IsNull && obj.Type?.Name == "MegaCrit.Sts2.Core.Combat.CombatState");

    if (!combatState.IsValid || combatState.IsNull || combatState.Type is null)
    {
        return;
    }

    var allies = TryReadObjectField(combatState, "_allies");
    if (allies is null)
    {
        return;
    }

    Console.WriteLine();
    Console.WriteLine("Combat allies");
    Console.WriteLine($"  list: 0x{allies.Value.Address:X} ({allies.Value.Type?.Name ?? "<unknown>"})");

    int size = TryReadIntField(allies.Value, "_size") ?? 0;
    Console.WriteLine($"  size: {size}");

    var array = TryReadObjectField(allies.Value, "_items");
    if (array is null || !array.Value.IsArray)
    {
        return;
    }

    try
    {
        var clrArray = array.Value.AsArray();
        for (int i = 0; i < Math.Min(size, clrArray.Length); i++)
        {
            var ally = clrArray.GetObjectValue(i);
            if (!ally.IsValid || ally.IsNull || ally.Type is null)
            {
                continue;
            }

            Console.WriteLine($"  [{i}] 0x{ally.Address:X} {ally.Type.Name}");
            DumpFocusedObject(ally, "    ", depth: 0, maxDepth: 2, new HashSet<ulong>());
        }
    }
    catch
    {
        // Best-effort diagnostic only.
    }
}


static void DumpIntentFields(ClrObject obj, string indent)
{
    if (obj.Type is null)
    {
        return;
    }

    HashSet<string> printedFields = new(StringComparer.Ordinal);
    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type).Reverse())
    {
        foreach (ClrInstanceField field in type.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !printedFields.Add(field.Name))
            {
                continue;
            }

            if (!field.Name.Contains("intent", StringComparison.OrdinalIgnoreCase)
                && !field.Name.Contains("move", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string fieldType = field.Type?.Name ?? field.ElementType.ToString();
                if (field.IsObjectReference)
                {
                    ulong address = field.ReadObject(obj.Address, interior: false);
                    if (address == 0)
                    {
                        continue;
                    }

                    ClrObject child = obj.Type.Heap.GetObject(address);
                    Console.WriteLine($"{indent}{field.Name} [{fieldType}] -> 0x{address:X} ({child.Type?.Name ?? "<unknown>"})");
                    if (child.IsArray)
                    {
                        DumpArray(child, indent + "  ", maxElements: 8);
                        if (field.Name.Contains("intent", StringComparison.OrdinalIgnoreCase))
                        {
                            DumpArrayObjects(child, indent + "  ", maxElements: 4, maxDepth: 2);
                        }
                    }
                }
                else
                {
                    string? scalar = ReadScalar(field, obj.Address);
                    if (!string.IsNullOrWhiteSpace(scalar))
                    {
                        Console.WriteLine($"{indent}{field.Name} [{fieldType}] = {scalar}");
                    }
                }
            }
            catch
            {
                // Best-effort diagnostic only.
            }
        }
    }
}

static void DumpArrayObjects(ClrObject obj, string indent, int maxElements, int maxDepth)
{
    try
    {
        ClrArray array = obj.AsArray();
        int count = Math.Min(array.Length, maxElements);
        for (int i = 0; i < count; i++)
        {
            ClrObject item = array.GetObjectValue(i);
            if (!item.IsValid || item.IsNull || item.Type is null)
            {
                continue;
            }

            Console.WriteLine($"{indent}[obj {i}] 0x{item.Address:X} {item.Type.Name}");
            DumpFocusedObject(item, indent + "  ", depth: 0, maxDepth, new HashSet<ulong>());
        }
    }
    catch
    {
        // Best-effort diagnostic only.
    }
}

static bool ShouldRecurseInto(string typeName)
{
    return typeName.Contains("ModelId", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("LocString", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("SerializableCard", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Combat", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Card", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Holder", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Pile", StringComparison.OrdinalIgnoreCase);
}

static bool ShouldFocusRecurseInto(string typeName)
{
    return typeName.Contains("ModelId", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("LocString", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("SerializableCard", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("CardState", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("CardPile", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Monster", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Intent", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Move", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("NCard", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Holder", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("List<", StringComparison.OrdinalIgnoreCase)
        || typeName.EndsWith("[]", StringComparison.OrdinalIgnoreCase);
}

static string? ReadScalar(ClrInstanceField field, ulong address)
{
    return field.ElementType switch
    {
        ClrElementType.Boolean => field.Read<bool>(address, interior: false).ToString(),
        ClrElementType.Char => field.Read<char>(address, interior: false).ToString(),
        ClrElementType.Int8 => field.Read<sbyte>(address, interior: false).ToString(),
        ClrElementType.UInt8 => field.Read<byte>(address, interior: false).ToString(),
        ClrElementType.Int16 => field.Read<short>(address, interior: false).ToString(),
        ClrElementType.UInt16 => field.Read<ushort>(address, interior: false).ToString(),
        ClrElementType.Int32 => field.Read<int>(address, interior: false).ToString(),
        ClrElementType.UInt32 => field.Read<uint>(address, interior: false).ToString(),
        ClrElementType.Int64 => field.Read<long>(address, interior: false).ToString(),
        ClrElementType.UInt64 => field.Read<ulong>(address, interior: false).ToString(),
        ClrElementType.Float => field.Read<float>(address, interior: false).ToString(),
        ClrElementType.Double => field.Read<double>(address, interior: false).ToString(),
        ClrElementType.NativeInt => field.Read<nint>(address, interior: false).ToString(),
        ClrElementType.NativeUInt => field.Read<nuint>(address, interior: false).ToString(),
        _ => null,
    };
}
