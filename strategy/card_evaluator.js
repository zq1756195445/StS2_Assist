function makeCardsByName(database) {
  return new Map(database.cards.map((card) => [card.name.toLowerCase(), card]));
}

function evaluateCardReward(gameState, deckAnalysis, database) {
  const cardsByName = makeCardsByName(database);
  const primaryArchetype = deckAnalysis.archetypes[0]?.key || "block";

  return gameState.rewards.cards
    .map((cardName) => {
      const card = cardsByName.get(cardName.toLowerCase());
      if (!card) {
        return {
          cardName,
          score: 1,
          reason: "Unknown card in local database."
        };
      }

      const synergyScore = card.synergy[primaryArchetype] || 0;
      const deckSizePenalty = gameState.deck.length > 15 ? -0.5 : 0;
      const lowHpBlockBonus =
        gameState.player.hp / Math.max(1, gameState.player.maxHp) < 0.45 &&
        card.tags.includes("block")
          ? 1.5
          : 0;
      const total = card.baseScore + synergyScore + deckSizePenalty + lowHpBlockBonus;

      return {
        cardName,
        score: Number(total.toFixed(1)),
        reason: buildReason(card, primaryArchetype, lowHpBlockBonus)
      };
    })
    .sort((left, right) => right.score - left.score);
}

function buildReason(card, primaryArchetype, lowHpBlockBonus) {
  const reasons = [`fits ${primaryArchetype} plan`];

  if (card.tags.includes("scaling")) {
    reasons.push("improves long fights");
  }

  if (lowHpBlockBonus > 0) {
    reasons.push("stabilizes low HP run");
  }

  return reasons.join(", ");
}

module.exports = {
  evaluateCardReward
};
