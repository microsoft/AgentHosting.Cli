const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.resolve(__dirname, "..", "..");

function readSkill(file = "SKILL.md") {
  return fs.readFileSync(path.join(root, "skills", "create-agent", file), "utf8");
}

test("create follows the MSAGENT scaffold, deployment, deploy, and pull lifecycle", () => {
  const skill = readSkill();
  const create = skill.indexOf("msagent agent create --agent-type MCSAgent");
  const show = skill.indexOf("msagent agent show --project", create);
  const deploymentList = skill.indexOf("msagent deployment list --project", show);
  const deploymentCreate = skill.indexOf("msagent deployment create --project", deploymentList);
  const deploy = skill.indexOf("msagent agent deploy --project", deploymentCreate);
  const pull = skill.indexOf("msagent agent pull --project", deploy);

  assert.notEqual(create, -1);
  assert.notEqual(show, -1);
  assert.notEqual(deploymentList, -1);
  assert.notEqual(deploymentCreate, -1);
  assert.notEqual(deploy, -1);
  assert.notEqual(pull, -1);
  assert.ok(create < show);
  assert.ok(show < deploymentList);
  assert.ok(deploymentList < deploymentCreate);
  assert.ok(deploymentCreate < deploy);
  assert.ok(deploy < pull);
});

test("create checks only create help up front and treats later unknown options as failures", () => {
  const skill = readSkill();
  const recovery = readSkill("references/error-recovery.md");
  const helpChecks = [...skill.matchAll(/^msagent [a-z ]+--help$/gm)].map((match) => match[0]);

  assert.deepEqual(helpChecks, ["msagent agent create --help"]);
  assert.ok(skill.indexOf("msagent agent create --help") < skill.indexOf("msagent agent create --agent-type"));
  assert.match(skill, /current Microsoft Agent\s+Hosting CLI is required and stop/);
  assert.match(skill, /unknown command or\s+option later is a failure/i);
  assert.match(recovery, /Unknown command or option[\s\S]*current Microsoft Agent Hosting CLI is required[\s\S]*resume from the first incomplete phase/);
});

test("create uses the canonical agent-name option in its help check and command template", () => {
  const skill = readSkill();

  assert.match(skill, /Help must include[^\r\n]*`--agent-name`/);
  assert.match(skill, /^msagent agent create --agent-type MCSAgent --mcs-agent-type CLI --agent-name '<display-name>' --description '<description>' --initial-prompt '<instructions>' --environment-id '<environment-id>' --project '<project>' --json --non-interactive$/m);
  assert.doesNotMatch(skill, /--name\b/);
});

test("create enforces Preview-safe fields and MSAGENT identifiers", () => {
  const skill = readSkill();

  assert.match(skill, /Display name[\s\S]*1-30 characters/i);
  assert.match(skill, /Description[\s\S]*1,024 characters/i);
  assert.match(skill, /Initial prompt\/instructions[\s\S]*8,000 characters/i);
  const recovery = readSkill("references/error-recovery.md");
  assert.match(recovery, /Field validation fails[\s\S]*Never blindly truncate explicit user text/i);
  assert.match(recovery, /over 8,000 characters[\s\S]*every material requirement/i);
  assert.match(
    skill,
    /Passing values safely[\s\S]*field-specific checks[\s\S]*double quotes[\s\S]*control characters/i
  );
  assert.match(skill, /\*\*Bash\*\*[\s\S]*`Tom's Bot` becomes[\s\S]*`'Tom'\\''s Bot'`/i);
  assert.match(skill, /\*\*PowerShell\*\*[\s\S]*double each straight or curly single quote/i);
  assert.match(
    skill,
    /MSAGENT-derived schema name[\s\S]*native derivation[\s\S]*`cr_`[\s\S]*lowercased/i
  );
  assert.match(skill, /Do not pass an independently\s+calculated schema name/i);
  assert.match(
    skill,
    /returned `schemaName` is authoritative[\s\S]*Never replace it with an independently calculated value/i
  );
  assert.match(skill, /Development deployment name[\s\S]*fixed name `development`/i);
});

test("create preserves the registration root and CLI-managed metadata", () => {
  const skill = readSkill();

  assert.match(skill, /`--project` selects the registration root/i);
  assert.match(skill, /expected authored workspace is `<project-root>\/<filesystem-safe-display-name>`/i);
  assert.doesNotMatch(skill, /<project-root>\\<filesystem-safe-display-name>/i);
  assert.match(
    skill,
    /`'<project>'` in a command template means the \*\*registration root\*\*[\s\S]*never means the authored child workspace/i
  );
  assert.match(
    skill,
    /every remaining `'<project>'` placeholder means exactly that returned registration-root value/i
  );
  assert.match(skill, /Never remove, move, merge, or overwrite existing content/i);
  assert.match(skill, /project root does not exist or is empty, continue[\s\S]*read\s+\[.*?\]\(references\/existing-destination\.md\)[\s\S]*cannot\s+be read, stop/i);
  const destination = readSkill("references/existing-destination.md");
  assert.match(destination, /existing registered multi-agent project/i);
  assert.match(destination, /Never remove, move, merge,\s+or overwrite existing content/i);
  assert.match(destination, /require confirmation before creating/i);
  assert.match(destination, /expected authored workspace exists and contains anything, stop/i);
  assert.match(skill, /Leave\s+`\.config\/`, `\.mcs\/`, and `agent\.sync\.yaml` untouched/i);
});

test("create validates the MCS2 authoring shape and deploys only a dev draft", () => {
  const skill = readSkill();

  assert.match(skill, /root `template` beginning with `cliagent-`/i);
  assert.match(skill, /`configuration\.authoringModel: CliCopilot`/);
  assert.match(skill, /`CLIAgentRecognizer` or `CLICopilotRecognizer`/);
  assert.match(skill, /Never create default topics/i);
  assert.match(skill, /--deployment-name 'development' --deployment-type dev/);
  assert.match(skill, /Do not pass `--publish`, `--overwrite`, or `--schema-name`/i);
});

test("explicit publication intent routes through publish-agent after success", () => {
  const skill = readSkill() + "\n" + readSkill("references/publish-handoff.md");

  assert.match(skill, /^allowed-tools:.*\bSkill\b/m);
  assert.match(skill, /Record whether the initial request explicitly asks to publish/i);
  assert.match(skill, /Do not infer publication intent[\s\S]*create[\s\S]*deploy[\s\S]*test/i);
  assert.match(skill, /invoke `msagent-assistant:publish-agent`/);
  assert.match(
    skill,
    /exact\s+project root, local agent ID,\s+development deployment ID, target environment, cloud MCS agent ID/i
  );
  assert.match(skill, /initial create-and-publish wording is intent, not that final confirmation/i);
  assert.match(skill, /failed or completed only partially, do not\s+invoke the publish\s+skill/i);
  assert.doesNotMatch(skill, /```bash\s+msagent agent deploy[^\r\n]*--publish/i);
});

test("memory contract uses the synced Boolean setting and preserves existing choices", () => {
  const raw = readSkill("references/components.md");
  const text = raw.replace(/\s+/g, " ");
  const memory = raw.split("## Persistent user memory")[1];

  assert.ok(memory, "components must define the shared memory contract");
  const example = memory.match(/```yaml\r?\n([\s\S]*?)```/);
  assert.ok(example, "memory must include a settings YAML example");
  const yaml = require("js-yaml");
  assert.deepEqual(yaml.load(example[1]), {
    configuration: { agentSettings: { enableMemory: true } },
  });
  assert.match(text, /only when explicitly requested/i);
  assert.match(text, /preserve.*(?:true|false).*absent/i);
  assert.match(text, /Boolean `false`.*disable/i);
  assert.match(text, /absent.*unspecified.*not.*disabled/i);
  assert.match(text, /28 days.*inactivity/i);
  assert.match(text, /disabled in group chats and (?:Microsoft )?Teams channels/i);
  assert.match(text, /turning.*off.*does not delete/i);
  assert.match(text, /local.*not.*deploy.*publish/i);
  assert.match(text, /https:\/\/learn\.microsoft\.com\/en-us\/microsoft-copilot-studio\/agents-experience\/memory-overview/);
});

test("architect reads the shared memory contract before changing memory", () => {
  const text = fs.readFileSync(
    path.join(root, "agents", "copilot-studio-architect.md"), "utf8"
  ).replace(/\s+/g, " ");

  assert.match(text, /skills\/create-agent\/references\/components\.md#persistent-user-memory/);
  assert.match(text, /before.*(?:changing|authoring).*memory/i);
  assert.match(text, /preserve.*configuration\.agentSettings\.enableMemory/i);
  assert.match(text, /explicit.*(?:enable|disable)/i);
  assert.match(text, /cannot be read.*stop/i);
});

test("describer distinguishes enabled, disabled, and unspecified memory without runtime claims", () => {
  const text = fs.readFileSync(
    path.join(root, "agents", "copilot-studio-describer.md"), "utf8"
  ).replace(/\s+/g, " ");

  assert.match(text, /configuration\.agentSettings\.enableMemory/);
  assert.match(text, /`true`.*enabled.*`false`.*disabled.*absent.*unspecified/i);
  assert.match(text, /not.*(?:infer|claim).*stored memor/i);
});

test("guided design considers memory only when material and does not default it on", () => {
  const text = readSkill("references/design-interview.md").replace(/\s+/g, " ");

  assert.match(text, /persistent user memory/i);
  assert.match(text, /only.*material.*not already.*(?:supplied|answered|specified)/i);
  assert.match(text, /components\.md#persistent-user-memory/);
  assert.match(text, /do not.*enable.*by default/i);
});
