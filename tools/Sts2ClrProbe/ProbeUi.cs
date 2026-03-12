using Microsoft.Diagnostics.Runtime;
using System.Text.Json;
using static Sts2ClrProbe.ProbeCommon;

namespace Sts2ClrProbe;

internal static class ProbeUi
{
    internal static object ReadUiCandidates(ClrHeap heap)
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

    internal static string InferSceneHint(
        List<string> hand,
        List<object> enemies,
        object? player,
        List<string> rewardCards,
        List<string> shopOffers,
        List<string> eventOptions,
        object uiCandidates)
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
                if (!UiCandidateLooksActive(item))
                {
                    continue;
                }

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
                if (!UiCandidateLooksActive(item))
                {
                    continue;
                }

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

        bool UiCandidateLooksActive(JsonElement item)
        {
            if (!item.TryGetProperty("flags", out JsonElement flags) || flags.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            foreach (JsonProperty flag in flags.EnumerateObject())
            {
                if ((string.Equals(flag.Name, "_disposed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(flag.Name, "<Disposed>k__BackingField", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(flag.Name, "_isDisposed", StringComparison.OrdinalIgnoreCase))
                    && string.Equals(flag.Value.GetString(), "True", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if ((string.Equals(flag.Name, "<IsOpen>k__BackingField", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(flag.Name, "_isOpen", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(flag.Name, "open", StringComparison.OrdinalIgnoreCase))
                    && string.Equals(flag.Value.GetString(), "False", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        if (hand.Count > 0 || enemies.Count > 0)
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

        if (eventOptions.Count > 0
            || HasType("eventScene", "Nodes.Events.NEventOptionButton", "Nodes.Screens.NAncientBgContainer"))
        {
            return "event";
        }

        if (shopOffers.Count > 0)
        {
            return "shop";
        }

        bool hasActiveRewardUi = HasType(
                "reward",
                "Nodes.Screens.NRewardsScreen",
                "Nodes.Rewards.NRewardButton",
                "Nodes.Screens.CardSelection.NCardRewardSelectionScreen",
                "Nodes.Screens.CardSelection.NCardRewardAlternativeButton");
        if ((rewardCards.Count > 0 && hasActiveRewardUi)
            || HasType("reward", "Nodes.Rewards.NRewardButton", "Nodes.Screens.CardSelection.NCardRewardAlternativeButton"))
        {
            return "reward";
        }

        if (player is not null)
        {
            return "battle";
        }

        return "unknown";
    }

    internal static void DumpUiCandidates(ClrHeap heap)
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

    internal static List<(string Scene, string TypeName, ulong Address, Dictionary<string, string> Flags)> ScanUiCandidates(ClrHeap heap)
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

    internal static object ToUiCandidatePayload(
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

    internal static string? MatchUiScene(
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

    internal static Dictionary<string, string> ReadUiFlags(ClrObject obj)
    {
        var interestingFlags = new[]
        {
            "active",
            "visible",
            "open",
            "opened",
            "enabled",
            "disposed",
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
}
