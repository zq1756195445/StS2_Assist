export const LOCALE_KEY = "spire-guide-locale";

export const appState = {
  currentLocale: loadStoredLocale(),
  historyOpen: false,
  refreshInFlight: false,
  debugEntries: [],
  lastSnapshot: null
};

export function loadStoredLocale() {
  const stored = window.localStorage.getItem(LOCALE_KEY);
  return stored === "zh-cn" ? "zh-cn" : "en-us";
}

export function setCurrentLocale(locale) {
  appState.currentLocale = locale;
}

export function setHistoryOpenState(open) {
  appState.historyOpen = open;
}

export function setRefreshInFlight(inFlight) {
  appState.refreshInFlight = inFlight;
}

export function setLastSnapshot(snapshot) {
  appState.lastSnapshot = snapshot;
}

export function pushDebugEntry(message, payload) {
  const timestamp = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  appState.debugEntries.unshift({ timestamp, message, payload });
  appState.debugEntries = appState.debugEntries.slice(0, 80);
}
