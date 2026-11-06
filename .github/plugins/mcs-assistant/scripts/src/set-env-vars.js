const fs = require("node:fs");
const cp = require("node:child_process");
const path = require("node:path");
const { resolvePluginPaths } = require("./plugin-paths");

const { pluginRoot, pluginData } = resolvePluginPaths({ start: path.resolve(__dirname, "..") });
const environmentFile = process.env.CLAUDE_ENV_FILE;

try {
  const manifest = fs.readFileSync(path.join(pluginRoot, "scripts", "native-deps.json"), "utf8");
  const marker = path.join(pluginData, "native-deps.installed.json");
  const dependencies = Object.keys(JSON.parse(manifest).dependencies);
  const installed = dependencies.every((name) =>
    fs.existsSync(path.join(pluginData, "node_modules", name, "package.json")));
  if (!installed || !fs.existsSync(marker) || fs.readFileSync(marker, "utf8") !== manifest) {
    fs.mkdirSync(pluginData, { recursive: true });
    fs.writeFileSync(path.join(pluginData, "package.json"), manifest);
    cp.execSync("npm install --no-audit --no-fund", {
      cwd: pluginData, stdio: ["ignore", "ignore", "inherit"],
    });
    fs.writeFileSync(marker, manifest);
  }
} catch (error) {
  console.error("[copilot-studio] optional encrypted Graph cache unavailable; using in-memory tokens: " + error.message);
}

if (environmentFile) {
  const quote = (value) => "'" + value.replace(/'/g, "'\\''") + "'";
  fs.appendFileSync(environmentFile,
    `export MCS_ASSISTANT_PLUGIN_DATA=${quote(pluginData)}\n` +
    `export MCS_ASSISTANT_PLUGIN_ROOT=${quote(pluginRoot)}\n`);
}
