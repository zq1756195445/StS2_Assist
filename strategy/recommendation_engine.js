const { computeDeckPowerScore } = require("./deck_analyzer");
const { evaluateCardReward } = require("./card_evaluator");
const { recommendPath } = require("./path_planner");

function buildRelicSuggestions(gameState, database) {
  const relicsByName = new Map(database.relics.map((relic) => [relic.name.toLowerCase(), relic]));

  return gameState.relics
    .map((relicName) => relicsByName.get(relicName.toLowerCase()))
    .filter(Boolean)
    .map((relic) => ({
      relicName: relic.name,
      suggestion: relic.suggestion
    }));
}

function explainTurn(gameState, deckAnalysis) {
  const hand = new Set(gameState.hand);

  if (deckAnalysis.archetypes[0]?.key === "poison" && hand.has("Deadly Poison")) {
    return ["Deadly Poison", "Backflip", "Defend"];
  }

  if (deckAnalysis.archetypes[0]?.key === "shiv" && hand.has("Blade Dance")) {
    return ["Footwork", "Blade Dance", "Backflip"];
  }

  return gameState.hand.slice(0, 3);
}

function generateRecommendations(gameState, database) {
  const deckAnalysis = computeDeckPowerScore(gameState, database);
  const cardRewards = evaluateCardReward(gameState, deckAnalysis, database);
  const pathRecommendation = recommendPath(gameState, deckAnalysis);
  const relicSuggestions = buildRelicSuggestions(gameState, database);

  return {
    deckAnalysis,
    cardRewards,
    pathRecommendation,
    relicSuggestions,
    turnSuggestion: explainTurn(gameState, deckAnalysis)
  };
}

module.exports = {
  generateRecommendations
};
