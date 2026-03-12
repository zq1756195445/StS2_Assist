import { appState } from "./state.js";

export const UI = {
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
      ["F8", "显示"],
      ["F10", "退出"]
    ]
  }
};

export function ui() {
  return UI[appState.currentLocale];
}

export function setText(id, value) {
  const node = document.getElementById(id);
  if (node) {
    node.textContent = value ?? "";
  }
}

export function setHtml(id, value) {
  const node = document.getElementById(id);
  if (node) {
    node.innerHTML = value ?? "";
  }
}

export function renderList(id, items, renderItem) {
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

export function applyStaticLabels() {
  document.documentElement.lang = appState.currentLocale;
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
