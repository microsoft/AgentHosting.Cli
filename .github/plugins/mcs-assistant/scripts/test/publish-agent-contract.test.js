const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.resolve(__dirname, "..", "..");

function read(relativePath) {
  return fs.readFileSync(path.join(root, relativePath), "utf8");
}

test("README requires publisher-approved MSAGENT acquisition without implicit setup", () => {
  const readme = read("README.md");

  assert.match(
    readme,
    /publisher-approved package source and installation instructions/i
  );
  assert.match(readme, /--add-source/);
  assert.match(readme, /--prerelease/);
  assert.match(readme, /Do not install, upgrade, sign in, switch accounts or enable CLI feature flags\s+implicitly/);
});

test("withheld publish does not claim that nothing live changed", () => {
  const skill = read("skills/publish-agent/SKILL.md") + "\n" +
    read("skills/publish-agent/references/error-recovery.md");

  assert.doesNotMatch(skill, /nothing live changed/i);
  assert.match(skill, /agent (?:itself )?was not published/i);
  assert.match(
    skill,
    /Eligible workflows or related components may already have changed;\s+surface every workflow\/component warning returned by the CLI\./
  );
  assert.match(skill, /publish-withheld-incomplete-push`[\s\S]*`partialResult`[\s\S]*authoritative/i);
  assert.match(skill, /publish-failed-after-push`[\s\S]*`partialResult`[\s\S]*authoritative/i);
  assert.match(skill, /summarize\s+(?:its|the)\s+authoritative partial side effects/i);
});

test("deployment resolution honors every explicit selector before using the dev default", () => {
  const skill = read("skills/publish-agent/SKILL.md");

  assert.match(
    skill,
    /deploymentId[\s\S]*deploymentName[\s\S]*deploymentType[\s\S]*environmentId/
  );
  assert.match(skill, /all supplied deployment\s+selectors/i);
  assert.match(skill, /only when no deployment selector was supplied/i);
});

test("a cloud publication target constrains the deployment instead of falling back to dev", () => {
  const skill = read("skills/publish-agent/SKILL.md").replace(/\s+/g, " ");
  const selection = skill.slice(skill.indexOf("### 3."), skill.indexOf("### 4."));

  assert.match(selection, /cloud `mcsAgentId` is also an explicit deployment constraint/);
  assert.match(selection, /require its `environmentId` and `schemaName`; stop and ask/);
  assert.match(selection, /deployment's `environmentId` when present; otherwise use the owning agent's `environmentId`/);
  assert.match(selection, /no deployment selector was supplied and no cloud `mcsAgentId` was supplied/);
  assert.match(selection, /Never use the dev default for a supplied cloud target/);
  assert.match(selection, /If an authored workspace was supplied, require it to match the selected agent's workspace/);
});

test("cloud publication verifies identity before pull and deploy and checks the published result", () => {
  const skill = read("skills/publish-agent/SKILL.md").replace(/\s+/g, " ");
  const preflight = skill.indexOf("#### Verify a supplied cloud target");
  const pull = skill.indexOf("msagent agent pull --project");
  const revalidation = skill.indexOf("repeat the cloud-target lookup above after pull");
  const deploy = skill.indexOf("msagent agent deploy --project");
  const verification = skill.slice(skill.indexOf("### 8."));

  assert.ok(preflight >= 0 && preflight < pull);
  assert.ok(revalidation > pull && revalidation < deploy);
  assert.match(skill, /msagent agent list --environment-id '<expected-environment-id>' --limit 1000 --json --non-interactive/);
  assert.match(skill, /exactly one row in `agents\[\]` matching the expected `mcsAgentId`, `schemaName`, and `environmentId`/);
  assert.match(skill, /Stop before pull or deploy if the target is missing, different, ambiguous, or the lookup fails/);
  assert.match(skill, /`truncated: true` is inconclusive[\s\S]*stop rather than selecting another target/);
  assert.match(skill, /Do not create, adopt, or retarget to a different cloud agent/);
  assert.match(skill, /Require the same local agent, deployment, effective environment, cloud ID, and schema; stop if any identity changed/);
  assert.match(verification, /`targetMcsAgentId` to equal the expected `mcsAgentId` and `targetEnvironmentId` to equal the expected `environmentId`/);
  assert.match(verification, /mismatch is a verification failure[\s\S]*do not claim the requested agent was published, retry chat, or retry publication/);
});

test("pathless project discovery checks enclosing projects before descendants", () => {
  const skill = read("skills/publish-agent/references/project-discovery.md");
  const upwardSearch = skill.indexOf("walk upward through up to five parent");
  const descendantSearch = skill.indexOf("search descendants");

  assert.notEqual(upwardSearch, -1);
  assert.notEqual(descendantSearch, -1);
  assert.ok(upwardSearch < descendantSearch);
});
