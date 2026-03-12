using Microsoft.Diagnostics.Runtime;
namespace Sts2ClrProbe;
internal static class ProbeCommon
{
    internal static List<ClrObject> ReadCreaturesFromList(ClrObject? listObject)
{
    return ReadObjectsFromList(listObject)
        .Where(LooksLikeCreature)
        .ToList();
}

    internal static List<ClrObject> ReadObjectsFromList(ClrObject? listObject)
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

    internal static List<string>? TryReadHandFromCardPile(ClrObject pile)
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

    internal static string? TryReadHandHolderCard(ClrObject holder)
{
    var model = TryReadObjectField(holder, "_model");
    return model is null ? null : TryReadCardName(model.Value);
}

    internal static string? TryReadRewardCardName(ClrObject card)
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

    if (typeName.Contains(".Models.Cards.", StringComparison.Ordinal))
    {
        return NormalizeCardTypeName(typeName);
    }

    return null;
}

    internal static string? TryReadCardName(ClrObject card)
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

    internal static string? TryReadCreatureName(ClrObject creature)
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

    internal static string? TryReadCreatureIntent(ClrObject creature)
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

    internal static string? TryReadIntentKind(ClrObject moveState)
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

    internal static string? TryReadModelEntry(ClrObject obj)
{
    var id = TryReadObjectField(obj, "<Id>k__BackingField") ?? TryReadObjectField(obj, "_id");
    if (id is null)
    {
        return null;
    }

    return TryReadStringField(id.Value, "<Entry>k__BackingField")
        ?? TryReadStringField(id.Value, "Entry");
}

    internal static ClrObject? TryReadObjectField(ClrObject obj, string fieldName)
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

    internal static ClrObject? TryReadObjectFieldByNames(ClrObject obj, params string[] fieldNames)
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

    internal static string? TryReadStringField(ClrObject obj, string fieldName)
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

    internal static int? TryReadIntField(ClrObject obj, string fieldName)
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

    internal static int? TryReadIntFieldByNames(ClrObject obj, params string[] fieldNames)
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

    internal static bool? TryReadBoolField(ClrObject obj, string fieldName)
{
    if (obj.Type is null)
    {
        return null;
    }

    foreach (ClrType type in EnumerateTypeHierarchy(obj.Type))
    {
        ClrInstanceField? field = type.Fields.FirstOrDefault(field => field.Name == fieldName);
        if (field is null)
        {
            continue;
        }

        try
        {
            if (field.ElementType == ClrElementType.Boolean)
            {
                return field.Read<bool>(obj.Address, interior: false);
            }

            if (field.ElementType == ClrElementType.Int32)
            {
                return field.Read<int>(obj.Address, interior: false) != 0;
            }
        }
        catch
        {
            return null;
        }
    }

    return null;
}

    internal static bool? TryReadBoolFieldByNames(ClrObject obj, params string[] fieldNames)
{
    foreach (string fieldName in fieldNames)
    {
        bool? value = TryReadBoolField(obj, fieldName);
        if (value is not null)
        {
            return value;
        }
    }

    return null;
}

    internal static ClrObject? TryReadPlayerObject(ClrObject combatState)
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

    internal static ClrObject? TryReadPrimaryAlly(ClrObject combatState)
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

    internal static int? TryReadEnergyFromRelatedObjects(ClrObject obj)
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

    internal static bool LooksLikeCreature(ClrObject obj)
{
    return TryReadIntFieldByNames(
            obj,
            "_currentHp",
            "<CurrentHp>k__BackingField",
            "_hp",
            "<Hp>k__BackingField")
        is not null;
}

    internal static string NormalizeCardEntry(string entry)
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

    internal static string NormalizeCardTypeName(string typeName)
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

    internal static string NormalizeModelEntry(string entry)
{
    string token = entry.Trim();
    int dotIndex = token.LastIndexOf('.');
    if (dotIndex >= 0 && dotIndex < token.Length - 1)
    {
        token = token[(dotIndex + 1)..];
    }

    return ToTitleWords(token);
}

    internal static string NormalizeModelTypeName(string typeName)
{
    string token = typeName[(typeName.LastIndexOf('.') + 1)..];
    return NormalizeCardTypeName(token);
}

    internal static string NormalizeStateId(string stateId)
{
    string token = stateId.Trim();
    if (token.EndsWith("_MOVE", StringComparison.OrdinalIgnoreCase))
    {
        token = token[..^"_MOVE".Length];
    }

    return ToTitleWords(token);
}

    internal static string ToTitleWords(string token)
{
    return string.Join(" ",
        token.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
}

    internal static IEnumerable<ClrType> EnumerateTypeHierarchy(ClrType type)
{
    for (ClrType? current = type; current is not null; current = current.BaseType)
    {
        yield return current;
    }
}

}
