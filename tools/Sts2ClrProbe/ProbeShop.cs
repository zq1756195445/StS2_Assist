using Microsoft.Diagnostics.Runtime;
using static Sts2ClrProbe.ProbeCommon;

namespace Sts2ClrProbe;

internal static class ProbeShop
{
    internal static List<string> ReadShopOffersFromHeap(ClrHeap heap)
    {
        HashSet<string> offers = new(StringComparer.OrdinalIgnoreCase);
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsNull || obj.Type?.Name is not string typeName)
            {
                continue;
            }

            string? offer = typeName switch
            {
                "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantCard" => ReadMerchantCardOffer(obj),
                "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantRelic" => ReadMerchantRelicOffer(obj),
                "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantPotion" => ReadMerchantPotionOffer(obj),
                "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantCardRemoval" => "Card Removal",
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(offer))
            {
                offers.Add(offer);
            }
        }

        return offers.Take(16).ToList();
    }

    internal static string? ReadMerchantCardOffer(ClrObject obj)
    {
        var cardNode = TryReadObjectField(obj, "_cardNode");
        var model = cardNode is null ? null : TryReadObjectField(cardNode.Value, "_model");
        return model is null ? null : TryReadCardName(model.Value);
    }

    internal static string? ReadMerchantRelicOffer(ClrObject obj)
    {
        var relic = TryReadObjectField(obj, "_relic");
        return relic is null ? null : TryReadShopOfferName(relic.Value);
    }

    internal static string? ReadMerchantPotionOffer(ClrObject obj)
    {
        var potion = TryReadObjectField(obj, "_potion");
        return potion is null ? null : TryReadShopOfferName(potion.Value);
    }

    internal static bool IsShopSourceType(string typeName)
    {
        return typeName.Contains("NMerchantCard", StringComparison.Ordinal)
            || typeName.Contains("NMerchantInventory", StringComparison.Ordinal)
            || typeName.Contains("NMerchantSlot", StringComparison.Ordinal)
            || typeName.Contains("NMerchantCardRemoval", StringComparison.Ordinal)
            || typeName.Contains("NMerchantRelic", StringComparison.Ordinal)
            || typeName.Contains("NMerchantPotion", StringComparison.Ordinal);
    }

    internal static int ShopSourcePriority(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return 99;
        }

        if (typeName.Contains("NMerchantCard", StringComparison.Ordinal)
            && !typeName.Contains("CardRemoval", StringComparison.Ordinal))
        {
            return 0;
        }

        if (typeName.Contains("NMerchantRelic", StringComparison.Ordinal))
        {
            return 1;
        }

        if (typeName.Contains("NMerchantPotion", StringComparison.Ordinal))
        {
            return 2;
        }

        if (typeName.Contains("NMerchantCardRemoval", StringComparison.Ordinal))
        {
            return 3;
        }

        if (typeName.Contains("NMerchantSlot", StringComparison.Ordinal))
        {
            return 4;
        }

        if (typeName.Contains("NMerchantInventory", StringComparison.Ordinal))
        {
            return 5;
        }

        return 99;
    }

    internal static void CollectShopOffersFromObject(
        ClrObject obj,
        HashSet<string> offers,
        int depth,
        int maxDepth,
        HashSet<ulong> visited)
    {
        if (!obj.IsValid || obj.IsNull || obj.Type is null || !visited.Add(obj.Address))
        {
            return;
        }

        string? offerName = TryReadShopOfferName(obj);
        if (!string.IsNullOrWhiteSpace(offerName))
        {
            offers.Add(offerName!);
            if (offers.Count >= 8)
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
                        TryCollectShopOffersFromArray(child, offers, depth + 1, maxDepth, visited);
                    }
                    else
                    {
                        CollectShopOffersFromObject(child, offers, depth + 1, maxDepth, visited);
                    }

                    if (offers.Count >= 8)
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

    internal static void TryCollectShopOffersFromArray(
        ClrObject obj,
        HashSet<string> offers,
        int depth,
        int maxDepth,
        HashSet<ulong> visited)
    {
        try
        {
            ClrArray array = obj.AsArray();
            int count = Math.Min(array.Length, 16);
            for (int i = 0; i < count; i++)
            {
                ClrObject child = array.GetObjectValue(i);
                if (!child.IsValid || child.IsNull)
                {
                    continue;
                }

                CollectShopOffersFromObject(child, offers, depth, maxDepth, visited);
                if (offers.Count >= 8)
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

    internal static string? TryReadShopOfferName(ClrObject obj)
    {
        string? typeName = obj.Type?.Name;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        if (typeName.Contains("NMerchantCardRemoval", StringComparison.Ordinal))
        {
            return "Card Removal";
        }

        string? modelEntry = TryReadModelEntry(obj);
        if (!string.IsNullOrWhiteSpace(modelEntry))
        {
            if (modelEntry.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeCardEntry(modelEntry);
            }

            if (modelEntry.StartsWith("RELIC.", StringComparison.OrdinalIgnoreCase)
                || modelEntry.StartsWith("POTION.", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeModelEntry(modelEntry);
            }
        }

        string? normalizedType = NormalizeCardTypeName(typeName);
        if (!string.IsNullOrWhiteSpace(normalizedType))
        {
            return normalizedType;
        }

        if (typeName.Contains(".Models.Relics.", StringComparison.Ordinal)
            || typeName.Contains(".Models.Potions.", StringComparison.Ordinal))
        {
            return NormalizeModelTypeName(typeName);
        }

        string? related = TryReadShopOfferNameFromRelatedObjects(obj, depth: 0, maxDepth: 3, new HashSet<ulong>());
        if (!string.IsNullOrWhiteSpace(related))
        {
            return related;
        }

        return null;
    }

    internal static string? TryReadShopOfferNameFromRelatedObjects(
        ClrObject obj,
        int depth,
        int maxDepth,
        HashSet<ulong> visited)
    {
        if (!obj.IsValid || obj.IsNull || obj.Type is null || !visited.Add(obj.Address) || depth >= maxDepth)
        {
            return null;
        }

        string[] preferredFields =
        {
            "_model",
            "_card",
            "_relic",
            "_potion",
            "_cardNode",
            "_relicNode",
            "_potionNode",
            "_entry",
            "_cardEntry",
            "_relicEntry",
            "_potionEntry",
            "_removalEntry",
        };

        foreach (string fieldName in preferredFields)
        {
            ClrObject? child = TryReadObjectField(obj, fieldName);
            if (child is null)
            {
                continue;
            }

            string? direct = TryReadShopOfferName(child.Value);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            direct = TryReadShopOfferNameFromRelatedObjects(child.Value, depth + 1, maxDepth, visited);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }
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

                    string? direct;
                    if (child.IsArray)
                    {
                        ClrArray array = child.AsArray();
                        int count = Math.Min(array.Length, 8);
                        direct = null;
                        for (int i = 0; i < count; i++)
                        {
                            child = array.GetObjectValue(i);
                            if (!child.IsValid || child.IsNull)
                            {
                                continue;
                            }

                            direct = TryReadShopOfferName(child);
                            if (!string.IsNullOrWhiteSpace(direct))
                            {
                                break;
                            }

                            direct = TryReadShopOfferNameFromRelatedObjects(child, depth + 1, maxDepth, visited);
                            if (!string.IsNullOrWhiteSpace(direct))
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        direct = TryReadShopOfferName(child);
                        if (string.IsNullOrWhiteSpace(direct))
                        {
                            direct = TryReadShopOfferNameFromRelatedObjects(child, depth + 1, maxDepth, visited);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(direct))
                    {
                        return direct;
                    }
                }
                catch
                {
                    // Ignore inconsistent heap fields.
                }
            }
        }

        return null;
    }
}
