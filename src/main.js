import "./styles.css";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

const LOCALE_KEY = "spire-guide-locale";
const UI = {
  "en-us": {
    history: "History",
    close: "Close",
    cardReward: "Card Reward",
    archetype: "Archetype",
    path: "Path",
    relic: "Relic",
    runHistory: "Run History",
    replayFeed: "Replay Feed",
    recentCards: "Recent Cards",
    contexts: "Contexts",
    currentPage: "Current Page",
    resolvedOutcome: "Resolved Outcome",
    offeredChoices: "Offered Choices",
    cardsGained: "Cards Gained",
    choices: "Choices",
    events: "Events",
    actionFeed: "Action Feed",
    attached: "Attached To Game",
    detached: "Detached Fallback",
    waiting: "Waiting for state...",
    noBattle: "No active battle",
    trackingReplay: "Tracking replay",
    hp: "HP",
    gold: "Gold",
    energy: "Energy",
    act: "Act",
    source: "Source",
    power: "Power",
    context: "Context",
    choiceModel: "Choice Model",
    unknownChoiceModel: "Choice Model unknown",
    noEventPage: "No event page",
    noPageContext: "No page context",
    noResolvedOutcome: "No resolved outcome",
    waitingOutcome: "Waiting for resolved outcome",
    phase: "phase",
    localeToggle: "中文"
  },
  "zh-cn": {
    history: "历史",
    close: "关闭",
    cardReward: "选牌建议",
    archetype: "体系",
    path: "路线",
    relic: "遗物",
    runHistory: "局内历史",
    replayFeed: "回放流",
    recentCards: "最近卡牌",
    contexts: "上下文",
    currentPage: "当前页面",
    resolvedOutcome: "已结算结果",
    offeredChoices: "提供选项",
    cardsGained: "获得卡牌",
    choices: "选择",
    events: "事件",
    actionFeed: "动作流",
    attached: "已附着到游戏",
    detached: "独立窗口",
    waiting: "等待状态中...",
    noBattle: "当前没有战斗",
    trackingReplay: "追踪回放",
    hp: "生命",
    gold: "金币",
    energy: "能量",
    act: "第",
    source: "来源",
    power: "强度",
    context: "上下文",
    choiceModel: "选择模型",
    unknownChoiceModel: "选择模型未知",
    noEventPage: "没有事件页面",
    noPageContext: "没有页面上下文",
    noResolvedOutcome: "没有已结算结果",
    waitingOutcome: "等待结算结果",
    phase: "阶段",
    localeToggle: "EN"
  }
};

let historyOpen = false;
let currentLocale = loadStoredLocale();
let refreshInFlight = false;

function loadStoredLocale() {
  const stored = window.localStorage.getItem(LOCALE_KEY);
  return stored === "zh-cn" ? "zh-cn" : "en-us";
}

function ui() {
  return UI[currentLocale];
}

function setText(id, value) {
  document.getElementById(id).textContent = value;
}

function setHtml(id, value) {
  document.getElementById(id).innerHTML = value;
}

function applyStaticLabels() {
  document.documentElement.lang = currentLocale;
  setText("history-toggle", ui().history);
  setText("history-close", ui().close);
  setText("locale-toggle", ui().localeToggle);
  setText("label-card-reward", ui().cardReward);
  setText("label-archetype", ui().archetype);
  setText("label-path", ui().path);
  setText("label-relic", ui().relic);
  setText("label-run-history", ui().runHistory);
  setText("label-replay-feed", ui().replayFeed);
  setText("label-recent-cards", ui().recentCards);
  setText("label-contexts", ui().contexts);
  setText("label-current-page", ui().currentPage);
  setText("label-resolved-outcome", ui().resolvedOutcome);
  setText("label-offered-choices", ui().offeredChoices);
  setText("label-cards-gained", ui().cardsGained);
  setText("label-choices", ui().choices);
  setText("label-events", ui().events);
  setText("label-action-feed", ui().actionFeed);
}

function setWindowMode(mode) {
  const sidebar = document.getElementById("sidebar");
  setText("window-mode", mode.attachedToGame ? ui().attached : ui().detached);
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

function currentSnapshotLocale(snapshot) {
  return snapshot.locale || currentLocale;
}

function renderSnapshot(snapshot) {
  currentLocale = currentSnapshotLocale(snapshot);
  applyStaticLabels();

  const { gameState, recommendations, overlay, replay } = snapshot;
  const enemy = gameState.battle.enemies[0];
  const encounterLine = enemy
    ? `${enemy.name} - ${enemy.intent}`
    : gameState.battle.encounterName
      ? `${gameState.battle.encounterName} - ${
          gameState.battle.currentPhase ||
          gameState.battle.lastCardPlayed ||
          gameState.battle.lastActionDetail ||
          ui().trackingReplay
        }`
      : ui().noBattle;
  applyOverlayMeta(overlay);

  setText("encounter", encounterLine);
  setText(
    "player-summary",
    `${ui().hp} ${gameState.player.hp}/${gameState.player.maxHp} - ${ui().gold} ${gameState.player.gold} - ${ui().energy} ${gameState.player.energy}`
  );
  const actLabel =
    currentLocale === "zh-cn"
      ? `${ui().act}${gameState.map.act}幕`
      : `${ui().act} ${gameState.map.act}`;
  setText("run-summary", `${actLabel} - ${gameState.map.currentNode} - ${ui().source} ${snapshot.source}`);
  setText("deck-score", `${ui().power} ${recommendations.deckAnalysis.score}`);
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
    `${replay.version || "?"} - ${replay.gitCommit || "?"} - hash ${replay.modelIdHash || "?"} - ${ui().phase} ${replay.phaseHint || "unknown"} - ${replay.updatedAt || "?"}`
  );
  setText("replay-page-title", replay.latestPage?.eventTitle || ui().noEventPage);
  setText(
    "replay-page-context",
    replay.latestPage ? `${ui().context} ${replay.latestPage.contextTitle}` : ui().noPageContext
  );
  setText(
    "replay-page-model",
    replay.latestPage ? `${ui().choiceModel} ${replay.latestPage.choiceModel}` : ui().unknownChoiceModel
  );
  setText("replay-outcome-title", replay.resolvedOutcome?.chosenTitle || ui().noResolvedOutcome);
  setText(
    "replay-outcome-meta",
    replay.resolvedOutcome
      ? `${replay.resolvedOutcome.eventId} - ${ui().gold} +${replay.resolvedOutcome.goldGained} - max HP -${replay.resolvedOutcome.maxHpLost} - damage ${replay.resolvedOutcome.damageTaken}`
      : ui().waitingOutcome
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

async function persistLocale(locale) {
  currentLocale = locale;
  window.localStorage.setItem(LOCALE_KEY, locale);
  await invoke("set_locale", {
    locale: locale
  });
  applyStaticLabels();
}

function wireHistoryControls() {
  document.getElementById("history-toggle").addEventListener("click", () => setHistoryOpen(true));
  document.getElementById("history-close").addEventListener("click", () => setHistoryOpen(false));
  document.getElementById("history-backdrop").addEventListener("click", () => setHistoryOpen(false));
  document.getElementById("locale-toggle").addEventListener("click", async () => {
    const nextLocale = currentLocale === "zh-cn" ? "en-us" : "zh-cn";
    await persistLocale(nextLocale);
    await refresh();
  });
}

async function refresh() {
  if (refreshInFlight) {
    return;
  }

  refreshInFlight = true;
  try {
    const snapshot = await invoke("get_snapshot");
    renderSnapshot(snapshot);
  } finally {
    refreshInFlight = false;
  }
}

async function syncWindowMode() {
  const mode = await invoke("sync_overlay_window");
  setWindowMode(mode);
}

async function bootstrap() {
  applyStaticLabels();
  wireHistoryControls();
  await persistLocale(currentLocale);
  await syncWindowMode();
  await listen("snapshot-updated", (event) => {
    renderSnapshot(event.payload);
  });
  await refresh();
  window.addEventListener("resize", syncWindowMode);
}

bootstrap();
