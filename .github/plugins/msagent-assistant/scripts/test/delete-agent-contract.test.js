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
  assert.doesNotMatch(skill + reference, /plugin-paths\.json|CLAUDE_PLUGIN_ROOT|msagent-assistant:/);
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

test("registered deletion confirms the actual binding rather than the home environment", () => {
  const text = readRegisteredFlow().replace(/\s+/g, " ");
  const binding = text.indexOf("If `.mcs/conn.json` exists, read it");
  const confirmation = text.indexOf("### 5. Confirm");
  const revalidation = text.indexOf("Immediately before deletion, re-run step 3");
  const deletion = text.indexOf("```bash msagent agent delete", revalidation);
  assert.ok(binding >= 0 && binding < confirmation);
  assert.ok(revalidation > confirmation && deletion > revalidation);
  assert.match(text, /show that bound cloud ID and environment, never the record's home environment/);
  assert.match(text, /unreadable or incomplete binding is an error, not permission to fall back/);
  assert.match(text, /Only when the workspace exists and has no binding/);
  assert.match(text, /Workspace absent[\s\S]*records-only deletion with no cloud target/);
  assert.match(text, /If the schema or target environment cannot be established, stop before confirmation/);
  assert.match(text, /binding appearing, disappearing, or changing cloud ID or environment is a target change/);
  assert.match(text, /Stop and repeat steps 4-5 with fresh typed confirmation/);
  assert.doesNotMatch(text, /home environment and its default deletion target/);
});

test("missing workspaces take records-only scope before every deployment branch", () => {
  const text = readRegisteredFlow().replace(/\s+/g, " ");
  const missing = text.indexOf("**Workspace absent**");
  const branches = ["**Workspace present, no deployments**", "**Workspace present, one deployment**",
    "**Workspace present, several deployments**"];
  for (const heading of branches) assert.ok(missing >= 0 && text.indexOf(heading) > missing);
  assert.match(text, /even when deployments exist or one was selected/);
  assert.match(text, /Pass no deployment selector for records-only cleanup/);
  assert.match(text, /Treat \*\*all\*\* recorded cloud targets as potentially surviving and becoming untracked/);
  assert.match(text, /including any originally selected deployment/);
  assert.match(text, /records-only cleanup[\s\S]*replace the remote deletion warning/);
  assert.match(text, /switch between records-only and cloud-deletion scope[\s\S]*fresh typed confirmation/);
  assert.match(text, /flag alone does \*\*not\*\* prove remote absence/);
  assert.match(text, /cloud existence was not checked and all previously recorded cloud targets may still exist/);
  assert.doesNotMatch(text, /no cloud agent existed|Remind the user the local workspace is still on disk|which agent was deleted and from which environment/);
});

test("registered deletion recognizes root markers rather than just a surviving directory", () => {
  const text = readRegisteredFlow().replace(/\s+/g, " ");
  const predicate = text.indexOf("Match the CLI's predicate:");
  const absent = text.indexOf("**Workspace absent**");
  assert.ok(predicate >= 0 && absent > predicate);
  assert.match(text, /`settings\.mcs\.yml` \*\*or\*\* `agent\.mcs\.yml` must exist directly in that directory/);
  assert.match(text, /Directory existence alone is insufficient/);
  assert.match(text, /`\.mcs\/conn\.json` or `collection\.mcs\.yml` alone does not qualify/);
  assert.match(text, /"workspace absent" includes an existing directory with neither root marker/);
  const revalidation = text.slice(text.indexOf("Immediately before deletion"));
  assert.match(revalidation, /repeat step 4's root-marker predicate, even if the directory and binding are unchanged/);
  assert.match(revalidation, /only for a recognized MCS root, re-read any present binding/);
  assert.match(revalidation, /Losing both root markers or gaining either one[\s\S]*fresh steps 4-5 consent, including the typed name/);
  assert.match(text, /If marker presence cannot be checked, stop and report the error/);
});

test("unregistered deletion compares the approved identity before init and after registration", () => {
  const text = readWorkspaceReference().replace(/\s+/g, " ");
  const beforeInit = text.indexOf("Immediately before initialization");
  const init = text.indexOf("msagent agent init --agent-name");
  const show = text.indexOf("msagent agent show --project", init);
  assert.ok(beforeInit >= 0 && init > beforeInit && show > init && show < text.indexOf("### U4."));
  assert.match(text, /re-read both identity files using U1's parsing rules and compare them with those approved values/);
  assert.match(text, /changed or cannot be read, stop before init or delete/);
  assert.match(text, /Require no deployments owned by that record/);
  assert.match(text, /`mcsAgentId` must equal the approved cloud `AgentId`/);
  assert.match(text, /`mcsSchemaName` must equal the approved `schemaName`/);
  assert.match(text, /`environmentId` must equal the approved `EnvironmentId`/);
  assert.match(text, /require the approved canonical workspace path/);
  assert.match(text, /Re-read the current connection and settings with U1's rules/);
  assert.match(text, /Never use the cloud `AgentId` as the local `--agent-id`/);
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
