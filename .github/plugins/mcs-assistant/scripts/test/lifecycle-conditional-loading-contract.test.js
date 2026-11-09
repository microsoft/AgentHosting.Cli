const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");

const root = path.resolve(__dirname, "..", "..");

function read(skill, file = "SKILL.md") {
  return fs.readFileSync(path.join(root, "skills", skill, file), "utf8").replace(/\s+/g, " ");
}

function section(text, heading, nextHeading) {
  const start = text.indexOf(heading);
  const end = text.indexOf(nextHeading, start);
  assert.ok(start >= 0 && end > start, `Missing section boundaries: ${heading}`);
  return text.slice(start, end);
}

test("create defers the optional interview without blocking instructions-only creation", () => {
  const skill = section(read("create-agent"), "### 3.", "### 4.");
  assert.match(skill, /guided design[\s\S]*read \[.*?\]\(references\/design-interview\.md\)[\s\S]*before asking/i);
  assert.match(skill, /Guided design is optional/);
  assert.match(skill, /Never create default topics/);
  assert.match(skill, /If the reference cannot be read, stop and report it/);
  assert.doesNotMatch(skill, /across \*\*Skills\*\*|concrete SharePoint locations/);
  const interview = read("create-agent", "references/design-interview.md");
  assert.match(interview, /\*\*Skills\*\*[\s\S]*\*\*Tools and workflows\*\*[\s\S]*\*\*Instructions\*\*[\s\S]*\*\*Data and knowledge\*\*[\s\S]*\*\*Settings\*\*/);
  assert.match(interview, /duplicate questions|repeat information/i);
});

test("create defers component authoring but retains its schema and metadata safeguards", () => {
  const skill = section(read("create-agent"), "### 8.", "### 9.");
  assert.match(skill, /Only when[\s\S]*accepted requirements[\s\S]*read\s+\[.*?\]\(references\/components\.md\)[\s\S]*before editing/i);
  assert.match(skill, /Leave\s+`\.config\/`, `\.mcs\/`, and `agent\.sync\.yaml` untouched/i);
  assert.match(skill, /If the reference cannot be read, stop before those edits and report it/);
  assert.doesNotMatch(skill, /reusable multi-step procedures|capabilities\/tools\//);
  const components = read("create-agent", "references/components.md");
  assert.match(components, /skills\/add-knowledge\/references\/knowledge-schema\.md/);
  assert.match(components, /skills\/add-skill\/references\/skill-schema\.md/);
  assert.match(components, /selected source or variant/);
  assert.match(components, /cannot be read, stop before authoring/);
  assert.match(components, /Metadata-only sidecars[\s\S]*only `mcs\.metadata`/);
  assert.match(components, /at most 100 characters/);
  assert.match(components, /Do not invent\s+connections/);
});

test("create loads publication handoff only for explicit intent after complete success", () => {
  const skill = section(read("create-agent"), "### 12.", "### 13.");
  const handoff = read("create-agent", "references/publish-handoff.md");
  assert.match(skill, /If the initial request included explicit publication intent[\s\S]*read \[.*?\]\(references\/publish-handoff\.md\)/);
  assert.match(skill, /failed or completed only partially, do not\s+invoke the publish\s+skill/);
  assert.match(skill, /If the reference cannot be read, stop and report it/);
  assert.doesNotMatch(skill, /invoke `mcs-assistant:publish-agent`/);
  assert.match(handoff, /invoke `mcs-assistant:publish-agent`/);
  assert.doesNotMatch(handoff, /handoff as blocked|cannot pin deploy/);
  assert.match(handoff, /initial create-and-publish wording is intent, not that final confirmation/i);
  assert.match(handoff, /pull before deploying/);
  assert.match(handoff, /publish skill is unavailable[\s\S]*publication was not performed, then stop/);
  assert.match(handoff, /Do not improvise an inline publishing command/);
});

test("clone loads display-name lookup only when an exact source selector is unavailable", () => {
  const skill = read("clone-agent");
  const resolution = section(skill, "### 4.", "### 5.");
  assert.match(resolution, /only a display name[\s\S]*read\s+\[.*?\]\(references\/source-lookup\.md\)[\s\S]*before listing/i);
  assert.match(resolution, /If the reference cannot be read, stop and report it/);
  assert.doesNotMatch(skill, /msagent agent list --environment-id/);
  assert.match(skill, /Never substitute a different agent/);
  const lookup = read("clone-agent", "references/source-lookup.md");
  assert.match(lookup, /--limit 1000 --json --non-interactive/);
  assert.match(lookup, /displayName` case-insensitively/);
  assert.match(lookup, /after the user confirms the match/);
  assert.match(lookup, /several rows match[\s\S]*exact `mcsAgentId`/);
  assert.match(lookup, /`truncated` is true, do not claim the agent\s+is absent/);
  assert.match(lookup, /accept an explicit ID or schema name even when listing cannot find it/);
});

test("clone keeps URL validation, destination protection and local result verification mandatory", () => {
  const skill = read("clone-agent");
  assert.match(skill, /percent-decode those two path segments/);
  assert.match(skill, /explicit agent ID must be a GUID/);
  assert.match(skill, /Always pass `--mcs-clone-environment-id`/);
  assert.match(skill, /Do not also pass `--environment-id`/);
  assert.match(skill, /Never edit\s+`\.config\/` or `\.mcs\/` by hand/);
  assert.match(skill, /success: true`[\s\S]*createdFrom: "clone"/);
  assert.match(skill, /every path in `clonedWorkspaces` exists/);
  assert.match(skill, /markers are absent, report verification failure rather than claiming success/);
});

for (const skill of ["push-agent", "pull-agent", "publish-agent"]) {
  test(`${skill} defers fallback discovery without changing enclosing-project precedence`, () => {
    const entrypoint = section(read(skill), "### 2.", "### 3.");
    assert.match(entrypoint, /directory containing `\.config\/agent\.config\.json`, use it/);
    assert.match(entrypoint, /Otherwise, read\s+\[.*?\]\(references\/project-discovery\.md\)[\s\S]*before resolving the root/);
    assert.match(entrypoint, /If the reference cannot be read, stop and report it/);
    assert.doesNotMatch(entrypoint, /walk upward through up to five parent|search descendants/);
    const discovery = read(skill, "references/project-discovery.md");
    const upward = discovery.indexOf("walk upward through up to five parent");
    const descendants = discovery.indexOf("search descendants");
    assert.notEqual(upward, -1);
    assert.notEqual(descendants, -1);
    assert.ok(upward < descendants);
    assert.match(discovery, /walk only its parent directories[\s\S]*nearest/);
    assert.match(discovery, /several descendant project roots match[\s\S]*ask the user to choose/);
    assert.match(discovery, /If no project root is found, stop/);
  });
}

test("push recovery does not treat a disconnected error as proof of a safe first push", () => {
  const recovery = read("push-agent", "references/error-recovery.md");
  assert.match(recovery, /`workspace-disconnected`[\s\S]*step 6's never-connected predicate/);
  assert.match(recovery, /Otherwise stop[\s\S]*Ask before restarting the sequence/);
  assert.match(recovery, /deploy-recorded-nowhere[\s\S]*cloud content was updated/);
});

test("push keeps the first-push predicate and connected synchronization mandatory", () => {
  const skill = read("push-agent");
  assert.match(skill, /never-connected when it has no cloud agent yet \(no `mcsAgentId`\) and the selected deployment has never been pushed/);
  assert.match(skill, /Skip this pull when the selected agent has never been deployed or connected/);
  assert.match(skill, /Never\s+silently choose the first agent or deployment/);
  assert.match(skill, /a never-connected agent \(no `mcsAgentId`\) has not yet created `\.mcs\/conn\.json`/);
  assert.match(skill, /For a test or prod deployment[\s\S]*require explicit confirmation for the prerequisite pull/);
  assert.match(skill, /pull request alone|push request alone is not consent/i);
  assert.match(skill, /merge conflict or another failure, stop[\s\S]*do not deploy/i);
  assert.match(skill, /`published: false`/);
  const commands = [...skill.matchAll(/```bash (msagent agent deploy --project [^`]+)```/g)];
  assert.equal(commands.length, 1);
  assert.doesNotMatch(commands[0][1], /--publish|--overwrite|--schema-name/);
});

test("test and prod require confirmation even when a first push skips the pull", () => {
  const effects = section(read("push-agent"), "### 5.", "### 6.");
  const stageConsent = effects.slice(effects.indexOf("For a test or prod deployment"),
    effects.indexOf("For every connected agent"));
  assert.match(stageConsent, /require explicit confirmation before deployment, even when the agent has never been connected/);
});

test("pull preserves explicit selection, connected-workspace checks and local-write consent", () => {
  const skill = read("pull-agent");
  assert.match(skill, /Never replace an explicit selector mismatch/);
  assert.match(skill, /plus `\.mcs\/conn\.json`/);
  assert.match(skill, /never reset, stash, delete, or automatically restore local files/i);
  assert.match(skill, /accept the caller's confirmation only if it covers this exact target, risk, and preservation plan/);
  assert.match(skill, /status` equal to `agent-pulled` or `up-to-date`/);
  assert.match(skill, /`mode: "pull"`/);
  const recovery = read("pull-agent", "references/error-recovery.md");
  assert.match(recovery, /merge conflict[\s\S]*Never discard either side/);
  assert.match(recovery, /Never fall back to `msagent agent create`/);
});

test("publish retains live-change confirmation, synchronization and partial-side-effect authority", () => {
  const skill = read("publish-agent");
  const confirmation = skill.indexOf("Publishing always requires explicit confirmation at this point");
  const pull = skill.indexOf("msagent agent pull --project", confirmation);
  const deploy = skill.indexOf("msagent agent deploy --project", pull);
  assert.notEqual(confirmation, -1);
  assert.ok(pull > confirmation);
  assert.ok(deploy > pull);
  assert.match(skill, /For test or prod, explicitly warn that target drift is overwritten/);
  assert.match(skill, /another failure, stop[\s\S]*do not deploy, publish/i);
  assert.match(skill, /`published: true` and a nonempty `publishedOn`/);
  assert.match(skill, /--deployment-id '<deployment-id>' --publish --json --non-interactive/);
  assert.match(skill, /Treat `partialResult` as authoritative for completed side effects/);
  const recovery = read("publish-agent", "references/error-recovery.md");
  assert.match(recovery, /publish-withheld-incomplete-push[\s\S]*agent itself was not published/);
  assert.match(recovery, /publish-failed-after-push[\s\S]*do not claim the agent is live/);
  assert.match(recovery, /then reconfirm publish/);
  assert.match(recovery, /Never bypass the documented MSAGENT publish workflow/);
});

for (const name of ["pull-agent", "push-agent", "publish-agent"]) {
  test(`${name} preserves authored bytes before pull and reconciles before reporting or uploading`, () => {
    const skill = read(name);
    const snapshot = skill.indexOf("capture a byte-for-byte snapshot");
    const pull = skill.indexOf("msagent agent pull --project");
    const compare = skill.toLowerCase().indexOf("compare the complete post-pull", pull);
    assert.ok(snapshot >= 0 && pull > snapshot && compare > pull);
    assert.match(skill, /before every pull, warn that knowledge-file downloads can replace local bytes/i);
    assert.match(skill, /hidden and untracked files, binary knowledge payloads, and linked component-collection/);
    assert.match(skill, /verify every copied file against its source/);
    assert.match(skill, /if complete preservation cannot be verified, stop before pull/);
    assert.match(skill, /missing and overwritten knowledge payloads/);
    assert.match(skill, /only with their explicit choice; do not blindly restore the snapshot/);
    assert.match(skill, /every intended pre-pull edit survived or was explicitly discarded by the user/);
    if (name !== "pull-agent") {
      assert.ok(skill.indexOf("msagent agent deploy --project", compare) > compare);
      assert.match(skill, /reconciliation cannot complete, stop before deploy/);
    }
  });
}

for (const skill of ["create-agent", "clone-agent", "push-agent", "pull-agent", "publish-agent"]) {
  test(`${skill} reads recovery only after failure and stops if it is unavailable`, () => {
    const skillText = read(skill);
    assert.doesNotMatch(skillText, /(?:always|unconditionally) read (?:all |every |the references)/i);
    const entrypoint = skillText.slice(skillText.indexOf("## Error handling"));
    assert.match(entrypoint, /On any command or result-verification failure, stop the normal workflow/);
    assert.match(entrypoint, /Only after a failure, read \[.*?\]\(references\/error-recovery\.md\)/);
    assert.match(entrypoint, /before remediation,\s+authentication changes, or retry/);
    assert.match(entrypoint, /If the reference cannot be read, stop and report it/);
    assert.match(entrypoint, /Never retry or add `--overwrite` without user consent/);
    assert.match(entrypoint, /`errorMessage`[\s\S]*`errorKind`[\s\S]*`remediation`[\s\S]*`partialResult`/);
    assert.doesNotMatch(entrypoint, /\| Failure \| Action \|/);
    const recovery = read(skill, "references/error-recovery.md");
    assert.match(recovery, /\| Failure \| Action \|/);
    assert.match(recovery, /With consent[\s\S]*msagent auth login/);
    assert.match(recovery, /Configuration is locked/);
  });

  test(`${skill} bundles its routing references without a plugin-root dependency`, () => {
    const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "lifecycle-skill-"));
    try {
      const standalone = path.join(temporary, skill);
      fs.cpSync(path.join(root, "skills", skill), standalone, { recursive: true });
      const entrypoint = fs.readFileSync(path.join(standalone, "SKILL.md"), "utf8");
      const links = [...entrypoint.matchAll(/\]\((references\/[^)#]+\.md)(?:#[^)]*)?\)/g)]
        .map((match) => match[1]);
      assert.ok(links.length >= 2);
      assert.doesNotMatch(entrypoint, /CLAUDE_PLUGIN_ROOT|plugin-paths\.json/);
      for (const link of links) {
        const resolved = path.resolve(standalone, link);
        assert.ok(resolved.startsWith(standalone + path.sep));
        const reference = fs.readFileSync(resolved, "utf8");
        assert.ok(reference.trim(), link);
        for (const [, target] of reference.matchAll(/\]\(([^)#]+\.md)(?:#[^)]*)?\)/g)) {
          const nested = path.resolve(path.dirname(resolved), target);
          assert.ok(nested.startsWith(standalone + path.sep));
          assert.ok(fs.readFileSync(nested, "utf8").trim(), target);
        }
      }
      fs.rmSync(path.join(standalone, links[0]));
      assert.throws(
        () => fs.readFileSync(path.join(standalone, links[0]), "utf8"),
        { code: "ENOENT" }
      );
      assert.match(entrypoint, /cannot be read, stop/);
    } finally {
      fs.rmSync(temporary, { recursive: true, force: true });
    }
  });
}
