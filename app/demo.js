const { SpireGuideService } = require("./service");

const service = new SpireGuideService();
const snapshot = service.snapshot();

console.log(JSON.stringify(snapshot, null, 2));
