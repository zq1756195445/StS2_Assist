#!/usr/bin/env python3
from __future__ import annotations

import argparse
from pathlib import Path


DEFAULT_REPLAY = Path(
    "/Users/cheemtain/Library/Application Support/SlayTheSpire2/steam/76561198818693118/profile1/replays/latest.mcr"
)
DEFAULT_SAVE = Path(
    "/Users/cheemtain/Library/Application Support/SlayTheSpire2/steam/76561198818693118/profile1/saves/current_run.save"
)


def read_u32_le(data: bytes, offset: int) -> tuple[int, int]:
    end = offset + 4
    return int.from_bytes(data[offset:end], "little"), end


def read_prefixed_string(data: bytes, offset: int) -> tuple[str, int]:
    length, offset = read_u32_le(data, offset)
    end = offset + length
    return data[offset:end].decode("utf-8", "replace"), end


def extract_ascii_strings(data: bytes, min_len: int = 8) -> list[tuple[int, str]]:
    strings: list[tuple[int, str]] = []
    start = None
    current: list[str] = []

    for index, byte in enumerate(data):
        if 32 <= byte < 127:
            if start is None:
                start = index
            current.append(chr(byte))
            continue

        if start is not None and len(current) >= min_len:
            strings.append((start, "".join(current)))
        start = None
        current = []

    if start is not None and len(current) >= min_len:
        strings.append((start, "".join(current)))

    return strings


def title_case(token: str) -> str:
    return " ".join(part.capitalize() for part in token.lower().split("_") if part)


def parse_title_context(entry: str) -> str | None:
    if not entry.endswith(".title") or ".options." in entry or ".pages." in entry or "CARD." in entry:
        return None
    return title_case(entry.removesuffix(".title"))


def parse_option_for_event(entry: str, event_key: str) -> str | None:
    if ".options." not in entry:
        return None
    if entry.split(".pages.")[0] != event_key:
        return None
    option = entry.split(".options.")[1].split(".")[0]
    return title_case(option)


def parse_latest_page(strings: list[tuple[int, str]]) -> dict[str, object] | None:
    latest = None
    values = [value for _, value in strings]
    for index, entry in enumerate(values):
        if ".options." not in entry:
            continue
        event_key = entry.split(".pages.")[0]
        context = "Unknown Context"
        for candidate in reversed(values[:index]):
            parsed = parse_title_context(candidate)
            if parsed:
                context = parsed
                break
        options: list[str] = []
        for candidate in values[: index + 1]:
            parsed = parse_option_for_event(candidate, event_key)
            if parsed and parsed not in options:
                options.append(parsed)
        latest = {
            "event_key": event_key,
            "event_title": title_case(event_key),
            "context_title": context,
            "choice_model": "Index" if options else "Unknown",
            "options": options,
        }
    return latest


def parse_recent_actions(strings: list[tuple[int, str]], limit: int) -> list[dict[str, object]]:
    actions: list[dict[str, object]] = []
    for offset, entry in strings:
        if "card: CARD." in entry:
            token = entry.split("card: CARD.", 1)[1].split()[0].rstrip(") ")
            token = token.rstrip("".join(ch for ch in token if not (ch.isalnum() or ch == "_")))
            target = entry.split("targetid: ", 1)[1].strip() if "targetid: " in entry else ""
            actions.append(
                {
                    "offset": offset,
                    "kind": "card",
                    "title": title_case(token),
                    "detail": f"Target {target}" if target else "No target",
                }
            )
            continue
        if ".options." in entry:
            event = entry.split(".pages.")[0]
            option = entry.split(".options.")[1].split(".")[0]
            actions.append(
                {
                    "offset": offset,
                    "kind": "choice",
                    "title": title_case(option),
                    "detail": f"Event {title_case(event)}",
                }
            )
            continue
        context = parse_title_context(entry)
        if context:
            actions.append(
                {
                    "offset": offset,
                    "kind": "context",
                    "title": context,
                    "detail": "Replay title resource",
                }
            )
            continue
        if entry in {"Gold", "HpLoss", "HEAL", "SMITH", "SmallChestGold"}:
            actions.append(
                {
                    "offset": offset,
                    "kind": "event",
                    "title": title_case(entry),
                    "detail": "Replay event marker",
                }
            )

    return actions[-limit:]


def normalize_card_id(card_id: str) -> str:
    return title_case(card_id.rsplit(".", 1)[-1])


def parse_resolved_outcome(save_path: Path) -> dict[str, object] | None:
    import json

    obj = json.loads(save_path.read_text())
    history = obj.get("map_point_history") or []
    if not history or not history[-1]:
        return None
    point = history[-1][-1]
    player_stats = point.get("player_stats") or []
    if not player_stats:
        return None
    stat = player_stats[0]
    chosen = None
    for item in stat.get("ancient_choice") or []:
        if item.get("was_chosen"):
            chosen = item.get("TextKey")
            break
    if not chosen:
        for item in stat.get("event_choices") or []:
            title = (item.get("title") or {}).get("key")
            if title:
                chosen = title.removesuffix(".title")
                break
    if not chosen:
        for item in stat.get("relic_choices") or []:
            if item.get("was_picked"):
                chosen = item.get("choice")
                break
    if not chosen:
        return None
    transformed = []
    for item in stat.get("cards_transformed") or []:
        original = (item.get("original_card") or {}).get("id")
        final = (item.get("final_card") or {}).get("id")
        if original and final:
            transformed.append(f"{normalize_card_id(original)} -> {normalize_card_id(final)}")
    room = ((point.get("rooms") or [{}])[-1]) if point.get("rooms") else {}
    return {
        "chosen_title": title_case(chosen.rsplit(".", 1)[-1]),
        "event_id": room.get("model_id") or (obj.get("pre_finished_room") or {}).get("event_id") or "unknown",
        "room_type": room.get("room_type") or (obj.get("pre_finished_room") or {}).get("room_type") or "unknown",
        "max_hp_lost": stat.get("max_hp_lost", 0),
        "damage_taken": stat.get("damage_taken", 0),
        "transformed_cards": transformed,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Inspect Slay the Spire 2 replay hints from latest.mcr")
    parser.add_argument("path", nargs="?", default=str(DEFAULT_REPLAY))
    parser.add_argument("--limit", type=int, default=12, help="number of recent actions to print")
    args = parser.parse_args()

    path = Path(args.path).expanduser()
    data = path.read_bytes()
    version, offset = read_prefixed_string(data, 0)
    commit, offset = read_prefixed_string(data, offset)
    model_hash, _ = read_u32_le(data, offset)
    strings = extract_ascii_strings(data)
    latest_page = parse_latest_page(strings)
    actions = parse_recent_actions(strings, args.limit)
    resolved_outcome = parse_resolved_outcome(DEFAULT_SAVE)

    print(f"Replay: {path}")
    print(f"Version: {version}")
    print(f"Commit: {commit}")
    print(f"Model hash: {model_hash:08x}")
    print()
    if latest_page:
        print("Latest page:")
        print(f"  Event: {latest_page['event_title']} ({latest_page['event_key']})")
        print(f"  Context: {latest_page['context_title']}")
        print(f"  Choice model: {latest_page['choice_model']}")
        print(f"  Options: {', '.join(latest_page['options']) or '-'}")
        print()

    if resolved_outcome:
        print("Resolved outcome:")
        print(f"  Chosen: {resolved_outcome['chosen_title']}")
        print(f"  Event id: {resolved_outcome['event_id']}")
        print(f"  Room type: {resolved_outcome['room_type']}")
        print(
            f"  Cost: max HP -{resolved_outcome['max_hp_lost']}, damage {resolved_outcome['damage_taken']}"
        )
        print(
            f"  Transforms: {', '.join(resolved_outcome['transformed_cards']) or '-'}"
        )
        print()

    print("Recent actions:")
    for action in actions:
        print(
            f"  @{action['offset']:>6}  {action['kind']:<7}  {action['title']}  [{action['detail']}]"
        )


if __name__ == "__main__":
    main()
