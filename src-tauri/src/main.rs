#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    fs,
    path::PathBuf,
    process::Command,
    sync::Mutex,
    time::{SystemTime, UNIX_EPOCH},
};
use tauri::{Manager, PhysicalPosition, PhysicalSize, State, WebviewWindow};

const TARGET_PROCESS_NAME: &str = "Slay the Spire 2";
const CURRENT_RUN_SAVE_PATH: &str =
    "/Users/cheemtain/Library/Application Support/SlayTheSpire2/steam/76561198818693118/profile1/saves/current_run.save";
const LATEST_REPLAY_PATH: &str =
    "/Users/cheemtain/Library/Application Support/SlayTheSpire2/steam/76561198818693118/profile1/replays/latest.mcr";

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CardDefinition {
    name: String,
    tags: Vec<String>,
    base_score: f64,
    synergy: HashMap<String, f64>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RelicDefinition {
    name: String,
    suggestion: String,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ArchetypeDefinition {
    character: String,
    name: String,
}

#[derive(Clone)]
struct Database {
    cards: Vec<CardDefinition>,
    relics: Vec<RelicDefinition>,
    archetypes: Vec<ArchetypeDefinition>,
}

impl Database {
    fn load() -> Self {
        Self {
            cards: serde_json::from_str(include_str!("../../database/cards.json")).expect("cards"),
            relics: serde_json::from_str(include_str!("../../database/relics.json"))
                .expect("relics"),
            archetypes: serde_json::from_str(include_str!("../../database/archetypes.json"))
                .expect("archetypes"),
        }
    }
}

struct AppState {
    database: Database,
    reader_cursor: Mutex<usize>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    game_state: GameState,
    recommendations: Recommendations,
    overlay: OverlayLayout,
    replay: ReplaySummary,
    source: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct GameState {
    timestamp: String,
    character: String,
    player: PlayerState,
    deck: Vec<String>,
    hand: Vec<String>,
    discard_pile: Vec<String>,
    draw_pile: Vec<String>,
    relics: Vec<String>,
    battle: BattleState,
    map: MapState,
    rewards: RewardState,
    source: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PlayerState {
    hp: i32,
    max_hp: i32,
    gold: i32,
    energy: i32,
    potions: Vec<String>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct BattleState {
    encounter_name: Option<String>,
    room_type: Option<String>,
    turns_taken: Option<i32>,
    current_phase: Option<String>,
    last_card_played: Option<String>,
    last_action_detail: Option<String>,
    enemies: Vec<EnemyState>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct EnemyState {
    name: String,
    hp: i32,
    intent: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct MapState {
    act: i32,
    current_node: String,
    upcoming_nodes: Vec<String>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct RewardState {
    cards: Vec<String>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Recommendations {
    deck_analysis: DeckAnalysis,
    card_rewards: Vec<CardRecommendation>,
    path_recommendation: PathRecommendation,
    relic_suggestions: Vec<RelicSuggestion>,
    turn_suggestion: Vec<String>,
    archetype_browser: Vec<String>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReplaySummary {
    source: String,
    version: String,
    git_commit: String,
    model_id_hash: String,
    updated_at: String,
    phase_hint: String,
    latest_page: Option<ReplayPage>,
    resolved_outcome: Option<ResolvedOutcome>,
    latest_contexts: Vec<String>,
    latest_cards: Vec<String>,
    latest_events: Vec<String>,
    latest_choices: Vec<String>,
    recent_actions: Vec<ReplayAction>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReplayAction {
    kind: String,
    title: String,
    detail: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReplayPage {
    event_key: String,
    event_title: String,
    context_title: String,
    choice_model: String,
    options: Vec<String>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ResolvedOutcome {
    room_type: String,
    event_id: String,
    chosen_title: String,
    offered_choices: Vec<String>,
    cards_gained: Vec<String>,
    gold_gained: i32,
    max_hp_lost: i32,
    damage_taken: i32,
    transformed_cards: Vec<String>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct DeckAnalysis {
    score: i32,
    tag_counts: TagCounts,
    archetypes: Vec<ArchetypeScore>,
}

#[derive(Clone, Serialize)]
struct TagCounts {
    poison: i32,
    shiv: i32,
    block: i32,
    scaling: i32,
}

#[derive(Clone, Serialize)]
struct ArchetypeScore {
    key: String,
    label: String,
    score: i32,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct CardRecommendation {
    card_name: String,
    score: f64,
    reason: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PathRecommendation {
    route: Vec<String>,
    reason: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct RelicSuggestion {
    relic_name: String,
    suggestion: String,
}

#[derive(Clone, Serialize)]
struct OverlayLayout {
    scene: String,
    scale: f32,
    condensed_sidebar: bool,
    visible_panels: Vec<String>,
    anchors: Vec<OverlayAnchor>,
}

#[derive(Clone, Deserialize)]
struct RunSave {
    acts: Vec<RunAct>,
    current_act_index: usize,
    map_point_history: Option<Vec<Vec<RunHistoryPoint>>>,
    pre_finished_room: Option<RunRoomRef>,
    players: Vec<RunPlayer>,
    save_time: Option<i64>,
    visited_map_coords: Vec<RunCoord>,
}

#[derive(Clone, Deserialize)]
struct RunRoomRef {
    event_id: Option<String>,
    room_type: Option<String>,
}

#[derive(Clone, Deserialize)]
struct RunAct {
    rooms: Option<RunActRooms>,
    saved_map: SavedMap,
}

#[derive(Clone, Deserialize)]
struct RunActRooms {
    ancient_id: Option<String>,
    boss_id: Option<String>,
    second_boss_id: Option<String>,
    boss_encounters_visited: Option<usize>,
    elite_encounter_ids: Option<Vec<String>>,
    elite_encounters_visited: Option<usize>,
    normal_encounter_ids: Option<Vec<String>>,
    normal_encounters_visited: Option<usize>,
}

#[derive(Clone, Deserialize)]
struct SavedMap {
    points: Vec<MapPoint>,
}

#[derive(Clone, Deserialize)]
struct MapPoint {
    coord: RunCoord,
    #[serde(rename = "type")]
    point_type: String,
    children: Option<Vec<RunCoord>>,
}

#[derive(Clone, Deserialize)]
struct RunCoord {
    col: i32,
    row: i32,
}

#[derive(Clone, Deserialize)]
struct RunPlayer {
    character_id: String,
    current_hp: i32,
    gold: i32,
    max_energy: i32,
    max_hp: i32,
    deck: Vec<RunCard>,
    relics: Vec<RunRelic>,
}

#[derive(Clone, Deserialize)]
struct RunCard {
    id: String,
}

#[derive(Clone, Deserialize)]
struct RunRelic {
    id: String,
}

#[derive(Clone, Deserialize)]
struct RunHistoryPoint {
    map_point_type: Option<String>,
    player_stats: Option<Vec<RunHistoryPlayerStat>>,
    rooms: Option<Vec<RunHistoryRoom>>,
}

#[derive(Clone, Deserialize)]
struct RunHistoryRoom {
    model_id: Option<String>,
    #[allow(dead_code)]
    monster_ids: Option<Vec<String>>,
    room_type: Option<String>,
    turns_taken: Option<i32>,
}

#[derive(Clone, Deserialize)]
struct RunHistoryPlayerStat {
    ancient_choice: Option<Vec<RunAncientChoice>>,
    card_choices: Option<Vec<RunCardChoice>>,
    cards_gained: Option<Vec<RunCard>>,
    cards_transformed: Option<Vec<RunCardTransform>>,
    damage_taken: Option<i32>,
    event_choices: Option<Vec<RunEventChoice>>,
    gold_gained: Option<i32>,
    max_hp_lost: Option<i32>,
    relic_choices: Option<Vec<RunRelicChoice>>,
}

#[derive(Clone, Deserialize)]
struct RunAncientChoice {
    #[serde(rename = "TextKey")]
    text_key: Option<String>,
    was_chosen: Option<bool>,
}

#[derive(Clone, Deserialize)]
struct RunCardChoice {
    card: Option<RunCard>,
    was_picked: Option<bool>,
}

#[derive(Clone, Deserialize)]
struct RunCardTransform {
    final_card: Option<RunCard>,
    original_card: Option<RunCard>,
}

#[derive(Clone, Deserialize)]
struct RunEventChoice {
    title: Option<RunTextRef>,
}

#[derive(Clone, Deserialize)]
struct RunRelicChoice {
    choice: Option<String>,
    was_picked: Option<bool>,
}

#[derive(Clone, Deserialize)]
struct RunTextRef {
    key: Option<String>,
}

#[derive(Clone, Serialize)]
struct OverlayAnchor {
    id: String,
    title: String,
    body: String,
    x: f32,
    y: f32,
    tone: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowMode {
    attached_to_game: bool,
}

#[derive(Clone)]
struct Bounds {
    x: i32,
    y: i32,
    width: u32,
    height: u32,
}

#[tauri::command]
fn get_snapshot(state: State<AppState>) -> Result<Snapshot, String> {
    let replay = read_replay_summary().unwrap_or_else(mock_replay_summary);
    let game_state = read_live_game_state(Some(&replay))
        .unwrap_or_else(|| next_mock_state(&state).expect("mock state"));
    let recommendations = generate_recommendations(&game_state, &state.database);
    let overlay = build_overlay_layout(&game_state, &recommendations, &replay);
    let source = game_state.source.clone();

    Ok(Snapshot {
        game_state,
        recommendations,
        overlay,
        replay,
        source,
    })
}

#[tauri::command]
fn sync_overlay_window(window: WebviewWindow) -> Result<WindowMode, String> {
    let attached = apply_window_bounds(&window)?;
    Ok(WindowMode {
        attached_to_game: attached,
    })
}

fn next_mock_state(state: &State<AppState>) -> Result<GameState, String> {
    let mut cursor = state
        .reader_cursor
        .lock()
        .map_err(|_| "reader lock poisoned")?;
    let states = mock_states();
    let next = states[*cursor % states.len()].clone();
    *cursor += 1;
    Ok(next)
}

fn read_live_game_state(replay: Option<&ReplaySummary>) -> Option<GameState> {
    let path = PathBuf::from(CURRENT_RUN_SAVE_PATH);
    let raw = fs::read_to_string(path).ok()?;
    let save: RunSave = serde_json::from_str(&raw).ok()?;
    let player = save.players.first()?;
    let act = save.acts.get(save.current_act_index)?;
    let current_coord = save.visited_map_coords.last().cloned();
    let current_node = current_coord
        .as_ref()
        .and_then(|coord| find_point_by_coord(&act.saved_map.points, coord))
        .map(|point| point.point_type.clone())
        .unwrap_or_else(|| "map".into());

    let upcoming_nodes = current_coord
        .as_ref()
        .and_then(|coord| find_point_by_coord(&act.saved_map.points, coord))
        .and_then(|point| point.children.as_ref())
        .map(|children| {
            children
                .iter()
                .filter_map(|child| find_point_by_coord(&act.saved_map.points, child))
                .map(|point| normalize_node_type(&point.point_type))
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();

    Some(GameState {
        timestamp: save
            .save_time
            .map(unix_timestamp_to_iso_like)
            .unwrap_or_else(now_timestamp_string),
        character: normalize_character_id(&player.character_id),
        player: PlayerState {
            hp: player.current_hp,
            max_hp: player.max_hp,
            gold: player.gold,
            energy: player.max_energy,
            potions: Vec::new(),
        },
        deck: player
            .deck
            .iter()
            .map(|card| normalize_card_id(&card.id))
            .collect(),
        hand: Vec::new(),
        discard_pile: Vec::new(),
        draw_pile: Vec::new(),
        relics: player
            .relics
            .iter()
            .map(|relic| normalize_relic_id(&relic.id))
            .collect(),
        battle: infer_live_battle_state(&save, act, &current_node, replay),
        map: MapState {
            act: (save.current_act_index + 1) as i32,
            current_node: normalize_node_type(&current_node),
            upcoming_nodes,
        },
        rewards: RewardState { cards: Vec::new() },
        source: "current_run.save".into(),
    })
}

fn infer_live_battle_state(
    save: &RunSave,
    act: &RunAct,
    current_node_type: &str,
    replay: Option<&ReplaySummary>,
) -> BattleState {
    let last_room = save
        .map_point_history
        .as_ref()
        .and_then(|history| history.last())
        .and_then(|points| points.last())
        .and_then(|point| point.rooms.as_ref())
        .and_then(|rooms| rooms.last());
    let latest_card =
        replay.and_then(|entry| find_latest_replay_action(&entry.recent_actions, "card"));
    let latest_phase =
        replay.and_then(|entry| find_latest_replay_action(&entry.recent_actions, "phase"));

    BattleState {
        encounter_name: infer_current_encounter_name(act.rooms.as_ref(), current_node_type)
            .or_else(|| {
                last_room
                    .and_then(|room| room.model_id.as_ref())
                    .map(|id| normalize_encounter_id(id))
            }),
        room_type: Some(normalize_node_type(current_node_type)),
        turns_taken: last_room.and_then(|room| room.turns_taken),
        current_phase: latest_phase.map(|action| action.title.clone()),
        last_card_played: latest_card.map(|action| action.title.clone()),
        last_action_detail: latest_card
            .map(|action| action.detail.clone())
            .or_else(|| latest_phase.map(|action| action.detail.clone())),
        enemies: Vec::new(),
    }
}

fn infer_current_encounter_name(
    act_rooms: Option<&RunActRooms>,
    current_node_type: &str,
) -> Option<String> {
    let rooms = act_rooms?;
    match current_node_type {
        "monster" => rooms
            .normal_encounter_ids
            .as_ref()
            .and_then(|encounters| encounters.get(rooms.normal_encounters_visited.unwrap_or(0)))
            .map(|id| normalize_encounter_id(id)),
        "elite" => rooms
            .elite_encounter_ids
            .as_ref()
            .and_then(|encounters| encounters.get(rooms.elite_encounters_visited.unwrap_or(0)))
            .map(|id| normalize_encounter_id(id)),
        "boss" => {
            let visited = rooms.boss_encounters_visited.unwrap_or(0);
            if visited == 0 {
                rooms.boss_id.as_ref().map(|id| normalize_encounter_id(id))
            } else {
                rooms
                    .second_boss_id
                    .as_ref()
                    .map(|id| normalize_encounter_id(id))
            }
        }
        "ancient" => rooms
            .ancient_id
            .as_ref()
            .map(|id| normalize_encounter_id(id)),
        _ => None,
    }
}

fn find_point_by_coord<'a>(points: &'a [MapPoint], coord: &RunCoord) -> Option<&'a MapPoint> {
    points
        .iter()
        .find(|point| point.coord.col == coord.col && point.coord.row == coord.row)
}

fn normalize_character_id(id: &str) -> String {
    id.rsplit('.')
        .next()
        .unwrap_or(id)
        .to_ascii_lowercase()
        .chars()
        .enumerate()
        .map(|(i, ch)| {
            if i == 0 {
                ch.to_ascii_uppercase().to_string()
            } else {
                ch.to_string()
            }
        })
        .collect::<String>()
}

fn normalize_card_id(id: &str) -> String {
    title_case_from_token(id.rsplit('.').next().unwrap_or(id))
}

fn normalize_relic_id(id: &str) -> String {
    title_case_from_token(id.rsplit('.').next().unwrap_or(id))
}

fn normalize_encounter_id(id: &str) -> String {
    title_case_from_token(id.rsplit('.').next().unwrap_or(id))
}

fn normalize_node_type(raw: &str) -> String {
    match raw {
        "monster" => "Battle".into(),
        "elite" => "Elite".into(),
        "rest_site" => "Rest".into(),
        "shop" => "Shop".into(),
        "treasure" => "Treasure".into(),
        "unknown" => "Unknown".into(),
        "boss" => "Boss".into(),
        "ancient" => "Start".into(),
        other => title_case_from_token(other),
    }
}

fn title_case_from_token(token: &str) -> String {
    token
        .split('_')
        .filter(|part| !part.is_empty())
        .map(|part| {
            let lower = part.to_ascii_lowercase();
            let mut chars = lower.chars();
            match chars.next() {
                Some(first) => first.to_ascii_uppercase().to_string() + chars.as_str(),
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn now_timestamp_string() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs())
        .unwrap_or(0);
    format!("unix:{secs}")
}

fn unix_timestamp_to_iso_like(timestamp: i64) -> String {
    format!("unix:{timestamp}")
}

fn read_replay_summary() -> Option<ReplaySummary> {
    let bytes = fs::read(LATEST_REPLAY_PATH).ok()?;
    let metadata = parse_replay_header(&bytes)?;
    let strings = extract_ascii_strings(&bytes, 8);
    let latest_page = parse_latest_replay_page(&strings);
    let resolved_outcome = read_resolved_outcome_from_save();
    let updated_at = fs::metadata(LATEST_REPLAY_PATH)
        .ok()
        .and_then(|meta| meta.modified().ok())
        .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
        .map(|duration| format!("unix:{}", duration.as_secs()))
        .unwrap_or_else(now_timestamp_string);

    let mut latest_cards = Vec::new();
    let mut latest_contexts = Vec::new();
    let mut latest_events = Vec::new();
    let mut latest_choices = Vec::new();
    let mut recent_actions = Vec::new();

    for entry in strings {
        if let Some(card) = parse_card_action(&entry) {
            push_unique_limited(&mut latest_cards, card.title.clone(), 6);
            push_limited(&mut recent_actions, card, 8);
            continue;
        }

        if let Some(phase) = parse_phase_marker(&entry) {
            push_limited(&mut recent_actions, phase, 8);
            continue;
        }

        if let Some(choice) = parse_event_choice(&entry) {
            push_unique_limited(&mut latest_choices, choice.title.clone(), 5);
            push_limited(&mut recent_actions, choice, 8);
            continue;
        }

        if let Some(context) = parse_title_context(&entry) {
            push_unique_limited(&mut latest_contexts, context.title.clone(), 5);
            push_limited(&mut recent_actions, context, 8);
            continue;
        }

        if matches!(entry.as_str(), "Gold" | "HpLoss" | "HEAL" | "SMITH") {
            let title = title_case_from_token(&entry);
            push_unique_limited(&mut latest_events, title.clone(), 5);
            push_limited(
                &mut recent_actions,
                ReplayAction {
                    kind: "event".into(),
                    title,
                    detail: "Replay event marker".into(),
                },
                8,
            );
        }
    }

    if latest_cards.is_empty()
        && latest_events.is_empty()
        && latest_choices.is_empty()
        && recent_actions.is_empty()
    {
        return None;
    }

    Some(ReplaySummary {
        source: "latest.mcr".into(),
        version: metadata.version,
        git_commit: metadata.git_commit,
        model_id_hash: format!("{:08x}", metadata.model_id_hash),
        updated_at,
        phase_hint: infer_replay_phase(&recent_actions),
        latest_page,
        resolved_outcome,
        latest_contexts,
        latest_cards,
        latest_events,
        latest_choices,
        recent_actions,
    })
}

struct ReplayHeader {
    version: String,
    git_commit: String,
    model_id_hash: u32,
}

fn parse_replay_header(bytes: &[u8]) -> Option<ReplayHeader> {
    let mut cursor = 0usize;
    Some(ReplayHeader {
        version: read_prefixed_string(bytes, &mut cursor)?,
        git_commit: read_prefixed_string(bytes, &mut cursor)?,
        model_id_hash: read_u32_le(bytes, &mut cursor)?,
    })
}

fn read_prefixed_string(bytes: &[u8], cursor: &mut usize) -> Option<String> {
    let len = read_u32_le(bytes, cursor)? as usize;
    let end = cursor.checked_add(len)?;
    let raw = bytes.get(*cursor..end)?;
    *cursor = end;
    String::from_utf8(raw.to_vec()).ok()
}

fn read_u32_le(bytes: &[u8], cursor: &mut usize) -> Option<u32> {
    let end = cursor.checked_add(4)?;
    let raw = bytes.get(*cursor..end)?;
    *cursor = end;
    Some(u32::from_le_bytes(raw.try_into().ok()?))
}

fn extract_ascii_strings(bytes: &[u8], min_len: usize) -> Vec<String> {
    let mut results = Vec::new();
    let mut current = String::new();

    for &byte in bytes {
        if (32..127).contains(&byte) {
            current.push(byte as char);
        } else {
            if current.len() >= min_len {
                results.push(current.clone());
            }
            current.clear();
        }
    }

    if current.len() >= min_len {
        results.push(current);
    }

    results
}

fn parse_card_action(entry: &str) -> Option<ReplayAction> {
    let marker = "card: CARD.";
    let start = entry.find(marker)? + marker.len();
    let card_token = entry[start..]
        .split_whitespace()
        .next()?
        .trim_end_matches(|ch: char| !ch.is_ascii_alphanumeric() && ch != '_');

    if card_token.is_empty() {
        return None;
    }

    let detail = if let Some(target_marker) = entry.split("targetid: ").nth(1) {
        let target = target_marker.trim();
        if target.is_empty() {
            "No target".into()
        } else {
            format!("Target {target}")
        }
    } else {
        "Card action".into()
    };

    Some(ReplayAction {
        kind: "card".into(),
        title: title_case_from_token(card_token),
        detail,
    })
}

fn parse_event_choice(entry: &str) -> Option<ReplayAction> {
    let marker = ".options.";
    let start = entry.find(marker)? + marker.len();
    let choice_token = entry[start..].split('.').next()?;

    if choice_token.is_empty() {
        return None;
    }

    let event_token = entry.split(".pages.").next().unwrap_or("event");
    Some(ReplayAction {
        kind: "choice".into(),
        title: title_case_from_token(choice_token),
        detail: format!("Event {}", title_case_from_token(event_token)),
    })
}

fn parse_phase_marker(entry: &str) -> Option<ReplayAction> {
    if !entry.contains(" phase ") {
        return None;
    }

    Some(ReplayAction {
        kind: "phase".into(),
        title: normalize_phase_marker(entry),
        detail: entry.into(),
    })
}

fn normalize_phase_marker(entry: &str) -> String {
    let trimmed = entry.trim();
    let stripped = trimmed.strip_prefix("after ").unwrap_or(trimmed);
    let normalized = stripped
        .replace(" phase ", " ")
        .replace("player turn", "player_turn")
        .replace("enemy turn", "enemy_turn")
        .replace(" end", "_end")
        .replace(" start", "_start");
    title_case_from_token(&normalized.replace(' ', "_"))
}

fn parse_title_context(entry: &str) -> Option<ReplayAction> {
    if !entry.ends_with(".title") || entry.contains(".options.") || entry.contains("CARD.") {
        return None;
    }

    let token = entry.trim_end_matches(".title");
    if token.contains(".pages.") {
        return None;
    }

    Some(ReplayAction {
        kind: "context".into(),
        title: title_case_from_token(token),
        detail: "Replay title resource".into(),
    })
}

fn parse_latest_replay_page(strings: &[String]) -> Option<ReplayPage> {
    let mut latest: Option<ReplayPage> = None;

    for (index, entry) in strings.iter().enumerate() {
        if !entry.contains(".options.") {
            continue;
        }

        let event_key = entry.split(".pages.").next()?.to_string();
        let context_title = strings[..index]
            .iter()
            .rev()
            .find_map(|candidate| parse_context_title(candidate))
            .unwrap_or_else(|| "Unknown Context".into());

        let options = strings[..=index]
            .iter()
            .filter_map(|candidate| parse_option_for_event(candidate, &event_key))
            .fold(Vec::new(), |mut acc, option| {
                if !acc.iter().any(|existing| existing == &option) {
                    acc.push(option);
                }
                acc
            });

        latest = Some(ReplayPage {
            event_title: title_case_from_token(&event_key),
            event_key,
            context_title,
            choice_model: infer_choice_model(&options),
            options,
        });
    }

    latest
}

fn read_resolved_outcome_from_save() -> Option<ResolvedOutcome> {
    let raw = fs::read_to_string(CURRENT_RUN_SAVE_PATH).ok()?;
    let save: RunSave = serde_json::from_str(&raw).ok()?;
    let history = save.map_point_history?;
    let pre_finished_room = save.pre_finished_room.clone();
    let history_point = history.last()?.last()?;
    let player_stat = history_point.player_stats.as_ref()?.first()?;
    let room = history_point
        .rooms
        .as_ref()
        .and_then(|rooms| rooms.last())
        .cloned();

    let chosen_title = player_stat
        .ancient_choice
        .as_ref()
        .and_then(|choices| {
            choices
                .iter()
                .find(|choice| choice.was_chosen.unwrap_or(false))
                .and_then(|choice| choice.text_key.clone())
        })
        .or_else(|| {
            player_stat
                .event_choices
                .as_ref()
                .and_then(|choices| choices.first())
                .and_then(|choice| choice.title.as_ref())
                .and_then(|title| title.key.as_ref())
                .map(|key| key.trim_end_matches(".title").to_string())
        })
        .or_else(|| {
            player_stat.relic_choices.as_ref().and_then(|choices| {
                choices
                    .iter()
                    .find(|choice| choice.was_picked.unwrap_or(false))
                    .and_then(|choice| choice.choice.clone())
            })
        })
        .or_else(|| {
            player_stat.card_choices.as_ref().and_then(|choices| {
                choices
                    .iter()
                    .find(|choice| choice.was_picked.unwrap_or(false))
                    .and_then(|choice| choice.card.as_ref())
                    .map(|card| card.id.clone())
            })
        })?;

    let offered_choices = player_stat
        .card_choices
        .as_ref()
        .map(|choices| {
            choices
                .iter()
                .filter_map(|choice| choice.card.as_ref().map(|card| normalize_card_id(&card.id)))
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();

    let cards_gained = player_stat
        .cards_gained
        .as_ref()
        .map(|cards| {
            cards
                .iter()
                .map(|card| normalize_card_id(&card.id))
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();

    let transformed_cards = player_stat
        .cards_transformed
        .as_ref()
        .map(|transforms| {
            transforms
                .iter()
                .filter_map(|transform| {
                    let original = transform
                        .original_card
                        .as_ref()
                        .map(|card| normalize_card_id(&card.id))?;
                    let final_card = transform
                        .final_card
                        .as_ref()
                        .map(|card| normalize_card_id(&card.id))?;
                    Some(format!("{original} -> {final_card}"))
                })
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();

    Some(ResolvedOutcome {
        room_type: room
            .as_ref()
            .and_then(|entry| entry.room_type.clone())
            .or_else(|| {
                pre_finished_room
                    .as_ref()
                    .and_then(|room| room.room_type.clone())
            })
            .unwrap_or_else(|| {
                history_point
                    .map_point_type
                    .clone()
                    .unwrap_or_else(|| "unknown".into())
            }),
        event_id: room
            .as_ref()
            .and_then(|entry| entry.model_id.clone())
            .or_else(|| {
                pre_finished_room
                    .as_ref()
                    .and_then(|room| room.event_id.clone())
            })
            .unwrap_or_else(|| "unknown".into()),
        chosen_title: title_case_from_token(
            chosen_title.rsplit('.').next().unwrap_or(&chosen_title),
        ),
        offered_choices,
        cards_gained,
        gold_gained: player_stat.gold_gained.unwrap_or(0),
        max_hp_lost: player_stat.max_hp_lost.unwrap_or(0),
        damage_taken: player_stat.damage_taken.unwrap_or(0),
        transformed_cards,
    })
}

fn parse_context_title(entry: &str) -> Option<String> {
    parse_title_context(entry).map(|action| action.title)
}

fn parse_option_for_event(entry: &str, event_key: &str) -> Option<String> {
    if !entry.contains(".options.") {
        return None;
    }
    if entry.split(".pages.").next()? != event_key {
        return None;
    }
    let token = entry.split(".options.").nth(1)?.split('.').next()?;
    Some(title_case_from_token(token))
}

fn infer_choice_model(options: &[String]) -> String {
    if options.is_empty() {
        "Unknown".into()
    } else {
        "Index".into()
    }
}

fn push_unique_limited(items: &mut Vec<String>, value: String, max_len: usize) {
    if items.iter().any(|item| item == &value) {
        return;
    }
    items.push(value);
    if items.len() > max_len {
        items.remove(0);
    }
}

fn push_limited<T>(items: &mut Vec<T>, value: T, max_len: usize) {
    items.push(value);
    if items.len() > max_len {
        items.remove(0);
    }
}

fn mock_replay_summary() -> ReplaySummary {
    ReplaySummary {
        source: "mock".into(),
        version: "v0.98.2".into(),
        git_commit: "f4eeecc6".into(),
        model_id_hash: "62796dc1".into(),
        updated_at: "unix:1741667100".into(),
        phase_hint: "battle".into(),
        latest_page: Some(ReplayPage {
            event_key: "THIS_OR_THAT".into(),
            event_title: "This Or That".into(),
            context_title: "Arcane Scroll".into(),
            choice_model: "Index".into(),
            options: vec!["Ornate".into()],
        }),
        resolved_outcome: Some(ResolvedOutcome {
            room_type: "event".into(),
            event_id: "EVENT.NEOW".into(),
            chosen_title: "Leafy Poultice".into(),
            offered_choices: vec![
                "Arcane Scroll".into(),
                "Nutritious Oyster".into(),
                "Leafy Poultice".into(),
            ],
            cards_gained: Vec::new(),
            gold_gained: 0,
            max_hp_lost: 10,
            damage_taken: 10,
            transformed_cards: vec![
                "Strike Ironclad -> Stomp".into(),
                "Defend Ironclad -> Anger".into(),
            ],
        }),
        latest_contexts: vec!["Arcane Scroll".into()],
        latest_cards: vec!["Deadly Poison".into(), "Backflip".into(), "Catalyst".into()],
        latest_events: vec!["Gold".into(), "Heal".into()],
        latest_choices: vec!["Ornate".into()],
        recent_actions: vec![
            ReplayAction {
                kind: "context".into(),
                title: "Arcane Scroll".into(),
                detail: "Replay title resource".into(),
            },
            ReplayAction {
                kind: "card".into(),
                title: "Deadly Poison".into(),
                detail: "No target".into(),
            },
            ReplayAction {
                kind: "choice".into(),
                title: "Ornate".into(),
                detail: "Event This Or That".into(),
            },
        ],
    }
}

fn infer_replay_phase(actions: &[ReplayAction]) -> String {
    match actions.last().map(|action| action.kind.as_str()) {
        Some("card") | Some("phase") => "battle".into(),
        Some("choice") | Some("event") => "event".into(),
        _ => "unknown".into(),
    }
}

fn find_latest_replay_action<'a>(
    actions: &'a [ReplayAction],
    kind: &str,
) -> Option<&'a ReplayAction> {
    actions.iter().rev().find(|action| action.kind == kind)
}

fn mock_states() -> Vec<GameState> {
    vec![
        GameState {
            timestamp: "2026-03-11T11:05:07.554Z".into(),
            character: "Silent".into(),
            player: PlayerState {
                hp: 46,
                max_hp: 70,
                gold: 118,
                energy: 3,
                potions: vec!["Dex Potion".into()],
            },
            deck: vec![
                "Strike",
                "Strike",
                "Strike",
                "Defend",
                "Defend",
                "Defend",
                "Neutralize",
                "Survivor",
                "Deadly Poison",
                "Bouncing Flask",
                "Backflip",
            ]
            .into_iter()
            .map(String::from)
            .collect(),
            hand: vec![
                "Neutralize",
                "Backflip",
                "Defend",
                "Strike",
                "Deadly Poison",
            ]
            .into_iter()
            .map(String::from)
            .collect(),
            discard_pile: vec!["Strike".into()],
            draw_pile: vec!["Bouncing Flask", "Defend", "Survivor", "Strike", "Defend"]
                .into_iter()
                .map(String::from)
                .collect(),
            relics: vec!["Bag of Preparation".into()],
            battle: BattleState {
                encounter_name: Some("Jaw Worm".into()),
                room_type: Some("Battle".into()),
                turns_taken: Some(2),
                current_phase: Some("Player Turn".into()),
                last_card_played: Some("Deadly Poison".into()),
                last_action_detail: Some("No target".into()),
                enemies: vec![EnemyState {
                    name: "Jaw Worm".into(),
                    hp: 38,
                    intent: "Attack 12".into(),
                }],
            },
            map: MapState {
                act: 1,
                current_node: "Battle".into(),
                upcoming_nodes: vec!["Elite".into(), "Rest".into(), "Shop".into()],
            },
            rewards: RewardState {
                cards: vec!["Catalyst".into(), "Backflip".into(), "Slice".into()],
            },
            source: "mock".into(),
        },
        GameState {
            timestamp: "2026-03-11T11:05:12.554Z".into(),
            character: "Silent".into(),
            player: PlayerState {
                hp: 28,
                max_hp: 70,
                gold: 172,
                energy: 3,
                potions: vec!["Fire Potion".into()],
            },
            deck: vec![
                "Strike",
                "Strike",
                "Defend",
                "Defend",
                "Neutralize",
                "Survivor",
                "Blade Dance",
                "Cloak And Dagger",
                "Slice",
                "Backflip",
                "Footwork",
            ]
            .into_iter()
            .map(String::from)
            .collect(),
            hand: vec!["Blade Dance", "Footwork", "Strike", "Defend", "Backflip"]
                .into_iter()
                .map(String::from)
                .collect(),
            discard_pile: vec!["Neutralize".into(), "Survivor".into()],
            draw_pile: vec!["Slice", "Defend", "Strike", "Cloak And Dagger"]
                .into_iter()
                .map(String::from)
                .collect(),
            relics: vec!["Kunai".into(), "Oddly Smooth Stone".into()],
            battle: BattleState {
                encounter_name: Some("Gremlin Nob".into()),
                room_type: Some("Elite".into()),
                turns_taken: Some(3),
                current_phase: Some("Player Turn".into()),
                last_card_played: Some("Blade Dance".into()),
                last_action_detail: Some("No target".into()),
                enemies: vec![EnemyState {
                    name: "Gremlin Nob".into(),
                    hp: 85,
                    intent: "Attack 14".into(),
                }],
            },
            map: MapState {
                act: 1,
                current_node: "Elite".into(),
                upcoming_nodes: vec!["Rest".into(), "Shop".into(), "Battle".into()],
            },
            rewards: RewardState {
                cards: vec!["Catalyst".into(), "Backflip".into(), "Slice".into()],
            },
            source: "mock".into(),
        },
    ]
}

fn generate_recommendations(game_state: &GameState, database: &Database) -> Recommendations {
    let deck_analysis = compute_deck_power_score(game_state, database);
    let card_rewards = evaluate_card_reward(game_state, &deck_analysis, database);
    let path_recommendation = recommend_path(game_state, &deck_analysis);
    let relic_suggestions = build_relic_suggestions(game_state, database);
    let turn_suggestion = explain_turn(game_state, &deck_analysis);
    let archetype_browser = database
        .archetypes
        .iter()
        .filter(|entry| entry.character == game_state.character)
        .map(|entry| entry.name.clone())
        .collect();

    Recommendations {
        deck_analysis,
        card_rewards,
        path_recommendation,
        relic_suggestions,
        turn_suggestion,
        archetype_browser,
    }
}

fn compute_deck_power_score(game_state: &GameState, database: &Database) -> DeckAnalysis {
    let cards_by_name: HashMap<String, &CardDefinition> = database
        .cards
        .iter()
        .map(|card| (card.name.to_lowercase(), card))
        .collect();
    let mut tag_counts = TagCounts {
        poison: 0,
        shiv: 0,
        block: 0,
        scaling: 0,
    };

    for card_name in &game_state.deck {
        if let Some(card) = cards_by_name.get(&card_name.to_lowercase()) {
            for tag in &card.tags {
                match tag.as_str() {
                    "poison" => tag_counts.poison += 1,
                    "shiv" => tag_counts.shiv += 1,
                    "block" => tag_counts.block += 1,
                    "scaling" => tag_counts.scaling += 1,
                    _ => {}
                }
            }
        }
    }

    let mut archetypes = vec![
        ArchetypeScore {
            key: "poison".into(),
            label: "Poison".into(),
            score: tag_counts.poison * 2 + tag_counts.scaling,
        },
        ArchetypeScore {
            key: "shiv".into(),
            label: "Shiv".into(),
            score: tag_counts.shiv * 2,
        },
        ArchetypeScore {
            key: "block".into(),
            label: "Block Scaling".into(),
            score: tag_counts.block + tag_counts.scaling,
        },
    ];
    archetypes.retain(|entry| entry.score > 0);
    archetypes.sort_by(|left, right| right.score.cmp(&left.score));

    let offense =
        tag_counts.poison * 8 + tag_counts.shiv * 6 + (game_state.deck.len() as i32 * 3 / 2);
    let defense = tag_counts.block * 8 + ((game_state.player.hp as f64) * 0.35).round() as i32;
    let scaling = tag_counts.scaling * 12;
    let relic_bonus = game_state.relics.len() as i32 * 4;
    let score = (offense + defense + scaling + relic_bonus).min(100);

    DeckAnalysis {
        score,
        tag_counts,
        archetypes,
    }
}

fn evaluate_card_reward(
    game_state: &GameState,
    deck_analysis: &DeckAnalysis,
    database: &Database,
) -> Vec<CardRecommendation> {
    let cards_by_name: HashMap<String, &CardDefinition> = database
        .cards
        .iter()
        .map(|card| (card.name.to_lowercase(), card))
        .collect();
    let primary_archetype = deck_analysis
        .archetypes
        .first()
        .map(|entry| entry.key.as_str())
        .unwrap_or("block");

    let mut results = Vec::new();

    for card_name in &game_state.rewards.cards {
        if let Some(card) = cards_by_name.get(&card_name.to_lowercase()) {
            let synergy_score = *card.synergy.get(primary_archetype).unwrap_or(&0.0);
            let deck_size_penalty = if game_state.deck.len() > 15 {
                -0.5
            } else {
                0.0
            };
            let hp_ratio = game_state.player.hp as f64 / game_state.player.max_hp.max(1) as f64;
            let low_hp_block_bonus =
                if hp_ratio < 0.45 && card.tags.iter().any(|tag| tag == "block") {
                    1.5
                } else {
                    0.0
                };
            let score = card.base_score + synergy_score + deck_size_penalty + low_hp_block_bonus;
            let mut reasons = vec![format!("fits {} plan", primary_archetype)];
            if card.tags.iter().any(|tag| tag == "scaling") {
                reasons.push("improves long fights".into());
            }
            if low_hp_block_bonus > 0.0 {
                reasons.push("stabilizes low HP run".into());
            }

            results.push(CardRecommendation {
                card_name: card_name.clone(),
                score: (score * 10.0).round() / 10.0,
                reason: reasons.join(", "),
            });
        } else {
            results.push(CardRecommendation {
                card_name: card_name.clone(),
                score: 1.0,
                reason: "Unknown card in local database.".into(),
            });
        }
    }

    results.sort_by(|left, right| right.score.partial_cmp(&left.score).unwrap());
    results
}

fn recommend_path(game_state: &GameState, deck_analysis: &DeckAnalysis) -> PathRecommendation {
    let hp_ratio = game_state.player.hp as f64 / game_state.player.max_hp.max(1) as f64;
    let preferred_order = if hp_ratio < 0.4 {
        (
            vec!["Rest", "Shop", "Battle", "Elite"],
            "Low HP: recover and spend before taking higher variance fights.",
        )
    } else if deck_analysis.score >= 65 {
        (
            vec!["Elite", "Shop", "Rest", "Battle"],
            "Deck looks strong enough to convert elite fights into scaling rewards.",
        )
    } else {
        (
            vec!["Battle", "Shop", "Rest", "Elite"],
            "Moderate deck strength: stabilize first, then take risk if rewards justify it.",
        )
    };

    let mut route = game_state.map.upcoming_nodes.clone();
    route.sort_by_key(|node| {
        preferred_order
            .0
            .iter()
            .position(|candidate| candidate == node)
            .unwrap_or(99)
    });

    PathRecommendation {
        route,
        reason: preferred_order.1.into(),
    }
}

fn build_relic_suggestions(game_state: &GameState, database: &Database) -> Vec<RelicSuggestion> {
    let relics_by_name: HashMap<String, &RelicDefinition> = database
        .relics
        .iter()
        .map(|relic| (relic.name.to_lowercase(), relic))
        .collect();

    game_state
        .relics
        .iter()
        .filter_map(|relic_name| relics_by_name.get(&relic_name.to_lowercase()))
        .map(|relic| RelicSuggestion {
            relic_name: relic.name.clone(),
            suggestion: relic.suggestion.clone(),
        })
        .collect()
}

fn explain_turn(game_state: &GameState, deck_analysis: &DeckAnalysis) -> Vec<String> {
    let hand = &game_state.hand;
    let primary = deck_analysis
        .archetypes
        .first()
        .map(|entry| entry.key.as_str())
        .unwrap_or("block");

    if primary == "poison" && hand.iter().any(|card| card == "Deadly Poison") {
        return vec!["Deadly Poison".into(), "Backflip".into(), "Defend".into()];
    }

    if primary == "shiv" && hand.iter().any(|card| card == "Blade Dance") {
        return vec!["Footwork".into(), "Blade Dance".into(), "Backflip".into()];
    }

    hand.iter().take(3).cloned().collect()
}

fn build_overlay_layout(
    game_state: &GameState,
    recommendations: &Recommendations,
    replay: &ReplaySummary,
) -> OverlayLayout {
    let enemy = game_state.battle.enemies.first();
    let top_reward = recommendations.card_rewards.first();
    let scene = detect_scene(game_state, replay);
    let battle_title = enemy
        .map(|entry| format!("{} 意图", entry.name))
        .or_else(|| {
            game_state
                .battle
                .encounter_name
                .as_ref()
                .map(|name| format!("{name} 战况"))
        })
        .unwrap_or_else(|| "战斗信息".into());
    let battle_body = if let Some(entry) = enemy {
        format!(
            "{}，建议优先看 {}。",
            entry.intent,
            recommendations
                .turn_suggestion
                .first()
                .cloned()
                .unwrap_or_else(|| "当前手牌".into())
        )
    } else if let Some(encounter) = &game_state.battle.encounter_name {
        let phase = game_state
            .battle
            .current_phase
            .clone()
            .or_else(|| game_state.battle.last_card_played.clone())
            .unwrap_or_else(|| "等待更多战斗动作".into());
        format!("{encounter}，{phase}。")
    } else {
        "暂无战斗目标。".into()
    };

    OverlayLayout {
        scale: overlay_scale_for_scene(&scene),
        condensed_sidebar: scene != "reward",
        visible_panels: visible_panels_for_scene(&scene),
        scene,
        anchors: vec![
            OverlayAnchor {
                id: "enemy-intent".into(),
                title: battle_title,
                body: battle_body,
                x: 0.08,
                y: 0.12,
                tone: "danger".into(),
            },
            OverlayAnchor {
                id: "hand-play".into(),
                title: "出牌顺序".into(),
                body: recommendations.turn_suggestion.join(" -> "),
                x: 0.14,
                y: 0.76,
                tone: "info".into(),
            },
            OverlayAnchor {
                id: "card-reward".into(),
                title: "选牌建议".into(),
                body: top_reward
                    .map(|entry| format!("{} ({:.1})", entry.card_name, entry.score))
                    .unwrap_or_else(|| "暂无奖励".into()),
                x: 0.64,
                y: 0.68,
                tone: "accent".into(),
            },
            OverlayAnchor {
                id: "map-route".into(),
                title: "路线建议".into(),
                body: recommendations.path_recommendation.route.join(" -> "),
                x: 0.62,
                y: 0.2,
                tone: "good".into(),
            },
        ],
    }
}

fn detect_scene(game_state: &GameState, replay: &ReplaySummary) -> String {
    if !game_state.rewards.cards.is_empty() {
        return "reward".into();
    }
    if !game_state.battle.enemies.is_empty() {
        return "battle".into();
    }
    if game_state.battle.encounter_name.is_some()
        && matches!(
            game_state.map.current_node.as_str(),
            "Battle" | "Elite" | "Boss"
        )
    {
        return "battle".into();
    }
    if replay.phase_hint == "event" {
        return "event".into();
    }
    if replay.phase_hint == "battle" {
        return "battle".into();
    }
    "map".into()
}

fn overlay_scale_for_scene(scene: &str) -> f32 {
    match scene {
        "reward" => 1.0,
        "battle" => 0.94,
        "event" => 0.96,
        "map" => 0.9,
        _ => 1.0,
    }
}

fn visible_panels_for_scene(scene: &str) -> Vec<String> {
    match scene {
        "reward" => vec!["header", "rewards", "archetypes"],
        "battle" => vec!["header", "relics"],
        "event" => vec!["header", "relics"],
        "map" => vec!["header", "path", "relics"],
        _ => vec!["header", "rewards", "path", "relics", "archetypes"],
    }
    .into_iter()
    .map(String::from)
    .collect()
}

fn apply_window_bounds(window: &WebviewWindow) -> Result<bool, String> {
    if let Some(target) = get_target_window_bounds() {
        window
            .set_position(PhysicalPosition::new(target.x, target.y))
            .map_err(|error| error.to_string())?;
        window
            .set_size(PhysicalSize::new(target.width, target.height))
            .map_err(|error| error.to_string())?;
        return Ok(true);
    }

    let monitor = window
        .current_monitor()
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "No active monitor".to_string())?;
    let work = monitor.work_area();
    let width = 520;
    let height = work.size.height.saturating_sub(48).min(860);
    let x = work.position.x + work.size.width as i32 - width as i32 - 24;
    let y = work.position.y + 24;

    window
        .set_position(PhysicalPosition::new(x, y))
        .map_err(|error| error.to_string())?;
    window
        .set_size(PhysicalSize::new(width, height))
        .map_err(|error| error.to_string())?;
    Ok(false)
}

fn get_target_window_bounds() -> Option<Bounds> {
    let script = format!(
        r#"
tell application "System Events"
  if not (exists process "{TARGET_PROCESS_NAME}") then
    return ""
  end if
  tell process "{TARGET_PROCESS_NAME}"
    if (count of windows) is 0 then
      return ""
    end if
    set {{xPos, yPos}} to position of front window
    set {{winWidth, winHeight}} to size of front window
    return (xPos as string) & "," & (yPos as string) & "," & (winWidth as string) & "," & (winHeight as string)
  end tell
end tell
"#
    );

    let output = Command::new("osascript")
        .arg("-e")
        .arg(script)
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }

    let stdout = String::from_utf8(output.stdout).ok()?;
    let values: Vec<i32> = stdout
        .trim()
        .split(',')
        .filter_map(|value| value.parse::<i32>().ok())
        .collect();
    if values.len() != 4 {
        return None;
    }

    Some(Bounds {
        x: values[0],
        y: values[1],
        width: values[2] as u32,
        height: values[3] as u32,
    })
}

fn main() {
    tauri::Builder::default()
        .manage(AppState {
            database: Database::load(),
            reader_cursor: Mutex::new(0),
        })
        .setup(|app| {
            if let Some(window) = app.get_webview_window("main") {
                let _ = apply_window_bounds(&window);
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![get_snapshot, sync_overlay_window])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

#[cfg(test)]
mod tests {
    use super::*;

    fn test_database() -> Database {
        Database::load()
    }

    fn poison_state() -> GameState {
        mock_states().into_iter().next().unwrap()
    }

    #[test]
    fn recommendation_engine_prioritizes_catalyst_in_poison_deck() {
        let database = test_database();
        let game_state = poison_state();
        let result = generate_recommendations(&game_state, &database);

        assert_eq!(result.card_rewards[0].card_name, "Catalyst");
        assert_eq!(result.deck_analysis.archetypes[0].key, "poison");
    }

    #[test]
    fn path_planner_avoids_elites_on_low_hp() {
        let database = test_database();
        let mut game_state = poison_state();
        game_state.player.hp = 18;
        game_state.map.upcoming_nodes = vec!["Elite".into(), "Rest".into(), "Shop".into()];
        let result = generate_recommendations(&game_state, &database);

        assert_eq!(result.path_recommendation.route[0], "Rest");
    }

    #[test]
    fn replay_parser_extracts_card_name_from_action_log() {
        let entry =
            "after executing action PlayCardAction card: CARD.BODYGUARD (61911667) index: 2";

        let action = parse_card_action(entry).expect("card action");
        assert_eq!(action.title, "Bodyguard");
    }

    #[test]
    fn replay_parser_extracts_choice_name_from_event_key() {
        let entry = "THIS_OR_THAT.pages.INITIAL.options.ORNATE.title";

        let action = parse_event_choice(entry).expect("choice action");
        assert_eq!(action.title, "Ornate");
    }

    #[test]
    fn replay_parser_extracts_phase_marker() {
        let entry = "after player turn phase two end";

        let action = parse_phase_marker(entry).expect("phase marker");
        assert_eq!(action.kind, "phase");
        assert_eq!(action.title, "Player Turn Two End");
    }

    #[test]
    fn replay_header_parser_reads_version_commit_and_model_hash() {
        let bytes = [
            7, 0, 0, 0, b'v', b'0', b'.', b'9', b'8', b'.', b'2', 8, 0, 0, 0, b'f', b'4', b'e',
            b'e', b'e', b'c', b'c', b'6', 0xc1, 0x6d, 0x79, 0x62,
        ];

        let header = parse_replay_header(&bytes).expect("header");
        assert_eq!(header.version, "v0.98.2");
        assert_eq!(header.git_commit, "f4eeecc6");
        assert_eq!(header.model_id_hash, 0x62796dc1);
    }

    #[test]
    fn replay_parser_extracts_context_title() {
        let action = parse_title_context("ARCANE_SCROLL.title").expect("context action");
        assert_eq!(action.title, "Arcane Scroll");
    }

    #[test]
    fn replay_parser_ignores_option_title_as_context() {
        assert!(parse_title_context("THIS_OR_THAT.pages.INITIAL.options.ORNATE.title").is_none());
    }

    #[test]
    fn replay_page_parser_collects_options_for_latest_event_page() {
        let strings = vec![
            "ARCANE_SCROLL.title".into(),
            "THIS_OR_THAT.pages.INITIAL.options.ORNATE.title".into(),
            "TOY_BOX.title".into(),
            "COLOSSAL_FLOWER.pages.REACH_DEEPER_1.options.REACH_DEEPER_2.title".into(),
            "COLOSSAL_FLOWER.pages.REACH_DEEPER_2.options.POLLINOUS_CORE.title".into(),
            "COLOSSAL_FLOWER.pages.INITIAL.options.REACH_DEEPER_1.title".into(),
        ];

        let page = parse_latest_replay_page(&strings).expect("replay page");
        assert_eq!(page.event_key, "COLOSSAL_FLOWER");
        assert_eq!(page.context_title, "Toy Box");
        assert_eq!(page.options.len(), 3);
    }

    #[test]
    fn replay_page_parser_keeps_options_scoped_to_event() {
        let strings = vec![
            "ARCANE_SCROLL.title".into(),
            "THIS_OR_THAT.pages.INITIAL.options.ORNATE.title".into(),
            "TOY_BOX.title".into(),
            "COLOSSAL_FLOWER.pages.INITIAL.options.REACH_DEEPER_1.title".into(),
        ];

        let page = parse_latest_replay_page(&strings).expect("replay page");
        assert_eq!(page.event_key, "COLOSSAL_FLOWER");
        assert_eq!(page.options, vec!["Reach Deeper 1"]);
    }

    #[test]
    fn replay_page_parser_marks_option_pages_as_index_choice() {
        let strings = vec![
            "ARCANE_SCROLL.title".into(),
            "THIS_OR_THAT.pages.INITIAL.options.ORNATE.title".into(),
        ];

        let page = parse_latest_replay_page(&strings).expect("replay page");
        assert_eq!(page.choice_model, "Index");
    }

    #[test]
    fn reward_outcome_parses_offered_choices_and_cards_gained() {
        let stat = RunHistoryPlayerStat {
            ancient_choice: None,
            card_choices: Some(vec![
                RunCardChoice {
                    card: Some(RunCard {
                        id: "CARD.ARMAMENTS".into(),
                    }),
                    was_picked: Some(true),
                },
                RunCardChoice {
                    card: Some(RunCard {
                        id: "CARD.BREAKTHROUGH".into(),
                    }),
                    was_picked: Some(false),
                },
            ]),
            cards_gained: Some(vec![RunCard {
                id: "CARD.ARMAMENTS".into(),
            }]),
            cards_transformed: None,
            damage_taken: Some(6),
            event_choices: None,
            gold_gained: Some(11),
            max_hp_lost: Some(0),
            relic_choices: None,
        };

        let offered = stat
            .card_choices
            .as_ref()
            .unwrap()
            .iter()
            .filter_map(|choice| choice.card.as_ref().map(|card| normalize_card_id(&card.id)))
            .collect::<Vec<_>>();
        let gained = stat
            .cards_gained
            .as_ref()
            .unwrap()
            .iter()
            .map(|card| normalize_card_id(&card.id))
            .collect::<Vec<_>>();

        assert_eq!(offered, vec!["Armaments", "Breakthrough"]);
        assert_eq!(gained, vec!["Armaments"]);
        assert_eq!(stat.gold_gained, Some(11));
    }

    #[test]
    fn normalized_choice_title_handles_relic_and_event_tokens() {
        assert_eq!(title_case_from_token("LEAFY_POULTICE"), "Leafy Poultice");
        assert_eq!(title_case_from_token("EVENT.NEOW"), "Event.neow");
    }

    #[test]
    fn current_monster_room_uses_next_encounter_from_act_pool() {
        let rooms = RunActRooms {
            ancient_id: Some("EVENT.NEOW".into()),
            boss_id: Some("ENCOUNTER.CEREMONIAL_BEAST_BOSS".into()),
            second_boss_id: None,
            boss_encounters_visited: Some(0),
            elite_encounter_ids: Some(vec!["ENCOUNTER.BYGONE_EFFIGY_ELITE".into()]),
            elite_encounters_visited: Some(0),
            normal_encounter_ids: Some(vec![
                "ENCOUNTER.SLIMES_WEAK".into(),
                "ENCOUNTER.SHRINKER_BEETLE_WEAK".into(),
            ]),
            normal_encounters_visited: Some(1),
        };

        assert_eq!(
            infer_current_encounter_name(Some(&rooms), "monster"),
            Some("Shrinker Beetle Weak".into())
        );
    }

    #[test]
    fn replay_phase_hint_promotes_event_scene_without_battle_state() {
        let mut game_state = poison_state();
        game_state.battle.enemies.clear();
        game_state.battle.encounter_name = None;
        game_state.battle.room_type = None;
        game_state.battle.turns_taken = None;
        game_state.battle.current_phase = None;
        game_state.battle.last_card_played = None;
        game_state.battle.last_action_detail = None;
        game_state.rewards.cards.clear();
        let replay = ReplaySummary {
            source: "latest.mcr".into(),
            version: "v0.98.2".into(),
            git_commit: "f4eeecc6".into(),
            model_id_hash: "62796dc1".into(),
            updated_at: "unix:1".into(),
            phase_hint: "event".into(),
            latest_page: Some(ReplayPage {
                event_key: "THIS_OR_THAT".into(),
                event_title: "This Or That".into(),
                context_title: "Arcane Scroll".into(),
                choice_model: "Index".into(),
                options: vec!["Ornate".into()],
            }),
            resolved_outcome: None,
            latest_contexts: vec!["Arcane Scroll".into()],
            latest_cards: Vec::new(),
            latest_events: vec!["Gold".into()],
            latest_choices: vec!["Ornate".into()],
            recent_actions: vec![ReplayAction {
                kind: "choice".into(),
                title: "Ornate".into(),
                detail: "Event This Or That".into(),
            }],
        };

        assert_eq!(detect_scene(&game_state, &replay), "event");
    }
}
