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
    var { createHash: createHash2 } = require("node:crypto");
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
      const identity = createHash2("sha256").update(canonical(pluginRoot2)).digest("hex").slice(0, 20);
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
var { createHash } = require("node:crypto");
var { resolvePluginPaths } = require_plugin_paths();
var { pluginRoot, pluginData } = resolvePluginPaths({ start: path.resolve(__dirname, "..") });
var environmentFile = process.env.CLAUDE_ENV_FILE;
function installNativeDependencies(directory) {
  return new Promise((resolve, reject) => {
    const windows = process.platform === "win32";
    const taskkill = windows ? path.join(process.env.SystemRoot, "System32", "taskkill.exe") : null;
    const timeoutMs = 15e3;
    const child = cp.spawn("npm ci --no-audit --no-fund --fetch-retries=0 --fetch-timeout=10000", {
      cwd: directory,
      shell: true,
      detached: !windows,
      windowsHide: true,
      stdio: ["ignore", "ignore", "pipe"]
    });
    let timer;
    let settled = false;
    const finish = (error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      child.stderr.destroy();
      if (error) reject(error);
      else resolve();
    };
    child.stderr.on("data", (chunk) => process.stderr.write(chunk));
    child.once("error", finish);
    child.once("close", (code, signal) => finish(code === 0 ? null : new Error(`npm ci exited with ${signal ? `signal ${signal}` : `code ${code}`}`)));
    timer = setTimeout(() => {
      let error = new Error(`npm ci timed out after ${timeoutMs} ms`);
      try {
        if (windows) {
          cp.execFileSync(taskkill, ["/PID", String(child.pid), "/T", "/F"], {
            stdio: ["ignore", "ignore", "pipe"],
            timeout: 5e3,
            windowsHide: true
          });
        } else {
          process.kill(-child.pid, "SIGKILL");
        }
      } catch (failure) {
        if (failure.code !== "ESRCH") {
          error = new Error(`${error.message}; process-tree cleanup failed: ${failure.message}`);
          try {
            child.kill("SIGKILL");
          } catch (rootFailure) {
            error = new Error(`${error.message}; installer cleanup failed: ${rootFailure.message}`);
          }
          child.unref();
        }
      }
      finish(error);
    }, timeoutMs);
  });
}
async function setup() {
  try {
    const manifest = fs.readFileSync(path.join(pluginRoot, "scripts", "native-deps.json"), "utf8");
    const lockfile = fs.readFileSync(path.join(pluginRoot, "scripts", "native-deps-lock.json"), "utf8");
    const installation = JSON.stringify({
      sha256: createHash("sha256").update(manifest).update("\0").update(lockfile).digest("hex")
    });
    const marker = path.join(pluginData, "native-deps.installed.json");
    const dependencies = Object.keys(JSON.parse(manifest).dependencies);
    const installed = dependencies.every((name) => fs.existsSync(path.join(pluginData, "node_modules", name, "package.json")));
    if (!installed || !fs.existsSync(marker) || fs.readFileSync(marker, "utf8") !== installation) {
      fs.mkdirSync(pluginData, { recursive: true });
      fs.writeFileSync(path.join(pluginData, "package.json"), manifest);
      fs.writeFileSync(path.join(pluginData, "package-lock.json"), lockfile);
      await installNativeDependencies(pluginData);
      fs.writeFileSync(marker, installation);
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
}
setup().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
