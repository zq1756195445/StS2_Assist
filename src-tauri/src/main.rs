#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod debug_state;

use debug_state::{
    format_refresh_source_label, print_debug_blob, push_debug_entry, DebugState, RefreshDebug,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    env, fs,
    io::{BufRead, BufReader},
    net::TcpListener,
    path::PathBuf,
    process::Command,
    sync::Arc,
    sync::Mutex,
    thread,
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use tauri::{AppHandle, Emitter, Manager, PhysicalPosition, PhysicalSize, State, WebviewWindow};

#[cfg(not(target_os = "windows"))]
const CURRENT_RUN_SAVE_PATH: &str =
    "/Users/cheemtain/Library/Application Support/SlayTheSpire2/steam/76561198818693118/profile1/saves/current_run.save";
#[cfg(not(target_os = "windows"))]
const LATEST_REPLAY_PATH: &str =
    "/Users/cheemtain/Library/Application Support/SlayTheSpire2/steam/76561198818693118/profile1/replays/latest.mcr";
const HUD_EVENT_BRIDGE_ADDR: &str = "127.0.0.1:43125";

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

#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
enum AppLocale {
    #[default]
    EnUs,
    ZhCn,
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

#[derive(Clone)]
struct TranslationTable {
    eng_to_zhs: HashMap<String, String>,
    zhs_to_eng: HashMap<String, String>,
}

impl TranslationTable {
    fn from_pairs(pairs: impl IntoIterator<Item = (String, String)>) -> Self {
        let mut eng_to_zhs = HashMap::new();
        let mut zhs_to_eng = HashMap::new();

        for (eng, zhs) in pairs {
            let eng = eng.trim().to_string();
            let zhs = zhs.trim().to_string();
            if eng.is_empty() || zhs.is_empty() {
                continue;
            }

            eng_to_zhs.insert(eng.to_ascii_lowercase(), zhs.clone());
            zhs_to_eng.insert(zhs.to_ascii_lowercase(), eng.clone());
        }

        Self {
            eng_to_zhs,
            zhs_to_eng,
        }
    }

    fn translate(&self, locale: AppLocale, value: &str) -> String {
        match locale {
            AppLocale::EnUs => self
                .zhs_to_eng
                .get(&value.to_ascii_lowercase())
                .cloned()
                .unwrap_or_else(|| value.to_string()),
            AppLocale::ZhCn => self
                .eng_to_zhs
                .get(&value.to_ascii_lowercase())
                .cloned()
                .unwrap_or_else(|| value.to_string()),
        }
    }
}

#[derive(Clone)]
struct LocalizationDb {
    cards: TranslationTable,
    relics: TranslationTable,
    monsters: TranslationTable,
    intents: TranslationTable,
    general: TranslationTable,
}

impl LocalizationDb {
    fn load() -> Self {
        Self {
            cards: TranslationTable::from_pairs(collect_title_pairs(&[(
                include_str!("../../database/sts2-localization/localization/eng/cards.json"),
                include_str!("../../database/sts2-localization/localization/zhs/cards.json"),
            )])),
            relics: TranslationTable::from_pairs(collect_title_pairs(&[(
                include_str!("../../database/sts2-localization/localization/eng/relics.json"),
                include_str!("../../database/sts2-localization/localization/zhs/relics.json"),
            )])),
            monsters: TranslationTable::from_pairs(collect_name_pairs(&[(
                include_str!("../../database/sts2-localization/localization/eng/monsters.json"),
                include_str!("../../database/sts2-localization/localization/zhs/monsters.json"),
            )])),
            intents: TranslationTable::from_pairs(collect_title_pairs(&[
                (
                    include_str!("../../database/sts2-localization/localization/eng/intents.json"),
                    include_str!("../../database/sts2-localization/localization/zhs/intents.json"),
                ),
                (
                    include_str!("../../database/sts2-localization/localization/eng/monsters.json"),
                    include_str!("../../database/sts2-localization/localization/zhs/monsters.json"),
                ),
            ])),
            general: TranslationTable::from_pairs(
                collect_general_pairs()
                    .into_iter()
                    .chain(collect_title_pairs(&[
                        (
                            include_str!(
                                "../../database/sts2-localization/localization/eng/events.json"
                            ),
                            include_str!(
                                "../../database/sts2-localization/localization/zhs/events.json"
                            ),
                        ),
                        (
                            include_str!(
                                "../../database/sts2-localization/localization/eng/ancients.json"
                            ),
                            include_str!(
                                "../../database/sts2-localization/localization/zhs/ancients.json"
                            ),
                        ),
                    ]))
                    .chain(collect_name_pairs(&[(
                        include_str!(
                            "../../database/sts2-localization/localization/eng/characters.json"
                        ),
                        include_str!(
                            "../../database/sts2-localization/localization/zhs/characters.json"
                        ),
                    )])),
            ),
        }
    }

    fn translate_card(&self, locale: AppLocale, value: &str) -> String {
        self.cards.translate(locale, value)
    }

    fn translate_relic(&self, locale: AppLocale, value: &str) -> String {
        self.relics.translate(locale, value)
    }

    fn translate_monster(&self, locale: AppLocale, value: &str) -> String {
        self.monsters.translate(locale, value)
    }

    fn translate_intent(&self, locale: AppLocale, value: &str) -> String {
        self.intents.translate(locale, value)
    }

    fn translate_general(&self, locale: AppLocale, value: &str) -> String {
        self.general.translate(locale, value)
    }
}

struct AppState {
    database: Database,
    localization: LocalizationDb,
    locale: Mutex<AppLocale>,
    cached_memory: Arc<Mutex<Option<MemorySnapshot>>>,
    cached_game_state: Arc<Mutex<Option<GameState>>>,
    overlay_enabled: Arc<Mutex<bool>>,
    overlay_interactive: Arc<Mutex<bool>>,
    debug_state: Arc<Mutex<DebugState>>,
}

#[derive(Clone, Default)]
struct MemorySnapshot {
    hand: Vec<String>,
    enemies: Vec<EnemyState>,
    player: Option<MemoryPlayerState>,
    reward_cards: Vec<String>,
    scene_hint: Option<String>,
    status: Option<String>,
}

#[derive(Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MemoryPlayerState {
    hp: Option<i32>,
    max_hp: Option<i32>,
    energy: Option<i32>,
}

#[derive(Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HudEventEnvelope {
    kind: String,
    source: Option<String>,
    trigger: Option<HudTrigger>,
}

#[derive(Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HudTrigger {
    type_name: Option<String>,
    method_name: Option<String>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MemoryReaderConfig {
    process_names: Option<Vec<String>>,
    hand: Option<MemoryBlobConfig>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MemoryBlobConfig {
    module_name: Option<String>,
    base_offset: usize,
    pointer_offsets: Option<Vec<usize>>,
    read_len: usize,
    encoding: Option<String>,
    separator: Option<String>,
    max_cards: Option<usize>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    locale: AppLocale,
    game_state: GameState,
    recommendations: Recommendations,
    overlay: OverlayLayout,
    replay: ReplaySummary,
    source: String,
    debug: DebugState,
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
    memory_status: Option<String>,
    enemies: Vec<EnemyState>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct EnemyState {
    name: String,
    hp: i32,
    block: Option<i32>,
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

#[allow(dead_code)]
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

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunRoomRef {
    event_id: Option<String>,
    room_type: Option<String>,
}

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunAct {
    rooms: Option<RunActRooms>,
    saved_map: Option<SavedMap>,
}

#[allow(dead_code)]
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

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunHistoryPoint {
    map_point_type: Option<String>,
    player_stats: Option<Vec<RunHistoryPlayerStat>>,
    rooms: Option<Vec<RunHistoryRoom>>,
}

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunHistoryRoom {
    model_id: Option<String>,
    #[allow(dead_code)]
    monster_ids: Option<Vec<String>>,
    room_type: Option<String>,
    turns_taken: Option<i32>,
}

#[allow(dead_code)]
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

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunAncientChoice {
    #[serde(rename = "TextKey")]
    text_key: Option<String>,
    was_chosen: Option<bool>,
}

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunCardChoice {
    card: Option<RunCard>,
    was_picked: Option<bool>,
}

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunCardTransform {
    final_card: Option<RunCard>,
    original_card: Option<RunCard>,
}

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunEventChoice {
    title: Option<RunTextRef>,
}

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
struct RunRelicChoice {
    choice: Option<String>,
    was_picked: Option<bool>,
}

#[allow(dead_code)]
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

#[tauri::command]
fn get_snapshot(state: State<AppState>) -> Result<Snapshot, String> {
    snapshot_from_state(&state)
}

fn snapshot_from_state(state: &AppState) -> Result<Snapshot, String> {
    let locale = *state.locale.lock().map_err(|_| "locale lock poisoned")?;
    let cached_memory = state
        .cached_memory
        .lock()
        .map_err(|_| "memory cache lock poisoned")?
        .clone();
    let cached_game_state = state
        .cached_game_state
        .lock()
        .map_err(|_| "game state cache lock poisoned")?
        .clone();
    let debug = state
        .debug_state
        .lock()
        .map_err(|_| "debug state lock poisoned")?
        .clone();
    let replay = empty_replay_summary();
    let game_state =
        cached_game_state.unwrap_or_else(|| empty_live_game_state(cached_memory.as_ref()));
    let recommendations = generate_recommendations(&game_state, &state.database, locale);
    let recommendations = localize_recommendations(&recommendations, locale, &state.localization);
    let overlay = build_overlay_layout_v2(
        &game_state,
        &recommendations,
        &replay,
        cached_memory.as_ref().and_then(|memory| memory.scene_hint.as_deref()),
        locale,
    );
    let game_state = localize_game_state(&game_state, locale, &state.localization);
    let replay = localize_replay_summary(&replay, locale, &state.localization);
    let source = localize_source(&game_state.source, locale, &state.localization);

    Ok(Snapshot {
        locale,
        game_state,
        recommendations,
        overlay,
        replay,
        source,
        debug,
    })
}

fn emit_snapshot_update(app: &AppHandle) {
    let Ok(snapshot) = snapshot_from_state(app.state::<AppState>().inner()) else {
        return;
    };

    if let Some(window) = app.get_webview_window("main") {
        let _ = window.emit("snapshot-updated", &snapshot);
    }
}

fn emit_locale_update(app: &AppHandle, locale: AppLocale) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.emit("locale-changed", &locale);
    }
}

#[tauri::command]
fn sync_overlay_window(window: WebviewWindow, state: State<AppState>) -> Result<WindowMode, String> {
    let enabled = *state
        .overlay_enabled
        .lock()
        .map_err(|_| "overlay enabled lock poisoned")?;
    let interactive = *state
        .overlay_interactive
        .lock()
        .map_err(|_| "overlay interactive lock poisoned")?;
    let attached = sync_overlay_window_state(&window, enabled, interactive)?;
    Ok(WindowMode {
        attached_to_game: attached,
    })
}

#[tauri::command]
fn set_locale(locale: AppLocale, state: State<AppState>) -> Result<(), String> {
    let mut current = state.locale.lock().map_err(|_| "locale lock poisoned")?;
    *current = locale;
    Ok(())
}

#[tauri::command]
fn set_overlay_interactive(
    interactive: bool,
    window: WebviewWindow,
    state: State<AppState>,
) -> Result<(), String> {
    {
        let mut current = state
            .overlay_interactive
            .lock()
            .map_err(|_| "overlay interactive lock poisoned")?;
        *current = interactive;
    }

    let enabled = *state
        .overlay_enabled
        .lock()
        .map_err(|_| "overlay enabled lock poisoned")?;
    let _ = sync_overlay_window_state(&window, enabled, interactive);
    Ok(())
}

fn read_live_game_state(cached_memory: Option<&MemorySnapshot>) -> Option<GameState> {
    let path = resolve_current_run_save_path()?;
    let raw = fs::read_to_string(path).ok()?;
    let save: RunSave = parse_run_save(&raw).ok()?;
    let player = save.players.first()?;
    let act = save.acts.get(save.current_act_index)?;
    let memory = cached_memory.cloned();
    let map_points = act.saved_map.as_ref().map(|map| map.points.as_slice());
    let current_coord = save.visited_map_coords.last().cloned();
    let current_node = current_coord
        .as_ref()
        .and_then(|coord| map_points.and_then(|points| find_point_by_coord(points, coord)))
        .map(|point| point.point_type.clone())
        .unwrap_or_else(|| "map".into());

    let upcoming_nodes = current_coord
        .as_ref()
        .and_then(|coord| map_points.and_then(|points| find_point_by_coord(points, coord)))
        .and_then(|point| point.children.as_ref())
        .map(|children| {
            children
                .iter()
                .filter_map(|child| {
                    map_points.and_then(|points| find_point_by_coord(points, child))
                })
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
            hp: memory
                .as_ref()
                .and_then(|snapshot| snapshot.player.as_ref())
                .and_then(|player| player.hp)
                .unwrap_or(player.current_hp),
            max_hp: memory
                .as_ref()
                .and_then(|snapshot| snapshot.player.as_ref())
                .and_then(|player| player.max_hp)
                .unwrap_or(player.max_hp),
            gold: player.gold,
            energy: memory
                .as_ref()
                .and_then(|snapshot| snapshot.player.as_ref())
                .and_then(|player| player.energy)
                .unwrap_or(player.max_energy),
            potions: Vec::new(),
        },
        deck: player
            .deck
            .iter()
            .map(|card| normalize_card_id(&card.id))
            .collect(),
        hand: memory
            .as_ref()
            .map(|snapshot| snapshot.hand.clone())
            .unwrap_or_default(),
        discard_pile: Vec::new(),
        draw_pile: Vec::new(),
        relics: player
            .relics
            .iter()
            .map(|relic| normalize_relic_id(&relic.id))
            .collect(),
        battle: infer_live_battle_state(&current_node, memory.as_ref()),
        map: MapState {
            act: (save.current_act_index + 1) as i32,
            current_node: normalize_node_type(&current_node),
            upcoming_nodes,
        },
        rewards: RewardState {
            cards: memory
                .as_ref()
                .map(|snapshot| snapshot.reward_cards.clone())
                .unwrap_or_default(),
        },
        source: compose_live_source(memory.as_ref()),
    })
}

fn infer_live_battle_state(
    current_node_type: &str,
    memory: Option<&MemorySnapshot>,
) -> BattleState {
    let enemies = memory
        .map(|snapshot| snapshot.enemies.clone())
        .unwrap_or_default();
    let encounter_name = enemies.first().map(|enemy| enemy.name.clone());
    let room_type = if enemies.is_empty() {
        None
    } else {
        Some(normalize_node_type(current_node_type))
    };

    BattleState {
        encounter_name,
        room_type,
        turns_taken: None,
        current_phase: None,
        last_card_played: None,
        last_action_detail: None,
        memory_status: memory.and_then(|snapshot| snapshot.status.clone()),
        enemies,
    }
}

fn empty_live_game_state(memory: Option<&MemorySnapshot>) -> GameState {
    GameState {
        timestamp: now_timestamp_string(),
        character: "Unknown".into(),
        player: PlayerState {
            hp: 0,
            max_hp: 0,
            gold: 0,
            energy: 0,
            potions: Vec::new(),
        },
        deck: Vec::new(),
        hand: memory
            .map(|snapshot| snapshot.hand.clone())
            .unwrap_or_default(),
        discard_pile: Vec::new(),
        draw_pile: Vec::new(),
        relics: Vec::new(),
        battle: infer_live_battle_state("map", memory),
        map: MapState {
            act: 0,
            current_node: "Map".into(),
            upcoming_nodes: Vec::new(),
        },
        rewards: RewardState { cards: Vec::new() },
        source: memory
            .and_then(|snapshot| snapshot.status.clone())
            .unwrap_or_else(|| "memory(cache)".into()),
    }
}

fn parse_run_save(raw: &str) -> Result<RunSave, serde_json::Error> {
    serde_json::from_str(raw)
}

fn compose_live_source(memory: Option<&MemorySnapshot>) -> String {
    let mut parts = vec!["current_run.save".to_string()];
    if let Some(status) = memory.and_then(|snapshot| snapshot.status.clone()) {
        parts.push(status);
    }
    parts.join(" + ")
}

fn localize_game_state(
    game_state: &GameState,
    locale: AppLocale,
    localization: &LocalizationDb,
) -> GameState {
    let mut localized = game_state.clone();
    localized.character = localization.translate_general(locale, &localized.character);
    localized.deck = localized
        .deck
        .iter()
        .map(|value| localization.translate_card(locale, value))
        .collect();
    localized.hand = localized
        .hand
        .iter()
        .map(|value| localization.translate_card(locale, value))
        .collect();
    localized.discard_pile = localized
        .discard_pile
        .iter()
        .map(|value| localization.translate_card(locale, value))
        .collect();
    localized.draw_pile = localized
        .draw_pile
        .iter()
        .map(|value| localization.translate_card(locale, value))
        .collect();
    localized.relics = localized
        .relics
        .iter()
        .map(|value| localization.translate_relic(locale, value))
        .collect();
    localized.player.potions = localized
        .player
        .potions
        .iter()
        .map(|value| localization.translate_general(locale, value))
        .collect();
    localized.battle.encounter_name = localized
        .battle
        .encounter_name
        .as_ref()
        .map(|value| localization.translate_monster(locale, value));
    localized.battle.room_type = localized
        .battle
        .room_type
        .as_ref()
        .map(|value| localization.translate_general(locale, value));
    localized.battle.current_phase = localized
        .battle
        .current_phase
        .as_ref()
        .map(|value| localization.translate_general(locale, value));
    localized.battle.last_card_played = localized
        .battle
        .last_card_played
        .as_ref()
        .map(|value| localization.translate_card(locale, value));
    localized.battle.last_action_detail = localized
        .battle
        .last_action_detail
        .as_ref()
        .map(|value| localization.translate_general(locale, value));
    localized.battle.memory_status = localized
        .battle
        .memory_status
        .as_ref()
        .map(|value| localization.translate_general(locale, value));
    localized.battle.enemies = localized
        .battle
        .enemies
        .iter()
        .map(|enemy| EnemyState {
            name: localization.translate_monster(locale, &enemy.name),
            hp: enemy.hp,
            block: enemy.block,
            intent: localize_intent_line(&enemy.intent, locale, localization),
        })
        .collect();
    localized.map.current_node =
        localization.translate_general(locale, &localized.map.current_node);
    localized.map.upcoming_nodes = localized
        .map
        .upcoming_nodes
        .iter()
        .map(|value| localization.translate_general(locale, value))
        .collect();
    localized.rewards.cards = localized
        .rewards
        .cards
        .iter()
        .map(|value| localization.translate_card(locale, value))
        .collect();
    localized.source = localize_source(&localized.source, locale, localization);
    localized
}

fn localize_replay_summary(
    replay: &ReplaySummary,
    locale: AppLocale,
    localization: &LocalizationDb,
) -> ReplaySummary {
    let mut localized = replay.clone();
    localized.source = localize_source(&localized.source, locale, localization);
    localized.phase_hint = localization.translate_general(locale, &localized.phase_hint);
    localized.latest_contexts = localized
        .latest_contexts
        .iter()
        .map(|value| localization.translate_general(locale, value))
        .collect();
    localized.latest_cards = localized
        .latest_cards
        .iter()
        .map(|value| localization.translate_card(locale, value))
        .collect();
    localized.latest_events = localized
        .latest_events
        .iter()
        .map(|value| localization.translate_general(locale, value))
        .collect();
    localized.latest_choices = localized
        .latest_choices
        .iter()
        .map(|value| localization.translate_general(locale, value))
        .collect();
    localized.recent_actions = localized
        .recent_actions
        .iter()
        .map(|action| ReplayAction {
            kind: action.kind.clone(),
            title: localize_general_title(&action.title, locale, localization),
            detail: localization.translate_general(locale, &action.detail),
        })
        .collect();
    localized.latest_page = localized.latest_page.as_ref().map(|page| ReplayPage {
        event_key: page.event_key.clone(),
        event_title: localization.translate_general(locale, &page.event_title),
        context_title: localization.translate_general(locale, &page.context_title),
        choice_model: localization.translate_general(locale, &page.choice_model),
        options: page
            .options
            .iter()
            .map(|value| localization.translate_general(locale, value))
            .collect(),
    });
    localized.resolved_outcome =
        localized
            .resolved_outcome
            .as_ref()
            .map(|outcome| ResolvedOutcome {
                room_type: localization.translate_general(locale, &outcome.room_type),
                event_id: outcome.event_id.clone(),
                chosen_title: localization.translate_general(locale, &outcome.chosen_title),
                offered_choices: outcome
                    .offered_choices
                    .iter()
                    .map(|value| localization.translate_general(locale, value))
                    .collect(),
                cards_gained: outcome
                    .cards_gained
                    .iter()
                    .map(|value| localization.translate_card(locale, value))
                    .collect(),
                gold_gained: outcome.gold_gained,
                max_hp_lost: outcome.max_hp_lost,
                damage_taken: outcome.damage_taken,
                transformed_cards: outcome
                    .transformed_cards
                    .iter()
                    .map(|value| localize_transform_line(value, locale, localization))
                    .collect(),
            });
    localized
}

fn localize_intent_line(value: &str, locale: AppLocale, localization: &LocalizationDb) -> String {
    let translated = localization.translate_intent(locale, value);
    if translated != value {
        return translated;
    }

    let value = localization.translate_general(locale, value);
    match locale {
        AppLocale::EnUs => value,
        AppLocale::ZhCn => value
            .replace("Attack", "攻击")
            .replace("Block", "格挡")
            .replace("Debuff", "减益")
            .replace("Buff", "增益")
            .replace("Unknown", "未知"),
    }
}

fn localize_transform_line(
    value: &str,
    locale: AppLocale,
    localization: &LocalizationDb,
) -> String {
    let parts = value.split(" -> ").collect::<Vec<_>>();
    if parts.len() != 2 {
        return localize_general_title(value, locale, localization);
    }

    format!(
        "{} -> {}",
        localization.translate_card(locale, parts[0]),
        localization.translate_card(locale, parts[1])
    )
}

fn localize_general_title(value: &str, locale: AppLocale, localization: &LocalizationDb) -> String {
    let card = localization.translate_card(locale, value);
    if card != value {
        return card;
    }
    let relic = localization.translate_relic(locale, value);
    if relic != value {
        return relic;
    }
    let monster = localization.translate_monster(locale, value);
    if monster != value {
        return monster;
    }
    localization.translate_general(locale, value)
}

fn localize_source(value: &str, locale: AppLocale, localization: &LocalizationDb) -> String {
    value
        .split(" + ")
        .map(|part| localization.translate_general(locale, part))
        .collect::<Vec<_>>()
        .join(" + ")
}

fn localize_recommendations(
    recommendations: &Recommendations,
    locale: AppLocale,
    localization: &LocalizationDb,
) -> Recommendations {
    let mut localized = recommendations.clone();
    localized.deck_analysis.archetypes = localized
        .deck_analysis
        .archetypes
        .iter()
        .map(|entry| ArchetypeScore {
            key: entry.key.clone(),
            label: localize_archetype_key(&entry.key, locale),
            score: entry.score,
        })
        .collect();
    localized.card_rewards = localized
        .card_rewards
        .iter()
        .map(|entry| CardRecommendation {
            card_name: localization.translate_card(locale, &entry.card_name),
            score: entry.score,
            reason: entry.reason.clone(),
        })
        .collect();
    localized.path_recommendation.route = localized
        .path_recommendation
        .route
        .iter()
        .map(|node| localization.translate_general(locale, node))
        .collect();
    localized.relic_suggestions = localized
        .relic_suggestions
        .iter()
        .map(|entry| RelicSuggestion {
            relic_name: localization.translate_relic(locale, &entry.relic_name),
            suggestion: localize_relic_suggestion(&entry.suggestion, locale),
        })
        .collect();
    localized.turn_suggestion = localized
        .turn_suggestion
        .iter()
        .map(|entry| localization.translate_card(locale, entry))
        .collect();
    localized.archetype_browser = localized
        .archetype_browser
        .iter()
        .map(|entry| localize_archetype_key(entry, locale))
        .collect();
    localized
}

fn localize_relic_suggestion(value: &str, locale: AppLocale) -> String {
    match (locale, value) {
        (AppLocale::ZhCn, "Increase attack card density to trigger Dexterity consistently.") => {
            "提高攻击牌密度，稳定触发敏捷增益。".into()
        }
        (AppLocale::ZhCn, "Lean into setup turns and stronger opening hands.") => {
            "更偏向启动回合和更强的开局手牌。".into()
        }
        (
            AppLocale::ZhCn,
            "You can take slightly greedier elite paths with better baseline defense.",
        ) => "基础防御更稳后，可以略微贪一些精英路线。".into(),
        _ => value.to_string(),
    }
}

fn resolve_current_run_save_path() -> Option<PathBuf> {
    resolve_sts2_file_path("STS2_CURRENT_RUN_SAVE", default_current_run_save_path())
}

#[allow(dead_code)]
fn resolve_latest_replay_path() -> Option<PathBuf> {
    resolve_sts2_file_path("STS2_LATEST_REPLAY", default_latest_replay_path())
}

fn resolve_sts2_file_path(env_key: &str, fallback: Option<PathBuf>) -> Option<PathBuf> {
    env::var_os(env_key)
        .map(PathBuf::from)
        .filter(|path| path.exists())
        .or(fallback)
}

#[cfg(target_os = "windows")]
fn default_current_run_save_path() -> Option<PathBuf> {
    find_windows_profile_file(&["saves", "current_run.save"])
}

#[cfg(target_os = "windows")]
#[allow(dead_code)]
fn default_latest_replay_path() -> Option<PathBuf> {
    find_windows_profile_file(&["replays", "latest.mcr"])
}

#[cfg(target_os = "windows")]
fn find_windows_profile_file(relative: &[&str]) -> Option<PathBuf> {
    let base = env::var_os("APPDATA")
        .map(PathBuf::from)
        .or_else(|| {
            env::var_os("USERPROFILE").map(|root| {
                let mut path = PathBuf::from(root);
                path.push("AppData");
                path.push("Roaming");
                path
            })
        })?
        .join("SlayTheSpire2")
        .join("steam");

    for steam_dir in fs::read_dir(base).ok()? {
        let steam_dir = steam_dir.ok()?.path();
        if !steam_dir.is_dir() {
            continue;
        }
        for profile_dir in fs::read_dir(&steam_dir).ok()? {
            let profile_dir = profile_dir.ok()?.path();
            let Some(name) = profile_dir.file_name().and_then(|value| value.to_str()) else {
                continue;
            };
            if !profile_dir.is_dir() || !name.starts_with("profile") {
                continue;
            }

            let candidate = relative
                .iter()
                .fold(profile_dir.clone(), |mut path, segment| {
                    path.push(segment);
                    path
                });
            if candidate.exists() {
                return Some(candidate);
            }
        }
    }

    None
}

#[cfg(not(target_os = "windows"))]
fn default_current_run_save_path() -> Option<PathBuf> {
    let path = PathBuf::from(CURRENT_RUN_SAVE_PATH);
    path.exists().then_some(path)
}

#[cfg(not(target_os = "windows"))]
fn default_latest_replay_path() -> Option<PathBuf> {
    let path = PathBuf::from(LATEST_REPLAY_PATH);
    path.exists().then_some(path)
}

fn read_memory_snapshot() -> (Option<MemorySnapshot>, RefreshDebug) {
    #[cfg(target_os = "windows")]
    {
        let Some(config) = MemoryReaderConfig::load() else {
            return (
                None,
                RefreshDebug {
                    probe_summary: Some("memory-reader config missing".into()),
                    ..RefreshDebug::default()
                },
            );
        };
        return windows_memory::read_memory_snapshot(&config);
    }

    #[cfg(not(target_os = "windows"))]
    {
        (
            None,
            RefreshDebug {
                probe_summary: Some("memory reader unsupported on this platform".into()),
                ..RefreshDebug::default()
            },
        )
    }
}

fn refresh_live_state_into(
    app: Option<&AppHandle>,
    memory_cache: &Arc<Mutex<Option<MemorySnapshot>>>,
    game_state_cache: &Arc<Mutex<Option<GameState>>>,
    debug_state: &Arc<Mutex<DebugState>>,
    source_override: Option<&str>,
) {
    push_debug_entry(
        debug_state,
        "refresh",
        format!("start {}", format_refresh_source_label(source_override)),
    );

    let (memory_raw, mut refresh_debug) = read_memory_snapshot();
    let memory = memory_raw.map(|mut snapshot| {
        if let Some(source) = source_override {
            snapshot.status = Some(source.to_string());
        }
        snapshot
    });

    if let Ok(mut current) = memory_cache.lock() {
        *current = memory.clone();
    }

    let next_game_state = read_live_game_state(memory.as_ref())
        .or_else(|| Some(empty_live_game_state(memory.as_ref())));

    if let Ok(mut current) = game_state_cache.lock() {
        *current = next_game_state.clone();
    }

    if let Ok(mut debug) = debug_state.lock() {
        debug.last_refresh_source = Some(source_override.unwrap_or("startup").to_string());
        debug.last_memory_summary = Some(format!(
            "hand={} enemies={} player={} status={}",
            memory.as_ref().map(|m| m.hand.len()).unwrap_or(0),
            memory.as_ref().map(|m| m.enemies.len()).unwrap_or(0),
            memory
                .as_ref()
                .and_then(|m| m.player.as_ref())
                .map(|_| "yes")
                .unwrap_or("no"),
            memory
                .as_ref()
                .and_then(|m| m.status.clone())
                .unwrap_or_else(|| "none".into())
        ));
        debug.last_game_state_summary = Some(format!(
            "hand={} enemies={} rewards={} node={} source={}",
            next_game_state.as_ref().map(|s| s.hand.len()).unwrap_or(0),
            next_game_state
                .as_ref()
                .map(|s| s.battle.enemies.len())
                .unwrap_or(0),
            next_game_state
                .as_ref()
                .map(|s| s.rewards.cards.len())
                .unwrap_or(0),
            next_game_state
                .as_ref()
                .map(|s| s.map.current_node.clone())
                .unwrap_or_else(|| "none".into()),
            next_game_state
                .as_ref()
                .map(|s| s.source.clone())
                .unwrap_or_else(|| "none".into())
        ));
        debug.last_merge_summary = refresh_debug.merge_summary.clone();
        debug.last_probe_summary = refresh_debug.probe_summary.clone();
        debug.last_probe_stdout = refresh_debug.probe_stdout.clone();
        debug.last_probe_stderr = refresh_debug.probe_stderr.clone();
    }

    if let Some(summary) = refresh_debug.probe_summary.take() {
        push_debug_entry(debug_state, "probe", summary);
    }
    print_debug_blob("probe", "stdout", &refresh_debug.probe_stdout);
    print_debug_blob("probe", "stderr", &refresh_debug.probe_stderr);
    if let Some(summary) = refresh_debug.merge_summary.take() {
        push_debug_entry(debug_state, "merge", summary);
    }

    if let Some(app) = app {
        emit_snapshot_update(app);
    }
}

impl MemoryReaderConfig {
    fn load() -> Option<Self> {
        let candidates = [
            env::var_os("STS2_MEMORY_READER_CONFIG").map(PathBuf::from),
            Some(PathBuf::from("memory-reader.json")),
            Some(PathBuf::from("src-tauri").join("memory-reader.json")),
        ];

        for path in candidates.into_iter().flatten() {
            if !path.exists() {
                continue;
            }
            let raw = fs::read_to_string(path).ok()?;
            return serde_json::from_str(&raw).ok();
        }

        None
    }

    fn process_names(&self) -> Vec<String> {
        self.process_names.clone().unwrap_or_else(|| {
            vec![
                "SlayTheSpire2.exe".into(),
                "Slay the Spire 2.exe".into(),
                "Slay the Spire 2".into(),
            ]
        })
    }
}

fn parse_hand_blob(config: &MemoryBlobConfig, bytes: &[u8]) -> Vec<String> {
    let encoding = config.encoding.as_deref().unwrap_or("utf8");
    let separator = config.separator.as_deref();
    let max_cards = config.max_cards.unwrap_or(12);
    let decoded = if encoding.eq_ignore_ascii_case("utf16") {
        let words = bytes
            .chunks_exact(2)
            .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
            .collect::<Vec<_>>();
        String::from_utf16_lossy(&words)
    } else {
        String::from_utf8_lossy(bytes).into_owned()
    };

    split_hand_entries(&decoded, separator)
        .into_iter()
        .map(|entry| normalize_card_id(&entry))
        .filter(|entry| !entry.is_empty())
        .take(max_cards)
        .collect()
}

fn split_hand_entries(raw: &str, separator: Option<&str>) -> Vec<String> {
    let mut entries = Vec::new();
    if let Some(separator) = separator {
        for chunk in raw.split(separator) {
            push_hand_entry(&mut entries, chunk);
        }
        return entries;
    }

    for chunk in raw.split(['|', '\n', '\r', '\0']) {
        push_hand_entry(&mut entries, chunk);
    }
    entries
}

fn push_hand_entry(entries: &mut Vec<String>, raw: &str) {
    let trimmed = raw.trim_matches(char::from(0)).trim();
    if trimmed.is_empty() {
        return;
    }

    let candidate = trimmed
        .split_whitespace()
        .find(|part| {
            part.contains("CARD.")
                || part
                    .chars()
                    .all(|ch| ch.is_ascii_alphanumeric() || ch == '_' || ch == '.')
        })
        .unwrap_or(trimmed)
        .trim_matches(|ch: char| ch == '"' || ch == '\'' || ch == '[' || ch == ']');

    if candidate.is_empty() {
        return;
    }

    entries.push(candidate.to_string());
}

#[allow(dead_code)]
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

#[allow(dead_code)]
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

fn collect_title_pairs(resources: &[(&str, &str)]) -> Vec<(String, String)> {
    collect_key_suffix_pairs(resources, ".title")
}

fn collect_name_pairs(resources: &[(&str, &str)]) -> Vec<(String, String)> {
    collect_key_suffix_pairs(resources, ".name")
}

fn collect_key_suffix_pairs(resources: &[(&str, &str)], suffix: &str) -> Vec<(String, String)> {
    let mut pairs = Vec::new();

    for (eng_raw, zhs_raw) in resources {
        let eng: HashMap<String, String> = serde_json::from_str(eng_raw).expect("eng locale json");
        let zhs: HashMap<String, String> = serde_json::from_str(zhs_raw).expect("zhs locale json");

        for (key, eng_value) in eng {
            if !key.ends_with(suffix) {
                continue;
            }
            if let Some(zhs_value) = zhs.get(&key) {
                pairs.push((eng_value, zhs_value.clone()));
            }
        }
    }

    pairs
}

fn collect_general_pairs() -> Vec<(String, String)> {
    vec![
        ("Battle".into(), "战斗".into()),
        ("Elite".into(), "精英".into()),
        ("Rest".into(), "休息".into()),
        ("Shop".into(), "商店".into()),
        ("Treasure".into(), "宝箱".into()),
        ("Unknown".into(), "未知".into()),
        ("Boss".into(), "Boss".into()),
        ("Start".into(), "起点".into()),
        ("Player Turn".into(), "玩家回合".into()),
        ("Tracking replay".into(), "追踪回放".into()),
        ("No active battle".into(), "当前没有战斗".into()),
        ("No target".into(), "无目标".into()),
        ("mock".into(), "模拟".into()),
        ("current_run.save".into(), "当前存档".into()),
        ("memory(attached)".into(), "内存已连接".into()),
        ("memory(hand)".into(), "已读取手牌".into()),
        ("latest.mcr".into(), "latest.mcr".into()),
        ("Gold".into(), "金币".into()),
        ("Heal".into(), "治疗".into()),
        ("Phase".into(), "阶段".into()),
        ("Context".into(), "上下文".into()),
        ("Choice Model".into(), "选择模型".into()),
    ]
}

fn localized_fit_reason(locale: AppLocale, archetype: &str) -> String {
    match locale {
        AppLocale::EnUs => format!("fits {} plan", archetype),
        AppLocale::ZhCn => format!("契合{}体系", localize_archetype_key(archetype, locale)),
    }
}

fn localized_scaling_reason(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "improves long fights".into(),
        AppLocale::ZhCn => "提升长线战斗能力".into(),
    }
}

fn localized_low_hp_reason(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "stabilizes low HP run".into(),
        AppLocale::ZhCn => "能稳住低血量局面".into(),
    }
}

fn localized_unknown_card_reason(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "Unknown card in local database.".into(),
        AppLocale::ZhCn => "本地数据库里还没有这张牌。".into(),
    }
}

fn localized_path_reason_low_hp(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "Low HP: recover and spend before taking higher variance fights.".into(),
        AppLocale::ZhCn => "当前血量偏低，先恢复和消费，再考虑高波动战斗。".into(),
    }
}

fn localized_path_reason_strong(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => {
            "Deck looks strong enough to convert elite fights into scaling rewards.".into()
        }
        AppLocale::ZhCn => "当前牌组强度足够，可以把精英战转化成成长收益。".into(),
    }
}

fn localized_path_reason_mid(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => {
            "Moderate deck strength: stabilize first, then take risk if rewards justify it.".into()
        }
        AppLocale::ZhCn => "当前牌组强度中等，先稳住，再在收益足够时承担风险。".into(),
    }
}

fn localized_battle_title(locale: AppLocale, name: &str) -> String {
    match locale {
        AppLocale::EnUs => format!("{name} Intent"),
        AppLocale::ZhCn => format!("{name} 意图"),
    }
}

fn localized_encounter_title(locale: AppLocale, name: &str) -> String {
    match locale {
        AppLocale::EnUs => format!("{name} Status"),
        AppLocale::ZhCn => format!("{name} 战况"),
    }
}

fn localized_battle_info_title(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "Battle Info".into(),
        AppLocale::ZhCn => "战斗信息".into(),
    }
}

fn localized_current_hand(locale: AppLocale) -> &'static str {
    match locale {
        AppLocale::EnUs => "current hand",
        AppLocale::ZhCn => "当前手牌",
    }
}

fn localized_waiting_more_actions(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "Waiting for more battle actions".into(),
        AppLocale::ZhCn => "等待更多战斗动作".into(),
    }
}

fn localized_battle_body(locale: AppLocale, intent: &str, suggestion: &str) -> String {
    match locale {
        AppLocale::EnUs => format!("{intent}, prioritize {suggestion}."),
        AppLocale::ZhCn => format!("{intent}，建议优先看 {suggestion}。"),
    }
}

fn localized_encounter_phase(locale: AppLocale, encounter: &str, phase: &str) -> String {
    match locale {
        AppLocale::EnUs => format!("{encounter}, {phase}."),
        AppLocale::ZhCn => format!("{encounter}，{phase}。"),
    }
}

fn localized_no_battle_target(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "No battle target yet.".into(),
        AppLocale::ZhCn => "暂时没有战斗目标。".into(),
    }
}

fn localized_play_order_title(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "Play Order".into(),
        AppLocale::ZhCn => "出牌顺序".into(),
    }
}

fn localized_card_reward_title(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "Card Reward".into(),
        AppLocale::ZhCn => "选牌建议".into(),
    }
}

fn localized_no_reward(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "No reward yet".into(),
        AppLocale::ZhCn => "暂时没有奖励".into(),
    }
}

fn localized_route_title(locale: AppLocale) -> String {
    match locale {
        AppLocale::EnUs => "Route".into(),
        AppLocale::ZhCn => "路线建议".into(),
    }
}

fn localize_archetype_key(value: &str, locale: AppLocale) -> String {
    match (locale, value) {
        (AppLocale::ZhCn, "poison") => "中毒".into(),
        (AppLocale::ZhCn, "shiv") => "小刀".into(),
        (AppLocale::ZhCn, "block") => "格挡".into(),
        (AppLocale::ZhCn, "scaling") => "成长".into(),
        _ => value.to_string(),
    }
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

#[allow(dead_code)]
fn read_replay_summary() -> Option<ReplaySummary> {
    let replay_path = resolve_latest_replay_path()?;
    let bytes = fs::read(&replay_path).ok()?;
    let metadata = parse_replay_header(&bytes)?;
    let strings = extract_ascii_strings(&bytes, 8);
    let latest_page = parse_latest_replay_page(&strings);
    let resolved_outcome = read_resolved_outcome_from_save();
    let updated_at = fs::metadata(replay_path)
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

#[allow(dead_code)]
struct ReplayHeader {
    version: String,
    git_commit: String,
    model_id_hash: u32,
}

#[allow(dead_code)]
fn parse_replay_header(bytes: &[u8]) -> Option<ReplayHeader> {
    let mut cursor = 0usize;
    Some(ReplayHeader {
        version: read_prefixed_string(bytes, &mut cursor)?,
        git_commit: read_prefixed_string(bytes, &mut cursor)?,
        model_id_hash: read_u32_le(bytes, &mut cursor)?,
    })
}

#[allow(dead_code)]
fn read_prefixed_string(bytes: &[u8], cursor: &mut usize) -> Option<String> {
    let len = read_u32_le(bytes, cursor)? as usize;
    let end = cursor.checked_add(len)?;
    let raw = bytes.get(*cursor..end)?;
    *cursor = end;
    String::from_utf8(raw.to_vec()).ok()
}

#[allow(dead_code)]
fn read_u32_le(bytes: &[u8], cursor: &mut usize) -> Option<u32> {
    let end = cursor.checked_add(4)?;
    let raw = bytes.get(*cursor..end)?;
    *cursor = end;
    Some(u32::from_le_bytes(raw.try_into().ok()?))
}

#[allow(dead_code)]
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

#[allow(dead_code)]
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

#[allow(dead_code)]
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

#[allow(dead_code)]
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

#[allow(dead_code)]
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

#[allow(dead_code)]
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

#[allow(dead_code)]
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

#[allow(dead_code)]
fn read_resolved_outcome_from_save() -> Option<ResolvedOutcome> {
    let path = resolve_current_run_save_path()?;
    let raw = fs::read_to_string(path).ok()?;
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

#[allow(dead_code)]
fn parse_context_title(entry: &str) -> Option<String> {
    parse_title_context(entry).map(|action| action.title)
}

#[allow(dead_code)]
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

#[allow(dead_code)]
fn infer_choice_model(options: &[String]) -> String {
    if options.is_empty() {
        "Unknown".into()
    } else {
        "Index".into()
    }
}

#[allow(dead_code)]
fn push_unique_limited(items: &mut Vec<String>, value: String, max_len: usize) {
    if items.iter().any(|item| item == &value) {
        return;
    }
    items.push(value);
    if items.len() > max_len {
        items.remove(0);
    }
}

#[allow(dead_code)]
fn push_limited<T>(items: &mut Vec<T>, value: T, max_len: usize) {
    items.push(value);
    if items.len() > max_len {
        items.remove(0);
    }
}

#[allow(dead_code)]
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

#[allow(dead_code)]
fn infer_replay_phase(actions: &[ReplayAction]) -> String {
    match actions.last().map(|action| action.kind.as_str()) {
        Some("card") | Some("phase") => "battle".into(),
        Some("choice") | Some("event") => "event".into(),
        _ => "unknown".into(),
    }
}

#[allow(dead_code)]
fn find_latest_replay_action<'a>(
    actions: &'a [ReplayAction],
    kind: &str,
) -> Option<&'a ReplayAction> {
    actions.iter().rev().find(|action| action.kind == kind)
}

#[allow(dead_code)]
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
                memory_status: None,
                enemies: vec![EnemyState {
                    name: "Jaw Worm".into(),
                    hp: 38,
                    block: Some(0),
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
                memory_status: None,
                enemies: vec![EnemyState {
                    name: "Gremlin Nob".into(),
                    hp: 85,
                    block: Some(0),
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

fn generate_recommendations(
    game_state: &GameState,
    database: &Database,
    locale: AppLocale,
) -> Recommendations {
    let deck_analysis = compute_deck_power_score(game_state, database);
    let card_rewards = evaluate_card_reward(game_state, &deck_analysis, database, locale);
    let path_recommendation = recommend_path(game_state, &deck_analysis, locale);
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
    locale: AppLocale,
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
            let mut reasons = vec![localized_fit_reason(locale, primary_archetype)];
            if card.tags.iter().any(|tag| tag == "scaling") {
                reasons.push(localized_scaling_reason(locale));
            }
            if low_hp_block_bonus > 0.0 {
                reasons.push(localized_low_hp_reason(locale));
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
                reason: localized_unknown_card_reason(locale),
            });
        }
    }

    results.sort_by(|left, right| right.score.partial_cmp(&left.score).unwrap());
    results
}

fn recommend_path(
    game_state: &GameState,
    deck_analysis: &DeckAnalysis,
    locale: AppLocale,
) -> PathRecommendation {
    let hp_ratio = game_state.player.hp as f64 / game_state.player.max_hp.max(1) as f64;
    let preferred_order = if hp_ratio < 0.4 {
        (
            vec!["Rest", "Shop", "Battle", "Elite"],
            localized_path_reason_low_hp(locale),
        )
    } else if deck_analysis.score >= 65 {
        (
            vec!["Elite", "Shop", "Rest", "Battle"],
            localized_path_reason_strong(locale),
        )
    } else {
        (
            vec!["Battle", "Shop", "Rest", "Elite"],
            localized_path_reason_mid(locale),
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
        reason: preferred_order.1,
    }
}

fn empty_replay_summary() -> ReplaySummary {
    ReplaySummary {
        source: "disabled".into(),
        version: "-".into(),
        git_commit: "-".into(),
        model_id_hash: "-".into(),
        updated_at: now_timestamp_string(),
        phase_hint: "map".into(),
        latest_page: None,
        resolved_outcome: None,
        latest_contexts: Vec::new(),
        latest_cards: Vec::new(),
        latest_events: Vec::new(),
        latest_choices: Vec::new(),
        recent_actions: Vec::new(),
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
    let enemy_intent = game_state
        .battle
        .enemies
        .first()
        .map(|enemy| enemy.intent.to_lowercase())
        .unwrap_or_default();
    let relics = game_state
        .relics
        .iter()
        .map(|relic| relic.to_lowercase())
        .collect::<Vec<_>>();
    let has_akabeko = relics.iter().any(|relic| relic == "akabeko");
    let has_lantern = relics.iter().any(|relic| relic == "lantern");
    let low_hp = game_state.player.hp * 10 <= game_state.player.max_hp.max(1) * 6;

    let mut plan = Vec::new();

    if primary == "poison" && hand.iter().any(|card| card == "Deadly Poison") {
        return vec!["Deadly Poison".into(), "Backflip".into(), "Defend".into()];
    }

    if primary == "shiv" && hand.iter().any(|card| card == "Blade Dance") {
        return vec!["Footwork".into(), "Blade Dance".into(), "Backflip".into()];
    }

    if enemy_intent.contains("attack") {
        if low_hp {
            push_if_in_hand(&mut plan, hand, "Defend");
        }
        if has_akabeko {
            push_first_present(
                &mut plan,
                hand,
                &["Bash", "Stomp", "Anger", "Strike", "Twin Strike"],
            );
        } else {
            push_first_present(&mut plan, hand, &["Bash", "Stomp", "Armaments", "Strike"]);
        }
        push_if_in_hand(&mut plan, hand, "Defend");
        if has_lantern {
            push_if_in_hand(&mut plan, hand, "Armaments");
        }
    } else if enemy_intent.contains("debuff") {
        if has_akabeko {
            push_first_present(
                &mut plan,
                hand,
                &["Bash", "Stomp", "Anger", "Strike", "Twin Strike"],
            );
        }
        push_first_present(&mut plan, hand, &["Bash", "Stomp", "Armaments", "Anger"]);
        if has_lantern {
            push_if_in_hand(&mut plan, hand, "Defend");
        }
    } else {
        if has_akabeko {
            push_first_present(
                &mut plan,
                hand,
                &["Bash", "Stomp", "Anger", "Strike", "Twin Strike"],
            );
        }
        if has_lantern {
            push_if_in_hand(&mut plan, hand, "Armaments");
        }
    }

    for card in hand {
        if plan.len() >= 3 {
            break;
        }
        push_if_in_hand(&mut plan, hand, card);
    }

    plan
}

fn push_if_in_hand(plan: &mut Vec<String>, hand: &[String], card_name: &str) {
    if hand.iter().any(|card| card == card_name) && !plan.iter().any(|card| card == card_name) {
        plan.push(card_name.into());
    }
}

fn push_first_present(plan: &mut Vec<String>, hand: &[String], candidates: &[&str]) {
    for candidate in candidates {
        if hand.iter().any(|card| card == candidate) {
            push_if_in_hand(plan, hand, candidate);
            return;
        }
    }
}

#[allow(dead_code)]
fn build_overlay_layout(
    game_state: &GameState,
    recommendations: &Recommendations,
    replay: &ReplaySummary,
    _locale: AppLocale,
) -> OverlayLayout {
    let enemy = game_state.battle.enemies.first();
    let top_reward = recommendations.card_rewards.first();
    let scene = detect_scene(game_state, replay, None);
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

fn detect_scene(game_state: &GameState, replay: &ReplaySummary, scene_hint: Option<&str>) -> String {
    if let Some(scene_hint) = scene_hint {
        if !scene_hint.is_empty() && scene_hint != "unknown" {
            return scene_hint.to_string();
        }
    }
    if !game_state.rewards.cards.is_empty() {
        return "reward".into();
    }
    if !game_state.battle.enemies.is_empty() {
        return "battle".into();
    }
    if replay.phase_hint == "event" {
        return "event".into();
    }
    "map".into()
}

fn build_overlay_layout_v2(
    game_state: &GameState,
    recommendations: &Recommendations,
    replay: &ReplaySummary,
    scene_hint: Option<&str>,
    locale: AppLocale,
) -> OverlayLayout {
    let enemy = game_state.battle.enemies.first();
    let top_reward = recommendations.card_rewards.first();
    let scene = detect_scene(game_state, replay, scene_hint);
    let battle_title = enemy
        .map(|entry| localized_battle_title(locale, &entry.name))
        .or_else(|| {
            game_state
                .battle
                .encounter_name
                .as_ref()
                .map(|name| localized_encounter_title(locale, name))
        })
        .unwrap_or_else(|| localized_battle_info_title(locale));
    let battle_body = if let Some(entry) = enemy {
        localized_battle_body(
            locale,
            &entry.intent,
            recommendations
                .turn_suggestion
                .first()
                .map(String::as_str)
                .unwrap_or_else(|| localized_current_hand(locale)),
        )
    } else if let Some(encounter) = &game_state.battle.encounter_name {
        let phase = game_state
            .battle
            .current_phase
            .clone()
            .or_else(|| game_state.battle.last_card_played.clone())
            .unwrap_or_else(|| localized_waiting_more_actions(locale));
        localized_encounter_phase(locale, encounter, &phase)
    } else {
        localized_no_battle_target(locale)
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
                title: localized_play_order_title(locale),
                body: recommendations.turn_suggestion.join(" -> "),
                x: 0.14,
                y: 0.76,
                tone: "info".into(),
            },
            OverlayAnchor {
                id: "card-reward".into(),
                title: localized_card_reward_title(locale),
                body: top_reward
                    .map(|entry| format!("{} ({:.1})", entry.card_name, entry.score))
                    .unwrap_or_else(|| localized_no_reward(locale)),
                x: 0.64,
                y: 0.68,
                tone: "accent".into(),
            },
            OverlayAnchor {
                id: "map-route".into(),
                title: localized_route_title(locale),
                body: recommendations.path_recommendation.route.join(" -> "),
                x: 0.62,
                y: 0.2,
                tone: "good".into(),
            },
        ],
    }
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
    let monitor = window
        .current_monitor()
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "No active monitor".to_string())?;
    let work = monitor.work_area();
    let width = work.size.width;
    let height = work.size.height;
    let x = work.position.x;
    let y = work.position.y;

    window
        .set_position(PhysicalPosition::new(x, y))
        .map_err(|error| error.to_string())?;
    window
        .set_size(PhysicalSize::new(width, height))
        .map_err(|error| error.to_string())?;
    Ok(false)
}

fn sync_overlay_window_state(
    window: &WebviewWindow,
    enabled: bool,
    interactive: bool,
) -> Result<bool, String> {
    if !enabled {
        let _ = window.hide();
        return Ok(false);
    }

    let attached = apply_window_bounds(window)?;
    let _ = window.set_ignore_cursor_events(!interactive);
    let _ = window.show();
    Ok(attached)
}

fn start_overlay_hotkey_manager(app: AppHandle) {
    #[cfg(target_os = "windows")]
    thread::spawn(move || {
        let mut f6_was_down = false;
        let mut f7_was_down = false;
        let mut f8_was_down = false;
        let mut f10_was_down = false;

        loop {
            let f6_down = windows_memory::is_virtual_key_pressed(0x75);
            let f7_down = windows_memory::is_virtual_key_pressed(0x76);
            let f8_down = windows_memory::is_virtual_key_pressed(0x77);
            let f10_down = windows_memory::is_virtual_key_pressed(0x79);

            if f6_down && !f6_was_down {
                if let Some(window) = app.get_webview_window("main") {
                    let _ = window.emit("history-toggle-requested", ());
                }
            }

            if f7_down && !f7_was_down {
                let locale = {
                    let state = app.state::<AppState>();
                    let mut current = match state.locale.lock() {
                        Ok(current) => current,
                        Err(_) => continue,
                    };
                    *current = match *current {
                        AppLocale::EnUs => AppLocale::ZhCn,
                        AppLocale::ZhCn => AppLocale::EnUs,
                    };
                    *current
                };

                emit_locale_update(&app, locale);
                emit_snapshot_update(&app);
            }

            if f8_down && !f8_was_down {
                if let Some(window) = app.get_webview_window("main") {
                    let enabled = {
                        let state = app.state::<AppState>();
                        let mut enabled = match state.overlay_enabled.lock() {
                            Ok(enabled) => enabled,
                            Err(_) => continue,
                        };
                        *enabled = !*enabled;
                        *enabled
                    };
                    let interactive = app
                        .state::<AppState>()
                        .overlay_interactive
                        .lock()
                        .map(|value| *value)
                        .unwrap_or(false);
                    let _ = sync_overlay_window_state(&window, enabled, interactive);
                }
            }

            if f10_down && !f10_was_down {
                app.exit(0);
                break;
            }

            f6_was_down = f6_down;
            f7_was_down = f7_down;
            f8_was_down = f8_down;
            f10_was_down = f10_down;
            thread::sleep(Duration::from_millis(60));
        }
    });
}

fn start_hud_event_bridge(
    app: AppHandle,
    memory_cache: Arc<Mutex<Option<MemorySnapshot>>>,
    game_state_cache: Arc<Mutex<Option<GameState>>>,
    debug_state: Arc<Mutex<DebugState>>,
) {
    #[cfg(target_os = "windows")]
    thread::spawn(move || {
        let listener = match TcpListener::bind(HUD_EVENT_BRIDGE_ADDR) {
            Ok(listener) => listener,
            Err(_) => return,
        };

        for stream in listener.incoming().flatten() {
            let reader = BufReader::new(stream);
            for line in reader.lines().map_while(Result::ok) {
                let event = match serde_json::from_str::<HudEventEnvelope>(&line) {
                    Ok(event) => event,
                    Err(_) => continue,
                };

                if event.kind != "refresh" {
                    continue;
                }

                let source = event.source.clone().unwrap_or_else(|| {
                    event
                        .trigger
                        .as_ref()
                        .and_then(|trigger| match (&trigger.type_name, &trigger.method_name) {
                            (Some(type_name), Some(method_name)) => {
                                Some(format!("event({type_name}.{method_name})"))
                            }
                            _ => None,
                        })
                        .unwrap_or_else(|| "event(hook)".into())
                });

                refresh_live_state_into(
                    Some(&app),
                    &memory_cache,
                    &game_state_cache,
                    &debug_state,
                    Some(&source),
                );
            }
        }
    });
}

#[cfg(target_os = "windows")]
mod windows_memory {
    use super::{parse_hand_blob, MemoryBlobConfig, MemoryReaderConfig, MemorySnapshot, RefreshDebug};
    use serde::Deserialize;
    use std::{
        ffi::OsString,
        mem::{size_of, zeroed},
        os::windows::ffi::OsStringExt,
        path::PathBuf,
    };
    use windows_sys::Win32::{
        Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE},
        System::{
            Diagnostics::{
                Debug::ReadProcessMemory,
                ToolHelp::{
                    CreateToolhelp32Snapshot, Module32FirstW, Module32NextW, Process32FirstW,
                    Process32NextW, MODULEENTRY32W, PROCESSENTRY32W, TH32CS_SNAPMODULE,
                    TH32CS_SNAPMODULE32, TH32CS_SNAPPROCESS,
                },
            },
            Threading::{OpenProcess, PROCESS_QUERY_INFORMATION, PROCESS_VM_READ},
        },
        UI::{
            Input::KeyboardAndMouse::GetAsyncKeyState,
        },
    };

    #[derive(Deserialize)]
    struct ClrProbeSnapshot {
        hand: Vec<String>,
        enemies: Vec<ClrProbeEnemy>,
        player: Option<ClrProbePlayer>,
        reward_cards: Vec<String>,
        scene_hint: Option<String>,
        status: Option<String>,
    }

    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct ClrProbeEnemy {
        name: String,
        hp: i32,
        block: Option<i32>,
        intent: Option<String>,
    }

    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct ClrProbePlayer {
        hp: Option<i32>,
        max_hp: Option<i32>,
        energy: Option<i32>,
    }

    pub(super) fn read_memory_snapshot(
        config: &MemoryReaderConfig,
    ) -> (Option<MemorySnapshot>, RefreshDebug) {
        let Some(pid) = find_process_id(&config.process_names()) else {
            return (
                None,
                RefreshDebug {
                    probe_summary: Some("target process not found".into()),
                    ..RefreshDebug::default()
                },
            );
        };
        let Some(process) = open_process(pid) else {
            return (
                None,
                RefreshDebug {
                    probe_summary: Some(format!("failed to open process pid={pid}")),
                    ..RefreshDebug::default()
                },
            );
        };
        let (clr_snapshot, debug) = read_clr_probe_snapshot(&config.process_names());
        let hand = clr_snapshot
            .as_ref()
            .map(|snapshot| snapshot.hand.clone())
            .filter(|cards| !cards.is_empty())
            .or_else(|| {
                config
                    .hand
                    .as_ref()
                    .and_then(|blob| read_hand_blob(process, pid, blob))
            });
        let status = clr_snapshot
            .as_ref()
            .and_then(|snapshot| snapshot.status.clone())
            .or_else(|| {
                hand.as_ref().map(|cards| {
                    if cards.is_empty() {
                        "memory(attached)".into()
                    } else {
                        "memory(hand)".into()
                    }
                })
            });
        let enemies = clr_snapshot
            .as_ref()
            .map(|snapshot| {
                snapshot
                    .enemies
                    .iter()
                    .map(|enemy| {
                        let base_intent = enemy.intent.clone().unwrap_or_else(|| "Unknown".into());
                        let intent = match enemy.block.filter(|block| *block > 0) {
                            Some(block) => format!("{base_intent} | Block {block}"),
                            None => base_intent,
                        };

                        super::EnemyState {
                            name: enemy.name.clone(),
                            hp: enemy.hp,
                            block: enemy.block,
                            intent: if intent == "Unknown" {
                                "Intent pending".into()
                            } else {
                                intent
                            },
                        }
                    })
                    .collect()
            })
            .unwrap_or_default();
        unsafe {
            CloseHandle(process);
        }

        (
            Some(MemorySnapshot {
                hand: hand.unwrap_or_default(),
                enemies,
                player: clr_snapshot.as_ref().and_then(|snapshot| {
                    snapshot
                        .player
                        .as_ref()
                        .map(|player| super::MemoryPlayerState {
                            hp: player.hp,
                            max_hp: player.max_hp,
                            energy: player.energy,
                        })
                }),
                reward_cards: clr_snapshot
                    .as_ref()
                    .map(|snapshot| snapshot.reward_cards.clone())
                    .unwrap_or_default(),
                scene_hint: clr_snapshot
                    .as_ref()
                    .and_then(|snapshot| snapshot.scene_hint.clone()),
                status,
            }),
            debug,
        )
    }

    fn read_clr_probe_snapshot(process_names: &[String]) -> (Option<ClrProbeSnapshot>, RefreshDebug) {
        let process_name = process_names
            .iter()
            .find_map(|name| {
                let trimmed = name.trim();
                if trimmed.is_empty() {
                    return None;
                }
                Some(trimmed.trim_end_matches(".exe").to_string())
            })
            .unwrap_or_else(|| "SlayTheSpire2".into());

        let Some(probe_path) = find_clr_probe_dll() else {
            return (
                None,
                RefreshDebug {
                    probe_summary: Some("Sts2ClrProbe.dll not found".into()),
                    ..RefreshDebug::default()
                },
            );
        };
        let output = match super::Command::new("dotnet")
            .arg("exec")
            .arg(probe_path)
            .arg("--json")
            .arg(process_name)
            .output()
        {
            Ok(output) => output,
            Err(error) => {
                return (
                    None,
                    RefreshDebug {
                        probe_summary: Some(format!("dotnet exec failed: {error}")),
                        ..RefreshDebug::default()
                    },
                );
            }
        };

        let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        let parsed = if output.status.success() {
            serde_json::from_slice::<ClrProbeSnapshot>(&output.stdout).ok()
        } else {
            None
        };

        (
            parsed,
            RefreshDebug {
                probe_summary: Some(format!(
                    "probe exit={} success={} stdout_len={} stderr_len={}",
                    output.status.code().unwrap_or(-1),
                    output.status.success(),
                    stdout.len(),
                    stderr.len()
                )),
                probe_stdout: (!stdout.is_empty()).then_some(stdout),
                probe_stderr: (!stderr.is_empty()).then_some(stderr),
                ..RefreshDebug::default()
            },
        )
    }

    fn find_clr_probe_dll() -> Option<PathBuf> {
        let candidates = [
            PathBuf::from("tools")
                .join("Sts2ClrProbe")
                .join("bin")
                .join("Debug")
                .join("net8.0")
                .join("Sts2ClrProbe.dll"),
            PathBuf::from("..")
                .join("tools")
                .join("Sts2ClrProbe")
                .join("bin")
                .join("Debug")
                .join("net8.0")
                .join("Sts2ClrProbe.dll"),
        ];

        candidates.into_iter().find(|path| path.exists())
    }

    fn read_hand_blob(process: HANDLE, pid: u32, config: &MemoryBlobConfig) -> Option<Vec<String>> {
        let base = module_base_address(pid, config.module_name.as_deref())?;
        let address = resolve_pointer_chain(
            process,
            base.checked_add(config.base_offset)?,
            config.pointer_offsets.as_deref().unwrap_or(&[]),
        )?;
        let bytes = read_memory(process, address, config.read_len)?;
        let cards = parse_hand_blob(config, &bytes);
        (!cards.is_empty()).then_some(cards)
    }

    fn find_process_id(names: &[String]) -> Option<u32> {
        let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
        if snapshot == INVALID_HANDLE_VALUE {
            return None;
        }

        let mut entry: PROCESSENTRY32W = unsafe { zeroed() };
        entry.dwSize = size_of::<PROCESSENTRY32W>() as u32;
        let mut result = None;

        let mut has_entry = unsafe { Process32FirstW(snapshot, &mut entry) } != 0;
        while has_entry {
            let exe = utf16_to_string(&entry.szExeFile);
            if names.iter().any(|name| exe.eq_ignore_ascii_case(name)) {
                result = Some(entry.th32ProcessID);
                break;
            }
            has_entry = unsafe { Process32NextW(snapshot, &mut entry) } != 0;
        }

        unsafe {
            CloseHandle(snapshot);
        }
        result
    }

    fn open_process(pid: u32) -> Option<HANDLE> {
        let handle = unsafe { OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, 0, pid) };
        (!handle.is_null()).then_some(handle)
    }

    fn module_base_address(pid: u32, module_name: Option<&str>) -> Option<usize> {
        let snapshot =
            unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid) };
        if snapshot == INVALID_HANDLE_VALUE {
            return None;
        }

        let mut entry: MODULEENTRY32W = unsafe { zeroed() };
        entry.dwSize = size_of::<MODULEENTRY32W>() as u32;
        let mut result = None;
        let target_module = module_name.map(|name| name.to_ascii_lowercase());

        let mut has_entry = unsafe { Module32FirstW(snapshot, &mut entry) } != 0;
        while has_entry {
            let module = utf16_to_string(&entry.szModule);
            let matches = target_module
                .as_ref()
                .map(|name| module.eq_ignore_ascii_case(name))
                .unwrap_or(true);
            if matches {
                result = Some(entry.modBaseAddr as usize);
                break;
            }
            has_entry = unsafe { Module32NextW(snapshot, &mut entry) } != 0;
        }

        unsafe {
            CloseHandle(snapshot);
        }
        result
    }

    fn resolve_pointer_chain(process: HANDLE, start: usize, offsets: &[usize]) -> Option<usize> {
        let mut address = start;
        for offset in offsets {
            let next = read_pointer(process, address)?;
            address = next.checked_add(*offset)?;
        }
        Some(address)
    }

    fn read_pointer(process: HANDLE, address: usize) -> Option<usize> {
        let raw = read_memory(process, address, size_of::<usize>())?;
        if size_of::<usize>() == 8 {
            let bytes: [u8; 8] = raw.try_into().ok()?;
            Some(u64::from_le_bytes(bytes) as usize)
        } else {
            let bytes: [u8; 4] = raw.try_into().ok()?;
            Some(u32::from_le_bytes(bytes) as usize)
        }
    }

    fn read_memory(process: HANDLE, address: usize, len: usize) -> Option<Vec<u8>> {
        if len == 0 {
            return Some(Vec::new());
        }

        let mut buffer = vec![0u8; len];
        let mut read = 0usize;
        let ok = unsafe {
            ReadProcessMemory(
                process,
                address as *const _,
                buffer.as_mut_ptr() as *mut _,
                len,
                &mut read,
            )
        };
        if ok == 0 || read == 0 {
            return None;
        }
        buffer.truncate(read);
        Some(buffer)
    }

    fn utf16_to_string(buffer: &[u16]) -> String {
        let len = buffer
            .iter()
            .position(|value| *value == 0)
            .unwrap_or(buffer.len());
        OsString::from_wide(&buffer[..len])
            .to_string_lossy()
            .into_owned()
    }
    pub(super) fn is_virtual_key_pressed(vkey: i32) -> bool {
        unsafe { (GetAsyncKeyState(vkey) as u16 & 0x8000) != 0 }
    }
}

fn main() {
    let memory_cache = Arc::new(Mutex::new(None));
    let game_state_cache = Arc::new(Mutex::new(None));
    let debug_state = Arc::new(Mutex::new(DebugState::default()));

    tauri::Builder::default()
        .manage(AppState {
            database: Database::load(),
            localization: LocalizationDb::load(),
            locale: Mutex::new(AppLocale::EnUs),
            cached_memory: memory_cache.clone(),
            cached_game_state: game_state_cache.clone(),
            overlay_enabled: Arc::new(Mutex::new(true)),
            overlay_interactive: Arc::new(Mutex::new(false)),
            debug_state: debug_state.clone(),
        })
        .setup(move |app| {
            if let Some(window) = app.get_webview_window("main") {
                let _ = sync_overlay_window_state(&window, true, false);
            }
            let handle = app.handle().clone();
            refresh_live_state_into(
                Some(&handle),
                &memory_cache,
                &game_state_cache,
                &debug_state,
                None,
            );
            start_hud_event_bridge(
                handle.clone(),
                memory_cache.clone(),
                game_state_cache.clone(),
                debug_state.clone(),
            );
            start_overlay_hotkey_manager(handle);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            get_snapshot,
            sync_overlay_window,
            set_locale,
            set_overlay_interactive
        ])
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
        let result = generate_recommendations(&game_state, &database, AppLocale::EnUs);

        assert_eq!(result.card_rewards[0].card_name, "Catalyst");
        assert_eq!(result.deck_analysis.archetypes[0].key, "poison");
    }

    #[test]
    fn path_planner_avoids_elites_on_low_hp() {
        let database = test_database();
        let mut game_state = poison_state();
        game_state.player.hp = 18;
        game_state.map.upcoming_nodes = vec!["Elite".into(), "Rest".into(), "Shop".into()];
        let result = generate_recommendations(&game_state, &database, AppLocale::EnUs);

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
    fn hand_blob_parser_normalizes_utf8_card_ids() {
        let config = MemoryBlobConfig {
            module_name: None,
            base_offset: 0,
            pointer_offsets: None,
            read_len: 64,
            encoding: Some("utf8".into()),
            separator: Some("|".into()),
            max_cards: Some(5),
        };

        assert_eq!(
            parse_hand_blob(&config, b"CARD.BASH|CARD.DEFEND|CARD.ANGER"),
            vec!["Bash", "Defend", "Anger"]
        );
    }

    #[test]
    fn hand_blob_parser_supports_utf16_payloads() {
        let config = MemoryBlobConfig {
            module_name: None,
            base_offset: 0,
            pointer_offsets: None,
            read_len: 64,
            encoding: Some("utf16".into()),
            separator: Some("|".into()),
            max_cards: Some(4),
        };
        let bytes = "CARD.ZAP|CARD.DUALCAST"
            .encode_utf16()
            .flat_map(|word| word.to_le_bytes())
            .collect::<Vec<_>>();

        assert_eq!(parse_hand_blob(&config, &bytes), vec!["Zap", "Dualcast"]);
    }

    #[test]
    fn compose_live_source_mentions_memory_when_attached() {
        let memory = MemorySnapshot {
            hand: vec!["Bash".into()],
            enemies: Vec::new(),
            player: None,
            status: Some("memory(hand)".into()),
        };

        assert_eq!(
            compose_live_source(Some(&memory)),
            "current_run.save + memory(hand)"
        );
    }

    #[test]
    #[cfg(target_os = "windows")]
    fn live_game_state_can_be_read_from_local_save() {
        let path = resolve_current_run_save_path().expect("save path");
        let raw = fs::read_to_string(path).expect("save contents");
        let state = parse_run_save(&raw).expect("run save parsed");
        let player = state.players.first().expect("player");
        assert!(!player.character_id.is_empty());
        let live = read_live_game_state(None).expect("live game state");
        assert!(!live.character.is_empty());
        assert!(live.player.max_hp > 0);
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

        assert_eq!(detect_scene(&game_state, &replay, None), "event");
    }

    #[test]
    fn battle_state_stays_empty_without_live_memory_enemies() {
        let battle = infer_live_battle_state("monster", None);
        assert!(battle.enemies.is_empty());
        assert!(battle.encounter_name.is_none());
        assert!(battle.room_type.is_none());
        assert!(battle.current_phase.is_none());
        assert!(battle.last_card_played.is_none());
    }
}
