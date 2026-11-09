const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { spawn, spawnSync, execFileSync } = require("node:child_process");
const { EventEmitter, once } = require("node:events");
const { PassThrough } = require("node:stream");
const net = require("node:net");
const vm = require("node:vm");
const yaml = require("js-yaml");
const { resolvePluginPaths } = require("../src/plugin-paths");
const { packageHost } = require("../src/package-host");

const root = path.resolve(__dirname, "..", "..");

function temporary(run) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "plugin-portability-"));
  try { return run(directory); }
  finally { fs.rmSync(directory, { recursive: true, force: true }); }
}

function fixture(directory, name) {
  const plugin = path.join(directory, name);
  fs.mkdirSync(path.join(plugin, ".claude-plugin"), { recursive: true });
  fs.mkdirSync(path.join(plugin, "scripts"));
  fs.writeFileSync(path.join(plugin, ".claude-plugin", "plugin.json"),
    JSON.stringify({ name: "mcs-assistant" }));
  return plugin;
}

function runHook({ platform = process.platform, childProcess, schedule, cancel = () => {},
  kill = (pid, signal) => process.kill(pid, signal), stderr = () => {},
  files = new Map(), nativeLock = '{"lockfileVersion":3}' }) {
  const writes = files;
  const errors = [];
  const source = fs.readFileSync(path.join(root, "scripts", "src", "set-env-vars.js"), "utf8");
  const context = {
    __dirname: path.join(root, "scripts", "src"),
    process: {
      platform, kill, stderr: { write: stderr },
      env: { ...process.env, CLAUDE_ENV_FILE: "", SystemRoot: process.env.SystemRoot || root },
    },
    console: { error: (message) => errors.push(String(message)) },
    setTimeout: schedule,
    clearTimeout: cancel,
    require(name) {
      if (name === "./plugin-paths") return { resolvePluginPaths: () => ({ pluginRoot: root, pluginData: root }) };
      if (name === "node:child_process") return childProcess;
      if (name === "node:fs") return {
        readFileSync: (file) => file === path.join(root, "scripts", "native-deps-lock.json")
          ? nativeLock : writes.get(file) || '{"dependencies":{"fixture":"1"}}',
        existsSync: (file) => writes.has(file),
        mkdirSync() {},
        writeFileSync: (file, content) => writes.set(file, content),
      };
      return require(name);
    },
  };
  return { completion: vm.runInNewContext(source, context), writes, errors };
}

test("different cached installations ignore the old singleton and receive different data roots", () => {
  temporary((directory) => {
    const first = fixture(directory, "first");
    const second = fixture(directory, "second");
    const legacy = path.join(directory, ".copilot-studio-cli");
    fs.mkdirSync(legacy);
    fs.writeFileSync(path.join(legacy, "plugin-paths.json"),
      JSON.stringify({ pluginRoot: second, pluginData: path.join(directory, "wrong") }));
    const resolve = (plugin) => resolvePluginPaths({
      start: path.join(plugin, "scripts"), env: {}, home: directory,
    });
    assert.equal(resolve(first).pluginRoot, first);
    assert.notEqual(resolve(first).pluginData, resolve(second).pluginData);
    assert.ok(!fs.existsSync(resolve(first).pluginData));
    assert.ok(!resolve(first).pluginData.endsWith("wrong"));
  });
});

test("a matching Copilot root/data pair wins over inherited Claude settings", () => {
  temporary((directory) => {
    const plugin = fixture(directory, "current");
    const result = resolvePluginPaths({
      start: path.join(plugin, "scripts"),
      env: {
        COPILOT_PLUGIN_ROOT: plugin,
        COPILOT_PLUGIN_DATA: path.join(directory, "copilot"),
        CLAUDE_PLUGIN_ROOT: path.join(directory, "other"),
        CLAUDE_PLUGIN_DATA: path.join(directory, "claude"),
      },
    });
    assert.equal(result.pluginData, path.join(directory, "copilot"));
    assert.throws(() => resolvePluginPaths({
      start: plugin,
      env: {
        COPILOT_PLUGIN_ROOT: plugin, CLAUDE_PLUGIN_ROOT: plugin,
        COPILOT_PLUGIN_DATA: "one", CLAUDE_PLUGIN_DATA: "two",
      },
    }), /Conflicting/);
  });
});

test("native dependency installation failures do not mark setup as completed", () => {
  temporary((directory) => {
    const plugin = fixture(directory, "plugin");
    const data = path.join(directory, "data");
    fs.mkdirSync(path.join(plugin, "hooks"));
    fs.copyFileSync(path.join(root, "hooks", "set-env-vars.bundle.js"),
      path.join(plugin, "hooks", "set-env-vars.bundle.js"));
    fs.copyFileSync(path.join(root, "scripts", "native-deps.json"),
      path.join(plugin, "scripts", "native-deps.json"));
    fs.copyFileSync(path.join(root, "scripts", "native-deps-lock.json"),
      path.join(plugin, "scripts", "native-deps-lock.json"));
    const result = spawnSync(process.execPath, [path.join(plugin, "hooks", "set-env-vars.bundle.js")], {
      encoding: "utf8", timeout: 30000,
      env: {
        ...process.env, PATH: "", CLAUDE_ENV_FILE: "",
        MCS_ASSISTANT_PLUGIN_ROOT: plugin, MCS_ASSISTANT_PLUGIN_DATA: data,
        CLAUDE_PLUGIN_ROOT: "", CLAUDE_PLUGIN_DATA: "",
        COPILOT_PLUGIN_ROOT: "", COPILOT_PLUGIN_DATA: "",
      },
    });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stderr, /in-memory tokens: npm ci exited/);
    assert.ok(!fs.existsSync(path.join(data, "native-deps.installed.json")));
  });
});

test("host launchers ignore inherited roots and Copilot does not write an inherited Claude environment file", () => {
  temporary((directory) => {
    const current = fixture(directory, "current $name's plugin");
    const inherited = fixture(directory, "other");
    for (const plugin of [current, inherited]) {
      fs.mkdirSync(path.join(plugin, "hooks"));
      fs.writeFileSync(path.join(plugin, "hooks", "set-env-vars.bundle.js"),
        "console.log(JSON.stringify({directory:__dirname,environmentFile:process.env.CLAUDE_ENV_FILE}))");
    }
    const hook = JSON.parse(fs.readFileSync(path.join(root, "hooks", "hooks.json"), "utf8"))
      .hooks.SessionStart[0].hooks[0];
    for (const name of ["command", "bash", "powershell"]) {
      const code = hook[name].match(/^node -e "(.*)"$/)[1];
      const claude = name === "command";
      const result = spawnSync(process.execPath, ["-e", code], {
        encoding: "utf8", timeout: 5000,
        env: {
          ...process.env,
          CLAUDE_PLUGIN_ROOT: claude ? current : inherited,
          COPILOT_PLUGIN_ROOT: claude ? inherited : current,
          CLAUDE_ENV_FILE: "inherited-environment",
        },
      });
      assert.equal(result.status, 0, result.stderr);
      const output = JSON.parse(result.stdout);
      assert.equal(output.directory, path.join(current, "hooks"));
      assert.equal(output.environmentFile, claude ? "inherited-environment" : undefined);
      const missing = spawnSync(process.execPath, ["-e", code], {
        encoding: "utf8", timeout: 5000,
        env: { ...process.env, CLAUDE_PLUGIN_ROOT: "", COPILOT_PLUGIN_ROOT: "" },
      });
      assert.notEqual(missing.status, 0);
      assert.match(missing.stderr, /Missing (?:Claude|Copilot) plugin root/);
    }
  });
});

test("optional native install records success only after a successful exit", async () => {
  for (const code of [0, 1]) {
    const child = new EventEmitter();
    child.stderr = new PassThrough();
    let cancelled = false;
    const result = runHook({
      childProcess: { spawn: () => child },
      schedule: () => 1,
      cancel: () => { cancelled = true; },
    });
    const marker = path.join(root, "native-deps.installed.json");
    assert.ok(!result.writes.has(marker));
    child.emit("close", code, null);
    await result.completion;
    assert.equal(result.writes.has(marker), code === 0);
    assert.equal(result.errors.length, code === 0 ? 0 : 1);
    if (code !== 0) assert.match(result.errors[0], /in-memory tokens: npm ci exited with code 1/);
    assert.ok(cancelled);
  }
});

test("native provisioning uses the locked tree and reinstalls after lock-only changes", async () => {
  const files = new Map();
  let attempts = 0;
  const marker = path.join(root, "native-deps.installed.json");
  for (const [lock, reinstall] of [
    ['{"lockfileVersion":3,"packages":{"node_modules/transitive":{"version":"1.0.0"}}}', true],
    ['{"lockfileVersion":3,"packages":{"node_modules/transitive":{"version":"1.0.0"}}}', false],
    ['{"lockfileVersion":3,"packages":{"node_modules/transitive":{"version":"1.0.1"}}}', true],
  ]) {
    const child = new EventEmitter();
    child.stderr = new PassThrough();
    const previous = attempts;
    const result = runHook({
      files, nativeLock: lock,
      childProcess: { spawn(command) {
        assert.match(command, /^npm ci /);
        assert.equal(files.get(path.join(root, "package-lock.json")), lock);
        attempts++;
        return child;
      } },
      schedule: () => 1,
    });
    child.emit("close", 0, null);
    await result.completion;
    assert.equal(attempts - previous, reinstall ? 1 : 0);
    assert.deepEqual(result.errors, []);
    assert.match(JSON.parse(files.get(marker)).sha256, /^[a-f0-9]{64}$/);
    files.set(path.join(root, "node_modules", "fixture", "package.json"), "{}");
  }
});

test("native lock pins the complete runtime tree without a private registry dependency", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, "scripts", "native-deps.json"), "utf8"));
  const lock = JSON.parse(fs.readFileSync(path.join(root, "scripts", "native-deps-lock.json"), "utf8"));
  assert.deepEqual(lock.packages[""].dependencies, manifest.dependencies);
  for (const version of Object.values(manifest.dependencies)) assert.match(version, /^\d+\.\d+\.\d+$/);
  for (const [name, dependency] of Object.entries(lock.packages)) {
    if (!name) continue;
    assert.match(dependency.version, /^\d+\.\d+\.\d+$/, name);
    assert.match(dependency.integrity, /^sha(?:1|256|384|512)-[A-Za-z0-9+/]+=*$/, name);
    assert.equal(dependency.resolved, undefined, name);
  }
});

test("optional native install times out the Windows tree or POSIX process group without a success marker", async () => {
  for (const platform of ["win32", "linux"]) {
    const child = new EventEmitter();
    child.pid = 12345;
    child.stderr = new PassThrough();
    let deadline;
    const terminations = [];
    const result = runHook({
      platform,
      childProcess: {
        spawn(command, options) {
          assert.match(command, /--fetch-retries=0/);
          assert.equal(options.detached, platform !== "win32");
          assert.equal(options.stdio[2], "pipe");
          return child;
        },
        execFileSync(executable, args, options) {
          assert.equal(executable, path.join(process.env.SystemRoot || root, "System32", "taskkill.exe"));
          assert.deepEqual(Array.from(args), ["/PID", String(child.pid), "/T", "/F"]);
          assert.equal(options.timeout, 5000);
          terminations.push("tree");
        },
      },
      schedule(callback, delay) {
        assert.equal(delay, 15000);
        deadline = callback;
        return 1;
      },
      kill(pid, signal) {
        assert.equal(pid, -child.pid);
        assert.equal(signal, "SIGKILL");
        terminations.push("group");
      },
    });
    deadline();
    child.emit("close", 0, null);
    await result.completion;
    assert.deepEqual(terminations, [platform === "win32" ? "tree" : "group"]);
    assert.match(result.errors[0], /in-memory tokens: npm ci timed out after 15000 ms/);
    assert.ok(child.stderr.destroyed);
    assert.ok(!result.writes.has(path.join(root, "native-deps.installed.json")));
  }
});

test("native install timeout stops a real descendant holding the stderr pipe open", { timeout: 10000 }, async (t) => {
  const ready = Promise.withResolvers();
  let installer;
  let descendant;
  let deadline;
  let terminated = false;
  t.after(() => {
    if (terminated) return;
    if (installer?.exitCode === null && installer.signalCode === null) {
      if (process.platform === "win32") {
        execFileSync(path.join(process.env.SystemRoot, "System32", "taskkill.exe"),
          ["/PID", String(installer.pid), "/T", "/F"], { stdio: "ignore", timeout: 5000 });
      } else {
        process.kill(-installer.pid, "SIGKILL");
      }
    }
    if (descendant) {
      try { process.kill(descendant.pid, "SIGKILL"); }
      catch (error) { if (error.code !== "ESRCH") throw error; }
    }
  });
  const descendantCode = 'const server=require("node:net").createServer(socket=>socket.end());' +
    'server.listen(0,"127.0.0.1",()=>console.error(JSON.stringify({pid:process.pid,port:server.address().port})));';
  const installerCode = 'require("node:child_process").spawn(process.execPath,["-e",' +
    JSON.stringify(descendantCode) + '],{stdio:["ignore","ignore","inherit"]});';
  let output = "";
  const result = runHook({
    childProcess: {
      spawn(command, options) {
        assert.match(command, /^npm ci /);
        installer = spawn(process.execPath, ["-e", installerCode], { ...options, shell: false });
        installer.once("error", ready.reject);
        return installer;
      },
      execFileSync,
    },
    schedule(callback) { deadline = callback; return 1; },
    stderr(chunk) {
      output += chunk;
      if (output.includes("\n")) {
        try { ready.resolve(JSON.parse(output.trim())); }
        catch (error) { ready.reject(error); }
      }
    },
  });
  descendant = await ready.promise;
  const closed = once(installer, "close");
  deadline();
  await result.completion;
  await closed;
  const socket = net.connect(descendant.port, "127.0.0.1");
  t.after(() => socket.destroy());
  await assert.rejects(once(socket, "connect"), /ECONNREFUSED/);
  assert.match(result.errors[0], /in-memory tokens: npm ci timed out/);
  assert.ok(!result.writes.has(path.join(root, "native-deps.installed.json")));
  terminated = true;
});

for (const target of ["gemini", "agency"]) {
  test(`${target} distribution is self-contained, deterministic and refuses overwrites`, () => {
    temporary((directory) => {
      const output = path.join(directory, "one", "mcs-assistant");
      packageHost({ target, output });
      for (const name of ["skills", "agents", "adapters", "hooks", "scripts", "LICENSE"]) {
        assert.ok(fs.existsSync(path.join(output, name)), name);
      }
      assert.ok(!fs.existsSync(path.join(output, "scripts", "node_modules")));
      assert.ok(!fs.existsSync(path.join(output, "scripts", "chat-with-agent.bundle.js")));
      const other = path.join(directory, "two", "mcs-assistant");
      packageHost({ target, output: other });
      const metadata = fs.readFileSync(path.join(output, "distribution.json"), "utf8");
      assert.equal(metadata, fs.readFileSync(path.join(other, "distribution.json"), "utf8"));
      assert.equal(JSON.parse(metadata).runtimeVerified, false);
      assert.throws(() => packageHost({ target, output }), /Refusing to overwrite/);
      if (target === "agency") {
        const agency = JSON.parse(fs.readFileSync(path.join(output, "agency.json"), "utf8"));
        assert.deepEqual(agency.engines, ["copilot"]);
        assert.ok(!agency.source);
        assert.equal(fs.existsSync(path.join(output, ".claude-plugin")), false);
        assert.equal(JSON.parse(fs.readFileSync(path.join(output, "plugin.json"))).name, "mcs-assistant");
      } else {
        const command = fs.readFileSync(path.join(output, "commands", "migrate.toml"), "utf8");
        const prompt = JSON.parse(command.split("\nprompt = ")[1].trim());
        assert.match(prompt, /Initial request: \{\{args\}\}/);
        assert.match(prompt, /checkpoint|planApproved/);
        assert.ok(!fs.existsSync(path.join(output, "commands", "migrate.md")));
        const agent = fs.readFileSync(path.join(output, "agents", "copilot-studio-init.md"), "utf8");
        const frontmatter = yaml.load(agent.split("---")[1]);
        assert.equal(frontmatter.name, "copilot-studio-init");
        assert.equal(frontmatter.kind, "local");
        assert.match(agent, /Do not modify the source agent/);
        const hook = JSON.parse(fs.readFileSync(path.join(output, "hooks", "hooks.json")));
        const startup = hook.hooks.SessionStart[0].hooks[0];
        assert.doesNotMatch(startup.command, /\$\{extensionPath\}/);
        assert.equal(startup.env.MCS_ASSISTANT_GEMINI_ROOT, "${extensionPath}");
      }
      const skill = path.join(output, "skills", "add-skill");
      const standalone = path.join(directory, "standalone");
      fs.cpSync(skill, standalone, { recursive: true });
      const result = spawnSync(process.execPath, [
        path.join(standalone, "scripts", "add-skill.bundle.js"), "--help",
      ], { cwd: directory, encoding: "utf8", timeout: 30000 });
      assert.equal(result.status, 0, result.stderr || result.stdout);
    });
  });
}

const geminiShells = process.platform === "win32"
  ? [[path.join(process.env.SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
    ["-NoProfile", "-NonInteractive", "-Command"]]]
  : [["/bin/sh", ["-c"]]];
if (process.platform === "win32") {
  const git = spawnSync("git", ["--exec-path"], { encoding: "utf8", timeout: 5000 });
  const bash = git.status === 0
    ? path.resolve(git.stdout.trim(), "..", "..", "..", "bin", "bash.exe") : undefined;
  if (bash && fs.existsSync(bash)) geminiShells.push([bash, ["--noprofile", "--norc", "-c"]]);
  else test("Gemini POSIX-shell path regression", { skip: "Git Bash is unavailable on Windows" }, () => {});
}

for (const [shell, prefix] of geminiShells) {
  test(`Gemini hook treats metacharacter paths as data through ${path.basename(shell)}`, () => {
    temporary((directory) => {
      const output = path.join(directory, "literal $CANARY $(echo EXPANDED) 'quoted' `tick`", "mcs-assistant");
      packageHost({ target: "gemini", output });
      fs.writeFileSync(path.join(output, "hooks", "set-env-vars.bundle.js"),
        "const paths=require('../scripts/src/plugin-paths').resolvePluginPaths({start:__dirname});" +
        "console.log(JSON.stringify({directory:__dirname,paths,environmentFile:process.env.CLAUDE_ENV_FILE}));");
      const hook = JSON.parse(fs.readFileSync(path.join(output, "hooks", "hooks.json"), "utf8"))
        .hooks.SessionStart[0].hooks[0];
      const hydrate = (text) => text.replaceAll("${extensionPath}", output).replaceAll("${/}", path.sep);
      const inherited = fixture(directory, "inherited");
      const foreignData = path.join(directory, "foreign-data");
      const result = spawnSync(shell, [...prefix, hydrate(hook.command)], {
        cwd: directory, encoding: "utf8", timeout: 10000,
        env: {
          ...process.env,
          MCS_ASSISTANT_PLUGIN_ROOT: inherited,
          MCS_ASSISTANT_PLUGIN_DATA: foreignData,
          CLAUDE_ENV_FILE: path.join(directory, "inherited-environment"),
          ...Object.fromEntries(Object.entries(hook.env || {}).map(([name, value]) => [name, hydrate(value)])),
        },
      });
      assert.equal(result.status, 0, result.stderr || result.error?.message);
      const actual = JSON.parse(result.stdout);
      assert.equal(actual.directory, path.join(output, "hooks"));
      assert.equal(actual.paths.pluginRoot, output);
      assert.notEqual(actual.paths.pluginData, foreignData);
      assert.equal(actual.environmentFile, undefined);
      assert.ok(!fs.existsSync(path.join(directory, "inherited-environment")));
    });
  });
}

test("distribution rejects unsupported hosts and output inside canonical source", () => {
  assert.throws(() => packageHost({ target: "m365", output: "unused" }), /M365 is deferred/);
  assert.throws(() => packageHost({
    target: "gemini", output: path.join(root, "mcs-assistant"),
  }), /outside the canonical/);
  temporary((directory) => {
    const plugin = fixture(directory, "source");
    const parent = path.join(plugin, "unexpected");
    assert.throws(() => packageHost({
      target: "gemini", output: path.join(parent, "mcs-assistant"), root: plugin,
    }), /outside the canonical/);
    assert.ok(!fs.existsSync(parent), "invalid output must not create directories in source");
  });
});
