# Author a new Agent Skill from requirements

Read only for a request to create or write a new skill. Do not run gallery or WorkIQ acquisition.

## 1. Establish the procedure

Reuse the supplied requirements, target workspace and output format. Ask only for missing
information that materially changes behavior. A reusable multi-step procedure belongs in a skill;
global tone and citation policy belong in agent instructions, searchable facts in knowledge, and
live data access in a real tool. A formatting skill does not create retrieval capabilities.
Prefer a few focused skills rather than a catch-all package.

## 2. Author a source manifest

Read the common `skill-schema.md` and `uploaded-skill.md` references in this directory before
authoring. The default is the upload layout, even when the skill is newly written rather than
downloaded. Do not create an inline `content:` component as the default.

Use a private session scratch directory outside the target workspace. Choose a new source folder
for each skill and do not overwrite existing files. If no writable scratch location is available,
ask for one. Keep the source separate from `behaviors/` so import can validate and stage writes
before changing the agent. Create a real, exactly cased `SKILL.md`, for example:

```markdown
---
name: verified-game-report
description: "Use when a caller requests a game result or a latest-completed-game report."
---
# Verified game report

Use only configured retrieval tools and retrieved public evidence.

1. Resolve the requested team and as-of date.
2. Check schedule coverage and choose a game explicitly marked final.
3. If coverage is incomplete, say the latest completed game cannot be verified.
4. Return a concise headline, game date, both teams, final score and exact supporting source.
   Do not invent missing facts, timestamps or source URLs.
```

Use a descriptive lowercase kebab-case name of 1-64 characters, without leading, trailing or
consecutive hyphens. The source folder's leaf must exactly match `name`; `--name` is only an
identity assertion, not an implicit rename. Avoid reserved Windows device names.
Keep the description a meaningful YAML string of at most 1024 characters; quoted scalars and
folded/literal block scalars are supported by the YAML parser. Escape strings correctly. It is the selection
signal for the orchestrator. State when to use the skill in the description and put the procedure
in the body.

Include required inputs, clarification conditions, available-tool use, ordered steps, side-effect
confirmation rules when relevant, output shape, and missing-data/error behavior. Add payload
files only when needed. Do not invent tools or connections, embed credentials, or add factual
reference data as if it were current. Never hand-write or hand-edit generated companions.

## 3. Check and import

Parse the frontmatter and validate required `name`/`description`, name/directory equality and
the standard fields (`name`, `description`, `license`, `compatibility`, `metadata`, `allowed-tools`).
Put host-specific hints under string-valued `metadata`; `allowed-tools` is experimental and
space-separated, not comma-separated. Run the import helper with `--dry-run` before import.
Walk through
a representative request and a missing-evidence/error case against the procedure. Verify the
requested output fields and uncertainty behavior, not just Markdown syntax.

Keep realistic positive and near-miss trigger prompts and a few outcome/error fixtures. Measure
repeated fresh-context runs and compare with a no-skill baseline; record assertions, traces,
timing and token usage. The repository's `evals/` harness demonstrates this separately from
offline contract tests. A walkthrough or local import does not establish model behavior.

Return to the entrypoint's optional import step and follow `import.md`; pass the source folder
as `--src`. Let the helper generate the `skill.mcs.yml` anchor and any payload companions.
Do not copy the source directly into `behaviors/`, bypass collision checks, or convert an existing
inline skill without explicit consent. If replacement is requested, retain the original content
until the replacement and its checks succeed; never add `--force` without explicit approval.

If import was not requested, report the authored source path. If it was requested, complete
`acceptance.md` after import. Keep local authoring distinct from cloud deployment and publication.
