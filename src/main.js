import "./styles.css";
import { deriveMode, currentSnapshotLocale } from "./mode.js";
import {
  appState,
  LOCALE_KEY,
  pushDebugEntry,
  setCurrentLocale,
  setHistoryOpenState,
  setLastSnapshot,
  setRefreshInFlight
} from "./state.js";
import { renderDebug, renderSnapshot } from "./render.js";
import { applyStaticLabels } from "./ui.js";
import { invoke, listen } from "./tauri.js";

function pushDebug(message, payload) {
  pushDebugEntry(message, payload);
  renderDebug();
}

async function setHistoryOpen(nextOpen) {
  setHistoryOpenState(nextOpen);
  document.getElementById("history-panel").hidden = !nextOpen;
  await invoke("set_overlay_interactive", { interactive: nextOpen });
}

async function persistLocale(locale) {
  setCurrentLocale(locale);
  window.localStorage.setItem(LOCALE_KEY, locale);
  await invoke("set_locale", { locale });
  applyStaticLabels();
  renderDebug();
}

function wireHistoryControls() {
  document.getElementById("history-close").addEventListener("click", () => {
    void setHistoryOpen(false);
  });
  document.getElementById("history-backdrop").addEventListener("click", () => {
    void setHistoryOpen(false);
  });
}

function renderIncomingSnapshot(snapshot) {
  setCurrentLocale(currentSnapshotLocale(snapshot, appState.currentLocale));
  setLastSnapshot(snapshot);
  renderSnapshot(snapshot);
  pushDebug("snapshot-updated", {
    scene: snapshot.overlay?.scene,
    mode: deriveMode(snapshot),
    hand: snapshot.gameState?.hand?.length || 0,
    enemies: snapshot.gameState?.battle?.enemies?.length || 0,
    rewards: snapshot.gameState?.rewards?.cards?.length || 0,
    source: snapshot.source
  });
}

async function refresh() {
  if (appState.refreshInFlight) {
    return;
  }

  setRefreshInFlight(true);
  pushDebug("refresh-start");
  try {
    const snapshot = await invoke("get_snapshot");
    renderIncomingSnapshot(snapshot);
  } finally {
    setRefreshInFlight(false);
    pushDebug("refresh-end");
  }
}

async function syncWindowMode() {
  await invoke("sync_overlay_window");
}

async function bootstrap() {
  applyStaticLabels();
  wireHistoryControls();
  await persistLocale(appState.currentLocale);
  await syncWindowMode();

  await listen("history-toggle-requested", () => {
    const nextOpen = !appState.historyOpen;
    void setHistoryOpen(nextOpen);
    pushDebug("history-toggle-requested", { open: nextOpen });
  });

  await listen("locale-changed", async (event) => {
    await persistLocale(event.payload);
    pushDebug("locale-changed", { locale: event.payload });
  });

  await listen("snapshot-updated", (event) => {
    renderIncomingSnapshot(event.payload);
  });

  await refresh();
  window.addEventListener("resize", syncWindowMode);
}

bootstrap();
