var __getOwnPropNames = Object.getOwnPropertyNames;
var __commonJS = (cb, mod) => function __require() {
  try {
    return mod || (0, cb[__getOwnPropNames(cb)[0]])((mod = { exports: {} }).exports, mod), mod.exports;
  } catch (e) {
    throw mod = 0, e;
  }
};

// src/plugin-paths.js
var require_plugin_paths = __commonJS({
  "src/plugin-paths.js"(exports2, module2) {
    var fs2 = require("node:fs");
    var os = require("node:os");
    var path2 = require("node:path");
    var { createHash } = require("node:crypto");
    function canonical(file) {
      let resolved = path2.resolve(file);
      try {
        resolved = fs2.realpathSync(resolved);
      } catch {
      }
      return process.platform === "win32" ? resolved.toLowerCase() : resolved;
    }
    function findPluginRoot(start) {
      let directory = path2.resolve(start);
      while (true) {
        for (const manifest of [
          path2.join(".claude-plugin", "plugin.json"),
          "plugin.json",
          "gemini-extension.json"
        ]) {
          try {
            const doc = JSON.parse(fs2.readFileSync(path2.join(directory, manifest), "utf8"));
            if (doc.name === "mcs-assistant") return directory;
          } catch {
          }
        }
        const parent = path2.dirname(directory);
        if (parent === directory) return path2.resolve(start);
        directory = parent;
      }
    }
    function resolvePluginPaths2({ start = __dirname, env = process.env, home = os.homedir() } = {}) {
      const pluginRoot2 = findPluginRoot(start);
      const bindings = [
        ["MCS_ASSISTANT_PLUGIN_ROOT", "MCS_ASSISTANT_PLUGIN_DATA"],
        ["CLAUDE_PLUGIN_ROOT", "CLAUDE_PLUGIN_DATA"],
        ["COPILOT_PLUGIN_ROOT", "COPILOT_PLUGIN_DATA"]
      ];
      const matching = bindings.filter(([root, data]) => env[data]?.trim() && env[root]?.trim() && canonical(env[root]) === canonical(pluginRoot2));
      const unbound = bindings.filter(([root, data]) => env[data]?.trim() && !env[root]?.trim());
      const candidates = matching.length ? matching : unbound;
      const directories = [...new Set(candidates.map(([, data]) => canonical(env[data])))];
      if (directories.length > 1) {
        throw new Error("Conflicting plugin data directories; set only the current installation's root/data pair.");
      }
      const identity = createHash("sha256").update(canonical(pluginRoot2)).digest("hex").slice(0, 20);
      const pluginData2 = candidates.length ? path2.resolve(env[candidates[0][1]]) : path2.join(home, ".copilot-studio-cli", "mcs-assistant", "instances", identity);
      return { pluginRoot: pluginRoot2, pluginData: pluginData2 };
    }
    module2.exports = { findPluginRoot, resolvePluginPaths: resolvePluginPaths2 };
  }
});

// src/set-env-vars.js
var fs = require("node:fs");
var cp = require("node:child_process");
var path = require("node:path");
var { resolvePluginPaths } = require_plugin_paths();
var { pluginRoot, pluginData } = resolvePluginPaths({ start: path.resolve(__dirname, "..") });
var environmentFile = process.env.CLAUDE_ENV_FILE;
try {
  const manifest = fs.readFileSync(path.join(pluginRoot, "scripts", "native-deps.json"), "utf8");
  const marker = path.join(pluginData, "native-deps.installed.json");
  const dependencies = Object.keys(JSON.parse(manifest).dependencies);
  const installed = dependencies.every((name) => fs.existsSync(path.join(pluginData, "node_modules", name, "package.json")));
  if (!installed || !fs.existsSync(marker) || fs.readFileSync(marker, "utf8") !== manifest) {
    fs.mkdirSync(pluginData, { recursive: true });
    fs.writeFileSync(path.join(pluginData, "package.json"), manifest);
    cp.execSync("npm install --no-audit --no-fund", {
      cwd: pluginData,
      stdio: ["ignore", "ignore", "inherit"]
    });
    fs.writeFileSync(marker, manifest);
  }
} catch (error) {
  console.error("[copilot-studio] optional encrypted Graph cache unavailable; using in-memory tokens: " + error.message);
}
if (environmentFile) {
  const quote = (value) => "'" + value.replace(/'/g, "'\\''") + "'";
  fs.appendFileSync(
    environmentFile,
    `export MCS_ASSISTANT_PLUGIN_DATA=${quote(pluginData)}
export MCS_ASSISTANT_PLUGIN_ROOT=${quote(pluginRoot)}
`
  );
}
