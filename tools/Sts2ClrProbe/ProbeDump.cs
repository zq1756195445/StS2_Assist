using Microsoft.Diagnostics.Runtime;
using static Sts2ClrProbe.ProbeCommon;
namespace Sts2ClrProbe;
internal static class ProbeDump
{
    internal static void DumpObject(ClrObject obj, string indent)
{
    if (obj.Type is null)
    {
        return;
    }

    HashSet<ulong> visited = new();
    DumpObjectRecursive(obj, indent, 0, visited);
}

    internal static void DumpObjectRecursive(ClrObject obj, string indent, int depth, HashSet<ulong> visited)
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

    internal static void DumpFocusedType(ClrHeap heap, string targetTypeName, int limit)
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

    internal static void DumpFocusedObject(ClrObject obj, string indent, int depth, int maxDepth, HashSet<ulong> visited)
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

    internal static void DumpArray(ClrObject obj, string indent, int maxElements)
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

    internal static void DumpCombatEnemies(ClrHeap heap)
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

    internal static void DumpCombatAllies(ClrHeap heap)
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


    internal static void DumpIntentFields(ClrObject obj, string indent)
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

    internal static void DumpArrayObjects(ClrObject obj, string indent, int maxElements, int maxDepth)
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

    internal static bool ShouldRecurseInto(string typeName)
{
    return typeName.Contains("ModelId", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("LocString", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("SerializableCard", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Combat", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Card", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Holder", StringComparison.OrdinalIgnoreCase)
        || typeName.Contains("Pile", StringComparison.OrdinalIgnoreCase);
}

    internal static bool ShouldFocusRecurseInto(string typeName)
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

    internal static string? ReadScalar(ClrInstanceField field, ulong address)
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
}