function setText(id, value) {
  document.getElementById(id).textContent = value;
}

function setWindowMode(mode) {
  const sidebar = document.getElementById("sidebar");
  setText("window-mode", mode.attachedToGame ? "Attached To Game" : "Detached Fallback");
  sidebar.classList.toggle("attached", Boolean(mode.attachedToGame));
}

function renderList(id, items, renderItem) {
  const list = document.getElementById(id);
  list.innerHTML = "";

  for (const item of items) {
    const li = document.createElement("li");
    li.innerHTML = renderItem(item);
    list.appendChild(li);
  }
}

function renderAnchors(overlay) {
  const layer = document.getElementById("anchor-layer");
  layer.innerHTML = "";

  for (const anchor of overlay.anchors || []) {
    const card = document.createElement("article");
    card.className = "anchor-card";
    card.dataset.tone = anchor.tone || "info";
    card.style.left = `${Math.round(anchor.x * 100)}%`;
    card.style.top = `${Math.round(anchor.y * 100)}%`;
    card.innerHTML = `
      <div class="anchor-title">${anchor.title}</div>
      <div class="anchor-body">${anchor.body}</div>
    `;
    layer.appendChild(card);
  }
}

function renderSnapshot(snapshot) {
  const { gameState, recommendations, overlay } = snapshot;
  const enemy = gameState.battle.enemies[0];

  setText("encounter", enemy ? `${enemy.name} • ${enemy.intent}` : "No active battle");
  setText(
    "player-summary",
    `HP ${gameState.player.hp}/${gameState.player.maxHp} • Gold ${gameState.player.gold} • Energy ${gameState.player.energy}`
  );
  setText("deck-score", `Power ${recommendations.deckAnalysis.score}`);
  setText(
    "path-route",
    recommendations.pathRecommendation.route.join(" -> ")
  );
  setText("path-reason", recommendations.pathRecommendation.reason);

  renderList(
    "card-rewards",
    recommendations.cardRewards,
    (item) => `<strong>${item.cardName}</strong> <span>(${item.score.toFixed(1)})</span><br /><span class="muted">${item.reason}</span>`
  );

  renderList(
    "archetypes",
    recommendations.deckAnalysis.archetypes,
    (item) => `${item.label} ${item.score}`
  );

  renderList(
    "relic-suggestions",
    recommendations.relicSuggestions,
    (item) => `<strong>${item.relicName}</strong><br /><span class="muted">${item.suggestion}</span>`
  );
  renderAnchors(overlay);
}

async function bootstrap() {
  const initialState = await window.spireGuide.getSnapshot();
  renderSnapshot(initialState);
  window.spireGuide.subscribe(renderSnapshot);
  window.spireGuide.onWindowMode(setWindowMode);
}

bootstrap();
