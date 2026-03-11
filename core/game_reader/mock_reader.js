const { normalizeGameState } = require("../state_parser/game_state");

const mockStates = [
  {
    character: "Silent",
    player: { hp: 46, maxHp: 70, gold: 118, energy: 3, potions: ["Dex Potion"] },
    deck: [
      "Strike",
      "Strike",
      "Strike",
      "Defend",
      "Defend",
      "Defend",
      "Neutralize",
      "Survivor",
      "Deadly Poison",
      "Bouncing Flask",
      "Backflip"
    ],
    hand: ["Neutralize", "Backflip", "Defend", "Strike", "Deadly Poison"],
    discardPile: ["Strike"],
    drawPile: ["Bouncing Flask", "Defend", "Survivor", "Strike", "Defend"],
    relics: ["Bag of Preparation"],
    battle: {
      enemies: [
        { name: "Jaw Worm", hp: 38, intent: "Attack 12" }
      ]
    },
    map: {
      act: 1,
      currentNode: "Battle",
      upcomingNodes: ["Elite", "Rest", "Shop"]
    },
    rewards: {
      cards: ["Catalyst", "Backflip", "Slice"]
    }
  },
  {
    character: "Silent",
    player: { hp: 28, maxHp: 70, gold: 172, energy: 3, potions: ["Fire Potion"] },
    deck: [
      "Strike",
      "Strike",
      "Defend",
      "Defend",
      "Neutralize",
      "Survivor",
      "Blade Dance",
      "Cloak And Dagger",
      "Slice",
      "Backflip",
      "Footwork"
    ],
    hand: ["Blade Dance", "Footwork", "Strike", "Defend", "Backflip"],
    discardPile: ["Neutralize", "Survivor"],
    drawPile: ["Slice", "Defend", "Strike", "Cloak And Dagger"],
    relics: ["Kunai", "Oddly Smooth Stone"],
    battle: {
      enemies: [
        { name: "Gremlin Nob", hp: 85, intent: "Attack 14" }
      ]
    },
    map: {
      act: 1,
      currentNode: "Elite",
      upcomingNodes: ["Rest", "Shop", "Battle"]
    },
    rewards: {
      cards: ["Catalyst", "Backflip", "Slice"]
    }
  }
];

class MockGameReader {
  constructor() {
    this.cursor = 0;
  }

  read() {
    const nextState = mockStates[this.cursor % mockStates.length];
    this.cursor += 1;
    return normalizeGameState(nextState);
  }
}

module.exports = {
  MockGameReader
};
