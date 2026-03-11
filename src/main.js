import "./styles.css";
import { invoke } from "@tauri-apps/api/core";

let historyOpen = false;

function setText(id, value) {
  document.getElementById(id).textContent = value;
}

function setWindowMode(mode) {
  const sidebar = document.getElementById("sidebar");
  setText("window-mode", mode.attachedToGame ? "Attached To Game" : "Detached Fallback");
  sidebar.classList.toggle("attached", Boolean(mode.attachedToGame));
}

function applyOverlayMeta(overlay) {
  const root = document.documentElement;
  const sidebar = document.getElementById("sidebar");
  root.dataset.scene = overlay.scene || "battle";
  root.style.setProperty("--hud-scale", `${overlay.scale || 1}`);
  sidebar.classList.toggle("condensed", Boolean(overlay.condensedSidebar));

  const visiblePanels = new Set(overlay.visiblePanels || []);
  for (const panel of document.querySelectorAll("[data-panel]")) {
    const keepVisible = visiblePanels.size === 0 || visiblePanels.has(panel.dataset.panel);
    panel.hidden = !keepVisible;
  }
}

function renderList(id, items, renderItem) {
  const list = document.getElementById(id);
  list.innerHTML = "";

  for (const item of items) {
    const li = document.createElement("li");
    li.innerHTML = renderItem(item);
    list.appendChild(li);
  }
}

function renderAnchors(overlay) {
  const layer = document.getElementById("anchor-layer");
  layer.innerHTML = "";
  const layerWidth = layer.clientWidth;
  const layerHeight = layer.clientHeight;

  for (const anchor of overlay.anchors || []) {
    const card = document.createElement("article");
    card.className = "anchor-card";
    card.dataset.tone = anchor.tone || "info";
    card.innerHTML = `
      <div class="anchor-title">${anchor.title}</div>
      <div class="anchor-body">${anchor.body}</div>
    `;
    layer.appendChild(card);

    const cardWidth = card.offsetWidth;
    const cardHeight = card.offsetHeight;
    const idealLeft = Math.round(anchor.x * layerWidth);
    const idealTop = Math.round(anchor.y * layerHeight);
    const left = clamp(idealLeft, 12, Math.max(12, layerWidth - cardWidth - 12));
    const top = clamp(idealTop, 12, Math.max(12, layerHeight - cardHeight - 18));

    card.style.left = `${left}px`;
    card.style.top = `${top}px`;
  }
}

function clamp(value, min, max) {
  return Math.min(Math.max(value, min), max);
}

function renderSnapshot(snapshot) {
  const { gameState, recommendations, overlay, replay } = snapshot;
  const enemy = gameState.battle.enemies[0];
  const encounterLine = enemy
    ? `${enemy.name} • ${enemy.intent}`
    : gameState.battle.encounterName
      ? `${gameState.battle.encounterName} • ${
          gameState.battle.currentPhase ||
          gameState.battle.lastCardPlayed ||
          gameState.battle.lastActionDetail ||
          "Tracking replay"
        }`
      : "No active battle";
  applyOverlayMeta(overlay);

  setText("encounter", encounterLine);
  setText(
    "player-summary",
    `HP ${gameState.player.hp}/${gameState.player.maxHp} • Gold ${gameState.player.gold} • Energy ${gameState.player.energy}`
  );
  setText("run-summary", `Act ${gameState.map.act} • ${gameState.map.currentNode} • Source ${snapshot.source}`);
  setText("deck-score", `Power ${recommendations.deckAnalysis.score}`);
  setText("path-route", recommendations.pathRecommendation.route.join(" -> "));
  setText("path-reason", recommendations.pathRecommendation.reason);

  renderList(
    "card-rewards",
    recommendations.cardRewards,
    (item) =>
      `<strong>${item.cardName}</strong> <span>(${item.score.toFixed(1)})</span><br /><span class="muted">${item.reason}</span>`
  );

  renderList(
    "archetypes",
    recommendations.deckAnalysis.archetypes,
    (item) => `${item.label} ${item.score}`
  );

  renderList(
    "relic-suggestions",
    recommendations.relicSuggestions,
    (item) => `<strong>${item.relicName}</strong><br /><span class="muted">${item.suggestion}</span>`
  );

  setText("replay-source", replay.source || "unknown");
  setText(
    "replay-meta",
    `${replay.version || "?"} • ${replay.gitCommit || "?"} • hash ${replay.modelIdHash || "?"} • phase ${replay.phaseHint || "unknown"} • ${replay.updatedAt || "?"}`
  );
  setText("replay-page-title", replay.latestPage?.eventTitle || "No event page");
  setText(
    "replay-page-context",
    replay.latestPage ? `Context ${replay.latestPage.contextTitle}` : "No page context"
  );
  setText(
    "replay-page-model",
    replay.latestPage ? `Choice Model ${replay.latestPage.choiceModel}` : "Choice Model unknown"
  );
  setText("replay-outcome-title", replay.resolvedOutcome?.chosenTitle || "No resolved outcome");
  setText(
    "replay-outcome-meta",
    replay.resolvedOutcome
      ? `${replay.resolvedOutcome.eventId} • gold +${replay.resolvedOutcome.goldGained} • max HP -${replay.resolvedOutcome.maxHpLost} • damage ${replay.resolvedOutcome.damageTaken}`
      : "Waiting for resolved outcome"
  );
  renderList("replay-cards", replay.latestCards || [], (item) => item);
  renderList("replay-contexts", replay.latestContexts || [], (item) => item);
  renderList("replay-page-options", replay.latestPage?.options || [], (item) => item);
  renderList("replay-outcome-choices", replay.resolvedOutcome?.offeredChoices || [], (item) => item);
  renderList("replay-outcome-gains", replay.resolvedOutcome?.cardsGained || [], (item) => item);
  renderList("replay-outcome-transforms", replay.resolvedOutcome?.transformedCards || [], (item) => item);
  renderList("replay-choices", replay.latestChoices || [], (item) => item);
  renderList("replay-events", replay.latestEvents || [], (item) => item);
  renderList(
    "replay-actions",
    [...(replay.recentActions || [])].reverse(),
    (item) => `<strong>${item.title}</strong><br /><span class="muted">${item.detail}</span>`
  );

  renderAnchors(overlay);
}

function setHistoryOpen(nextOpen) {
  historyOpen = nextOpen;
  document.getElementById("history-panel").hidden = !nextOpen;
}

function wireHistoryControls() {
  document.getElementById("history-toggle").addEventListener("click", () => setHistoryOpen(true));
  document.getElementById("history-close").addEventListener("click", () => setHistoryOpen(false));
  document.getElementById("history-backdrop").addEventListener("click", () => setHistoryOpen(false));
}

async function refresh() {
  const mode = await invoke("sync_overlay_window");
  setWindowMode(mode);
  const snapshot = await invoke("get_snapshot");
  renderSnapshot(snapshot);
}

async function bootstrap() {
  wireHistoryControls();
  await refresh();
  window.setInterval(refresh, 5000);
  window.addEventListener("resize", refresh);
}

bootstrap();
