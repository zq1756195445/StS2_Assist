using Microsoft.Diagnostics.Runtime;
using static Sts2ClrProbe.ProbeCommon;

namespace Sts2ClrProbe;

internal static class ProbeEvent
{
    private sealed record EventOptionSnapshot(
        ulong EventAddress,
        int? Index,
        string EventId,
        string PageId,
        string OptionId,
        string RootKey,
        string TitleKey,
        string DescriptionKey);

    private const string EventOptionButtonType = "MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton";

    internal static List<string> ReadEventOptionsFromHeap(ClrHeap heap)
    {
        const int eventOptionOutputLimit = 12;
        var options = ReadCurrentEventOptionSnapshots(heap);
        if (options.Count == 0)
        {
            return new List<string>();
        }

        HashSet<string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            if (!string.IsNullOrWhiteSpace(option.RootKey))
            {
                values.Add(option.RootKey);
            }

            if (!string.IsNullOrWhiteSpace(option.TitleKey))
            {
                values.Add(option.TitleKey);
            }

            if (!string.IsNullOrWhiteSpace(option.DescriptionKey))
            {
                values.Add(option.DescriptionKey);
            }
        }

        string? eventId = options
            .Select(option => option.EventId)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        string? pageId = options
            .Select(option => option.PageId)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (!string.IsNullOrWhiteSpace(eventId))
        {
            values.Add(eventId);
            if (!string.IsNullOrWhiteSpace(pageId))
            {
                values.Add($"{eventId}.pages.{pageId}.description");
            }
        }

        return values
            .OrderBy(EventOptionSortKey)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(eventOptionOutputLimit)
            .ToList();
    }

    internal static bool HasActiveEventPage(ClrHeap heap)
    {
        return ReadCurrentEventOptionSnapshots(heap).Count > 0;
    }

    internal static object? ReadEventPageFromHeap(ClrHeap heap)
    {
        var options = ReadCurrentEventOptionSnapshots(heap);
        if (options.Count == 0)
        {
            return null;
        }

        string? eventId = options
            .Select(option => option.EventId)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        string pageId = options
            .Select(option => option.PageId)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? "INITIAL";

        return new
        {
            eventId,
            pageId,
            descriptionKey = $"{eventId}.pages.{pageId}.description",
            options = options
                .OrderBy(option => option.Index ?? int.MaxValue)
                .ThenBy(option => option.OptionId, StringComparer.OrdinalIgnoreCase)
                .Select(option => new
                {
                    optionId = option.OptionId,
                    rootKey = option.RootKey,
                    titleKey = option.TitleKey,
                    descriptionKey = option.DescriptionKey,
                })
                .ToList(),
        };
    }

    private static List<EventOptionSnapshot> ReadCurrentEventOptionSnapshots(ClrHeap heap)
    {
        var buttons = heap.EnumerateObjects()
            .Where(obj => obj.IsValid
                && !obj.IsNull
                && string.Equals(obj.Type?.Name, EventOptionButtonType, StringComparison.Ordinal))
            .Select(TryReadEventButtonSnapshot)
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .ToList();

        if (buttons.Count == 0)
        {
            return new List<EventOptionSnapshot>();
        }

        ulong activeEventAddress = buttons
            .Where(snapshot => snapshot.EventAddress != 0)
            .GroupBy(snapshot => snapshot.EventAddress)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Max(snapshot => snapshot.Index ?? -1))
            .Select(group => group.Key)
            .FirstOrDefault();

        var filtered = buttons
            .Where(snapshot => snapshot.EventAddress != 0 && snapshot.EventAddress == activeEventAddress)
            .GroupBy(snapshot => snapshot.OptionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(snapshot => snapshot.Index ?? int.MaxValue)
                .First())
            .ToList();

        return filtered;
    }

    private static EventOptionSnapshot? TryReadEventButtonSnapshot(ClrObject button)
    {
        bool? isDisposed = TryReadBoolFieldByNames(
            button,
            "_disposed",
            "<Disposed>k__BackingField",
            "_isDisposed");
        if (isDisposed == true)
        {
            return null;
        }

        bool? isEnabled = TryReadBoolFieldByNames(
            button,
            "_isEnabled",
            "<IsEnabled>k__BackingField",
            "_enabled");
        if (isEnabled == false)
        {
            return null;
        }

        var eventObj = TryReadObjectField(button, "<Event>k__BackingField");
        var optionObj = TryReadObjectField(button, "<Option>k__BackingField");
        if (eventObj is null || optionObj is null || !optionObj.Value.IsValid || optionObj.Value.IsNull)
        {
            return null;
        }

        string? rootKey = TryReadStringField(optionObj.Value, "<TextKey>k__BackingField");
        string? titleKey = TryReadLocEntryKey(optionObj.Value, "<Title>k__BackingField");
        string? descriptionKey = TryReadLocEntryKey(optionObj.Value, "<Description>k__BackingField");
        if (string.IsNullOrWhiteSpace(rootKey)
            && string.IsNullOrWhiteSpace(titleKey)
            && string.IsNullOrWhiteSpace(descriptionKey))
        {
            return null;
        }

        string? canonicalRootKey = !string.IsNullOrWhiteSpace(rootKey)
            ? rootKey
            : titleKey ?? descriptionKey;
        if (string.IsNullOrWhiteSpace(canonicalRootKey))
        {
            return null;
        }

        string? eventId = TryExtractEventRootKey(canonicalRootKey!);
        string? pageId = TryExtractEventPageId(canonicalRootKey!);
        string optionId = TryExtractEventOptionId(canonicalRootKey!, pageId)
            ?? $"OPTION_{button.Address:X}";

        string resolvedRootKey = rootKey
            ?? $"{eventId}.pages.{pageId ?? "INITIAL"}.options.{optionId}";
        string resolvedTitleKey = titleKey
            ?? $"{eventId}.pages.{pageId ?? "INITIAL"}.options.{optionId}.title";
        string resolvedDescriptionKey = descriptionKey
            ?? $"{eventId}.pages.{pageId ?? "INITIAL"}.options.{optionId}.description";

        return new EventOptionSnapshot(
            eventObj.Value.Address,
            TryReadIntField(button, "<Index>k__BackingField"),
            eventId ?? string.Empty,
            pageId ?? "INITIAL",
            optionId,
            resolvedRootKey,
            resolvedTitleKey,
            resolvedDescriptionKey);
    }

    private static string? TryReadLocEntryKey(ClrObject owner, string fieldName)
    {
        var loc = TryReadObjectField(owner, fieldName);
        if (loc is null || !loc.Value.IsValid || loc.Value.IsNull)
        {
            return null;
        }

        return TryReadStringField(loc.Value, "<locEntryKey>P")
            ?? TryReadStringField(loc.Value, "_locEntryKey")
            ?? TryReadStringField(loc.Value, "<LocEntryKey>k__BackingField");
    }

    private static string? TryExtractEventOptionId(string value, string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return null;
        }

        string marker = $".pages.{pageId}.options.";
        int optionsIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (optionsIndex < 0)
        {
            return null;
        }

        string tail = value[(optionsIndex + marker.Length)..];
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        int dotIndex = tail.IndexOf('.');
        return dotIndex < 0 ? tail : tail[..dotIndex];
    }

    internal static void CollectEventOptionStrings(
        ClrObject obj,
        HashSet<string> options,
        int depth,
        int maxDepth,
        HashSet<ulong> visited)
    {
        if (!obj.IsValid || obj.IsNull || obj.Type is null || !visited.Add(obj.Address))
        {
            return;
        }

        foreach (string text in ReadInterestingStringsFromObject(obj))
        {
            if (IsLikelyEventOptionText(text))
            {
                options.Add(text);
                if (options.Count >= 16)
                {
                    return;
                }
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
                        TryCollectEventOptionStringsFromArray(child, options, depth + 1, maxDepth, visited);
                    }
                    else
                    {
                        CollectEventOptionStrings(child, options, depth + 1, maxDepth, visited);
                    }

                    if (options.Count >= 16)
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

    internal static void TryCollectEventOptionStringsFromArray(
        ClrObject obj,
        HashSet<string> options,
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

                CollectEventOptionStrings(child, options, depth, maxDepth, visited);
                if (options.Count >= 16)
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

    internal static IEnumerable<string> ReadInterestingStringsFromObject(ClrObject obj)
    {
        if (obj.Type is null)
        {
            yield break;
        }

        HashSet<string> emitted = new(StringComparer.Ordinal);
        foreach (ClrType type in EnumerateTypeHierarchy(obj.Type))
        {
            foreach (ClrInstanceField field in type.Fields)
            {
                if (field.ElementType != ClrElementType.String)
                {
                    continue;
                }

                string? value = null;
                try
                {
                    value = field.ReadString(obj.Address, interior: false);
                }
                catch
                {
                    // Ignore inconsistent heap fields.
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                string normalized = value.Trim();
                if (emitted.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }
    }

    internal static bool IsLikelyEventOptionText(string text)
    {
        string value = text.Trim();
        if (value.Length < 2 || value.Length > 120)
        {
            return false;
        }

        string lower = value.ToLowerInvariant();
        string[] bannedFragments =
        {
            "megacrit.sts2",
            "displayclass",
            "button",
            "optionbutton",
            "current",
            "enabled",
            "false",
            "true",
            "card reward",
            "card pile",
            "temporary card",
            "trash to treasure",
            "pagestorm",
            "rampage",
            "reward",
            "merchant",
        };

        if (bannedFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal)))
        {
            return false;
        }

        if (string.Equals(value, "EVENT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "events", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Contains(".pages.", StringComparison.OrdinalIgnoreCase))
        {
            return value.Contains(".options.", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".description", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".title", StringComparison.OrdinalIgnoreCase);
        }

        int digitCount = value.Count(char.IsDigit);
        int letterOrHanCount = value.Count(ch => char.IsLetter(ch) || (ch >= 0x4e00 && ch <= 0x9fff));
        if (letterOrHanCount < 2)
        {
            return false;
        }

        if (digitCount > 0
            && !value.Contains("\u91d1", StringComparison.Ordinal)
            && !value.Contains("gold", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("\u91d1\u5e01", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    internal static int EventOptionSortKey(string value)
    {
        if (value.Contains(".options.", StringComparison.OrdinalIgnoreCase)
            && !value.Contains(".title", StringComparison.OrdinalIgnoreCase)
            && !value.Contains(".description", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (value.EndsWith(".title", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (value.EndsWith(".description", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (value.Contains(".options.", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (value.EndsWith(".description", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (!value.Contains('.', StringComparison.Ordinal))
        {
            return 5;
        }

        return 6;
    }

    internal static object? BuildEventPage(List<string> eventOptions)
    {
        if (eventOptions.Count == 0)
        {
            return null;
        }

        string? eventId = eventOptions
            .Select(TryExtractEventRootKey)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        string? pageId = eventOptions
            .Select(TryExtractEventPageId)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? "INITIAL";

        string? description = eventOptions
            .FirstOrDefault(value => value.EndsWith(".description", StringComparison.OrdinalIgnoreCase)
                && value.Contains($".pages.{pageId}.", StringComparison.OrdinalIgnoreCase)
                && !value.Contains(".options.", StringComparison.OrdinalIgnoreCase));

        var options = eventOptions
            .Select(value => TryParseEventOption(value, pageId!))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .GroupBy(value => value.OptionId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                string optionId = group.Key;
                string? root = group.Select(item => item.RootKey).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                string? title = group.Select(item => item.TitleKey).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                string? detail = group.Select(item => item.DescriptionKey).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                return new
                {
                    optionId,
                    rootKey = root ?? $"{eventId}.pages.{pageId}.options.{optionId}",
                    titleKey = title ?? $"{eventId}.pages.{pageId}.options.{optionId}.title",
                    descriptionKey = detail ?? $"{eventId}.pages.{pageId}.options.{optionId}.description",
                };
            })
            .OrderBy(item => item.optionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            eventId,
            pageId,
            descriptionKey = description,
            options,
        };
    }

    internal static string? TryExtractEventPageId(string value)
    {
        string text = value.Trim();
        int pagesIndex = text.IndexOf(".pages.", StringComparison.OrdinalIgnoreCase);
        if (pagesIndex < 0)
        {
            return null;
        }

        string tail = text[(pagesIndex + ".pages.".Length)..];
        int dotIndex = tail.IndexOf('.');
        if (dotIndex <= 0)
        {
            return null;
        }

        return tail[..dotIndex];
    }

    internal static (string OptionId, string? RootKey, string? TitleKey, string? DescriptionKey)? TryParseEventOption(string value, string pageId)
    {
        string marker = $".pages.{pageId}.options.";
        int optionsIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (optionsIndex < 0)
        {
            return null;
        }

        string tail = value[(optionsIndex + marker.Length)..];
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        string optionId;
        string? suffix = null;
        int dotIndex = tail.IndexOf('.');
        if (dotIndex < 0)
        {
            optionId = tail;
        }
        else
        {
            optionId = tail[..dotIndex];
            suffix = tail[(dotIndex + 1)..];
        }

        if (string.IsNullOrWhiteSpace(optionId))
        {
            return null;
        }

        return (
            optionId,
            suffix is null ? value : null,
            string.Equals(suffix, "title", StringComparison.OrdinalIgnoreCase) ? value : null,
            string.Equals(suffix, "description", StringComparison.OrdinalIgnoreCase) ? value : null
        );
    }

    internal static string? TryExtractEventRootKey(string value)
    {
        string text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!text.Contains(".pages.", StringComparison.OrdinalIgnoreCase))
        {
            return text.All(ch => ch == '_' || char.IsLetterOrDigit(ch)) ? text : null;
        }

        int index = text.IndexOf(".pages.", StringComparison.OrdinalIgnoreCase);
        if (index <= 0)
        {
            return null;
        }

        return text[..index];
    }

    internal static void CollectEventStringsByRoot(ClrHeap heap, string eventRoot, HashSet<string> options)
    {
        string needle = $"{eventRoot}.pages.";
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.IsNull || obj.Type is null)
            {
                continue;
            }

            foreach (string text in ReadInterestingStringsFromObject(obj))
            {
                if (!text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsLikelyEventOptionText(text))
                {
                    continue;
                }

                options.Add(text);
                if (options.Count >= 16)
                {
                    return;
                }
            }
        }
    }
}
