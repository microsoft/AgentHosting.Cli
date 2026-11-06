const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const yaml = require("js-yaml");

const root = path.resolve(__dirname, "..", "..");
const skillRoot = path.join(root, "skills", "add-channel");

function read(file) {
  const location = path.join(root, file);
  assert.ok(fs.existsSync(location), `Missing channel guidance: ${file}`);
  return fs.readFileSync(location, "utf8");
}

const normalized = (file) => read(file).replace(/\s+/g, " ");

test("add-channel is discoverable for Teams and Microsoft 365 Copilot requests", () => {
  const text = read("skills/add-channel/SKILL.md");
  const frontmatter = text.match(/^---\r?\n([\s\S]*?)\r?\n---/);
  assert.ok(frontmatter, "skill needs YAML frontmatter");
  const metadata = yaml.load(frontmatter[1]);
  assert.equal(metadata.name, "add-channel");
  assert.match(metadata.description, /^Use when/);
  assert.match(metadata.description, /Teams/);
  assert.match(metadata.description, /Copilot/);
  assert.match(metadata["allowed-tools"], /Write/);
  const links = [...text.matchAll(/\]\((references\/[^)#]+\.md)\)/g)];
  assert.ok(links.some((match) => match[1] === "references/channel-schema.md"));
  for (const [, reference] of links) {
    assert.ok(fs.existsSync(path.join(skillRoot, reference)), reference);
  }
});

test("channel schema contains exactly the two verified exported YAML entries", () => {
  const text = read("skills/add-channel/references/channel-schema.md");
  const blocks = [...text.matchAll(/```yaml\r?\n([\s\S]*?)```/g)];
  assert.equal(blocks.length, 1, "keep one authoritative YAML example");
  assert.deepEqual(yaml.load(blocks[0][1]), {
    configuration: {
      channels: [
        { id: "MsTeams", channelId: "MsTeams" },
        { id: "Microsoft365Copilot", channelId: "Microsoft365Copilot" },
      ],
    },
  });
  const contract = text.replace(/\s+/g, " ");
  assert.match(contract, /only.*(?:two|2).*supported/i);
  assert.match(contract, /GitHub.*harness/i);
  assert.match(contract, /msteams.*MsTeams/);
  assert.match(contract, /copilot.*Microsoft365Copilot/i);
  assert.match(contract, /case-sensitive/i);
  assert.match(contract, /channelId.*not.*deployment.*ID/i);
});

test("channel authoring resolves workspace and harness before any edits", () => {
  const skill = normalized("skills/add-channel/SKILL.md");
  assert.match(skill, /settings\.mcs\.yml/);
  assert.match(skill, /(?:multiple|several).*ask/i);
  assert.match(skill, /no.*(?:workspace|match).*clone-agent/i);
  assert.match(skill, /CLI-authored MCS.*GitHub harness/i);
  assert.match(skill, /cliagent-/);
  assert.match(skill, /CliCopilot.*CLICopilotRecognizer/);
  assert.match(skill, /unknown.*ask.*before editing/i);
  assert.match(skill, /cannot be read, stop/i);
  assert.match(skill, /unsupported.*before.*(?:edit|writ)/i);
  assert.match(skill, /Do not partially/i);
});

test("channel contract requires additive idempotent edits without destructive normalization", () => {
  const contract = normalized("skills/add-channel/references/channel-schema.md");
  assert.match(contract, /absent.*create/i);
  assert.match(contract, /(?:scalar|null).*stop/i);
  assert.match(contract, /duplicate.*(?:mismatch|conflict).*stop/i);
  assert.match(contract, /already.*(?:present|configured).*no-op/i);
  assert.match(contract, /preserve.*extra.*fields/i);
  assert.match(contract, /unknown.*preserve/i);
  assert.match(contract, /append.*only.*missing.*requested/i);
  assert.match(contract, /Do not.*(?:remove|reorder)/i);
  assert.match(contract, /instructions.*model.*memory.*authentication/i);
  assert.match(contract, /parse.*(?:before|after)/i);
  assert.match(contract, /diff/i);
});

test("channel skill stays local and excludes the CodeAgent channel CLI", () => {
  const skill = normalized("skills/add-channel/SKILL.md");
  assert.match(skill, /not.*msagent deployment channel.*CodeAgent/i);
  assert.match(skill, /Never.*(?:pull|deploy).*publish/i);
  assert.match(skill, /\.config\/.*\.mcs\/.*agent\.sync\.yaml/);
  assert.match(skill, /push-agent.*publish-agent/);
  assert.match(skill, /(?:not|never).*claim.*(?:accessible|available|installed|published)/i);
  const schema = normalized("skills/add-channel/references/channel-schema.md");
  assert.match(schema, /No.*(?:new|additional).*file/i);
});

test("creation and architect require add-channel while describer reports file evidence", () => {
  for (const file of [
    "skills/create-agent/references/components.md",
    "agents/copilot-studio-architect.md",
  ]) {
    const text = normalized(file);
    assert.match(text, /skills\/add-channel\/SKILL\.md/);
    assert.match(text, /before.*(?:authoring|changing|adding).*channels/i);
    assert.match(text, /cannot be read.*stop/i);
  }
  const describer = normalized("agents/copilot-studio-describer.md");
  assert.match(describer, /configuration\.channels/);
  assert.match(describer, /MsTeams.*Microsoft365Copilot/);
  assert.match(describer, /absent.*empty.*(?:not|neither).*prov/i);
  assert.match(describer, /not.*(?:infer|claim).*(?:publish|publication)/i);
  const readme = normalized("README.md");
  assert.match(readme, /`add-channel`/);
  assert.match(readme, /nine skills/);
});
