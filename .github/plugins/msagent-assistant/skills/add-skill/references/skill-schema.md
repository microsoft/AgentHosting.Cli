# Agent Skill Schema (authoritative)

**Authoritative common contract** for modern Copilot Studio CLI agentic-loop skills (`behaviors/`).
The importer implements these rules. Read `skills/add-skill/SKILL.md` before creating or importing
skills; this schema is not a substitute for its routing, authoring, import and acceptance steps.
Schema consumers read this contract plus only their selected variant. Edit schema bodies in
their owning files, not in both workflows.

| Workflow | Required variant file in this directory |
|---|---|
| Author a new skill or import an acquired skill using `scripts/add-skill.bundle.js` (default) | `uploaded-skill.md` |
| Explicit, compatibility-verified request for inline authoring | `inline-skill.md` |

Read the common and selected variant before authoring. Do not silently convert existing skills
between variants: their runtime equivalence has not been verified.

The shapes below match what the platform produces when a skill is added in the browser and cloned
locally through the MSAGENT `clone-agent` workflow.

---

## Supported workspace

The upload importer supports **CLI-agent workspaces**, including cloned extension workspaces and
MSAGENT CLI-agent scaffolds, for local materialization. Before materializing or replacing
any `behaviors/<folder>/` content, it reads the root `settings.mcs.yml` and requires a root-level,
single-line `template:` scalar beginning `cliagent-`, case-insensitively (for example,
`template: cliagent-1.0.0`). Plain, single-quoted, and double-quoted values are accepted, including a
trailing comment.

If the settings file or template key is absent, unreadable, malformed, or names a non-CLI template,
the import stops with an unsupported-workspace error and leaves the workspace unchanged.
`agent.sync.yaml` remains the expected cloned-workspace layout marker, but it is not used as the
CLI-agent identity signal. A missing marker produces a warning. Local template acceptance does not
establish cloud synchronization compatibility; follow `import.md` and `acceptance.md`.

## Two variants

A skill is always `kind: InlineAgentSkill`, but it is materialized in one of two ways:

| Variant | Where the skill text lives | Marker | Emitted by |
|---|---|---|---|
| **Inline** | Embedded in the component's `content:` block | no `authoringSource` | Explicit inline exception only; preserve existing inline skills |
| **Upload** (default) | A real `SKILL.md` file on disk, plus optional payload files | `authoringSource: Upload` | `add-skill` for authored or acquired skills via `scripts/add-skill.bundle.js`, and a portal upload |

**Authoring policy:** default to a real `SKILL.md` plus helper-generated `skill.mcs.yml`, including
for skills created from an idea by the architect. This is a workflow policy, not a claim that the
two variants are runtime-equivalent. Use inline only for an explicit inline request with established
target compatibility. Do not automatically convert existing inline or uploaded skills.

## File layout

```text
<agent>/
└── behaviors/
    └── <folder>/
        ├── SKILL.md                 # upload variant only — the manifest, this exact casing
        ├── skill.mcs.yml            # upload variant only — the anchor component
        ├── scripts/
        │   ├── redline.py           # payload file, copied verbatim
        │   └── redline.py.mcs.yml   # one sidecar per non-manifest payload file
        └── <folder>.mcs.yml         # inline variant — the whole skill in one component
```

A **single-`SKILL.md`** skill gets only the anchor: no `bundle`, no `manifestSchemaName`, no sidecars.
A **bundle** skill (any payload file beyond `SKILL.md`) gets all three.

## YAML-safe scalar encoding

Treat folder names, descriptions, and payload paths as external data, not YAML. Never paste a value
directly after `componentName:` or `description:`.

A value **must** be emitted as a double-quoted scalar whenever it:

- is empty, starts with a YAML indicator (`- ? : , [ ] { } # & * ! | > ' " % @ \``) or whitespace,
  contains `: `, ` #`, a trailing `:`, trailing whitespace, or any newline/tab; **or**
- would resolve to a non-string under **YAML 1.1**, which is what the Copilot Studio tooling reads
  these files with — `y`, `n`, `yes`, `no`, `true`, `false`, `on`, `off` (any casing); `~`, `null`;
  any integer, float, hex, octal, `.inf`/`.nan`; sexagesimals such as `1:30`; and timestamps such as
  `2026-09-17` or `2026-09-17T12:00:00Z`.

A skill folder named `on` or `123` is the common trap: emitted plain, it comes back as the boolean
`true` or the number `123`.

Per variant:

- **Upload** — quote only when the rules above require it. The portal writes plain scalars for
  ordinary values, and the anchor should stay byte-comparable with a portal import. The reference
  implementation is `yamlScalar()` in `scripts/add-skill.bundle.js`; it already enforces this, so do not
  hand-edit generated companions.
- **Inline** — always double-quote `componentName` and `description`. These are authored by a model
  from user text, where the cheapest safe rule is to quote unconditionally.

## Folder naming

For upload imports, `behaviors/<folder>`, the source directory leaf and manifest `name` must
match exactly. Use 1-64 lowercase letters/digits with single separating hyphens; no leading,
trailing or consecutive hyphens. The validator accepts normalized Unicode letters/digits as
the reference implementation does; prefer ASCII for cross-platform portability.
Windows reserved device names (`con`, `prn`, `aux`, `nul`, `com1`-`com9`, `lpt1`-`lpt9`)
are rejected, not silently prefixed. `--name` cannot rename or sanitize an imported identity.
Existing inline components are not migrated by this policy.

The Dataverse schema segment is derived separately (see below), with its own length budget;
it need not match the readable folder name character-for-character.

## Schema names

Every component schema name has the shape:

```text
<agentSchemaName>.<infix>.<segment>_<token>
```

| Component | `<infix>` | `<segment>` | `<token>` |
|---|---|---|---|
| Skill anchor | `skill` | the `behaviors/` folder name with every non-alphanumeric removed (`my.cool-skill` → `mycoolskill`), falling back to `skill` when that empties it | 3 chars |
| Manifest, bundle, payload file | `file` | the file name, lowercased, non-alphanumerics removed (`SKILL.md` → `skillmd`, `redline.py` → `redlinepy`), falling back to `file` when that empties it | 5 chars |

`<agentSchemaName>` is read from the workspace `settings.mcs.yml` (e.g. `crbab_guitarcoach_dcF_b3`).
`<token>` is a random base62 string; its length is cosmetic parity with portal output — only validity
and uniqueness matter.

Both segments are reduced to alphanumerics because a `.` in a segment would silently add a fourth
schema-name level, and the extension mints its own segments the same way
(`SkillLayout.MintBundleSchemaName` keeps only ASCII letters and digits).

### Reading the agent schema name

`settings.mcs.yml` is scraped, not fully parsed, so resolve the `schemaName:` value the way a YAML
parser would before using it as a prefix:

- Honour double and single quoting, and unescape the contents. `schemaName: "crbab_x"` is the prefix
  `crbab_x`, never `"crbab_x"` — pasting the quotes through produces an invalid schema name *and*
  invalid YAML in the anchor.
- Drop a trailing `#` comment on an unquoted value.
- Require the result to match `^[A-Za-z][A-Za-z0-9_]*$`. Anything else is not a usable Dataverse
  prefix; treat it as "no prefix" and fall back to a bare skill rather than emitting it verbatim.
- Keep the value on the `schemaName:` line. Full YAML scalar forms such as `>-` blocks are outside
  this zero-runtime-dependency parser's contract; the fallback warning identifies the accepted
  single-line plain or quoted form.

### 100-character schema-name budget

Dataverse caps a schema name at 100 characters. Apply the limit **before** writing:

- Segment budget = `100 - length(agentSchemaName) - length(infix) - length(token) - 3`
  (the two dots and the underscore).
- Strip unsupported characters first, then truncate the segment on the right to its budget, and warn
  that the on-disk name and the schema name now differ.
- If the budget is less than one character for **either** shape, stop and report that the agent
  schema name leaves no valid component-name space. Check this before materializing any file, so a
  failure does not leave a half-written skill folder behind.

The 60-character folder cap is not sufficient on its own: a 40-character agent schema name plus a
60-character folder yields a 111-character anchor schema name.

## Collision and overwrite rules

- Never overwrite an existing `behaviors/<folder>/` without explicit confirmation (`--force`).
- Regenerate companions; never copy a `.mcs.yml` from the skill source.
- Gallery sidecars (`metadata.json`, `metadata.yaml`, `metadata.yml`, `README.md`) are excluded from
  the payload by default, matched **case-insensitively** and only at the skill root.
- Payload archives are copied verbatim like any other file. The one exception is a root-level
  `<folder>.zip`, which would collide with the bundle archive the extension mints for the skill:
  skip it and say so, rather than dropping it silently.
- The manifest must land as exactly `SKILL.md`. If the source has several root manifests differing
  only by casing, keep one and report the one skipped.

## Best practices

- **Prefer a few focused skills over one comprehensive package.** Each skill should cover one clear
  procedure.
- **Add a skill only when the task genuinely needs a procedure.** For tasks a model already handles
  from general knowledge, a skill adds little and can get in the way.
- **Write the description for the orchestrator.** It is the selection signal, not documentation.
- Skill content should cover trigger/use guidance, required inputs, clarifying questions, tool-use
  steps, confirmation rules for side effects, expected outputs, and fallback/escalation behavior.

Classification guidance — skill vs tool, skill vs knowledge, and skill effectiveness heuristics —
lives in the `copilot-studio-architect` agent, not here. This file covers the **schema** only.

## Limitations

- `InlineAgentSkill` is supported on **Copilot Studio CLI (code-first) agents** only.
- Import materializes files on disk only. Upload/deploy and publication are separate authorized
  lifecycle workflows; local import does not verify either. Follow `acceptance.md` before
  claiming extension recognition, cloud acceptance or runtime behavior.
