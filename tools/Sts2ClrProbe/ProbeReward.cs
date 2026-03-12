using Microsoft.Diagnostics.Runtime;
using static Sts2ClrProbe.ProbeCommon;

namespace Sts2ClrProbe;

internal static class ProbeReward
{
    private const string RewardSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";

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

        if (typeName.Contains(".Models.Cards.", StringComparison.Ordinal)
            || typeName.Contains(".Entities.Cards.", StringComparison.Ordinal))
        {
            return NormalizeCardTypeName(typeName);
        }

        return null;
    }

    internal static List<string> ReadRewardCardsFromHeap(ClrHeap heap)
    {
        var activeCards = ReadRewardCardsFromSelectionScreens(heap);
        if (activeCards.Count > 0)
        {
            return activeCards;
        }

        return ReadRewardCardsFromCandidates(heap);
    }

    private static List<string> ReadRewardCardsFromSelectionScreens(ClrHeap heap)
    {
        foreach (ClrObject screen in heap.EnumerateObjects())
        {
            if (!screen.IsValid || screen.IsNull || screen.Type?.Name != RewardSelectionScreenType)
            {
                continue;
            }

            if (!IsSelectionScreenActive(screen))
            {
                continue;
            }

            var options = TryReadObjectField(screen, "_options");
            List<ClrObject> optionEntries = ReadObjectsFromList(options);
            if (optionEntries.Count == 0)
            {
                continue;
            }

            List<string> cards = new();
            foreach (ClrObject entry in optionEntries)
            {
                var originalCard = TryReadObjectField(entry, "originalCard");
                if (originalCard is null || !originalCard.Value.IsValid || originalCard.Value.IsNull)
                {
                    continue;
                }

                string? cardName = TryReadRewardCardName(originalCard.Value);
                if (!string.IsNullOrWhiteSpace(cardName))
                {
                    cards.Add(cardName);
                }
            }

            if (cards.Count > 0)
            {
                return cards.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            }
        }

        return new List<string>();
    }

    private static bool IsSelectionScreenActive(ClrObject screen)
    {
        if (screen.Type is null)
        {
            return false;
        }

        foreach (ClrType type in EnumerateTypeHierarchy(screen.Type))
        {
            ClrInstanceField? field = type.Fields.FirstOrDefault(f => string.Equals(f.Name, "_disposed", StringComparison.Ordinal));
            if (field is null)
            {
                continue;
            }

            try
            {
                if (field.ElementType == ClrElementType.Boolean)
                {
                    return !field.Read<bool>(screen.Address, interior: false);
                }

                if (field.ElementType == ClrElementType.Int32)
                {
                    return field.Read<int>(screen.Address, interior: false) == 0;
                }
            }
            catch
            {
                return true;
            }
        }

        return true;
    }

    private static List<string> ReadRewardCardsFromCandidates(ClrHeap heap)
    {
        var sources = heap.EnumerateObjects()
            .Where(obj => obj.IsValid && !obj.IsNull && obj.Type?.Name is string typeName && IsRewardSourceType(typeName))
            .OrderBy(obj => RewardSourcePriority(obj.Type?.Name))
            .Take(24)
            .ToList();

        if (sources.Count == 0)
        {
            return new List<string>();
        }

        HashSet<string> cards = new(StringComparer.OrdinalIgnoreCase);
        foreach (ClrObject source in sources)
        {
            CollectCardNamesFromObject(source, cards, depth: 0, maxDepth: 3, new HashSet<ulong>());
            if (cards.Count >= 3)
            {
                break;
            }
        }

        return cards.Take(3).ToList();
    }

    internal static bool IsRewardSourceType(string typeName)
    {
        return typeName.Contains("NCardRewardAlternativeButton", StringComparison.Ordinal)
            || typeName.Contains("CardRewardAlternative", StringComparison.Ordinal)
            || typeName.Contains("NCardRewardSelectionScreen", StringComparison.Ordinal)
            || typeName.Contains("NRewardsScreen", StringComparison.Ordinal);
    }

    internal static int RewardSourcePriority(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return 99;
        }

        if (typeName.Contains("NCardRewardAlternativeButton", StringComparison.Ordinal))
        {
            return 0;
        }

        if (typeName.Contains("CardRewardAlternative", StringComparison.Ordinal))
        {
            return 1;
        }

        if (typeName.Contains("NCardRewardSelectionScreen", StringComparison.Ordinal))
        {
            return 2;
        }

        if (typeName.Contains("NRewardsScreen", StringComparison.Ordinal))
        {
            return 3;
        }

        return 99;
    }

    internal static void CollectCardNamesFromObject(
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

    internal static void TryCollectCardNamesFromArray(
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
}
