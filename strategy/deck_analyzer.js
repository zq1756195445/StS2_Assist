function countTags(deck, cardsByName) {
  const counts = {
    poison: 0,
    shiv: 0,
    block: 0,
    scaling: 0
  };

  for (const cardName of deck) {
    const card = cardsByName.get(cardName.toLowerCase());
    if (!card) {
      continue;
    }

    for (const tag of card.tags) {
      if (counts[tag] !== undefined) {
        counts[tag] += 1;
      }
    }
  }

  return counts;
}

function detectArchetypes(deckTagCounts) {
  return [
    { key: "poison", label: "Poison", score: deckTagCounts.poison * 2 + deckTagCounts.scaling },
    { key: "shiv", label: "Shiv", score: deckTagCounts.shiv * 2 },
    { key: "block", label: "Block Scaling", score: deckTagCounts.block + deckTagCounts.scaling }
  ]
    .filter((entry) => entry.score > 0)
    .sort((left, right) => right.score - left.score);
}

function computeDeckPowerScore(gameState, database) {
  const cardsByName = new Map(database.cards.map((card) => [card.name.toLowerCase(), card]));
  const tagCounts = countTags(gameState.deck, cardsByName);

  const offense = tagCounts.poison * 8 + tagCounts.shiv * 6 + gameState.deck.length * 1.5;
  const defense = tagCounts.block * 8 + gameState.player.hp * 0.35;
  const scaling = tagCounts.scaling * 12;
  const relicBonus = gameState.relics.length * 4;

  const total = Math.round(Math.min(100, offense + defense + scaling + relicBonus));

  return {
    score: total,
    tagCounts,
    archetypes: detectArchetypes(tagCounts)
  };
}

module.exports = {
  computeDeckPowerScore
};
