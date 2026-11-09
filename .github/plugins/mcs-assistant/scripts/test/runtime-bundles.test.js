const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { isBuiltin } = require("node:module");
const { spawnSync } = require("node:child_process");
const { createHash } = require("node:crypto");
const test = require("node:test");
const { buildSync } = require("esbuild");
const yaml = require("js-yaml");

const root = path.resolve(__dirname, "..", "..");
const scripts = path.join(root, "scripts");
const commands = require("../package.json").scripts;
const builds = commands.build.split(" && ").map((command) => {
  assert.match(command, /^npm run build:[\w-]+$/);
  const name = command.slice("npm run ".length);
  const build = commands[name];
  assert.match(build, /^esbuild \S+ --bundle /);
  assert.match(build, /--preserve-symlinks\b/);
  assert.match(build, /--platform=node\b/);
  assert.match(build, /--target=node22\b/);
  assert.match(build, /--format=cjs\b/);
  const input = build.match(/^esbuild (\S+)/)[1];
  const output = build.match(/--outfile=(\S+)/)[1];
  return { name, build, input, output: path.resolve(scripts, output) };
});

function javascriptFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) return javascriptFiles(file);
    return entry.isFile() && entry.name.endsWith(".js") ? [file] : [];
  });
}

test("the aggregate build covers every shipped JavaScript entry point", () => {
  const runtimeFiles = [
    ...javascriptFiles(path.join(root, "hooks")),
    ...javascriptFiles(path.join(root, "skills")),
    ...fs.readdirSync(scripts, { withFileTypes: true })
      .filter((entry) => entry.isFile() && entry.name.endsWith(".js"))
      .map((entry) => path.join(scripts, entry.name)),
  ];
  assert.deepEqual(builds.map((build) => build.output).sort(), runtimeFiles.sort());
  for (const { input, output } of builds) {
    assert.ok(output.endsWith(".bundle.js"), output);
    const source = path.resolve(scripts, input);
    assert.ok(source.startsWith(path.join(scripts, "src") + path.sep), source);
    assert.ok(fs.statSync(source).isFile(), source);
  }
});

for (const { name, build, input, output } of builds) {
  test(`${name} reproduces its shipped bundle with only documented externals`, () => {
    const external = [...build.matchAll(/--external:(\S+)/g)].map((match) => match[1]);
    const banner = build.match(/--banner:js="(.*)"$/)?.[1];
    const result = buildSync({
      absWorkingDir: scripts,
      entryPoints: [input],
      outfile: output,
      bundle: true,
      preserveSymlinks: true,
      platform: "node",
      target: "node22",
      format: "cjs",
      external,
      ...(banner ? { banner: { js: banner } } : {}),
      write: false,
      metafile: true,
    });
    assert.equal(fs.readFileSync(output, "utf8").replace(/\r\n/g, "\n"),
      result.outputFiles[0].text);
    for (const input of Object.keys(result.metafile.inputs)) {
      assert.ok(path.resolve(scripts, input).startsWith(scripts + path.sep),
        `${name}: nonportable source path ${input}`);
    }
    const allowedPackages = new Set(["keytar", "@azure/msal-node-extensions"]);
    for (const artifact of Object.values(result.metafile.outputs)) {
      for (const dependency of artifact.imports) {
        if (dependency.external) {
          assert.ok(isBuiltin(dependency.path) || allowedPackages.has(dependency.path),
            `${name}: unexpected external package ${dependency.path}`);
        }
      }
    }
  });
}

test("the bundled knowledge verifier dry-run is independent of retired chat configuration", () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "bundled-knowledge-"));
  try {
    const helper = path.join(temporary, "verify-knowledge-access.bundle.js");
    const home = path.join(temporary, "home");
    const data = path.join(temporary, "data");
    fs.copyFileSync(path.join(root, "skills", "add-knowledge", "scripts",
      "verify-knowledge-access.bundle.js"), helper);
    fs.mkdirSync(home);
    fs.mkdirSync(data);
    fs.writeFileSync(path.join(data, "chat-config.json"), JSON.stringify({
      tenantDefaults: { ignored: { appClientId: "retired-chat-id" } },
    }));
    const result = spawnSync(process.execPath, [
      helper, "--dry-run", "--tenant-id", "00000000-0000-0000-0000-000000000003",
      "--url", "https://contoso.sharepoint.com/sites/Policies",
    ], {
      cwd: temporary,
      encoding: "utf8",
      timeout: 30000,
      env: {
        ...process.env,
        HOME: home,
        USERPROFILE: home,
        NODE_PATH: "",
        CLAUDE_PLUGIN_DATA: data,
        CLAUDE_PLUGIN_ROOT: "",
        COPILOT_PLUGIN_ROOT: "",
        COPILOT_PLUGIN_DATA: "",
        MCS_ASSISTANT_PLUGIN_ROOT: "",
        MCS_ASSISTANT_PLUGIN_DATA: "",
        MCS_KNOWLEDGE_CLIENT_ID: "",
      },
    });
    assert.equal(result.status, 0, result.stderr || result.error?.message || result.stdout);
    const plan = JSON.parse(result.stdout);
    assert.equal(plan.status, "ok");
    assert.equal(plan.dryRun, true);
    assert.equal(plan.needsClientId, true);
    assert.deepEqual(fs.readdirSync(data), ["chat-config.json"]);
    assert.ok(!fs.existsSync(path.join(home, ".copilot-studio-cli")));
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
});

test("hook launchers and migration permissions reference the bundled entry points", () => {
  const hook = JSON.parse(fs.readFileSync(
    path.join(root, "hooks", "hooks.json"), "utf8"
  )).hooks.SessionStart[0].hooks[0];
  for (const launcher of ["command", "bash", "powershell"]) {
    assert.match(hook[launcher], /set-env-vars\.bundle\.js/);
  }
  const migration = fs.readFileSync(path.join(root, "commands", "migrate.md"), "utf8");
  assert.match(migration, /Bash\(node \*convert-actions-to-tools\.bundle\.js\*\)/);
});

test("the bundled session hook scopes data and safely quotes exported paths", () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "bundled-hook-"));
  try {
    const plugin = path.join(temporary, "plugin");
    const data = path.join(temporary, "data's-$literal");
    const home = path.join(temporary, "home");
    const environmentFile = path.join(temporary, "environment");
    fs.mkdirSync(path.join(plugin, "hooks"), { recursive: true });
    fs.mkdirSync(path.join(plugin, "scripts"));
    fs.mkdirSync(data);
    fs.mkdirSync(home);
    fs.copyFileSync(path.join(root, "hooks", "set-env-vars.bundle.js"),
      path.join(plugin, "hooks", "set-env-vars.bundle.js"));
    const manifest = JSON.parse(fs.readFileSync(path.join(scripts, "native-deps.json"), "utf8"));
    for (const dependency of Object.keys(manifest.dependencies)) {
      const directory = path.join(data, "node_modules", dependency);
      fs.mkdirSync(directory, { recursive: true });
      fs.writeFileSync(path.join(directory, "package.json"), "{}");
    }
    for (const name of ["native-deps.json", "native-deps-lock.json"]) {
      fs.copyFileSync(path.join(scripts, name), path.join(plugin, "scripts", name));
    }
    const installation = JSON.stringify({
      sha256: createHash("sha256").update(fs.readFileSync(path.join(scripts, "native-deps.json")))
        .update("\0").update(fs.readFileSync(path.join(scripts, "native-deps-lock.json"))).digest("hex"),
    });
    fs.writeFileSync(path.join(data, "native-deps.installed.json"), installation);
    const result = spawnSync(process.execPath, [
      path.join(plugin, "hooks", "set-env-vars.bundle.js"),
    ], {
      cwd: temporary,
      encoding: "utf8",
      timeout: 30000,
      env: {
        ...process.env,
        HOME: home,
        USERPROFILE: home,
        PATH: "",
        CLAUDE_PLUGIN_DATA: data,
        CLAUDE_PLUGIN_ROOT: plugin,
        COPILOT_PLUGIN_ROOT: "",
        COPILOT_PLUGIN_DATA: "",
        MCS_ASSISTANT_PLUGIN_ROOT: "",
        MCS_ASSISTANT_PLUGIN_DATA: "",
        CLAUDE_ENV_FILE: environmentFile,
      },
    });
    assert.equal(result.status, 0, result.stderr || result.error?.message);
    assert.equal(result.stderr, "");
    assert.ok(!fs.existsSync(path.join(home, ".copilot-studio-cli", "plugin-paths.json")));
    const quote = (value) => "'" + value.replace(/'/g, "'\\''") + "'";
    assert.equal(fs.readFileSync(environmentFile, "utf8"),
      `export MCS_ASSISTANT_PLUGIN_DATA=${quote(data)}\nexport MCS_ASSISTANT_PLUGIN_ROOT=${quote(plugin)}\n`);
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
});

test("the bundled migration helper converts a connector action without sibling modules", () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "bundled-migration-"));
  try {
    const helper = path.join(temporary, "convert-actions-to-tools.bundle.js");
    const actions = path.join(temporary, "actions");
    const tools = path.join(temporary, "tools");
    const report = path.join(temporary, "report.json");
    fs.copyFileSync(path.join(scripts, "convert-actions-to-tools.bundle.js"), helper);
    fs.mkdirSync(actions);
    fs.writeFileSync(path.join(actions, "Echo.mcs.yml"), [
      "kind: TaskDialog",
      "mcs.metadata:",
      "  componentName: Echo",
      "modelDescription: Echo a message.",
      "action:",
      "  kind: InvokeConnectorTaskAction",
      "  connectionReference: cr_demo.shared_office365",
      "  operationId: Echo",
      "",
    ].join("\n"));
    const result = spawnSync(process.execPath, [
      helper, actions, tools, "--all", "--report", report,
    ], { cwd: temporary, encoding: "utf8", timeout: 30000 });
    assert.equal(result.status, 0, result.stderr || result.error?.message || result.stdout);
    const migration = JSON.parse(fs.readFileSync(report, "utf8"));
    assert.equal(migration.totals.converted, 1);
    assert.equal(migration.totals.invalid, 0);
    assert.equal(migration.totals.skippedUnsupported, 0);
    const converted = yaml.load(fs.readFileSync(migration.converted[0].targetPath, "utf8"));
    assert.equal(converted.kind, "ConnectorTool");
    assert.equal(converted.connectionReference, "cr_demo.shared_office365");
    assert.equal(converted.operationId, "Echo");
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
});
