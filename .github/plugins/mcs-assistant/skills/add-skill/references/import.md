# Optional workspace import

Read only after the user agrees to import an authored or acquired skill. Before any writes, also read the
entrypoint's common schema and upload companion references; do not load inline-authoring guidance.

Use a Copilot Studio CLI-agent workspace: either a cloned extension workspace or an MSAGENT
CLI-agent scaffold. For MSAGENT, select the authored child workspace containing
`settings.mcs.yml`, not the registration root containing `.config/agent.config.json`.
Do not read or edit `.config/`, `.mcs/`, or `agent.sync.yaml` manually.
If no supported workspace is available, stop after authoring/acquisition and report the source.

## Workspace and output

Before writing, the helper requires a root-level single-line `template:` value beginning
`cliagent-` (e.g. `template: cliagent-1.0.0`; plain and quoted forms are accepted).
Missing settings/key or a non-CLI template is an unsupported-workspace error. Relay it and
do not retry with `--force`; do not create or replace any `behaviors/<name>/` content.

The helper's local CLI-template check also accepts MSAGENT scaffolds without `agent.sync.yaml`;
the missing marker produces a warning, not a local import failure. This does not verify that
the MSAGENT cloud synchronization path accepts the resulting skill representation.
For an MSAGENT-only workspace, require a usable agent `schemaName` from settings and generated
companions; if the prefix is absent/invalid, stop before import. Do not use `--no-sidecars` for
MSAGENT-only authoring or rely on extension synthesis that is not available.

The helper copies the manifest as `SKILL.md` and other payload files verbatim under
`behaviors/<name>/`. By default it generates a portal-style `skill.mcs.yml` anchor and bundle
payload sidecars. Do not generate or edit those companions yourself.

It prefixes schema names using the agent's `schemaName`. Missing or unusable prefixes, or explicit
`--no-sidecars`, produce a bare folder; the extension synthesizes companions on its next workspace
read/sync. Relay the fallback warning, including the accepted single-line plain or quoted syntax.
An explicit `--no-sidecars` does not produce that fallback warning. A prefix leaving no room under
the 100-character schema-name limit is an error before writes: suggest a shorter agent schema name.

## Import command

```bash
node "<scriptPath>" import --src "<skill-folder>" --workspace "<agent-workspace>" [--name "<folder>"] [--force] [--no-sidecars]
```

- `--src` is the authored or acquired unpacked folder with a top-level `SKILL.md` (case-insensitive on input,
  written with exact casing). For a ZIP, use the folder the **user** extracted; not the archive.
- `--workspace` is the CLI-agent workspace root. The source directory's leaf and optional `--name`
  must exactly match the manifest's valid `name`; neither is silently sanitized or rewritten.
  If an acquired source has a mismatched directory, explain the error and obtain approval to
  prepare a correctly named source copy before retrying. Do not repair metadata implicitly.
- For an MSAGENT scaffold, `--workspace` is the authored child workspace, not the registered root.
- `--force` overwrites `behaviors/<name>/`; obtain explicit confirmation first.
- `--no-sidecars` requests a bare folder.
- `--dry-run` validates and returns planned paths without creating or replacing workspace files.
  Boolean switches take no values: `--force false` is invalid, not consent.
- Root gallery metadata (`metadata.json`, `metadata.yaml`, `metadata.yml`, `README.md`) is excluded
  case-insensitively unless `--include-sidecars` is requested. Source `.mcs.yml` files are ignored
  and regenerated.
- Payload archives are copied. A root-level `<name>.zip` is skipped because it would collide with
  the extension's bundle archive; relay its warning.

Use `--help` for the complete CLI contract. `--limit` (default 20, maximum 100) and `--offset`
page each stdout array. `--output "<new-file.json>"` saves complete JSON without overwriting;
it cannot be combined with `--dry-run`. Use a private session artifact path for full results.
JSON stdout also has a 12 KB budget: `truncatedForSize` means fewer than the requested entries
fit, and `summaryOmitted` means scalar metadata itself exceeded that budget. Read the full output
file rather than assuming a short page is complete. `pages.*.returned` is the actual page size.

Result JSON includes `{ ok, dryRun, folder, workspace, behaviorsDir, manifest, files, companions, schemaPrefix, warnings, next, pages }`.
Read `pages` totals and retrieve full results before claiming every file or warning was reviewed.
Report companions (empty for bare), schema prefix, and every warning. A non-CLI workspace is an error,
not a fallback, and receives no files. After success, read [acceptance checks](acceptance.md)
before reporting local completion. Never push or publish as part of import.
