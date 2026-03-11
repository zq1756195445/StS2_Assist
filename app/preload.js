const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("spireGuide", {
  getSnapshot: () => ipcRenderer.invoke("spireguide:snapshot"),
  onWindowMode: (callback) => {
    const listener = (_event, payload) => callback(payload);
    ipcRenderer.on("spireguide:window-mode", listener);
    return () => ipcRenderer.removeListener("spireguide:window-mode", listener);
  },
  subscribe: (callback) => {
    const listener = (_event, payload) => callback(payload);
    ipcRenderer.on("spireguide:update", listener);
    return () => ipcRenderer.removeListener("spireguide:update", listener);
  }
});
