const { execFile } = require("node:child_process");

const TARGET_PROCESS_NAME = "Slay the Spire 2";

function getTargetWindowBounds() {
  return new Promise((resolve) => {
    const script = `
      tell application "System Events"
        if not (exists process "${TARGET_PROCESS_NAME}") then
          return ""
        end if
        tell process "${TARGET_PROCESS_NAME}"
          if (count of windows) is 0 then
            return ""
          end if
          set {xPos, yPos} to position of front window
          set {winWidth, winHeight} to size of front window
          return (xPos as string) & "," & (yPos as string) & "," & (winWidth as string) & "," & (winHeight as string)
        end tell
      end tell
    `;

    execFile("osascript", ["-e", script], (error, stdout) => {
      if (error) {
        resolve(null);
        return;
      }

      const value = String(stdout || "").trim();
      if (!value) {
        resolve(null);
        return;
      }

      const [x, y, width, height] = value.split(",").map(Number);
      if ([x, y, width, height].some((item) => !Number.isFinite(item))) {
        resolve(null);
        return;
      }

      resolve({ x, y, width, height });
    });
  });
}

module.exports = {
  getTargetWindowBounds
};
