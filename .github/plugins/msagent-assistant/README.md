# Copilot Studio CLI Plugin

This self-contained plugin is the successor to [skills-for-copilot-studio](https://github.com/microsoft/skills-for-copilot-studio). Its canonical source is `.github/plugins/msagent-assistant` in [microsoft/AgentHosting.Cli](https://github.com/microsoft/AgentHosting.Cli). It supports creating, editing, validating, and migrating Copilot Studio agents to the GitHub Copilot harness. `source.json` records the imported source revision.

Agent lifecycle operations and migration require the Microsoft Agent Hosting CLI (`msagent`).
Migration with connector-backed tools also requires its `deployment connection add`, `list`, and
`update` commands. Obtain the publisher-approved package source and installation instructions;
preview access and feed availability are separate from plugin installation. When the publisher
supplies a package source, a typical .NET tool installation is:

```bash
dotnet tool install --global Microsoft.Agents.Hosting.Cli --prerelease --add-source "<approved-msagent-package-source>"
```

Verify the installation with `msagent --version` and the selected workflow's capability checks.
Do not install, upgrade, sign in, switch accounts or enable CLI feature flags implicitly.

Migration initializes a separate registered project and an unpublished development draft before
authoring migrated behavior. Connector setup uses deployment-scoped connection references for that
target; it never rebinds the source agent's references. The final upload uses `deploy-agent`, not
publication. `/chat` is a thin workflow around `msagent test ask`; the CLI owns authentication,
exact target resolution, transport and output. It requires the publisher-approved live-testing
capability. The plugin does not enable that capability or retain its retired runtime client.

## Disclaimer

This plugin work in progress and supported by Github Issues only at this time, we are working to improve this feature set. The Copilot Studio YAML schema may change without notice. Always review and validate generated YAML before pushing to your environment - AI-generated output may contain errors or unsupported patterns.

## Installation

```bash
/plugin marketplace add microsoft/AgentHosting.Cli
/plugin install msagent-assistant@copilot-studio-cli-plugin
```

These repository instructions apply after the plugin changes are merged. For an uncommitted
checkout, use the host's local-plugin loading option with the absolute path to this directory.
Repository access is required; publishing a catalog entry does not grant source or tenant access.
The repository-root marketplace points here; this directory remains an independently copyable
plugin. It is not bundled into the MSAGENT NuGet package.

See [host integration and distribution](COMPATIBILITY.md) for generated Gemini/Agency packages,
prerequisites, compatibility evidence and deferred validation.

## Conditional skill loading

All nine skills keep routing and common safeguards in `SKILL.md`.
Detailed instructions stay in skill-local references and are read only for the selected path:

| Skill | Required path | Conditional additions |
|---|---|---|
| `add-channel` | Exact authored workspace, GitHub-harness check, channel schema and additive merge rules | Teams, Microsoft 365 Copilot, or both, only when requested; no deployment or publication |
| `add-knowledge` | Common schema plus the selected source schema | SharePoint/OneDrive URL normalization; access-check setup only after informed opt-in |
| `add-skill` | Author from requirements, or acquire from local upload, gallery, or WorkIQ AI Catalog | Default authoring produces `SKILL.md` plus helper-generated upload companions; import instructions after consent; acceptance after import; inline only for an explicit, compatibility-verified exception |
| `delete-agent` | Registered project or unregistered workspace | Initialization/deletion recovery only on failure; existing-record inspection only when recovery requires it |
| `create-agent` | Field validation, single create-help check, scaffold, development deployment, deploy, and final pull | Existing-destination rules only for a non-empty root; guided interview; accepted component authoring; explicit publish handoff only after full success; failure recovery (including over-limit fields and outdated-CLI resume) |
| `clone-agent` | Exact source selector, environment, destination, clone, and local-result verification | Display-name lookup or source-not-found recovery; detailed failure recovery |
| `deploy-agent` | Exact local selection, effects/consent, first-push predicate, connected pull, and draft deploy | Project discovery unless an explicit registered root is supplied; failure recovery |
| `pull-agent` | Exact local selection, connected-workspace checks, local-write consent, pull, and verification | Project discovery unless an explicit registered root is supplied; failure recovery |
| `publish-agent` | Exact local selection, final live-change confirmation, pull, publish deploy, and result verification | Project discovery unless an explicit registered root is supplied; failure recovery, with partial side effects authoritative |

Install or copy the **entire skill directory**, including `references/` and `scripts/`, for
standalone use. A missing required reference stops its operation before side effects. Optional
knowledge checks remain best-effort and report unavailable setup without blocking local authoring.
Schema consumers use the same common and selected-source/variant contracts rather than duplicate
YAML examples.

Creation's optional enrichment still requires the companion `add-channel` workflow,
`add-knowledge` schema contracts or
the entire `add-skill` workflow, and its publication handoff still requires
`msagent-assistant:publish-agent`. Both creation and the architect must read `skills/add-skill/SKILL.md`
before authoring skills, not only its schema references. Copying
`create-agent` alone does not supply those dependencies; missing contracts or helpers stop authoring, and a
missing publish skill leaves the verified draft unpublished. This refactor does not change the
creation CLI flag contract or make those optional integrations fully standalone.

### Adding MCS channels

Use `add-channel` for Microsoft Teams or Microsoft 365 Copilot on CLI-authored MCS agents using the
GitHub Copilot harness. These are the only two supported channels for that harness. The skill
merges requested entries into `settings.mcs.yml`, preserving existing configuration and avoiding
duplicates; it does not create trigger files or run CodeAgent channel commands. Creation and
the architect route through the same skill and schema contract.

Changes remain local. Use separately authorized `deploy-agent` or `publish-agent` workflows to
deploy or publish them. Channel configuration alone does not establish installation or user access.

This structure follows [Copilot skill loading](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)
and [progressive disclosure guidance](https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices):
the full selected `SKILL.md` loads, but unrelated reference bodies need not. Moving always-required
text to another file does not by itself save per-path context.

Run the local contract and helper tests with `npm --prefix scripts test`. They check routing,
copied-directory reference completeness, YAML examples, lifecycle/deletion safeguards, and offline
helper behavior. Failure recovery, lookup, and fallback discovery retain the applicable consent,
selector, conflict, and side-effect checks even though their details load later.
They do not prove that a model follows the routing instructions. Verifying that unrelated files
are never read requires actual Copilot-session read traces; no live cloud test is part of this suite.

The shipped manifests use portable Agent Skills frontmatter: host argument hints are retained
under `metadata` (hosts may not display them), and experimental `allowed-tools` values are
space-separated. Local tests validate all shipped manifests.
Opt-in [live skill evaluations](evals/README.md) add repeated trigger/decision/artifact probes,
baseline comparisons, traces and reported usage; they consume model credits and are not cloud tests.

Before a connected pull, lifecycle skills require a verified private snapshot of authored
files and reconciliation afterward; knowledge payloads can be overwritten without a merge conflict.

### Creating and importing Agent Skills

Requests to create skills from requirements, including reporting and formatting procedures, use
`add-skill`'s authoring route without a gallery/source interview. Author a real `SKILL.md` in
scratch space outside the agent workspace, then use the skill-local import helper to generate
`behaviors/<name>/SKILL.md` and `skill.mcs.yml` (plus payload companions for a bundle).
Do not hand-write generated companions or silently convert existing inline skills.

Local import accepts a CLI-agent workspace with a versioned `cliagent-` template, including an
MSAGENT scaffold. Point it at the authored child workspace, not the registration root.
MSAGENT-only authoring requires a usable schema prefix and generated companions; it cannot rely
on the extension synthesizing a bare import. Missing sync-marker warnings must still be reported.

The acceptance reference separates local file checks, extension recognition, cloud
synchronization/round-trip, publication and runtime behavior. Offline tests verify local import
into an MSAGENT-style scaffold; they do not prove that a cloud deployment accepts that layout.
Use separate, authorized lifecycle workflows for upload/deployment or publication, never as an
automatic consequence of adding a skill.

## Building JavaScript helpers

All five shipped JavaScript entry points are prebuilt with esbuild from `scripts/src/`.
Source-only modules are included in the bundles that use them; tests are not bundled.
The generated files are checked into the repository so consumers do not need to build them.
Builds preserve logical dependency paths so linked dependency folders do not embed machine-specific
paths in the output.

```bash
npm --prefix scripts ci
npm --prefix scripts run build
npm --prefix scripts test
```

The build covers knowledge access verification, WorkIQ acquisition, skill acquisition/import,
action migration, and the session-start hook. Every generated entry point uses a `.bundle.js`
filename, including `skills/add-skill/scripts/add-skill.bundle.js`,
`scripts/convert-actions-to-tools.bundle.js`, and `hooks/set-env-vars.bundle.js`.
Edit their sources in `scripts/src/` and
regenerate the outputs rather than editing the shipped files directly.

Node.js 22 or newer is the declared prerequisite and build target, not a claim of a runtime-tested
minimum host version. The optional native `keytar` and
`@azure/msal-node-extensions` packages remain external to the authentication bundles and are
provisioned into the current installation's data directory by the session-start hook.
They serve only the Graph knowledge verifier. If provisioning fails, that verifier uses an
in-memory token cache, never plaintext credentials. Graph configuration is independent of
MSAGENT and does not read the retired chat configuration; see its
[opt-in setup](skills/add-knowledge/references/access-precheck.md).
The tests check entry-point coverage, source/bundle parity, and standalone helper behavior.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft
trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
