const fs = require("node:fs");
const path = require("node:path");

function loadJsonFile(fileName) {
  const filePath = path.join(__dirname, fileName);
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function createDatabase() {
  return {
    cards: loadJsonFile("cards.json"),
    relics: loadJsonFile("relics.json"),
    archetypes: loadJsonFile("archetypes.json")
  };
}

module.exports = {
  createDatabase
};
