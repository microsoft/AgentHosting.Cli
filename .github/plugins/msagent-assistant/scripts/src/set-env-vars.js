const fs = require("node:fs");
const cp = require("node:child_process");
const path = require("node:path");
const { createHash, randomUUID } = require("node:crypto");
const { resolvePluginPaths } = require("./plugin-paths");

const { pluginRoot, pluginData } = resolvePluginPaths({ start: path.resolve(__dirname, "..") });
const environmentFile = process.env.CLAUDE_ENV_FILE;

function releaseProvisioningLock(directory, owner) {
  if (owner) {
    try { fs.unlinkSync(path.join(directory, owner)); }
    catch (error) {
      if (error.code === "ENOENT") return;
      throw error;
    }
  }
  try { fs.rmdirSync(directory); }
  catch (error) {
    if (!["ENOENT", "ENOTEMPTY", "EEXIST"].includes(error.code)) throw error;
  }
}

function isProcessRunning(pid) {
  try { process.kill(pid, 0); return true; }
  catch (error) {
    if (error.code === "ESRCH") return false;
    if (error.code === "EPERM") return true;
    throw error;
  }
}

function acquireProvisioningLock() {
  const directory = path.join(pluginData, "native-deps.install.lock");
  const token = randomUUID();
  let owner = `${process.pid}-${token}`;
  const contender = `${directory}-${owner}`;
  fs.mkdirSync(contender);
  let acquired = false;
  let retained = false;
  let collision;
  try {
    fs.writeFileSync(path.join(contender, owner), "");
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        fs.renameSync(contender, directory);
        acquired = true;
        return {
          release() {
            if (!retained) releaseProvisioningLock(directory, owner);
          },
          beginInstall() {
            const next = `${owner}.cleanup-pending`;
            fs.renameSync(path.join(directory, owner), path.join(directory, next));
            owner = next;
          },
          trackInstaller(pid) {
            if (pid === undefined) return;
            if (!Number.isSafeInteger(pid) || pid <= 0) throw new Error("Invalid native dependency installer PID.");
            const next = `${process.pid}.${pid}-${token}.cleanup-pending`;
            fs.renameSync(path.join(directory, owner), path.join(directory, next));
            owner = next;
          },
          retainForCleanup() {
            retained = true;
            return directory;
          },
        };
      } catch (error) {
        if (!["EEXIST", "ENOTEMPTY", "EPERM"].includes(error.code)) throw error;
        collision = error;
      }

      let owners;
      try { owners = fs.readdirSync(directory); }
      catch (error) {
        if (error.code === "ENOENT") continue;
        throw error;
      }
      if (owners.length > 1) throw new Error("Unrecognized native dependency provisioning lock.");
      if (owners.length === 1) {
        const match = owners[0].match(/^([1-9]\d*)(?:\.([1-9]\d*))?-[\da-f-]{36}(\.cleanup-pending)?$/);
        const pid = Number(match?.[1]);
        const installerPid = match?.[2] === undefined ? null : Number(match[2]);
        if (!Number.isSafeInteger(pid) || pid <= 0 ||
            (installerPid !== null && (!Number.isSafeInteger(installerPid) || installerPid <= 0))) {
          throw new Error("Unrecognized native dependency provisioning lock owner.");
        }
        if (isProcessRunning(pid) || (installerPid !== null &&
            isProcessRunning(process.platform === "win32" ? installerPid : -installerPid))) {
          throw new Error("Native dependency provisioning is already in progress in another session.");
        }
        if ((match[3] && installerPid === null) ||
            (process.platform === "win32" && (match[3] || installerPid !== null))) {
          throw new Error(`Native dependency installer tree cleanup is unconfirmed. Confirm its processes have stopped before removing '${directory}' and retrying.`);
        }
        releaseProvisioningLock(directory, owners[0]);
        continue;
      }
      releaseProvisioningLock(directory);
    }
    throw collision;
  } finally {
    if (!acquired) fs.rmSync(contender, { recursive: true, force: true });
  }
}

function installNativeDependencies(directory, lock) {
  return new Promise((resolve, reject) => {
    const windows = process.platform === "win32";
    const taskkill = windows ? path.join(process.env.SystemRoot, "System32", "taskkill.exe") : null;
    const timeoutMs = 15000;
    lock.beginInstall();
    const child = cp.spawn("npm ci --no-audit --no-fund --fetch-retries=0 --fetch-timeout=10000", {
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
      ? null : new Error(`npm ci exited with ${signal ? `signal ${signal}` : `code ${code}`}`)));
    const terminate = (failure) => {
      let error = failure;
      try {
        if (windows) {
          cp.execFileSync(taskkill, ["/PID", String(child.pid), "/T", "/F"], {
            stdio: ["ignore", "ignore", "pipe"], timeout: 5000, windowsHide: true,
          });
        } else {
          process.kill(-child.pid, "SIGKILL");
        }
      } catch (failure) {
        if (windows || failure.code !== "ESRCH") {
          const directory = lock.retainForCleanup();
          error = new Error(`${error.message}; process-tree cleanup failed: ${failure.message}; provisioning lock retained at '${directory}'`);
          try { child.kill("SIGKILL"); }
          catch (rootFailure) { error = new Error(`${error.message}; installer cleanup failed: ${rootFailure.message}`); }
          child.unref();
        }
      }
      finish(error);
    };
    timer = setTimeout(() => terminate(new Error(`npm ci timed out after ${timeoutMs} ms`)), timeoutMs);
    try { lock.trackInstaller(child.pid); }
    catch (error) { terminate(error); }
  });
}

async function setup() {
  try {
    const manifest = fs.readFileSync(path.join(pluginRoot, "scripts", "native-deps.json"), "utf8");
    const lockfile = fs.readFileSync(path.join(pluginRoot, "scripts", "native-deps-lock.json"), "utf8");
    const installation = JSON.stringify({
      schemaVersion: 1,
      sha256: createHash("sha256").update(manifest).update("\0").update(lockfile).digest("hex"),
    });
    const marker = path.join(pluginData, "native-deps.installed.json");
    const dependencies = Object.keys(JSON.parse(manifest).dependencies);
    fs.mkdirSync(pluginData, { recursive: true });
    const lock = acquireProvisioningLock();
    try {
      const installed = dependencies.every((name) =>
        fs.existsSync(path.join(pluginData, "node_modules", name, "package.json")));
      if (!installed || !fs.existsSync(marker) || fs.readFileSync(marker, "utf8") !== installation) {
        fs.rmSync(marker, { force: true });
        fs.writeFileSync(path.join(pluginData, "package.json"), manifest);
        fs.writeFileSync(path.join(pluginData, "package-lock.json"), lockfile);
        await installNativeDependencies(pluginData, lock);
        fs.writeFileSync(marker, installation);
      }
    } finally {
      lock.release();
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
