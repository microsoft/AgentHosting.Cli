const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { createHash } = require("node:crypto");

function canonical(file) {
  let resolved = path.resolve(file);
  try { resolved = fs.realpathSync(resolved); } catch {}
  return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}

function findPluginRoot(start) {
  let directory = path.resolve(start);
  while (true) {
    for (const manifest of [
      path.join(".claude-plugin", "plugin.json"), "plugin.json", "gemini-extension.json",
    ]) {
      try {
        const doc = JSON.parse(fs.readFileSync(path.join(directory, manifest), "utf8"));
        if (doc.name === "msagent-assistant") return directory;
      } catch {}
    }
    const parent = path.dirname(directory);
    if (parent === directory) return path.resolve(start);
    directory = parent;
  }
}

function resolvePluginPaths({ start = __dirname, env = process.env, home = os.homedir() } = {}) {
  const pluginRoot = findPluginRoot(start);
  const bindings = [
    ["MCS_ASSISTANT_PLUGIN_ROOT", "MCS_ASSISTANT_PLUGIN_DATA"],
    ["CLAUDE_PLUGIN_ROOT", "CLAUDE_PLUGIN_DATA"],
    ["COPILOT_PLUGIN_ROOT", "COPILOT_PLUGIN_DATA"],
  ];
  const matching = bindings.filter(([root, data]) =>
    env[data]?.trim() && env[root]?.trim() && canonical(env[root]) === canonical(pluginRoot));
  const unbound = bindings.filter(([root, data]) => env[data]?.trim() && !env[root]?.trim());
  const candidates = matching.length ? matching : unbound;
  const directories = [...new Set(candidates.map(([, data]) => canonical(env[data])))];
  if (directories.length > 1) {
    throw new Error("Conflicting plugin data directories; set only the current installation's root/data pair.");
  }
  const identity = createHash("sha256").update(canonical(pluginRoot)).digest("hex").slice(0, 20);
  const pluginData = candidates.length
    ? path.resolve(env[candidates[0][1]])
    : path.join(home, ".copilot-studio-cli", "msagent-assistant", "instances", identity);
  return { pluginRoot, pluginData };
}

module.exports = { findPluginRoot, resolvePluginPaths };
