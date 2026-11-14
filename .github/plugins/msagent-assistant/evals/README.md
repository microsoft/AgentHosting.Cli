# Skill evaluations

These are opt-in, live-model tests, separate from `npm --prefix scripts test`.
They require an authenticated GitHub Copilot CLI supporting `--available-tools`,
`--output-format json` and `--usage-output-file`. Live runs consume model credits.
The runner uses the configured model; it does not change model preferences.

## Run

Use a **new output directory** under private session storage, outside the repository:

```powershell
npm --prefix scripts run eval:skills -- --output "C:\temp\skill-eval-run"
```

The default smoke suite makes **30 fresh model calls**: four trigger cases, plus
three outcome cases both with and without the selected skill body, repeated three
times. `--suite all` covers all 22 trigger prompts plus thirteen outcomes
(144 calls at three repeats). `--split train` or `--split validation` selects a
corpus split. `--case id,id` narrows selection; `--repeats 1` is useful for a
single-run diagnostic, not a stability measurement. Use `--suite all --case ...`
to select a non-smoke case. Runs are sequential and stop on operational failure.

After correcting only a grader, reassess saved responses without paying for more
model calls:

```powershell
npm --prefix scripts run eval:skills -- --regrade "C:\temp\skill-eval-run" --output "C:\temp\skill-eval-regraded"
```

Regrading refuses changed prompts or skill/reference context. It writes a new
report pointing to the original evidence; original traces and reports are retained.

Keep validation prompts held out while tuning descriptions; use train results
for edits and a new validation run afterwards. Add realistic prompts and expected
outcomes when behavior changes, rather than weakening a failing assertion.

## What is measured

- **Trigger probes:** choose among the nine checked-in names/descriptions and
  `none`. Positive prompts cover every skill; negatives include Git operations,
  local deletion and posting to Teams rather than configuring a channel.
- **Outcome probes:** unsupported-workspace refusal, candidate website YAML plus
  post-write verification, exact creation-to-publication identity handoff, explicit
  workspace mismatch refusal, cloud-target deployment selection, incomplete pre-pull
  preservation, and literal PowerShell import arguments.
  Assertions check structured decisions, concrete identifiers and parsed YAML,
  not a second model's opinion.
  Handoff arguments may be flat or nested under `arguments`; conflicting explicit
  target values fail rather than being silently normalized away.
- **Baseline:** the same outcome request without the selected skill body or its
  references. Ambient installed-skill metadata may still be present in the CLI;
  this is a no-skill-body baseline, not a completely empty host configuration.
- **Evidence:** per-case prompts, complete JSONL traces, stderr and CLI usage JSON;
  `report.json` contains individual assertion results, response artifacts, skill
  and corpus hashes, per-case pass rates, timing and reported token/usage totals.
  Input totals include uncached, cache-read and cache-write token buckets; the
  separate buckets remain available for billing interpretation.
  Missing usage stays distinguishable through `reportedRuns`; it is not measured
  zero usage. Raw `nanoAiu` is retained in the CLI's units, not converted to money.

Review individual artifacts and traces as well as aggregate rates. Exit code 1
means a with-skill assertion failed or the client failed; baseline failures alone
do not fail the suite. Never treat a failed or incomplete run as a passing gate.

## Isolation and limits

Each call starts a new session in an empty temporary working directory. The model
receives synthetic fixtures and injected skill/reference text; action, read and
network tools are hidden through a nonmatching tool allowlist. Built-in MCP servers
and WorkIQ are disabled, custom instructions are disabled, and auto-approval is
disabled. The runner rejects traces containing tool requests. It does not load
the repository as a plugin or run its startup hooks.

These tests **do not** prove native host skill activation, conditional reference
reads, actual filesystem edits, authentication, synchronization or cloud behavior.
They test routing decisions, instruction-following and proposed artifact quality.
The offline suite separately verifies real importer writes, no-write dry runs and
helper contracts. Native activation needs a separately instrumented host test;
cloud mutation tests require their own explicit targets and authorization.

The CLI can still load personal plugin metadata or startup configuration. Inspect
the captured trace for that context when comparing runs on different machines.
Use synthetic fixtures only; do not put credentials or workplace data in prompts.
