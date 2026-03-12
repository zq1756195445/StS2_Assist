export function currentSnapshotLocale(snapshot, fallbackLocale) {
  return snapshot.locale || fallbackLocale;
}

export function deriveMode(snapshot) {
  const scene = snapshot.overlay?.scene;
  const gameState = snapshot.gameState;

  if (scene === "map-overlay" || scene === "map") {
    return "map-overlay";
  }
  if (scene === "shop" || gameState.map.currentNode === "Shop") {
    return "shop";
  }
  if (scene === "battle" || gameState.hand.length > 0 || gameState.battle.enemies.length > 0) {
    return "battle";
  }
  if (
    ["reward", "event", "rest", "treasure"].includes(scene) ||
    (gameState.rewards.cards || []).length > 0
  ) {
    return "choice";
  }
  if (["Rest", "Treasure", "Start", "Unknown", "Event"].includes(gameState.map.currentNode)) {
    return "choice";
  }
  return "unknown";
}
