---
name: add-skill
description: Create, write, or format a new Copilot Studio Agent Skill from requirements, or add, import, upload, or install an existing skill from a local SKILL.md/.zip, the Power CAT gallery, or the WorkIQ AI Catalog. Use for requests to create skills, add reporting or formatting procedures to an agent, or import existing skills into a CLI-agent workspace, including an MSAGENT-authored workspace.
metadata:
  argument-hint: Skill requirements, an existing skill name/slug, or a local path to a SKILL.md / .zip; optional target agent workspace
allowed-tools: Bash(node *add-skill.bundle.js*) Bash(node *workiq-catalog.bundle.js*) Read Write Glob Grep
---

# Add a Copilot Studio Agent Skill

Author a new skill from requirements or acquire an existing skill, then optionally import it into
a Copilot Studio CLI-agent workspace. This only materializes local files and never pushes.
The default for newly authored skills is a real `SKILL.md` plus helper-generated `skill.mcs.yml`,
not a hand-written inline `content:` component. Read this entrypoint before writing skill files;
schema references alone are not the workflow.

Local import supports both cloned extension workspaces and MSAGENT CLI-agent scaffolds that pass
the selected import checks. Local import does not establish cloud compatibility. Upload,
deployment, and publication are separate, explicitly authorized lifecycle operations.

Initial request: $ARGUMENTS

Resolve all references and required helpers from this skill's own directory, not a plugin root or
sibling skill. If a required reference or helper cannot be read, stop and report its path.
Use `scripts/add-skill.bundle.js` as `<scriptPath>` for import or gallery acquisition; execute it
without reading its source. Resolve the WorkIQ helper only for selected WorkIQ acquisition.

## 1. Choose authoring or acquisition

- A request to create or write skills from requirements, including procedures for formatting
  information returned to a caller -> **author from requirements**. Read
  [new-skill authoring](references/authoring.md) before writing any files. Do not ask a gallery
  or source question for an already clear authoring request.
- A request to add the skills to a specified project is import consent for that target, but
  not overwrite, upload, deployment, or publication consent. Do not ask for it again.
- For an existing skill, use acquisition below. If new authoring versus acquiring an existing
  skill is genuinely unclear, ask that one question first.

Do not silently reformat existing inline or uploaded skills. An explicit request for inline
authoring is an exception: read the [common contract](references/skill-schema.md) and
[inline exception](references/inline-skill.md), check the stated compatibility requirements, and
stop if they cannot be established. Otherwise keep the default authoring/import route.

## 2. Choose the acquisition source

If the initial request already makes the source obvious, skip the question:

- A path ending in `.md` or `.zip`, or an existing file path -> **local upload**.
- A request that mentions WorkIQ or the WorkIQ AI Catalog -> **My WorkIQ skills**.
- Any other skill name/slug that is not a path -> treat as a **gallery** pick, matching it against
  the listed skills.

For acquisition only, otherwise ask the user to choose the source **as a single, standalone question** with exactly three
options:

- **Upload from local drive** - they already have a `SKILL.md` or a `.zip`.
- **Select from the gallery** - browse the Cat Agent Skills catalog.
- **My WorkIQ skills** - skills they are authorized to use in the WorkIQ AI Catalog (Microsoft
  sign-in in the browser).

Keep this question to the source choice only. Do **not** bundle any other field into it - in
particular, never add an upload-path field with "leave blank if downloading from the gallery" (or
similar) wording. Ask for a local path only after the user picks **Upload**. Never ask for a
gallery or WorkIQ download destination; the helpers choose a temporary folder.

## 3. Load and acquire

Read only the selected acquisition reference, completely, before acting:

| Choice | Required reference |
|---|---|
| Local `SKILL.md` or `.zip` | [Local upload](references/local-upload.md) |
| Gallery name/slug or browsing | [Gallery acquisition](references/gallery.md) |
| My WorkIQ skills | [WorkIQ AI Catalog acquisition](references/workiq-catalog.md) |

For selected WorkIQ acquisition use `scripts/workiq-catalog.bundle.js` as `<workiqPath>`;
execute it without reading its source.

## 4. Optional import

If import consent is not already supplied, offer import into the selected CLI-agent workspace.
Use the authored workspace containing `settings.mcs.yml`, not an MSAGENT registration root.
If declined or no supported workspace is available, stop after authoring/acquisition and report
the saved files. Do not synthesize missing project or connection metadata.

Read [import instructions](references/import.md) only after the user agrees to import,
before writing any workspace files. Also read [common schema rules](references/skill-schema.md)
and [upload companion schema](references/uploaded-skill.md) unless already loaded for new-skill
authoring. Follow those contracts; the helper generates companions.
Never add `--force` without explicit overwrite consent. Do not hand-edit generated companions.

## 5. Verify the selected outcome

After import, read [acceptance checks](references/acceptance.md) and verify the actual files and
helper result before reporting success. A YAML parse or `{ ok: true }` alone is not cloud
acceptance. Do not run deployment or publication just to validate local authoring.

## 6. Report or stop on failure

Report the name/slug, resolved folder and any ZIP path, and whether it is a single-file skill or
bundle. If imported, report `behaviors/<name>/`, companions or bare fallback, every warning, and
the acceptance level actually verified. State that files remain local unless a separate
authorized lifecycle operation established otherwise.

The helpers emit `{ "ok": false, "error": "..." }` and exit non-zero on failure. Surface the error;
never report acquisition/import success after a failure. Use the selected runbook's recovery;
do not retry without consent.
