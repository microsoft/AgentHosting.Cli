# Host compatibility and distribution

The canonical source is `microsoft/AgentHosting.Cli`, under
`.github/plugins/mcs-assistant`. Plugin identity remains `mcs-assistant` and
marketplace identity remains `copilot-studio-plugin`. The root marketplace
points to this self-contained directory. Do not copy its skills or agents into
the CLI repository's auto-discovered `.github/skills` or `.github/agents`.

## Evidence and boundaries

| Target | Package | Evidence |
|---|---|---|
| Claude Code | Canonical `.claude-plugin/plugin.json` package | Documented format; native execution unverified |
| GitHub Copilot CLI | Same Claude-compatible package | Documented legacy format; native execution unverified |
| Gemini CLI | Generated native extension, TOML commands and local agents | Documented format plus offline generation checks; native execution unverified |
| Agency Copilot | Generated self-contained marketplace directory with `agency.json` | Documented format plus offline generation checks; onboarding and execution unverified |
| M365 Cowork | Deferred | No independently usable subset established under the documented execution constraints |

No tested minimum host versions or runtime parity are claimed. Node.js >=22 is
the declared helper prerequisite. MSAGENT is acquired separately through the
publisher-approved route. Missing CLI capabilities stop the affected workflow;
the plugin never installs MSAGENT or enables feature flags.

Connected pull/push/publish workflows require verified preservation and reconciliation
of authored files, including binary knowledge payloads, before any subsequent upload.

Source access, catalog access, local execution, service/Hub execution and release
approval are independent. Agency's local executable/artifact support does not
establish that remote jobs can run MSAGENT. No remote backend is introduced.
Agency targets Copilot; standalone Claude compatibility remains in the canonical
package, not an implied Agency Claude release commitment.

The host adapter in `adapters/README.md` maps question, skill, delegation and
continuation operations. Shared workflows retain exact targets, checkpointed
resume, source isolation, approval gates, typed deletion and draft/publication
separation. Unsupported background or messaging APIs use the documented
sequential/full-context route, not invented tool calls.

## Generate host packages

Run from this plugin directory after restoring tooling and rebuilding helpers:

```powershell
npm --prefix scripts ci
npm --prefix scripts run build
npm --prefix scripts test
npm --prefix scripts run package:host -- --target gemini --output "C:\temp\gemini\mcs-assistant"
npm --prefix scripts run package:host -- --target agency --output "C:\marketplace\plugins\mcs-assistant"
```

Outputs must be new directories named `mcs-assistant`, outside the canonical
plugin directory. Existing output is never overwritten. Generation copies all
runtime resources, references and bundled helpers, without symlinks,
`node_modules`, CLI binaries or imports from sibling CLI source. Host-specific
metadata is generated, not independently maintained. `distribution.json`
records the canonical content hash and explicitly marks runtime verification false.
Regenerate from canonical source rather than editing a generated package.

Gemini receives `gemini-extension.json`, `GEMINI.md`, native TOML commands with
`{{args}}`, slug-named agents and extension-relative hooks. The hook receives its
extension root through `env`, never by inserting the path into shell source. Model selection is
inherited. The optional `MCS_KNOWLEDGE_CLIENT_ID` setting is declared because
Gemini sanitizes extension environments; `--client-id` also works. Do not assume
other shell environment variables reach the extension.

For Agency, generate **into the chosen marketplace checkout's plugin directory**.
The output contains the real code, root `plugin.json`, stable `pluginId`, and
Copilot-only `agency.json`; there is no `source` redirect or CLI download.
Generation is not onboarding: run that marketplace's metadata/governance checks,
then use its normal reviewed publication process. No catalog manifests are
hand-edited, no marketplace is published, and no administrative approval is implied.
Local staging packages may be generated outside a marketplace for review.

## M365 subset assessment

| Skills | Required execution not established in Cowork |
|---|---|
| create, clone, pull, push, publish, delete | Local MSAGENT, project/deployment records and exact cloud-target operations |
| add-channel | Local authored-workspace reads/edits and YAML parsing |
| add-knowledge | Local workspace/file authoring; optional local Node/MSAL Graph verifier |
| add-skill | Local skill payload acquisition, Node importer and generated workspace companions |

The documented M365 conversion path carries skills, not this plugin's commands,
agents, hooks or executable dependencies. Reducing a workflow to generic YAML
advice would remove its verified-write and target safeguards, not produce an
independently equivalent offering. Therefore no M365 package is emitted now.
This is a scoped deferral, not a claim that future M365 tooling cannot support it.

## Validation and release

Offline checks cover manifests, copied references/helpers, bundle parity,
installation-isolated data routing, generated packages and existing lifecycle
safeguards. Existing repository CI runs these checks; plugin versioning and
resources do not depend on the .NET package build.

Before any release claim, perform the deferred small manual baseline on exact
host versions, OSes and execution modes: installation, discovery, supported
workflows, failures, approvals and no unintended writes. Use a familiar subject
with independently verifiable answers for live behavior. Approve any tenant,
identity, query and side effects separately. Host installation, sign-in, cloud
mutation and live model/agent execution are not part of offline CI.

Authoritative format references:

- [Agent Skills](https://agentskills.io/specification)
- [Claude plugins](https://code.claude.com/docs/en/plugins-reference)
- [Copilot plugins](https://docs.github.com/en/copilot/reference/cli-plugin-reference)
- [Gemini extensions](https://geminicli.com/docs/extensions/reference/)
- [Gemini commands](https://geminicli.com/docs/cli/custom-commands/)
- [Gemini subagents](https://geminicli.com/docs/core/subagents/)

Agency packaging follows its current `creating-plugins.md`, marketplace and
compatibility guidance; actual marketplace acceptance remains separate.
