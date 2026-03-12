using Microsoft.Diagnostics.Runtime;
using static Sts2ClrProbe.ProbeCommon;

namespace Sts2ClrProbe;

internal static class ProbeTreasure
{
    private const string TreasureRelicHolderType = "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicHolder";
    private const string SceneContainerType = "MegaCrit.Sts2.Core.Nodes.NSceneContainer";
    private const string TreasureRoomNodeType = "MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom";

    internal static bool HasActiveTreasureRelicHolder(ClrHeap heap)
    {
        return ReadCurrentTreasureRelicHolders(heap).Any(IsTreasureRelicHolderActive);
    }

    internal static List<string> ReadTreasureRelicsFromHeap(ClrHeap heap)
    {
        HashSet<string> relics = new(StringComparer.OrdinalIgnoreCase);

        foreach (ClrObject obj in ReadCurrentTreasureRelicHolders(heap))
        {
            if (!IsTreasureRelicHolderActive(obj))
            {
                continue;
            }

            var relicNode = TryReadObjectField(obj, "<Relic>k__BackingField")
                ?? TryReadObjectField(obj, "_relic");
            if (relicNode is null || !relicNode.Value.IsValid || relicNode.Value.IsNull)
            {
                continue;
            }

            string? relicName = TryReadTreasureRelicNameFromNode(relicNode.Value);
            if (!string.IsNullOrWhiteSpace(relicName))
            {
                relics.Add(relicName);
            }
        }

        return relics.Take(8).ToList();
    }

    private static List<ClrObject> ReadCurrentTreasureRelicHolders(ClrHeap heap)
    {
        var treasureRoomNode = FindCurrentTreasureRoomNode(heap);
        if (treasureRoomNode is null || !treasureRoomNode.Value.IsValid || treasureRoomNode.Value.IsNull)
        {
            return new List<ClrObject>();
        }

        var relicCollection = TryReadObjectField(treasureRoomNode.Value, "_relicCollection");
        if (relicCollection is null || !relicCollection.Value.IsValid || relicCollection.Value.IsNull)
        {
            return new List<ClrObject>();
        }

        List<ClrObject> holdersInUse = ReadObjectsFromList(
            TryReadObjectField(relicCollection.Value, "_holdersInUse"))
            .Where(holder => holder.Type?.Name == TreasureRelicHolderType)
            .ToList();
        if (holdersInUse.Count > 0)
        {
            return holdersInUse;
        }

        var singleplayerHolder = TryReadObjectField(relicCollection.Value, "<SingleplayerRelicHolder>k__BackingField");
        if (singleplayerHolder is not null
            && singleplayerHolder.Value.IsValid
            && !singleplayerHolder.Value.IsNull
            && singleplayerHolder.Value.Type?.Name == TreasureRelicHolderType)
        {
            return new List<ClrObject> { singleplayerHolder.Value };
        }

        return ReadObjectsFromList(TryReadObjectField(relicCollection.Value, "_multiplayerHolders"))
            .Where(holder => holder.Type?.Name == TreasureRelicHolderType)
            .ToList();
    }

    private static ClrObject? FindCurrentTreasureRoomNode(ClrHeap heap)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsNull || obj.Type?.Name != SceneContainerType || !IsUiObjectActive(obj))
            {
                continue;
            }

            var currentScene = TryReadObjectField(obj, "_currentScene");
            if (currentScene is null
                || !currentScene.Value.IsValid
                || currentScene.Value.IsNull
                || currentScene.Value.Type?.Name != TreasureRoomNodeType
                || !IsUiObjectActive(currentScene.Value))
            {
                continue;
            }

            return currentScene.Value;
        }

        return null;
    }

    private static bool IsTreasureRelicHolderActive(ClrObject holder)
    {
        if (!holder.IsValid || holder.IsNull)
        {
            return false;
        }

        if (!IsUiObjectActive(holder))
        {
            return false;
        }

        bool? isDisposed = TryReadBoolFieldByNames(
            holder,
            "_disposed",
            "<Disposed>k__BackingField",
            "_isDisposed");
        if (isDisposed == true)
        {
            return false;
        }

        bool? isOpen = TryReadBoolFieldByNames(
            holder,
            "<IsOpen>k__BackingField",
            "_isOpen");
        if (isOpen == false)
        {
            return false;
        }

        bool? isEnabled = TryReadBoolFieldByNames(
            holder,
            "_isEnabled",
            "<IsEnabled>k__BackingField",
            "_enabled");
        return isEnabled ?? true;
    }

    private static bool IsUiObjectActive(ClrObject obj)
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

        return true;
    }

    private static string? TryReadTreasureRelicNameFromNode(ClrObject relicNode)
    {
        var directModel = TryReadObjectField(relicNode, "_model")
            ?? TryReadObjectField(relicNode, "<Model>k__BackingField");
        if (directModel is not null && directModel.Value.IsValid && !directModel.Value.IsNull)
        {
            string? direct = TryReadTreasureRelicName(directModel.Value);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }
        }

        return TryReadTreasureRelicNameFromRelatedObjects(relicNode, depth: 0, maxDepth: 3, new HashSet<ulong>());
    }

    private static string? TryReadTreasureRelicNameFromRelatedObjects(
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
            "<Model>k__BackingField",
            "_relic",
            "<Relic>k__BackingField",
            "_relicModel",
            "_viewModel",
        };

        foreach (string fieldName in preferredFields)
        {
            var child = TryReadObjectField(obj, fieldName);
            if (child is null || !child.Value.IsValid || child.Value.IsNull)
            {
                continue;
            }

            string? direct = TryReadTreasureRelicName(child.Value);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            direct = TryReadTreasureRelicNameFromRelatedObjects(child.Value, depth + 1, maxDepth, visited);
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
                        direct = null;
                        ClrArray array = child.AsArray();
                        int count = Math.Min(array.Length, 8);
                        for (int i = 0; i < count; i++)
                        {
                            ClrObject item = array.GetObjectValue(i);
                            if (!item.IsValid || item.IsNull)
                            {
                                continue;
                            }

                            direct = TryReadTreasureRelicName(item);
                            if (!string.IsNullOrWhiteSpace(direct))
                            {
                                break;
                            }

                            direct = TryReadTreasureRelicNameFromRelatedObjects(item, depth + 1, maxDepth, visited);
                            if (!string.IsNullOrWhiteSpace(direct))
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        direct = TryReadTreasureRelicName(child);
                        if (string.IsNullOrWhiteSpace(direct))
                        {
                            direct = TryReadTreasureRelicNameFromRelatedObjects(child, depth + 1, maxDepth, visited);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(direct))
                    {
                        return direct;
                    }
                }
                catch
                {
                    // Best-effort traversal only.
                }
            }
        }

        return null;
    }

    internal static string? TryReadTreasureRelicName(ClrObject relicModel)
    {
        string? modelEntry = TryReadModelEntry(relicModel);
        if (!string.IsNullOrWhiteSpace(modelEntry)
            && modelEntry.StartsWith("RELIC.", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeModelEntry(modelEntry);
        }

        string? typeName = relicModel.Type?.Name;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        if (typeName.Contains(".Models.Relics.", StringComparison.Ordinal))
        {
            return NormalizeModelTypeName(typeName);
        }

        return null;
    }
}
