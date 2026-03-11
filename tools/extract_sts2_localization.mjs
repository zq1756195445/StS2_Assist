import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const defaultPckPath = "G:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\SlayTheSpire2.pck";
const pckPath = process.env.STS2_PCK_PATH || defaultPckPath;
const toolPath =
  process.env.GODOT_PCK_TOOL ||
  path.join(repoRoot, "tools", "godotpcktool", "godotpcktool.exe");
const outputDir = path.join(repoRoot, "database", "sts2-localization");
const includeRegex = "^localization/(eng|zhs)/.*\\.json$";
const coverageFiles = ["cards", "relics", "monsters", "bestiary", "intents"];

function assertFileExists(filePath, label) {
  if (!fs.existsSync(filePath)) {
    throw new Error(`${label} not found: ${filePath}`);
  }
}

function readJson(relativePath) {
  return JSON.parse(fs.readFileSync(path.join(outputDir, relativePath), "utf8"));
}

function buildCoverageReport() {
  const report = {
    generatedAt: new Date().toISOString(),
    pckPath,
    toolPath,
    languages: {},
    coverage: {}
  };

  for (const lang of ["eng", "zhs"]) {
    report.languages[lang] = {};

    for (const file of coverageFiles) {
      const data = readJson(path.join("localization", lang, `${file}.json`));
      report.languages[lang][file] = Object.keys(data).length;
    }
  }

  for (const file of coverageFiles) {
    const eng = readJson(path.join("localization", "eng", `${file}.json`));
    const zhs = readJson(path.join("localization", "zhs", `${file}.json`));
    const engKeys = Object.keys(eng).sort();
    const zhsKeys = Object.keys(zhs).sort();
    const missingInZhs = engKeys.filter((key) => !(key in zhs));
    const missingInEng = zhsKeys.filter((key) => !(key in eng));

    report.coverage[file] = {
      eng: engKeys.length,
      zhs: zhsKeys.length,
      missingInZhs,
      missingInEng
    };
  }

  return report;
}

assertFileExists(toolPath, "GodotPckTool");
assertFileExists(pckPath, "Slay the Spire 2 .pck");

fs.mkdirSync(outputDir, { recursive: true });

execFileSync(
  toolPath,
  ["-p", pckPath, "-a", "extract", "-o", outputDir, "-i", includeRegex, "-q"],
  {
    stdio: "inherit"
  }
);

const report = buildCoverageReport();
fs.writeFileSync(
  path.join(outputDir, "report.json"),
  `${JSON.stringify(report, null, 2)}\n`,
  "utf8"
);

console.log(`Localization extracted to ${outputDir}`);
console.log(`Coverage report written to ${path.join(outputDir, "report.json")}`);
