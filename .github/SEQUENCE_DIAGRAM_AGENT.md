# Sequence Diagram Verification Agent

The [workflow](workflows/verify-sequence-diagrams.yml) audits tracked
`docs/*sequence-diagram.md` files against the `msagent` CLI implementation. It runs
Mondays at 08:00 UTC or on manual `workflow_dispatch`.

**There are currently no matching diagrams.** Until they are added, the workflow
reports a successful no-op before installing tools, requiring model credentials,
or invoking Copilot. It does not generate new diagrams.

When diagrams exist, Copilot proposes conservative Markdown corrections locally.
Mermaid renders the results, and the workflow publishes diffs to a single GitHub
issue. It never commits, pushes, creates branches, or opens pull requests.

## Components

| File | Purpose |
|---|---|
| [Workflow](workflows/verify-sequence-diagrams.yml) | Discovery, tool setup, audit, rendering/repair, and publication |
| [Audit prompt](prompts/verify-sequence-diagrams.prompt.md) | Compare diagrams with CLI source and correct demonstrated drift |
| [Syntax-repair prompt](prompts/fix-mermaid-syntax.prompt.md) | Repair Mermaid syntax without changing meaning |
| [Helper](scripts/sequence-diagram-audit.mjs) | Discover tracked diagrams, validate edit boundaries, and refresh issue comments |
| [Offline tests](scripts/sequence-diagram-audit.test.mjs) | Exercise Git fixtures and simulated GitHub responses without model or service access |

The prompts use [.github/copilot-instructions.md](copilot-instructions.md) for this
repository's architecture. Source projects are directly under `src/`, not the SDK's
`src/libraries/` layout.

## Setup

1. Commit the workflow, prompts, helper, and tests to the default branch so scheduled
   and manual runs can use them.
2. When ready to audit real diagrams, configure the repository secret
   **`COPILOT_CLI_PAT`** with a fine-grained user PAT whose account permission is
   **Copilot Requests: Read**. No repository permissions are needed on this PAT.
   The owner needs Copilot access and must satisfy applicable organization policies.
3. Ensure repository Actions policy allows the pinned actions and Copilot usage.
   Publication uses the workflow's `GITHUB_TOKEN` with `contents: read` and
   `issues: write`; it does not need a PAT with repository write access.
4. Ensure the **`documentation`** label exists. It is applied when the issue is created.
5. Add tracked Markdown diagrams directly under `docs/` with names ending in
   `sequence-diagram.md`. Include fenced `mermaid` blocks, clear participants, and
   source paths that let the auditor follow the documented implementation.

Create or replace the model secret through GitHub repository settings, or:

```powershell
gh secret set COPILOT_CLI_PAT --repo microsoft/AgentHosting.Cli
```

The audit and syntax-repair steps receive the PAT only as `COPILOT_GITHUB_TOKEN`.
They inspect local files and receive no `GH_TOKEN`. Checkout does not persist Git
credentials. Only the deterministic publication step receives
`GH_TOKEN: ${{ github.token }}`.

## Execution

1. **Discover:** enumerate diagrams tracked at the checked-out commit. Skip the
   remaining steps if none match. Discovery uses the runner's preinstalled Node;
   the workflow then selects Node 22 for tool execution.
2. **Prepare:** check model authentication, run the helper's offline tests, and
   install pinned Copilot CLI `1.0.98` and Mermaid CLI `12.0.0`. Mermaid requires
   Node 22.13.0 or newer. No .NET setup is needed for static source inspection.
3. **Audit:** inspect source and edit diagrams only where the code proves a
   discrepancy. No build, test, sign-in, or live-service operations are permitted.
4. **Validate and render:** compare all tracked changes with the original commit.
   Reject edits outside the original diagram set, additions, deletions, renames,
   untracked files, changed HEAD, invalid UTF-8, and added/removed UTF-8 BOMs.
   Render every diagram with Mermaid.
5. **Repair:** if rendering fails, supply the errors to Copilot for at most two
   syntax-repair attempts. Check edit boundaries again before each rendering pass.
   Persistent renderer failures fail the job without publishing proposals.
6. **Publish:** validate boundaries again, then open or reuse the issue titled
   **`docs: sequence diagram drift detected`**. Each changed diagram gets a
   workflow-owned comment with the audited commit, audit note, and proposed diff.
   If no diagrams changed, no GitHub API calls are made.

Comments use stable hidden markers and are updated when their content changes.
Identical reports are left alone. Only comments authored by `github-actions[bot]`
are eligible for updates; human comments cannot suppress a report or be overwritten.
Comments from the previous workflow format are migrated in place. The workflow
does not automatically close issues or remove older comments for resolved diagrams;
check each proposal's cited commit before applying it.

The validator/publisher is copied outside the checkout before the agent starts,
and logs/rendered artifacts stay in `RUNNER_TEMP`. This prevents accidental
repository edits from replacing the validation code or polluting the proposed diff.
The checks are **not a security sandbox**: Copilot still has unrestricted tool
approval and the model token in its process. Review that trust boundary before
expanding inputs or capabilities.

The whole job is limited to 30 minutes, and each model invocation has an eight-minute
timeout. Up to three model invocations may occur: the audit and two repair attempts.

## Review and apply

Run **Actions > Verify sequence diagrams > Run workflow** for an on-demand audit.
Review each proposed diff against its audited commit before applying it. A maintainer
can apply the changes manually or assign the issue to Copilot where that capability
is enabled. Assignment is not automatic and is not a prerequisite for this workflow.

The issue-based approach is intentional; it does not assume any particular enterprise
policy concerning branch pushes or pull-request creation.

## Maintenance and offline checks

Run the helper tests without restoring .NET or npm dependencies:

```powershell
node --test .github\scripts\sequence-diagram-audit.test.mjs
```

They create disposable local Git repositories and simulate GitHub responses.
They do not authenticate, spend Copilot credits, render through a browser, or create
real issues. A successful local test run does not replace an eventual Actions smoke
test with a real diagram and approved model access.

Keep the workflow's discovery, validator, lint scope, prompts, and this guide aligned
if diagram locations change. Upgrade pinned tools deliberately, and check their Node
requirements. Change `max_fixups`, invocation/job timeouts, or the schedule in the
workflow when adjusting operating limits.
