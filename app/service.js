const { MockGameReader } = require("../core/game_reader/mock_reader");
const { createDatabase } = require("../database");
const { generateRecommendations } = require("../strategy/recommendation_engine");

class SpireGuideService {
  constructor() {
    this.reader = new MockGameReader();
    this.database = createDatabase();
  }

  snapshot() {
    const gameState = this.reader.read();
    const recommendations = generateRecommendations(gameState, this.database);

    return {
      gameState,
      recommendations,
      overlay: buildOverlayLayout(gameState, recommendations)
    };
  }
}

function buildOverlayLayout(gameState, recommendations) {
  const enemy = gameState.battle.enemies[0];
  const topReward = recommendations.cardRewards[0];

  return {
    anchors: [
      {
        id: "enemy-intent",
        title: enemy ? `${enemy.name} 意图` : "战斗信息",
        body: enemy ? `${enemy.intent}，建议优先看 ${recommendations.turnSuggestion[0] || "当前手牌"}。` : "暂无战斗目标。",
        x: 0.08,
        y: 0.12,
        tone: "danger"
      },
      {
        id: "hand-play",
        title: "出牌顺序",
        body: recommendations.turnSuggestion.join(" -> "),
        x: 0.18,
        y: 0.64,
        tone: "info"
      },
      {
        id: "card-reward",
        title: "选牌建议",
        body: topReward ? `${topReward.cardName} (${topReward.score.toFixed(1)})` : "暂无奖励",
        x: 0.74,
        y: 0.72,
        tone: "accent"
      },
      {
        id: "map-route",
        title: "路线建议",
        body: recommendations.pathRecommendation.route.join(" -> "),
        x: 0.72,
        y: 0.24,
        tone: "good"
      }
    ]
  };
}

module.exports = {
  SpireGuideService
};
