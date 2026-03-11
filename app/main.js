const path = require("node:path");
const { app, BrowserWindow, ipcMain, screen } = require("electron");
const { SpireGuideService } = require("./service");
const { getTargetWindowBounds } = require("./target_window");

const service = new SpireGuideService();
let windowTracker = null;

function getFallbackBounds() {
  const display = screen.getPrimaryDisplay();
  const workArea = display.workArea;
  return {
    width: 340,
    height: Math.min(860, workArea.height - 48),
    x: workArea.x + workArea.width - 340 - 24,
    y: workArea.y + 24
  };
}

function createWindow() {
  const fallbackBounds = getFallbackBounds();

  const win = new BrowserWindow({
    width: fallbackBounds.width,
    height: fallbackBounds.height,
    x: fallbackBounds.x,
    y: fallbackBounds.y,
    transparent: true,
    frame: false,
    alwaysOnTop: true,
    resizable: true,
    hasShadow: false,
    movable: true,
    focusable: false,
    skipTaskbar: true,
    webPreferences: {
      preload: path.join(__dirname, "preload.js")
    }
  });

  win.setMenuBarVisibility(false);
  win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  win.setAlwaysOnTop(true, "screen-saver");
  win.setIgnoreMouseEvents(true, { forward: true });
  win.loadFile(path.join(__dirname, "../overlay/ui/index.html"));

  const sendSnapshot = () => {
    win.webContents.send("spireguide:update", service.snapshot());
  };

  win.webContents.once("did-finish-load", sendSnapshot);
  setInterval(sendSnapshot, 5000);
  startWindowTracking(win);
}

function startWindowTracking(win) {
  const syncBounds = async () => {
    if (win.isDestroyed()) {
      return;
    }

    const targetBounds = await getTargetWindowBounds();
    const nextBounds = targetBounds || getFallbackBounds();
    win.setBounds(nextBounds, false);
    win.webContents.send("spireguide:window-mode", {
      attachedToGame: Boolean(targetBounds)
    });
  };

  syncBounds();
  windowTracker = setInterval(syncBounds, 2000);
}

app.whenReady().then(() => {
  ipcMain.handle("spireguide:snapshot", () => service.snapshot());
  createWindow();
});

app.on("window-all-closed", () => {
  if (windowTracker) {
    clearInterval(windowTracker);
    windowTracker = null;
  }
  if (process.platform !== "darwin") {
    app.quit();
  }
});
