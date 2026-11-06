const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { spawnSync } = require("node:child_process");
const yaml = require("js-yaml");

const root = path.resolve(__dirname, "..", "..");
const skillRoot = path.join(root, "skills", "add-skill");
const read = (file) => fs.readFileSync(path.join(root, file), "utf8").replace(/\s+/g, " ");

test("add-skill routes new skill requests to authoring without an acquisition interview", () => {
  const entrypoint = read("skills/add-skill/SKILL.md");
  assert.match(entrypoint, /description:.*create.*write.*format/i);
  assert.match(entrypoint, /allowed-tools:.*Write/);
  assert.match(entrypoint, /author.*requirements[\s\S]*references\/authoring\.md/i);
  assert.match(entrypoint, /do not ask.*(?:gallery|source)/i);
  assert.match(entrypoint, /request.*add.*project.*import consent/i);
  assert.doesNotMatch(entrypoint, /Do not import into an `msagent`-only project/);
});

test("new authoring stages a real manifest and delegates companion generation", () => {
  const authoring = read("skills/add-skill/references/authoring.md");
  assert.match(authoring, /outside the (?:target|agent) workspace/i);
  assert.match(authoring, /SKILL\.md[\s\S]*name:[\s\S]*description:/);
  assert.match(authoring, /description[\s\S]*1024[\s\S]*folded\/literal block scalars/i);
  assert.match(authoring, /Never.*(?:hand-write|hand-edit).*companions/i);
  assert.match(authoring, /Do not.*content:[\s\S]*default/i);
  assert.match(authoring, /(?:import\.md|optional import)/);
  const raw = fs.readFileSync(
    path.join(skillRoot, "references", "authoring.md"), "utf8"
  );
  const example = raw.match(/```markdown\r?\n---\r?\n([\s\S]*?)\r?\n---/);
  assert.ok(example, "authoring must show a real manifest example");
  const manifest = yaml.load(example[1]);
  assert.match(manifest.name, /^[a-z0-9]+(?:-[a-z0-9]+)*$/);
  assert.equal(typeof manifest.description, "string");
  assert.ok(manifest.description.length);
});

test("create and architect route through the add-skill entrypoint, not raw inline schemas", () => {
  for (const file of [
    "skills/create-agent/references/components.md",
    "agents/copilot-studio-architect.md",
  ]) {
    const text = read(file);
    assert.match(text, /skills\/add-skill\/SKILL\.md/);
    assert.match(text, /before.*(?:creating|authoring|writing).*skill/i);
    assert.doesNotMatch(text, /Author new skills as the \*\*inline\*\* variant/);
  }
  const architect = read("agents/copilot-studio-architect.md");
  assert.doesNotMatch(architect, /only responsible for creating YAML/);
  const creation = read("skills/create-agent/SKILL.md");
  assert.match(creation, /stage source manifests.*outside/i);
  assert.doesNotMatch(creation, /Edit only inside the authored workspace/);
});

test("skill formats have an explicit default without automatic conversion", () => {
  const schema = read("skills/add-skill/references/skill-schema.md");
  const inline = read("skills/add-skill/references/inline-skill.md");
  assert.match(schema, /default[\s\S]*SKILL\.md[\s\S]*skill\.mcs\.yml/i);
  assert.match(schema, /explicit.*inline/i);
  assert.match(inline, /explicit.*inline/i);
  assert.doesNotMatch(schema, /Architect authors a new skill from an idea.*inline-skill\.md/);
  assert.match(schema, /Do not.*convert.*existing/i);
});

test("import documents scaffold support, overwrite safety, and cloud acceptance boundaries", () => {
  const importing = read("skills/add-skill/references/import.md");
  assert.match(importing, /MSAGENT.*scaffold/i);
  assert.match(importing, /authored.*child.*(?:workspace|folder)/i);
  assert.match(importing, /--force.*explicit confirmation|explicit confirmation.*--force/i);
  assert.match(importing, /no-sidecars[\s\S]*MSAGENT/i);
  assert.match(importing, /references\/acceptance\.md|acceptance\.md/);
  const acceptance = read("skills/add-skill/references/acceptance.md");
  assert.match(acceptance, /SKILL\.md[\s\S]*skill\.mcs\.yml[\s\S]*authoringSource: Upload/);
  assert.match(acceptance, /local.*(?:not|does not).*cloud/i);
  assert.match(acceptance, /push-agent[\s\S]*publish-agent/);
  assert.match(acceptance, /(?:pull|round.trip)[\s\S]*(?:preserv|recogn)/i);
  assert.match(acceptance, /(?:not|never).*deploy.*(?:without|unless).*consent/i);
});

test("helper imports an authored skill into an MSAGENT-style scaffold without a sync marker", () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "authored-skill-"));
  try {
    const project = path.join(temporary, "project");
    const workspace = path.join(project, "Demo Agent");
    const source = path.join(temporary, "demo-report");
    fs.mkdirSync(path.join(project, ".config"), { recursive: true });
    fs.mkdirSync(workspace);
    fs.mkdirSync(source);
    const registration = '{"fixture":"CLI-managed state must remain untouched"}\n';
    fs.writeFileSync(path.join(project, ".config", "agent.config.json"), registration);
    const settings = [
      "displayName: Demo Agent",
      "schemaName: cr_demo",
      "configuration:",
      "  recognizer:",
      "    kind: CLICopilotRecognizer",
      "  authoringModel: CliCopilot",
      "template: cliagent-1.0.0",
      "",
    ].join("\n");
    fs.writeFileSync(path.join(workspace, "settings.mcs.yml"), settings);
    const manifest = [
      "---",
      "name: demo-report",
      'description: "Use when a caller requests a factual report."',
      "---",
      "# Demo report",
      "Report verified facts and disclose missing evidence.",
      "",
    ].join("\n");
    fs.writeFileSync(path.join(source, "SKILL.md"), manifest);
    const result = spawnSync(process.execPath, [
      path.join(skillRoot, "scripts", "add-skill.bundle.js"),
      "import", "--src", source, "--workspace", workspace,
    ], { encoding: "utf8", timeout: 30000 });
    assert.equal(result.status, 0, result.stderr || result.error?.message || result.stdout);
    const imported = JSON.parse(result.stdout);
    assert.equal(imported.ok, true);
    assert.equal(imported.schemaPrefix, "cr_demo");
    assert.equal(imported.folder, "demo-report");
    const destination = path.join(workspace, "behaviors", imported.folder);
    assert.deepEqual(fs.readdirSync(destination).sort(), ["SKILL.md", "skill.mcs.yml"]);
    assert.equal(fs.readFileSync(path.join(destination, "SKILL.md"), "utf8"), manifest);
    const anchor = yaml.load(fs.readFileSync(path.join(destination, "skill.mcs.yml"), "utf8"));
    assert.equal(anchor.kind, "InlineAgentSkill");
    assert.equal(anchor.authoringSource, "Upload");
    assert.equal(anchor["mcs.metadata"].componentName, "demo-report");
    assert.match(anchor["mcs.metadata"].schemaName, /^cr_demo\.skill\.demoreport_[A-Za-z0-9]{3}$/);
    assert.equal(anchor["mcs.metadata"].bundle, undefined);
    assert.equal(anchor["mcs.metadata"].manifestSchemaName, undefined);
    assert.equal(fs.readFileSync(path.join(workspace, "settings.mcs.yml"), "utf8"), settings);
    assert.equal(
      fs.readFileSync(path.join(project, ".config", "agent.config.json"), "utf8"),
      registration
    );
    assert.ok(!fs.existsSync(path.join(workspace, "agent.sync.yaml")));
    assert.match(imported.warnings.join(" "), /agent\.sync\.yaml/);
    assert.match(imported.next.join(" "), /local.*only/i);
    assert.doesNotMatch(imported.next.join(" "), /sync push\) to publish/i);
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
});
