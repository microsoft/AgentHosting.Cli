const fs = require("node:fs");
const cp = require("node:child_process");
const path = require("node:path");
const { resolvePluginPaths } = require("./plugin-paths");

const { pluginRoot, pluginData } = resolvePluginPaths({ start: path.resolve(__dirname, "..") });
const environmentFile = process.env.CLAUDE_ENV_FILE;

function installNativeDependencies(directory) {
  return new Promise((resolve, reject) => {
    const windows = process.platform === "win32";
    const taskkill = windows ? path.join(process.env.SystemRoot, "System32", "taskkill.exe") : null;
    const timeoutMs = 15000;
    const child = cp.spawn("npm install --no-audit --no-fund --fetch-retries=0 --fetch-timeout=10000", {
      cwd: directory, shell: true, detached: !windows, windowsHide: true,
      stdio: ["ignore", "ignore", "pipe"],
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
    child.once("close", (code, signal) => finish(code === 0
      ? null : new Error(`npm install exited with ${signal ? `signal ${signal}` : `code ${code}`}`)));
    timer = setTimeout(() => {
      let error = new Error(`npm install timed out after ${timeoutMs} ms`);
      try {
        if (windows) {
          cp.execFileSync(taskkill, ["/PID", String(child.pid), "/T", "/F"], {
            stdio: ["ignore", "ignore", "pipe"], timeout: 5000, windowsHide: true,
          });
        } else {
          process.kill(-child.pid, "SIGKILL");
        }
      } catch (failure) {
        if (failure.code !== "ESRCH") {
          error = new Error(`${error.message}; process-tree cleanup failed: ${failure.message}`);
          try { child.kill("SIGKILL"); }
          catch (rootFailure) { error = new Error(`${error.message}; installer cleanup failed: ${rootFailure.message}`); }
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
    const marker = path.join(pluginData, "native-deps.installed.json");
    const dependencies = Object.keys(JSON.parse(manifest).dependencies);
    const installed = dependencies.every((name) =>
      fs.existsSync(path.join(pluginData, "node_modules", name, "package.json")));
    if (!installed || !fs.existsSync(marker) || fs.readFileSync(marker, "utf8") !== manifest) {
      fs.mkdirSync(pluginData, { recursive: true });
      fs.writeFileSync(path.join(pluginData, "package.json"), manifest);
      await installNativeDependencies(pluginData);
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
}

setup().catch((error) => { console.error(error); process.exitCode = 1; });
