---
name: add-knowledge
description: Add a knowledge source (public website, SharePoint, OneDrive, or a locally uploaded file) to a locally-cloned Copilot Studio agentic-loop agent by writing the modern capabilities/knowledge YAML. Use when the user asks to add, attach, or ground a knowledge source (a public website, SharePoint or OneDrive link, or an uploaded file) on a locally-cloned Copilot Studio agent.
metadata:
  argument-hint: A URL (website / SharePoint / OneDrive) or a local file path, plus an optional name/description
allowed-tools: Read Write Glob Grep
---

# Add a Knowledge Source

Add one knowledge source to a locally-cloned Copilot Studio agentic-loop agent. This skill only
authors local files; it does not push or publish. Never invent behavior the files do not support.

Initial request: $ARGUMENTS

## 1. Resolve the target and input

1. If an agent workspace was supplied, resolve that exact directory first and require its
   root-level `settings.mcs.yml`. If it is missing, invalid, or ambiguous, stop and report the
   supplied target; never substitute a discovered workspace. Only when no workspace was supplied,
   auto-discover with `Glob: **/settings.mcs.yml`. A cloned agentic-loop workspace contains
   `settings.mcs.yml` at its root. **Never hardcode an agent name.**
2. If none is found, tell the user this skill needs a **locally-cloned agentic-loop agent**. Direct
   them to the `clone-agent` workflow, which uses `msagent agent create --agent-type MCSAgent` with
   either `--mcs-clone-agent-id` or `--mcs-clone-agent-schemaname`, plus the source environment and
   project root. Do not clone implicitly or bypass that workflow. Stop.
3. If several are found, ask the user which agent to add the knowledge to.
4. Read the agent's **`schemaName`** from `settings.mcs.yml` (e.g. `crbab_guitarcoach_dcF_b3`) — it is
   the filename prefix for source-backed knowledge components.
   Parse the settings with an available local YAML parser. Verify a versioned `cliagent-`
   template and the GitHub Copilot harness: `configuration.authoringModel: CliCopilot` or
   `configuration.recognizer.kind: CLICopilotRecognizer`. If another harness or a non-CLI
   template is identified, stop before any writes. If identity is unknown, ask for
   clarification; do not infer support from the schema name or folder alone.
5. Extract the source URL or local file path, plus any requested name/description. Ask for missing
   input; do not create directories or files yet.

## 2. Select and load the authoring contract

Read only the references required for the selected source, completely, before writing any YAML or
copying any payload. Resolve every reference from this skill's own directory, never from a plugin
root or sibling skill. If a required reference cannot be read, stop and report its path.

Every source requires [common schema and naming rules](references/knowledge-schema.md), plus:

| Input | Selected source reference | Additional required reference |
|---|---|---|
| Absolute HTTPS URL on a `-my.sharepoint.*` host with `/personal/...` | [OneDrive file](references/onedrive.md) | [SharePoint/OneDrive URL normalization](references/sharepoint-url.md) |
| HTTPS URL on `*.sharepoint.com`, `*.sharepoint.us`, `*.sharepoint.cn`, or `*.sharepoint-mil.us` | [SharePoint file/folder](references/sharepoint.md) | [SharePoint/OneDrive URL normalization](references/sharepoint-url.md) |
| Other absolute HTTPS URL | [Public website](references/website.md) | None |
| Existing regular local file | [Uploaded file](references/uploaded-file.md) | None |

All URLs must have no embedded username or password. Reject unsupported or ambiguous inputs and
explain the problem; ask for a supported source rather than guessing. Apply the selected reference's
validation before authoring.

## 3. Optional access check

Only for SharePoint/OneDrive, offer the author-only check or honor an explicit request for it.
Explain that each end user's permissions govern runtime retrieval: author access does not prove
end-user access. Explain that delegated `Files.ReadWrite` consent grants read/write access to files
the signed-in user can access, before asking the user to opt in.

Read [access-check setup and results](references/access-precheck.md) only after the user opts in,
before authenticating. If the user declines, do not load it, resolve the helper, or authenticate.
The check is optional and best-effort. If its reference/helper or setup is unavailable, report that
the check could not run and continue adding the source without it.

## 4. Author and report

Follow the loaded common and selected-source contracts exactly. Encode externally derived YAML
strings using `JSON.stringify(value)` semantics. Check the 100-character schema-name budget and
component collisions before writing. Never overwrite an uploaded payload or sidecar without
explicit replacement consent; stop if replacement cannot identify exactly one existing sidecar.

Re-read and parse every written YAML artifact with an available local YAML parser. Verify
the selected source kind, exact normalized URL or relative payload path, unique component
identity, and 100-character schema budget against the loaded contracts. For uploads, verify
the copied file's bytes match the source. Review the diff (or before/after file inventory for
an untracked workspace): only the intended knowledge component and approved payload/sidecar
may change; preserve unrelated content and leave `.config/`, `.mcs/`, and `agent.sync.yaml`
untouched. If parsing, artifact validation, or diff review cannot complete, report the failure
and do not claim success.

Report the source kind, normalized `siteUrl` or copied file, and written component path. State that
the changes remain local. To upload a draft, direct the user to the `push-agent` workflow
(`msagent agent deploy`); to make the knowledge live, direct them to `publish-agent`
(`msagent agent deploy --publish`), which requires explicit publication confirmation.
Both routes require the registered MSAGENT project root and an explicit deployment ID or name;
the authored agent workspace is not the registration root. There is no standalone pack step.
Do not run either lifecycle workflow as part of adding knowledge. Use the common contract's
limitations for unsupported knowledge types.
