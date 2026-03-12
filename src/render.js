import { appState } from "./state.js";
import { deriveMode } from "./mode.js";
import { applyStaticLabels, renderList, setText, ui } from "./ui.js";

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
    meta: metaParts.join(" / ")
  };
}

function buildChoiceCard(snapshot) {
  const { gameState, recommendations } = snapshot;
  const topReward = recommendations.cardRewards?.[0];
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
    title: ui().choiceTitle,
    body,
    meta: `${ui().act} ${snapshot.gameState.map.act} / ${snapshot.gameState.map.currentNode}`
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

export function renderPrimaryCard(snapshot) {
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

export function renderHistory(snapshot) {
  const replay = snapshot.replay || {};
  const latestPage = replay.latestPage;

  setText(
    "history-source",
    `${replay.source || "disabled"} / ${replay.version || "-"} / ${replay.updatedAt || "-"}`
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

export function renderDebug() {
  renderList(
    "debug-log",
    appState.debugEntries,
    (entry) =>
      `<strong>${entry.timestamp}</strong><span>${entry.message}</span>${
        entry.payload ? `<pre>${JSON.stringify(entry.payload, null, 2)}</pre>` : ""
      }`
  );

  const backendItems = [];
  if (appState.lastSnapshot?.debug) {
    const debug = appState.lastSnapshot.debug;
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
        `<strong>${entry.stage} / ${entry.timestamp}</strong><span>${entry.message}</span>`
      );
    }
  }

  renderList(
    "backend-debug",
    backendItems.length > 0 ? backendItems : [ui().waiting],
    (item) => item
  );
  setText("probe-stdout", appState.lastSnapshot?.debug?.lastProbeStdout || ui().waiting);
  setText("probe-stderr", appState.lastSnapshot?.debug?.lastProbeStderr || ui().waiting);
  setText(
    "debug-json",
    appState.lastSnapshot ? JSON.stringify(appState.lastSnapshot, null, 2) : ui().waiting
  );
}

export function renderSnapshot(snapshot) {
  appState.lastSnapshot = snapshot;
  applyStaticLabels();
  renderPrimaryCard(snapshot);
  renderHistory(snapshot);
  renderDebug();
}
