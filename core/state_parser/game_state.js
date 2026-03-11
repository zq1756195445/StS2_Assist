function normalizePile(cards) {
  return Array.isArray(cards) ? cards.filter(Boolean) : [];
}

function normalizeGameState(rawState) {
  const player = rawState.player || {};
  const battle = rawState.battle || {};
  const map = rawState.map || {};

  return {
    timestamp: rawState.timestamp || new Date().toISOString(),
    character: rawState.character || "Silent",
    player: {
      hp: Number(player.hp || 0),
      maxHp: Number(player.maxHp || 0),
      gold: Number(player.gold || 0),
      energy: Number(player.energy || 0),
      potions: normalizePile(player.potions)
    },
    deck: normalizePile(rawState.deck),
    hand: normalizePile(rawState.hand),
    discardPile: normalizePile(rawState.discardPile),
    drawPile: normalizePile(rawState.drawPile),
    relics: normalizePile(rawState.relics),
    battle: {
      enemies: Array.isArray(battle.enemies) ? battle.enemies : []
    },
    map: {
      act: map.act || 1,
      currentNode: map.currentNode || "?",
      upcomingNodes: normalizePile(map.upcomingNodes)
    },
    rewards: {
      cards: normalizePile(rawState.rewards?.cards)
    }
  };
}

module.exports = {
  normalizeGameState
};
