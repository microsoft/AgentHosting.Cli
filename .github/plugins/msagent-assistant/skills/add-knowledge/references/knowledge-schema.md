# Knowledge Source Schema (authoritative)

**Authoritative common contract** for modern Copilot Studio agentic-loop knowledge. The
`add-knowledge` skill and `copilot-studio-architect` use these shared rules plus the selected
source contract below. Edit each rule in its owning reference; do not duplicate schema bodies.

| Source | Schema file in this directory | Also required |
|---|---|---|
| Website | `website.md` | None |
| SharePoint file/folder | `sharepoint.md` | `sharepoint-url.md` |
| OneDrive file | `onedrive.md` | `sharepoint-url.md` |
| Uploaded file | `uploaded-file.md` | None |

Read only the selected source files, before authoring. Optional access-check details are in
`access-precheck.md`; read them only after informed opt-in, not as part of the common schema.

The shapes below match what the platform produces when a source is added in the browser and cloned
locally. Use the `clone-agent` workflow (`msagent agent create` with an MCS clone selector) for
initial download, or `pull-agent` (`msagent agent pull`) to refresh an already registered,
connected workspace. Use these workflows rather than alternative synchronization commands.

---

## File layout

```text
<agent>/
└── capabilities/
    └── knowledge/
        ├── <schemaName>.<slug>_<id>.mcs.yml   # source-backed sources (website / SharePoint / OneDrive)
        └── files/
            ├── <uploaded-file>                # the copied file itself
            └── <slug>_<id>.mcs.yml            # metadata-only sidecar for the uploaded file
```

## Metadata block

Every source-backed component starts with an `mcs.metadata:` block, then
`kind: KnowledgeSourceConfiguration`, then a `source:` block:

- `componentName` — a friendly identifier: the **file name** for files, or the **full URL** for
  websites.
- `description` — describe the **subject matter** the source covers, not just its name. The
  orchestrator uses this to decide when to search the source, so write it to be genuinely
  descriptive (e.g. *"HR leave policies and employee entitlements"* beats *"HR docs"*).

### YAML-safe scalar encoding

Treat names, descriptions, URLs, and paths as external data, not YAML. Emit every externally derived
string as a double-quoted YAML scalar using `JSON.stringify(value)` semantics. This escapes quotes,
backslashes, control characters, and newlines while producing a JSON string that is also valid YAML.
Never paste a user-provided value directly after `componentName:`, `description:`, or `siteUrl:`.
For example, `Policy area: HR` must be emitted as `"Policy area: HR"`, not as a raw plain scalar.

## Filename conventions

- **Source-backed** (website / SharePoint / OneDrive):
  `capabilities/knowledge/<schemaName>.<slug>_<id>.mcs.yml`
  - `<schemaName>` — the agent's schema name from `settings.mcs.yml` (e.g. `crbab_guitarcoach_dcF_b3`).
  - `<slug>` — the `componentName` with non-alphanumeric/underscore characters removed
    (e.g. `popular_guitar_brands.docx` → `popular_guitar_brandsdocx`;
    `https://en.wikipedia.org/wiki/Electric_guitar` → `httpsenwikipediaorgwikiElectric_guitar`).
  - `<id>` — a 12-character alphanumeric suffix.
- **Uploaded file** sidecar: `capabilities/knowledge/files/<slug>_<id>.mcs.yml`
  - `<slug>` — the file name with non-alphanumeric characters removed
    (e.g. `electric-guitar-history.md` → `electricguitarhistorymd`).
- When **editing** an existing component, keep its existing filename and suffix.

### 100-character schema-name budget

Dataverse schema names must be at most 100 characters. Apply the limit before writing:

- Source-backed stem: `<schemaName>.<slug>_<id>`. Its slug budget is
  `100 - length(schemaName) - length(id) - 2` for the dot and underscore.
- Uploaded sidecar stem on disk: `<slug>_<id>`. Compilation qualifies it as
  `<schemaName>.file.<slug>_<id>`, so its slug budget is
  `100 - length(schemaName) - length(".file.") - length(id) - 1`.
- Strip unsupported slug characters first, use `knowledge` if that produces an empty slug, then
  truncate the slug on the right to its budget. If the budget is less than one character, stop and
  report that the agent schema name leaves no valid component-name space.
- Confirm the resulting `*.mcs.yml` path does not exist. Generate a new 12-character id if it does.

Do not prefix an uploaded sidecar's on-disk stem with `<schemaName>`: compilation adds
`<schemaName>.file.` while compiling that folder.

## Best practices

- **One source per content domain.** Prefer several narrow, well-described sources over one broad
  one; avoid overlapping sources — it degrades relevance ranking.
- **Descriptions matter.** The orchestrator reads each source's `description` to decide when to
  search it.
- **Content quality.** Use documents with clear headings/titles, one topic each; avoid image-only or
  table-only files.
- **Test after adding.** Ask the agent a representative question and verify it retrieves and cites the
  new source.

## Limitations

Public Website, SharePoint, OneDrive, and uploaded files can be authored directly in YAML. Other
knowledge types require Power Platform setup in the Copilot Studio UI and must be re-cloned/pulled to
edit locally:

- Dataverse tables
- AI Search
- SQL Server
- Microsoft Graph connectors (registered in the M365 admin center)
