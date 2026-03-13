use serde::Serialize;
use std::{
    sync::{Arc, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DebugState {
    pub entries: Vec<DebugEntry>,
    pub last_refresh_source: Option<String>,
    pub last_memory_summary: Option<String>,
    pub last_game_state_summary: Option<String>,
    pub last_merge_summary: Option<String>,
    pub last_probe_summary: Option<String>,
    pub last_probe_stdout: Option<String>,
    pub last_probe_stderr: Option<String>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DebugEntry {
    pub timestamp: String,
    pub stage: String,
    pub message: String,
}

#[derive(Clone, Default)]
pub struct RefreshDebug {
    pub probe_summary: Option<String>,
    pub probe_stdout: Option<String>,
    pub probe_stderr: Option<String>,
    pub merge_summary: Option<String>,
}

fn now_timestamp_string() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs())
        .unwrap_or(0);
    format!("unix:{secs}")
}

pub fn push_debug_entry(
    debug_state: &Arc<Mutex<DebugState>>,
    stage: &str,
    message: impl Into<String>,
) {
    let message = message.into();
    eprintln!("[spire-guide][{stage}] {message}");
    if let Ok(mut debug) = debug_state.lock() {
        debug.entries.insert(
            0,
            DebugEntry {
                timestamp: now_timestamp_string(),
                stage: stage.to_string(),
                message,
            },
        );
        debug.entries.truncate(120);
    }
}

pub fn format_refresh_source_label(source: Option<&str>) -> String {
    match source.unwrap_or("startup") {
        value if value.starts_with("game-log/") => value.replacen("game-log/", "action=", 1),
        value if value.starts_with("event(") => format!("action={value}"),
        value => format!("source={value}"),
    }
}

pub fn print_debug_blob(stage: &str, label: &str, value: &Option<String>) {
    if let Some(value) = value.as_ref().filter(|value| !value.is_empty()) {
        let formatted = serde_json::from_str::<serde_json::Value>(value)
            .ok()
            .and_then(|json| serde_json::to_string_pretty(&json).ok())
            .unwrap_or_else(|| value.to_string());
        eprintln!("[spire-guide][{stage}][{label}]");
        for line in formatted.lines() {
            eprintln!("  {line}");
        }
    }
}
