const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const test = require("node:test");
const yaml = require("js-yaml");
const { parseManifest } = require("../src/skill-manifest");
const { emitResult } = require("../src/helper-cli");
const { importSkill } = require("../../skills/add-skill/scripts/add-skill.bundle.js");

const root = path.resolve(__dirname, "..", "..");
const addSkill = path.join(root, "skills", "add-skill", "scripts", "add-skill.bundle.js");
const workiq = path.join(root, "skills", "add-skill", "scripts", "workiq-catalog.bundle.js");
const knowledge = path.join(root, "skills", "add-knowledge", "scripts", "verify-knowledge-access.bundle.js");

function fixture(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "skill-standards-"));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const src = path.join(dir, "demo");
  const workspace = path.join(dir, "agent");
  fs.mkdirSync(src);
  fs.mkdirSync(workspace);
  fs.writeFileSync(path.join(src, "SKILL.md"), "---\nname: demo\ndescription: A demo.\n---\nBody.\n");
  fs.writeFileSync(path.join(workspace, "settings.mcs.yml"), "template: cliagent-1.0.0\nschemaName: cr_demo\n");
  return { dir, src, workspace };
}

function run(helper, args) {
  return spawnSync(process.execPath, [helper, ...args], { encoding: "utf8", timeout: 30000 });
}

test("all shipped skills satisfy the portable frontmatter contract", () => {
  for (const name of fs.readdirSync(path.join(root, "skills"))) {
    const doc = parseManifest(fs.readFileSync(path.join(root, "skills", name, "SKILL.md"), "utf8"), name);
    assert.ok(doc.metadata["argument-hint"]);
  }
});

test("invalid manifests cannot create or replace a skill", (t) => {
  const { src, workspace } = fixture(t);
  const cases = [
    "Body without frontmatter.",
    "---\ndescription: Description.\n---\n",
    "---\nname: demo\n---\n",
    "---\nname: Bad_Name\ndescription: Description.\n---\n",
    "---\nname: different\ndescription: Description.\n---\n",
    `---\nname: demo\ndescription: ${"x".repeat(1025)}\n---\n`,
    "---\nname: demo\ndescription: [not, a, string]\n---\n",
    "---\nname: demo\ndescription: A\nargument-hint: Host extension\n---\n",
    "---\nname: demo\ndescription: A\nname: duplicate\n---\n",
  ];
  const existing = path.join(workspace, "behaviors", "demo");
  fs.mkdirSync(existing, { recursive: true });
  fs.writeFileSync(path.join(existing, "keep"), "original");
  for (const content of cases) {
    fs.writeFileSync(path.join(src, "SKILL.md"), content);
    assert.throws(() => importSkill({ src, workspace, force: true }));
    assert.deepEqual(fs.readdirSync(existing), ["keep"]);
  }
});

test("folded and literal descriptions retain parsed text in generated companions", (t) => {
  const { src, workspace } = fixture(t);
  for (const [scalar, expected] of [[">-\n  Verified data\n  when requested.", "Verified data when requested."],
    ["|\n  First line\n  Second line", "First line\nSecond line\n"]]) {
    fs.writeFileSync(path.join(src, "SKILL.md"), `---\nname: demo\ndescription: ${scalar}\n---\nBody.\n`);
    const result = importSkill({ src, workspace, force: true });
    const doc = yaml.load(fs.readFileSync(path.join(result.behaviorsDir, "skill.mcs.yml"), "utf8"));
    assert.equal(doc["mcs.metadata"].description, expected);
  }
});

test("import dry-run does not create directories or overwrite an existing skill", (t) => {
  const { src, workspace } = fixture(t);
  const args = ["import", "--src", src, "--workspace", workspace, "--dry-run"];
  let result = run(addSkill, args);
  assert.equal(result.status, 0, result.stdout);
  assert.equal(JSON.parse(result.stdout).dryRun, true);
  assert.deepEqual(fs.readdirSync(workspace), ["settings.mcs.yml"]);
  importSkill({ src, workspace });
  const manifest = path.join(workspace, "behaviors", "demo", "SKILL.md");
  fs.writeFileSync(manifest, "Keep this.");
  result = run(addSkill, [...args, "--force"]);
  assert.equal(result.status, 0, result.stdout);
  assert.equal(fs.readFileSync(manifest, "utf8"), "Keep this.");
  assert.deepEqual(fs.readdirSync(path.dirname(manifest)).sort(), ["SKILL.md", "skill.mcs.yml"].sort());
});

test("all helpers provide help without authentication or required inputs", () => {
  for (const helper of [addSkill, workiq, knowledge]) {
    const result = run(helper, ["--help"]);
    assert.equal(result.status, 0, result.stdout);
    assert.match(result.stdout, /Usage:[\s\S]*--dry-run[\s\S]*Exit codes:/);
    assert.equal(result.stderr, "");
  }
});

test("CLI rejects unknown flags, missing values and string-valued consent before writes", (t) => {
  const { src, workspace } = fixture(t);
  const inputs = [
    [addSkill, ["import", "--src", src, "--workspace", workspace, "--unknown-option"]],
    [addSkill, ["import", "--src", src, "--workspace", workspace, "--force", "false"]],
    [addSkill, ["import", "--src", src, "--workspace", workspace, "--force", "true"]],
    [addSkill, ["import", "--src", src, "--workspace", workspace, "--name"]],
    [addSkill, ["download", "--slug", "demo", "--dry-run"]],
    [workiq, ["list", "--dry-run", "false"]],
    [workiq, ["list", "--dry-run", "--unknown-option"]],
    [knowledge, ["--url", "https://contoso.sharepoint.com/sites/demo", "--dry-run", "--unknown-option"]],
    [knowledge, ["--url", "--dry-run"]],
    [knowledge, ["--url", "https://contoso.sharepoint.com/sites/demo", "--dry-run", "false"]],
    [knowledge, ["--dry-run", "true"]],
    [knowledge, ["--dry-run", "false"]],
    [knowledge, ["--dry-run", "TRUE", "https://contoso.sharepoint.com/sites/demo"]],
  ];
  for (const [helper, args] of inputs) {
    const result = run(helper, args);
    assert.equal(result.status, 1, `${args.join(" ")}: ${result.stdout}`);
    assert.match(result.stdout, /error/);
  }
  assert.deepEqual(fs.readdirSync(workspace), ["settings.mcs.yml"]);
});

test("knowledge verifier accepts positional URLs before and after dry-run switches", () => {
  const url = "https://contoso.sharepoint.com/sites/demo";
  for (const args of [[url, "--dry-run"], ["--dry-run", url], ["--dry-run", "--url", url]]) {
    const result = run(knowledge, [
      ...args, "--tenant-id", "11111111-1111-1111-1111-111111111111",
      "--client-id", "22222222-2222-2222-2222-222222222222",
    ]);
    assert.equal(result.status, 0, result.stdout);
    const plan = JSON.parse(result.stdout);
    assert.equal(plan.dryRun, true);
    assert.equal(plan.url, url);
    assert.equal(result.stderr, "");
  }
});

test("large imports page stdout and preserve full results in an explicit new output file", (t) => {
  const { dir, src, workspace } = fixture(t);
  for (let i = 0; i < 200; i++) fs.writeFileSync(path.join(src, `file-${i}.txt`), "payload");
  const output = path.join(dir, "result.json");
  const result = run(addSkill, ["import", "--src", src, "--workspace", workspace, "--output", output]);
  assert.equal(result.status, 0, result.stdout);
  const summary = JSON.parse(result.stdout);
  const complete = JSON.parse(fs.readFileSync(output, "utf8"));
  assert.equal(summary.files.length, 20);
  assert.equal(summary.pages.files.total, 201);
  assert.equal(summary.pages.files.hasMore, true);
  assert.equal(complete.files.length, 201);
  assert.equal(complete.companions.length, 201);
  assert.ok(result.stdout.length < 12000);
  assert.equal(run(addSkill, ["import", "--src", src, "--workspace", workspace, "--force", "--output", output]).status, 1);
  assert.equal(fs.readFileSync(output, "utf8"), JSON.stringify(complete, null, 2) + "\n");
  const page = run(addSkill, ["import", "--src", src, "--workspace", workspace, "--force", "--dry-run", "--offset", "200", "--limit", "5"]);
  assert.equal(page.status, 0, page.stdout);
  assert.equal(JSON.parse(page.stdout).files.length, 1);
});

test("stdout byte budgeting shortens oversized pages without losing full output", (t) => {
  const { dir } = fixture(t);
  const output = path.join(dir, "full.json");
  const skills = Array.from({ length: 30 }, (_, i) => ({ name: `skill-${i}`, description: "x".repeat(1024) }));
  const original = process.stdout.write;
  let stdout = "";
  try {
    process.stdout.write = (text) => { stdout += text; return true; };
    emitResult({ ok: true, skills }, { limit: 20, output });
  } finally {
    process.stdout.write = original;
  }
  const page = JSON.parse(stdout);
  assert.ok(Buffer.byteLength(stdout) <= 12000);
  assert.equal(page.truncatedForSize, true);
  assert.equal(page.pages.skills.total, 30);
  assert.equal(page.pages.skills.returned, page.skills.length);
  assert.ok(page.skills.length < 20);
  assert.equal(JSON.parse(fs.readFileSync(output, "utf8")).skills.length, 30);
});

test("knowledge verifier can retain large results without overflowing stdout or authenticating", (t) => {
  const { dir } = fixture(t);
  const output = path.join(dir, "knowledge.json");
  const url = "https://example.com/" + "a".repeat(14000);
  const result = run(knowledge, ["--url", url, "--output", output]);
  assert.equal(result.status, 0, result.stdout);
  const summary = JSON.parse(result.stdout);
  assert.equal(summary.status, "skipped");
  assert.equal(summary.summaryOmitted, true);
  assert.ok(Buffer.byteLength(result.stdout) <= 12000);
  assert.equal(JSON.parse(fs.readFileSync(output, "utf8")).url, url);
  assert.equal(run(knowledge, ["--url", url, "--dry-run", "--output", path.join(dir, "plan.json")]).status, 1);
  assert.ok(!fs.existsSync(path.join(dir, "plan.json")));
});

test("knowledge verifier saves full validation errors to the requested output file", (t) => {
  const { dir } = fixture(t);
  const url = "x".repeat(14000);
  const output = path.join(dir, "error.json");
  const result = run(knowledge, ["--url", url, "--output", output]);
  assert.equal(result.status, 1, result.stdout);
  const summary = JSON.parse(result.stdout);
  assert.equal(summary.status, "error");
  assert.equal(summary.summaryOmitted, true);
  assert.equal(summary.output, output);
  assert.ok(Buffer.byteLength(result.stdout) <= 12000);
  const full = JSON.parse(fs.readFileSync(output, "utf8"));
  assert.equal(full.status, "error");
  assert.equal(full.url, url);
  assert.ok(full.error);
});

test("knowledge verifier falls back to stdout when an error output file cannot be written", (t) => {
  const { dir } = fixture(t);
  const output = path.join(dir, "error.json");
  const script = `
    const fs = require("node:fs");
    const access = fs.accessSync;
    fs.accessSync = (...args) => {
      access(...args);
      fs.writeFileSync(${JSON.stringify(output)}, "Keep this.");
    };
    process.argv = [process.execPath, ${JSON.stringify(knowledge)},
      "--url", "not-a-url", "--output", ${JSON.stringify(output)}];
    require("node:module").runMain();
  `;
  const result = spawnSync(process.execPath, ["-e", script], { encoding: "utf8", timeout: 30000 });
  assert.equal(result.status, 1, result.stderr);
  const failure = JSON.parse(result.stdout);
  assert.equal(failure.status, "error");
  assert.equal(failure.error, "Not a valid URL.");
  assert.match(failure.outputError, /EEXIST/);
  assert.equal(failure.output, undefined);
  assert.equal(fs.readFileSync(output, "utf8"), "Keep this.");
});

test("knowledge requires identity validation and actual post-write evidence", () => {
  const skill = fs.readFileSync(path.join(root, "skills", "add-knowledge", "SKILL.md"), "utf8");
  assert.match(skill, /versioned `cliagent-`[\s\S]*CliCopilot[\s\S]*CLICopilotRecognizer/);
  assert.match(skill, /Re-read and parse every written YAML artifact/);
  assert.match(skill, /copied file's bytes match the source/);
  assert.match(skill, /do not claim success/);
});

test("creation explicitly passes verified schema and environment with cloud identity", () => {
  const handoff = fs.readFileSync(path.join(root, "skills", "create-agent", "references", "publish-handoff.md"), "utf8");
  assert.match(handoff, /verified `schemaName`/);
  assert.match(handoff, /environment[\s\S]*`schemaName` explicitly alongside `mcsAgentId`/);
});
