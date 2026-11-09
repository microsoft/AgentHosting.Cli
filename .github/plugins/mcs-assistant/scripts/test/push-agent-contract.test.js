const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.resolve(__dirname, "..", "..");

function read(relativePath) {
  return fs.readFileSync(path.join(root, relativePath), "utf8");
}

test("management delegates lifecycle requests before listing authentication", () => {
  const manager = read("agents/copilot-studio-manage.md");
  const workflowSelection = manager.indexOf("Select the workflow before authentication");
  const listingAuthentication = manager.indexOf("Authenticate only for listing here");

  assert.notEqual(workflowSelection, -1);
  assert.notEqual(listingAuthentication, -1);
  assert.ok(workflowSelection < listingAuthentication);
  for (const skill of ["create", "clone", "pull", "push", "publish", "delete"]) {
    assert.ok(manager.includes(`mcs-assistant:${skill}-agent`), skill);
  }
  assert.match(manager, /msagent agent list --environment-id/);
  assert.match(manager, /truncated[\s\S]*partial/);
});

test("migration initialization uses the guarded create workflow for a separate draft", () => {
  const init = read("agents/copilot-studio-init.md").replace(/\s+/g, " ");

  assert.match(init, /Invoke `mcs-assistant:create-agent` with the Skill tool/);
  assert.match(init, /instructions-only[\s\S]*no optional skills, knowledge, tools, channels, or publication/);
  assert.match(init, /target schema must differ from the source schema, ignoring case/);
  assert.match(init, /new project root that does not already exist/);
  assert.match(init, /normalization[\s\S]*ask for a distinct container name/);
  assert.match(init, /reported registration root equals the approved root/);
  assert.match(init, /must be outside the source workspace and must not contain the source workspace/);
  assert.match(init, /schema[\s\S]*already exists[\s\S]*stop/);
  assert.match(init, /truncated[\s\S]*stop/);
  assert.match(init, /cloud MCS agent ID must differ from the source agent's cloud ID/);
  assert.match(init, /draft deployment and final pull successfully/);
  assert.match(init, /partial side effects[\s\S]*do not retry creation/);
});

test("migration carries the registered target identity through its draft upload", () => {
  const migration = read("commands/migrate.md").replace(/\s+/g, " ");
  const initialization = migration.indexOf("### 4.");
  const conversion = migration.indexOf("### 6.");
  const architect = migration.indexOf("### 7.");
  const upload = migration.indexOf("### 8.");
  assert.ok(initialization >= 0 && conversion > initialization &&
    architect > conversion && upload > architect);
  assert.match(migration, /target cloud ID differs from the source cloud ID/);
  assert.match(migration, /Invoke `mcs-assistant:push-agent` with the Skill tool[\s\S]*registration root, local agent ID, development deployment ID, and environment ID/);
  assert.match(migration, /Do not pass the authored child workspace as `--project`/);
  assert.match(migration, /do not recreate the target or repeat a completed connection-reference declaration/i);
  assert.match(migration, /Do not blanket-rename files or prefix uploaded-file\/skill payload sidecars/);
});

test("migration checkpoints initialization before creation without approving behavior", () => {
  const migration = read("commands/migrate.md").replace(/\s+/g, " ");
  const bootstrap = migration.indexOf("Write the bootstrap checkpoint");
  const initialization = migration.indexOf("Delegate initialization");

  assert.ok(bootstrap >= 0 && initialization > bootstrap);
  assert.match(migration, /before invoking the initializer/);
  assert.match(migration, /If this write fails, stop before target creation/);
  assert.match(migration, /`planApproved: false`, `initializationComplete: false`/);
  assert.match(migration, /root is absent and all phases are pending, continue fresh initialization with the saved approved target/);
  assert.match(migration, /root is missing after any phase was attempted or completed, stop for user resolution rather than recreating it/);
  assert.match(migration, /initialization is complete but `planApproved` is false, skip target creation and continue steps 5-5b/);
  assert.match(migration, /Only an approved plan can resume tool migration, architect, or push/);
  assert.match(migration, /update the existing sibling checkpoint[\s\S]*`planApproved: true`[\s\S]*preserving initialization phases and identities/);
  assert.match(migration, /Do not create a second plan file/);
});

test("initializer permits only verified checkpoint resumes of existing targets", () => {
  const init = read("agents/copilot-studio-init.md").replace(/\s+/g, " ");

  assert.match(init, /existing root is allowed only for a matching checkpoint resume verified through the create skill's local CLI projections/);
  assert.match(init, /Without that checkpoint, stop/);
  assert.match(init, /For fresh initialization only, resolve the proposed schema/);
  assert.match(init, /do not reject its own cloud draft as a new collision/);
  assert.match(init, /If initialization is already complete[\s\S]*without invoking creation again/);
  assert.match(init, /absolute \*\*migration initialization checkpoint\*\* path/);
  assert.match(init, /target root is absent and every phase is pending, use fresh initialization, not resume mode/);
  assert.match(init, /checkpoint-resume mode when the target exists or any phase has been attempted or completed/);
  assert.match(init, /`initializationComplete: true` to be saved in the checkpoint before reporting completion/);
});

test("migration creation records each phase and reconciles uncertain outcomes before resuming", () => {
  const create = read("skills/create-agent/SKILL.md").replace(/\s+/g, " ");
  const checkpoint = create.slice(create.indexOf("#### Migration initialization checkpoint"), create.indexOf("### 6."));

  for (const phase of ["scaffold", "deployment-slot", "draft-deploy", "final-pull"]) {
    assert.ok(checkpoint.includes(`\`${phase}\``), phase);
  }
  assert.match(checkpoint, /Before each phase's command, save it as `in-progress`[\s\S]*save it as `complete`/);
  assert.match(checkpoint, /If a checkpoint write fails, stop before the next command/);
  assert.match(checkpoint, /existence of the checkpoint alone does not require resume mode/);
  assert.match(checkpoint, /reconcile first using `agent show` and `deployment list`/);
  assert.match(checkpoint, /ask the user to confirm that recovered target/);
  assert.match(checkpoint, /bypasses step 6's new-destination path and the `agent create` command in step 7, not its workspace\/result checks/);
  assert.match(checkpoint, /if none exists and that phase has never completed, follow step 10 to create it once/);
  assert.match(checkpoint, /Never blindly replay an ambiguous deploy/);
  assert.match(checkpoint, /deploy is verified complete but final pull is not, resume only the pull after user consent/);
  assert.match(checkpoint, /Never repeat `agent create` or create a duplicate deployment/);
});

test("migration discovers settings-based and legacy source workspaces without duplicate candidates", () => {
  const migration = read("commands/migrate.md");
  const discovery = migration.slice(migration.indexOf("### 2."), migration.indexOf("### 4."));

  assert.match(discovery, /Glob: \*\*\/settings\.mcs\.yml/);
  assert.match(discovery, /Glob: \*\*\/agent\.mcs\.yml/);
  assert.match(discovery, /Group matches by workspace directory[\s\S]*one candidate/);
  assert.match(discovery, /workspace path and confirm that `settings\.mcs\.yml` or the legacy `agent\.mcs\.yml` exists/);
});

test("migration resolves source identity settings-first and rejects invalid metadata before creation", () => {
  const migration = read("commands/migrate.md").replace(/\s+/g, " ");
  const identity = migration.slice(migration.indexOf("### 4."), migration.indexOf("#### 4a."));

  assert.match(identity, /If `settings\.mcs\.yml` exists[\s\S]*top-level `displayName` and `schemaName`/);
  assert.match(identity, /identity file even when `agent\.mcs\.yml` is also present/);
  assert.match(identity, /Only when `settings\.mcs\.yml` is absent[\s\S]*legacy `agent\.mcs\.yml`/);
  assert.match(identity, /Require a nonempty `schemaName`/);
  assert.match(identity, /unreadable, invalid, or missing that field, stop before target creation/);
  assert.match(identity, /Do not fall back from an existing settings file to `agent\.mcs\.yml`/);
  assert.match(identity, /If `displayName` is missing, use the source `schemaName` as the display-name fallback/);
  assert.match(identity, /If an explicit `displayName` is empty, stop and ask for the source display name before target creation/);
  assert.match(identity, /`EnvironmentId` and `AgentId`\. Require both before continuing/);
  assert.match(identity, /never modify the source files or binding/);
});

test("connector migration keeps declarations resumable and requires verified bindings", () => {
  const migration = read("commands/migrate.md").replace(/\s+/g, " ");

  assert.match(migration, /connection add --project '<target-project-root>' --agent-id '<target-local-agent-id>' --deployment-id '<target-deployment-id>' --connector '<connector-id>'/);
  assert.match(migration, /Record the returned reference logical name immediately/);
  assert.match(migration, /compare those internal IDs case-insensitively without changing the tool's `connectorId`/);
  assert.match(migration, /connection-reference-declared[\s\S]*is \*\*not\*\* a usable binding/);
  assert.match(migration, /connection-reference-bound[\s\S]*ready: true[\s\S]*connectionId/);
  assert.match(migration, /Never retry `add` after a declaration or an ambiguous failure/);
  assert.match(migration, /connection update[\s\S]*--connection-reference '<target-reference-logical-name>'[\s\S]*--connection-id '<connection-id>'/);
  assert.match(migration, /re-read the target's references with `connection list`/);
  assert.match(migration, /state: "undeclared"[\s\S]*Never bind or adopt those source references/);
  assert.match(migration, /Require the selected row[\s\S]*resolved `state`/);
  assert.match(migration, /After all retained tools have been retargeted[\s\S]*connectionsReady: true/);
  assert.match(migration, /Never modify the source agent or rebind its existing connection references/);
  assert.match(migration, /Stop before architect implementation while any retained connector tool has an unresolved binding/);
});

test("migration preserves distinct source references and authentication contexts for one connector", () => {
  const migration = read("commands/migrate.md").replace(/\s+/g, " ");

  assert.match(migration, /tuple of normalized connector ID, original source `connectionReference`, and `authMode`, not by connector ID alone/);
  assert.match(migration, /Tools may share a target reference only when all three context fields match/);
  assert.match(migration, /Two Maker tools[\s\S]*different source references must remain separate, as must Maker and Invoker contexts/);
  assert.match(migration, /Keep target reference logical names distinct across contexts/);
  assert.match(migration, /update only the checkpointed tools in that source context[\s\S]*preserving `authMode` and all other tool fields/);
  assert.match(migration, /Verify every tool against its checkpointed source context[\s\S]*exact mapped target reference, and its approved binding/);
  const upload = migration.slice(migration.indexOf("### 8."));
  assert.match(upload, /After architect edits, repeat step 6's per-tool context and binding verification with a fresh `connection list`/);
  assert.match(upload, /Stop before push[\s\S]*no verified context mapping[\s\S]*`authMode` or target reference changed[\s\S]*binding no longer matches the approved context/);
  assert.doesNotMatch(migration, /Group tools by the exact connector ID|one dedicated target reference per connector/);
});

test("migration resume reconciles original contexts instead of reusing connector-wide mappings", () => {
  const migration = read("commands/migrate.md").replace(/\s+/g, " ");

  assert.match(migration, /Before any rewrite, checkpoint each source context with its original fields and exact tool paths/);
  assert.match(migration, /On resume, use this saved inventory and verify it against the approved source\/conversion report/);
  assert.match(migration, /never interpret an already rewritten target reference as a new source context/);
  assert.match(migration, /legacy checkpoint lacking this inventory, reconstruct it from the approved original source actions, not the rewritten target YAML/);
  assert.match(migration, /Reuse an already recorded target reference only for that same source context/);
  assert.match(migration, /legacy connector-only checkpoint[\s\S]*exactly one source context[\s\S]*binding is verified/);
  assert.match(migration, /If it spans multiple contexts, stop before any add, update, or tool rewrite/);
  assert.match(migration, /If checkpointing fails, stop before another connection mutation or tool rewrite/);
});

test("migration requires per-context connection choices and explicit account consolidation approval", () => {
  const migration = read("commands/migrate.md").replace(/\s+/g, " ");

  assert.match(migration, /multiple source contexts, require an explicit user-selected target connection ID for each context before declaring or binding it/);
  assert.match(migration, /same target `connectionId` for distinct contexts, obtain explicit account-consolidation approval[\s\S]*checkpoint it before mutation/);
  assert.match(migration, /Keep their target references separate even with that approval/);
  assert.match(migration, /this option is mandatory and must carry that context's approved connection ID, including on retries/);
  assert.match(migration, /resolve an eligible connection only for a connector with one source context/);
  assert.match(migration, /bound `connectionId` to equal the context's approved ID/);
  assert.match(migration, /Whether the binding came from `add`, `update`, or reuse on resume, re-read the target's references with `connection list`/);
  assert.match(migration, /shared connection ID has the recorded account-consolidation approval[\s\S]*Apply these checks to reused bindings on resume/);
  assert.match(migration, /Approval of a connector set alone is not approval to consolidate accounts/);
});

test("thin chat preserves exact targeting and never automatically publishes or replays", () => {
  const chat = read("commands/chat.md").replace(/\s+/g, " ");

  assert.match(chat, /allowed-tools:[^#]*\bSkill\b/);
  assert.match(chat, /msagent test ask --help/);
  assert.match(chat, /--deployment-id '<deployment-id>'/);
  assert.match(chat, /never silently use the development\/default deployment/);
  assert.match(chat, /local managed-agent ID is not a cloud MCS ID/);
  assert.match(chat, /Keep all selectors identical between turns/);
  assert.match(chat, /first turn omits `--conversation-id`/);
  assert.match(chat, /exact ID returned by the successful prior turn for this same target/);
  assert.match(chat, /Never replay a query automatically/);
  assert.match(chat, /Do not auto-publish, retarget, change clouds, or retry with another account/);
  assert.match(chat, /mcs-assistant:publish-agent[\s\S]*exact deployment constraints/);
  assert.match(chat, /chat retry still requires the user's explicit request/);
  assert.doesNotMatch(chat, /node .+chat-with-agent|--set-client-id|--direct-connect-url/);
});

test("deployment stage and effective environment use the MSAGENT projection fields", () => {
  const skill = read("skills/push-agent/SKILL.md");

  assert.match(skill, /`deploymentType` as its stage/i);
  assert.match(
    skill,
    /`environmentId` when present; otherwise use the owning\s+agent's `environmentId`/i
  );
  assert.match(skill, /dev deployment has `deploymentType: "dev"`/i);
});

test("pathless project discovery checks enclosing projects before descendants", () => {
  const skill = read("skills/push-agent/references/project-discovery.md");
  const upwardSearch = skill.indexOf("walk upward through up to five parent");
  const descendantSearch = skill.indexOf("search descendants");

  assert.notEqual(upwardSearch, -1);
  assert.notEqual(descendantSearch, -1);
  assert.ok(upwardSearch < descendantSearch);
});

test("deployment selection combines every supplied selector before using the dev default", () => {
  const skill = read("skills/push-agent/SKILL.md");

  assert.match(
    skill,
    /matching all supplied deployment\s+selectors: `deploymentId`, exact `deploymentName`, `deploymentType` \(stage\), and `environmentId`/i
  );
  assert.match(skill, /Treat multiple selectors as cumulative constraints/i);
  assert.match(skill, /Only when no deployment selector was supplied/i);
  assert.match(skill, /Never replace an explicit selector with a\s+default/i);
});

test("push retains exact workspace selection and resolves supplied cloud IDs", () => {
  const skill = read("skills/push-agent/SKILL.md").replace(/\s+/g, " ");
  assert.match(skill, /`agentLocation` relative to the project root[\s\S]*exactly one canonical path match/);
  assert.match(skill, /all supplied agent selectors as cumulative constraints/);
  assert.match(skill, /missing or ambiguous match, stop[\s\S]*never substitute the project's sole agent/);
  assert.doesNotMatch(skill, /Exact cloud-ID requests \(blocking\)/);
  assert.match(skill, /`mcsAgentId` must first be mapped to its local record/);
  assert.match(skill, /cloud `mcsAgentId` is also an explicit deployment constraint/);
  assert.match(skill, /require its `environmentId` and `schemaName`; stop and ask/);
  assert.match(skill, /Never use the dev default for a supplied cloud target/);
});

test("push verifies supplied cloud targets before pulling and deploying and checks the result", () => {
  const skill = read("skills/push-agent/SKILL.md").replace(/\s+/g, " ");
  const preflight = skill.indexOf("#### Verify a supplied cloud target");
  const pull = skill.indexOf("msagent agent pull --project");
  const revalidation = skill.indexOf("repeat the cloud-target lookup above after reconciliation");
  const deploy = skill.indexOf("msagent agent deploy --project");
  const verification = skill.slice(skill.indexOf("### 8."));

  assert.ok(preflight >= 0 && preflight < pull && revalidation > pull && revalidation < deploy);
  assert.match(skill, /msagent agent list --environment-id '<expected-environment-id>' --limit 1000 --json --non-interactive/);
  assert.match(skill, /exactly one row in `agents\[\]` matching the expected `mcsAgentId`, `schemaName`, and `environmentId`/);
  assert.match(skill, /Stop before pull or deploy if the target is missing, different, ambiguous, or the lookup fails/);
  assert.match(skill, /`truncated: true` is inconclusive[\s\S]*stop rather than selecting another target/);
  assert.match(skill, /same local agent, workspace, deployment, effective environment, cloud ID, and schema; stop if any identity changed/);
  assert.match(skill, /require the schema to equal the expected `schemaName`/);
  assert.match(verification, /`targetMcsAgentId` to equal the expected `mcsAgentId` and `targetEnvironmentId` to equal the expected `environmentId`/);
  assert.match(verification, /mismatch is a verification failure[\s\S]*do not claim the requested cloud agent was updated or retry against another deployment/);
});

test("the pre-push pull is skipped and unblocked end-to-end for a never-connected agent", () => {
  const skill = read("skills/push-agent/SKILL.md") + "\n" +
    read("skills/push-agent/references/error-recovery.md");

  // Regression guard for microsoft/AgentHosting.Cli#22: a never-deployed first push must not
  // block on the pre-push pull or on a missing .mcs/conn.json, and workspace-disconnected must
  // be documented in the error-handling table.
  assert.match(skill, /Skip this pull when the selected agent has never been deployed or connected/i);
  assert.match(skill, /no `mcsAgentId`/);
  assert.match(
    skill,
    /a never-connected agent \(no `mcsAgentId`\) has not yet\s+created `\.mcs\/conn\.json`/i
  );
  assert.match(skill, /\|\s*`workspace-disconnected`/);
});
