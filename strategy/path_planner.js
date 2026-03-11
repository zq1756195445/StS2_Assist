function recommendPath(gameState, deckAnalysis) {
  const hpRatio = gameState.player.hp / Math.max(1, gameState.player.maxHp);
  const powerScore = deckAnalysis.score;
  const path = [...gameState.map.upcomingNodes];

  if (hpRatio < 0.4) {
    return {
      route: prioritize(path, ["Rest", "Shop", "Battle", "Elite"]),
      reason: "Low HP: recover and spend before taking higher variance fights."
    };
  }

  if (powerScore >= 65) {
    return {
      route: prioritize(path, ["Elite", "Shop", "Rest", "Battle"]),
      reason: "Deck looks strong enough to convert elite fights into scaling rewards."
    };
  }

  return {
    route: prioritize(path, ["Battle", "Shop", "Rest", "Elite"]),
    reason: "Moderate deck strength: stabilize first, then take risk if rewards justify it."
  };
}

function prioritize(nodes, preferredOrder) {
  const rank = new Map(preferredOrder.map((node, index) => [node, index]));
  return nodes.sort((left, right) => (rank.get(left) ?? 99) - (rank.get(right) ?? 99));
}

module.exports = {
  recommendPath
};
