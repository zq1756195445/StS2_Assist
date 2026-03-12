import "./styles.css";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

const LOCALE_KEY = "spire-guide-locale";

const UI = {
  "en-us": {
    history: "History",
    close: "Close",
    waiting: "Waiting for state...",
    noAdvice: "No recommendation yet",
    playOrder: "Recommended Play Order",
    playMode: "Battle",
    choiceTitle: "Recommended Choice",
    choiceMode: "Choice",
    shopTitle: "Recommended Purchase",
    shopMode: "Shop",
    mapTitle: "Map Open",
    mapMode: "Map",
    mapBody: "HUD is minimized while the map overlay is open.",
    unknownTitle: "Waiting",
    unknownMode: "Idle",
    hp: "HP",
    energy: "Energy",
    gold: "Gold",
    act: "Act",
    skip: "Skip",
    source: "Source",
    currentPage: "Current Page",
    recentActions: "Recent Actions",
    debugLog: "Debug Log",
    backendDebug: "Backend Debug",
    probeStdout: "Probe Stdout",
    probeStderr: "Probe Stderr",
    rawSnapshot: "Raw Snapshot",
    noPage: "No page tracked",
    noActions: "No actions tracked",
    hotkeys: [
      ["F6", "History"],
      ["F7", "Language"],
      ["F8", "HUD"],
      ["F10", "Exit"]
    ]
  },
  "zh-cn": {
    history: "历史",
    close: "关闭",
    waiting: "等待状态中...",
    noAdvice: "暂时没有建议",
    playOrder: "推荐出牌顺序",
    playMode: "战斗",
    choiceTitle: "推荐选项",
    choiceMode: "选择",
    shopTitle: "推荐购买",
    shopMode: "商店",
    mapTitle: "地图已打开",
    mapMode: "地图",
    mapBody: "地图覆盖层打开时，HUD 会保持极简显示。",
    unknownTitle: "等待中",
    unknownMode: "空闲",
    hp: "生命",
    energy: "能量",
    gold: "金币",
    act: "第",
    skip: "跳过",
    source: "来源",
    currentPage: "当前页面",
    recentActions: "最近动作",
    debugLog: "调试日志",
    backendDebug: "后端调试",
    probeStdout: "Probe 标准输出",
    probeStderr: "Probe 错误输出",
    rawSnapshot: "原始快照",
    noPage: "暂无页面信息",
    noActions: "暂无动作记录",
    hotkeys: [
      ["F6", "历史"],
      ["F7", "语言"],
      ["F8", "显隐"],
      ["F10", "退出"]
    ]
  }
};

let currentLocale = loadStoredLocale();
let historyOpen = false;
let refreshInFlight = false;
let debugEntries = [];
let lastSnapshot = null;

function loadStoredLocale() {
  const stored = window.localStorage.getItem(LOCALE_KEY);
  return stored === "zh-cn" ? "zh-cn" : "en-us";
}

function ui() {
  return UI[currentLocale];
}

function setText(id, value) {
  const node = document.getElementById(id);
  if (node) {
    node.textContent = value ?? "";
  }
}

function setHtml(id, value) {
  const node = document.getElementById(id);
  if (node) {
    node.innerHTML = value ?? "";
  }
}

function renderList(id, items, renderItem) {
  const list = document.getElementById(id);
  if (!list) {
    return;
  }
  list.innerHTML = "";
  for (const item of items) {
    const li = document.createElement("li");
    li.innerHTML = renderItem(item);
    list.appendChild(li);
  }
}

function pushDebug(message, payload) {
  const timestamp = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  debugEntries.unshift({ timestamp, message, payload });
  debugEntries = debugEntries.slice(0, 80);
  renderDebug();
}

function applyStaticLabels() {
  document.documentElement.lang = currentLocale;
  setText("history-title", ui().history);
  setText("history-close", ui().close);
  setText("history-actions-title", ui().recentActions);
  setText("debug-log-title", ui().debugLog);
  setText("backend-debug-title", ui().backendDebug);
  setText("probe-stdout-title", ui().probeStdout);
  setText("probe-stderr-title", ui().probeStderr);
  setText("debug-json-title", ui().rawSnapshot);
  setHtml(
    "hud-hotkeys",
    ui().hotkeys
      .map(
        ([key, label]) =>
          `<span class="hotkey-chip"><strong>${key}</strong><span>${label}</span></span>`
      )
      .join("")
  );
}

function currentSnapshotLocale(snapshot) {
  return snapshot.locale || currentLocale;
}

function deriveMode(snapshot) {
  const scene = snapshot.overlay?.scene;
  const gameState = snapshot.gameState;

  if (scene === "map-overlay") {
    return "map-overlay";
  }
  if (scene === "shop" || gameState.map.currentNode === "Shop") {
    return "shop";
  }
  if (scene === "battle-like" || gameState.hand.length > 0 || gameState.battle.enemies.length > 0) {
    return "battle";
  }
  if (scene === "choice-like") {
    return "choice";
  }
  if (["Rest", "Treasure", "Start", "Unknown", "Event"].includes(gameState.map.currentNode)) {
    return "choice";
  }
  return "unknown";
}

function buildBattleCard(snapshot) {
  const { gameState, recommendations } = snapshot;
  const steps =
    recommendations.turnSuggestion && recommendations.turnSuggestion.length > 0
      ? recommendations.turnSuggestion
      : gameState.hand.slice(0, 3);
  const enemy = gameState.battle.enemies[0];
  const metaParts = [
    `${ui().hp} ${gameState.player.hp}/${gameState.player.maxHp}`,
    `${ui().energy} ${gameState.player.energy}`
  ];
  if (enemy) {
    metaParts.push(enemy.name);
    if (enemy.intent) {
      metaParts.push(enemy.intent);
    }
  }

  return {
    visible: true,
    mode: ui().playMode,
    title: ui().playOrder,
    body: steps.length > 0 ? steps.join(" -> ") : ui().noAdvice,
    meta: metaParts.join(" · ")
  };
}

function buildChoiceCard(snapshot) {
  const { gameState, recommendations } = snapshot;
  const topReward = recommendations.cardRewards?.[0];
  const title = ui().choiceTitle;
  let body = ui().noAdvice;

  if (topReward) {
    body = `${topReward.cardName} (${topReward.score.toFixed(1)})`;
  } else if ((gameState.rewards.cards || []).length > 0) {
    body = `${gameState.rewards.cards.join(" / ")} / ${ui().skip}`;
  } else if (recommendations.relicSuggestions?.[0]) {
    const relic = recommendations.relicSuggestions[0];
    body = `${relic.relicName} - ${relic.suggestion}`;
  }

  return {
    visible: true,
    mode: ui().choiceMode,
    title,
    body,
    meta: `${ui().act} ${snapshot.gameState.map.act} · ${snapshot.gameState.map.currentNode}`
  };
}

function buildShopCard(snapshot) {
  const topShop = snapshot.recommendations.relicSuggestions?.[0];
  return {
    visible: true,
    mode: ui().shopMode,
    title: ui().shopTitle,
    body: topShop ? `${topShop.relicName} - ${topShop.suggestion}` : ui().noAdvice,
    meta: `${ui().gold} ${snapshot.gameState.player.gold}`
  };
}

function buildMapCard() {
  return {
    visible: true,
    mode: ui().mapMode,
    title: ui().mapTitle,
    body: ui().mapBody,
    meta: ""
  };
}

function buildUnknownCard(snapshot) {
  return {
    visible: false,
    mode: ui().unknownMode,
    title: ui().unknownTitle,
    body: ui().waiting,
    meta: `${ui().source} ${snapshot.source || ""}`.trim()
  };
}

function renderPrimaryCard(snapshot) {
  const mode = deriveMode(snapshot);
  const card =
    mode === "battle"
      ? buildBattleCard(snapshot)
      : mode === "choice"
        ? buildChoiceCard(snapshot)
        : mode === "shop"
          ? buildShopCard(snapshot)
          : mode === "map-overlay"
            ? buildMapCard()
            : buildUnknownCard(snapshot);

  const cardNode = document.getElementById("primary-card");
  cardNode.hidden = !card.visible;
  document.documentElement.dataset.mode = mode;
  if (!card.visible) {
    return;
  }

  setText("primary-mode", card.mode);
  setText("primary-title", card.title);
  setText("primary-body", card.body);
  setText("primary-meta", card.meta);
}

function renderHistory(snapshot) {
  const replay = snapshot.replay || {};
  const latestPage = replay.latestPage;

  setText(
    "history-source",
    `${replay.source || "disabled"} · ${replay.version || "-"} · ${replay.updatedAt || "-"}`
  );
  setText("history-summary", `${ui().source} ${snapshot.source || "-"}`);
  setText("history-page-title", latestPage?.eventTitle || ui().noPage);
  setText("history-page-context", latestPage?.contextTitle || "");

  renderList("history-options", latestPage?.options || [], (item) => item);

  const actions = [...(replay.recentActions || [])].reverse();
  renderList(
    "history-actions",
    actions.length > 0 ? actions : [ui().noActions],
    (item) =>
      typeof item === "string"
        ? item
        : `<strong>${item.title}</strong><span>${item.detail}</span>`
  );
}

function renderDebug() {
  renderList(
    "debug-log",
    debugEntries,
    (entry) =>
      `<strong>${entry.timestamp}</strong><span>${entry.message}</span>${
        entry.payload ? `<pre>${JSON.stringify(entry.payload, null, 2)}</pre>` : ""
      }`
  );
  const backendItems = [];
  if (lastSnapshot?.debug) {
    const debug = lastSnapshot.debug;
    const pairs = [
      ["lastRefreshSource", debug.lastRefreshSource],
      ["lastMemorySummary", debug.lastMemorySummary],
      ["lastGameStateSummary", debug.lastGameStateSummary],
      ["lastMergeSummary", debug.lastMergeSummary],
      ["lastProbeSummary", debug.lastProbeSummary]
    ];
    for (const [label, value] of pairs) {
      if (value) {
        backendItems.push(`<strong>${label}</strong><span>${value}</span>`);
      }
    }
    for (const entry of debug.entries || []) {
      backendItems.push(
        `<strong>${entry.stage} · ${entry.timestamp}</strong><span>${entry.message}</span>`
      );
    }
  }
  renderList(
    "backend-debug",
    backendItems.length > 0 ? backendItems : [ui().waiting],
    (item) => item
  );
  setText(
    "probe-stdout",
    lastSnapshot?.debug?.lastProbeStdout || ui().waiting
  );
  setText(
    "probe-stderr",
    lastSnapshot?.debug?.lastProbeStderr || ui().waiting
  );
  setText("debug-json", lastSnapshot ? JSON.stringify(lastSnapshot, null, 2) : ui().waiting);
}

function renderSnapshot(snapshot) {
  currentLocale = currentSnapshotLocale(snapshot);
  lastSnapshot = snapshot;
  applyStaticLabels();
  renderPrimaryCard(snapshot);
  renderHistory(snapshot);
  renderDebug();
  pushDebug("snapshot-updated", {
    scene: snapshot.overlay?.scene,
    mode: deriveMode(snapshot),
    hand: snapshot.gameState?.hand?.length || 0,
    enemies: snapshot.gameState?.battle?.enemies?.length || 0,
    rewards: snapshot.gameState?.rewards?.cards?.length || 0,
    source: snapshot.source
  });
}

async function setHistoryOpen(nextOpen) {
  historyOpen = nextOpen;
  document.getElementById("history-panel").hidden = !nextOpen;
  await invoke("set_overlay_interactive", { interactive: nextOpen });
}

async function persistLocale(locale) {
  currentLocale = locale;
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

async function refresh() {
  if (refreshInFlight) {
    return;
  }

  refreshInFlight = true;
  pushDebug("refresh-start");
  try {
    const snapshot = await invoke("get_snapshot");
    renderSnapshot(snapshot);
  } finally {
    refreshInFlight = false;
    pushDebug("refresh-end");
  }
}

async function syncWindowMode() {
  await invoke("sync_overlay_window");
}

async function bootstrap() {
  applyStaticLabels();
  wireHistoryControls();
  await persistLocale(currentLocale);
  await syncWindowMode();
  await listen("history-toggle-requested", () => {
    const nextOpen = !historyOpen;
    void setHistoryOpen(nextOpen);
    pushDebug("history-toggle-requested", { open: nextOpen });
  });
  await listen("locale-changed", async (event) => {
    await persistLocale(event.payload);
    pushDebug("locale-changed", { locale: event.payload });
  });
  await listen("snapshot-updated", (event) => {
    renderSnapshot(event.payload);
  });
  await refresh();
  window.addEventListener("resize", syncWindowMode);
}

bootstrap();
