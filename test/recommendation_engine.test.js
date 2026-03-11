const test = require("node:test");
const assert = require("node:assert/strict");

const { createDatabase } = require("../database");
const { normalizeGameState } = require("../core/state_parser/game_state");
const { generateRecommendations } = require("../strategy/recommendation_engine");

test("recommendation engine prioritizes Catalyst in poison deck", () => {
  const database = createDatabase();
  const gameState = normalizeGameState({
    character: "Silent",
    player: { hp: 50, maxHp: 70, gold: 99, energy: 3 },
    deck: ["Deadly Poison", "Bouncing Flask", "Backflip", "Defend", "Strike"],
    relics: ["Bag of Preparation"],
    hand: ["Deadly Poison", "Backflip", "Defend"],
    map: { act: 1, currentNode: "Battle", upcomingNodes: ["Elite", "Rest", "Shop"] },
    rewards: { cards: ["Catalyst", "Backflip", "Slice"] }
  });

  const result = generateRecommendations(gameState, database);

  assert.equal(result.cardRewards[0].cardName, "Catalyst");
  assert.equal(result.deckAnalysis.archetypes[0].key, "poison");
});

test("path planner avoids elites on low HP", () => {
  const database = createDatabase();
  const gameState = normalizeGameState({
    character: "Silent",
    player: { hp: 18, maxHp: 70, gold: 110, energy: 3 },
    deck: ["Footwork", "Backflip", "Defend", "Defend"],
    relics: [],
    hand: ["Footwork", "Backflip"],
    map: { act: 1, currentNode: "Battle", upcomingNodes: ["Elite", "Rest", "Shop"] },
    rewards: { cards: ["Backflip", "Slice"] }
  });

  const result = generateRecommendations(gameState, database);

  assert.equal(result.pathRecommendation.route[0], "Rest");
});
