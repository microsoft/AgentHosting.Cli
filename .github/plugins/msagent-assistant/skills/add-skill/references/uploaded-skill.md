# Upload skill schema (authoritative)

Read for default upload-layout authoring or import, together with the common contract. The helper owns companion
generation; do not hand-edit companions or convert this variant to inline.

## Anchor: `behaviors/<folder>/skill.mcs.yml`

```yaml
mcs.metadata:
  componentName: pdf-redline
  description: Redlines a PDF contract and produces a summary of changes.
  schemaName: crbab_guitarcoach_dcF_b3.skill.pdfredline_Kq2
  bundle: crbab_guitarcoach_dcF_b3.file.pdfredlinezip_9Xa2L
  manifestSchemaName: crbab_guitarcoach_dcF_b3.file.skillmd_bT4nQ
kind: InlineAgentSkill
authoringSource: Upload
```

- `componentName` is the `behaviors/<folder>` name.
- `description` comes from valid, required manifest frontmatter, parsed as YAML (including blocks).
- Follow the common contract's schema-name rules.
- `bundle` / `manifestSchemaName` appear only for bundle skills. They name the notional
  `<folder>.zip` and `SKILL.md` file components.
- `authoringSource: Upload` is required.
- The portal writes anchors without a trailing newline; sidecars retain theirs.

## Payload sidecar: `behaviors/<folder>/<path>.mcs.yml`

One per non-manifest payload, next to its file: `scripts/redline.py` gets
`scripts/redline.py.mcs.yml`. Metadata only: no `kind:` or `authoringSource:`.

```yaml
mcs.metadata:
  componentName: ./scripts/redline.py
  schemaName: crbab_guitarcoach_dcF_b3.file.redlinepy_9Xa2L
```

`componentName` is a POSIX-style path relative to the skill folder, prefixed with `./`.
Do not write a sidecar for `SKILL.md`; the anchor's `manifestSchemaName` names it.

## Bare fallback

An unreadable or unusable agent `schemaName` produces only payload files, no anchor or sidecars,
with a warning. The VS Code extension synthesizes companions on the next read/sync. This is valid
only inside a code-first CLI-agent workspace with a versioned `cliagent-` template and normally
`agent.sync.yaml`. A non-CLI workspace is never a bare fallback.
