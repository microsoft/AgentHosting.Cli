const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { spawnSync } = require("node:child_process");
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
    assert.match(result.stderr, /in-memory tokens/);
    assert.ok(!fs.existsSync(path.join(data, "native-deps.installed.json")));
  });
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
        assert.match(hook.hooks.SessionStart[0].hooks[0].command, /\$\{extensionPath\}/);
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
