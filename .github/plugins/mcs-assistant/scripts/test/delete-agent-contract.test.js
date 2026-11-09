const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const skillDirectory = path.resolve(__dirname, "..", "..", "skills", "delete-agent");

function readSkill() {
  return fs.readFileSync(path.join(skillDirectory, "SKILL.md"), "utf8");
}

function readWorkspaceReference() {
  return fs.readFileSync(
    path.join(skillDirectory, "references", "unregistered-workspace.md"),
    "utf8"
  );
}

function readRegisteredFlow() {
  return readSkill() + "\n" + fs.readFileSync(
    path.join(skillDirectory, "references", "registered-project.md"), "utf8"
  );
}

test("unregistered deletion loads its skill-local instructions before acting", () => {
  const reference = readWorkspaceReference();
  const skill = readSkill();
  const route = skill.slice(skill.indexOf("## Unregistered agent workspace"));

  assert.match(route, /before reading its identity\s+or running any command/);
  assert.match(route, /read the complete\s+\[[^\]]+\]\(references\/unregistered-workspace\.md\)/);
  assert.match(skill, /bundled inside this skill's directory/);
  assert.match(route, /If the reference cannot be read, stop/);
  assert.match(reference, /### U1\./);
  assert.match(reference, /### U4\./);
  assert.doesNotMatch(skill, /### U[1-4]\./);
  assert.doesNotMatch(skill, /```bash\s+msagent agent init/);
  assert.doesNotMatch(skill + reference, /plugin-paths\.json|CLAUDE_PLUGIN_ROOT|mcs-assistant:/);
});

test("deletion requires typed confirmation and exact target selection", () => {
  const skill = readRegisteredFlow();

  assert.match(skill, /Before \*\*either\*\* `msagent agent init` or `msagent agent delete`/);
  assert.match(skill, /--non-interactive` bypasses the CLI confirmation/);
  assert.match(skill, /Ask the user to \*\*type the agent's display name exactly\*\*/);
  assert.match(skill, /ignoring\s+surrounding whitespace/);
  assert.match(skill, /not exactly `displayName`[\s\S]*nothing was deleted[\s\S]*stop/);
  assert.match(skill, /An empty answer is a decline; stop/);
  assert.match(skill, /Never substitute an agent the user did not name/);
  assert.match(skill, /`agentLocation`[\s\S]*canonical filesystem path[\s\S]*retained workspace constraint/);
  assert.match(skill, /missing or ambiguous match, stop before init or delete/);
  assert.match(skill, /No agent named and no workspace supplied/);
  assert.match(skill, /only the deployments whose `agentId` equals the chosen agent's/);
  assert.match(skill, /Require the user to explicitly acknowledge this[\s\S]*nothing\s+is deleted/);
  assert.match(
    skill,
    /msagent agent delete --project '<projectDir>' --agent-id '<agentId>' --json --non-interactive/
  );
  assert.match(skill, /--agent-id '<agentId>' --deployment-id '<deploymentId>'/);
  assert.doesNotMatch(skill, /```bash\s+msagent agent delete[^\r\n]*--(?:agent-)?name\b/);
});

test("command inputs retain GUID validation and Bash and PowerShell escaping", () => {
  const skill = readSkill();

  assert.ok(
    skill.includes(
      "^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$"
    )
  );
  assert.match(skill, /agentId[\s\S]*deploymentId[\s\S]*tenantId/);
  assert.match(skill, /double quotes, line breaks, and any control character/);
  assert.match(skill, /report the invalid value\s+and construct no command/);
  assert.ok(skill.includes("'Tom'\\''s Bot'"));
  assert.ok(skill.includes("'Tom''s Bot'"));
  for (const quote of ["'", "\u2018", "\u2019", "\u201a", "\u201b"]) {
    assert.ok(skill.includes("`" + quote + "`"));
  }
  assert.ok(skill.includes("'O\u2019\u2019Brien'"));
  assert.match(skill, /single-quoted literal/);
});

test("project discovery retains lookup precedence and explicit folder selection", () => {
  const skill = readRegisteredFlow();
  const discovery = skill.slice(skill.indexOf("### 2."), skill.indexOf("### 3."));
  const registered = discovery.indexOf("Glob: **/.config/agent.config.json");
  const unregistered = discovery.indexOf("Glob: **/.mcs/conn.json");

  assert.ok(registered >= 0 && unregistered > registered);
  assert.match(discovery, /Registered config in that folder or its parent/);
  assert.match(discovery, /Settings without a connection[\s\S]*nothing to delete/);
  assert.match(discovery, /direct children only \(one level\)/);
  assert.match(discovery, /require a numbered\s+choice even for one match/);
  assert.match(discovery, /never silently select a match/);
  assert.match(discovery, /Repeat until the user supplies a usable folder or stops/);
});

test("workspace identity parsing remains fail-closed before registration", () => {
  const reference = readWorkspaceReference();

  assert.match(reference, /Read these files with the Read tool\. Do not modify them/);
  assert.match(reference, /Any other backslash sequence[\s\S]*makes the value unreadable/);
  assert.match(reference, /key appears more than once/);
  assert.match(reference, /EnvironmentId` or `AgentId` is missing or empty[\s\S]*stop/);
  assert.match(reference, /schemaName` is missing, empty, or unreadable, stop/);
  assert.match(reference, /displayName` is missing, empty, or unreadable, stop/);
  assert.match(reference, /If it matches neither[\s\S]*Nothing is deleted/);
  assert.match(reference, /compare the trimmed answer with `displayName` exactly/);
});

test("registration verifies the binding and reconfirms existing registrations", () => {
  const reference = readWorkspaceReference() + "\n" + fs.readFileSync(
    path.join(skillDirectory, "references", "init-recovery.md"), "utf8"
  );

  assert.match(
    reference,
    /msagent agent init --agent-name '<displayName>' --mcs-agent-source '<workspaceDir>' --json --non-interactive/
  );
  assert.match(reference, /`agentType` is `MCSAgent`/);
  assert.match(reference, /`connected` is `true`/);
  assert.match(reference, /`environmentId` equals `EnvironmentId`[\s\S]*case-insensitive/);
  assert.match(reference, /If any check fails, \*\*do not delete\*\*/);
  assert.match(reference, /confirmation covers only a new registration[\s\S]*does not carry\s+over/);
  assert.match(reference, /`agentLocation` is `\.`[\s\S]*`mcsAgentId` equals `AgentId`/);
  assert.match(reference, /cloud target and orphan checks[\s\S]*ask\s+again for the typed display name/);
  assert.match(reference, /None or several[\s\S]*nothing was deleted[\s\S]*stop/i);
});

test("conditional error handling retains consent, timeout checks, and result reporting", () => {
  const skill = readRegisteredFlow() + "\n" + fs.readFileSync(
    path.join(skillDirectory, "references", "delete-recovery.md"), "utf8"
  );

  assert.match(skill, /On consent[\s\S]*re-run the \*\*same\*\* delete command once/);
  assert.match(skill, /Re-confirm \(step 5\) if any input changed/);
  assert.match(skill, /transport-timeout[\s\S]*Do not retry blindly[\s\S]*only with their consent/);
  assert.match(skill, /records-survived-cloud-delete/);
  assert.match(skill, /workspace-identity-mismatch[\s\S]*Do not edit/);
  assert.match(skill, /If `partialResult` is present, summarize it/);
  assert.match(skill, /mcsAgentAlreadyAbsent[\s\S]*only the records were removed/);
  assert.match(skill, /CLI's authoritative list[\s\S]*even if it differs from the step 4 preview/);
});

test("a standalone skill-directory copy includes the unregistered deletion flow", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "delete-agent-skill-"));

  try {
    const standalone = path.join(directory, "delete-agent");
    fs.cpSync(skillDirectory, standalone, { recursive: true });
    const skill = fs.readFileSync(path.join(standalone, "SKILL.md"), "utf8");
    const reference = fs.readFileSync(
      path.join(standalone, "references", "unregistered-workspace.md"),
      "utf8"
    );

    assert.ok(skill.includes("references/unregistered-workspace.md"));
    assert.match(reference, /### U1\.[\s\S]*### U2\.[\s\S]*### U3\.[\s\S]*### U4\./);
    assert.match(reference, /Use `projectDirectory` and `agentId` from the U3 result/);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
