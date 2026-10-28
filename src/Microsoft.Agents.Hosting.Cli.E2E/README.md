# Microsoft.Agents.Hosting.Cli.E2E — live end-to-end suite

`msagent-e2e` packs the `msagent` CLI from source and drives the real binary against a **real tenant**. It is
not a unit test project: it signs in, creates and deletes Copilot Studio agents, and — when you opt
in — creates and deletes billable Azure resources.

It is a console application rather than a test project, so `dotnet test` never executes it. Nothing
here runs unless you run it.

> **It writes to the environments you name.** Naming an environment in `e2e.config.json` *is* the
> grant; there is no second switch. Point it at a disposable environment holding nothing you care
> about.

---

## What it covers

**210 scenarios** across seven features. Every one drives the packaged binary through a real command
line and asserts on exit code, stdout, and — where it matters — the cloud state afterwards.

| Feature | Ids | Count | Pack | What it pins |
|---|---|---|---|---|
| `mcs-agent` | `MA-01`–`MA-85` | 78 | `core` | Environment selection, `agent init/create/show/list/pull/deploy/delete`, deployment slots, connection reporting and binding, workflow activation, dev→test promotion, agent sharing, and the delete-confirmation paths |
| `mcs-workflow` | `MW-01`–`MW-61` | 14 | `core` | Standalone cloud flows with no agent: scaffold, `agent init`, deploy and publish, clone from cloud, the pull/deploy round trip and its overwrite semantics, cloud-only listing with `--filter`, and the delete-confirmation paths |
| `code-agent` | `CA-01`–`CA-20` | 20 | `azure` | Unified `deployment` routing between agent types, Azure Bot Service provisioning, update/delete, and option-shape rejection |
| `abs` | `AB-01`–`AB-09` | 9 | `azure` | `abs preview` planning, `abs apply --force` create/update/no-op, and the refusal paths |
| `identity` | `ID-01`–`ID-05` | 5 | `azure` | `identity create app/blueprint --dry-run` and argument validation |
| `mcs-eval` | `EV-01`–`EV-28` | 28 | `eval` | `agent eval` conversation test sets, CSV import and export, generated conversations, project-based agent resolution for both workspace shapes, and the scored runs — start, list, show, cancel, delete and `--watch` |
| `mcs-standaloneworkflow` | `SW-A01`–`SW-X03` | 56 | `swf` | The aggressive standalone-workflow suite: every fixture shape deployed as draft and activated, with full, partial and missing connections; clone/pull round trips against a second operator; listing, delete and slot rules; refusals checked for side effects; every report checked against a read-only Dataverse oracle. See [Standalone-workflow suite](#standalone-workflow-suite-swf) |

Each scenario carries a **safety tier**, which is what the confirmation prompt counts:

| Tier | Meaning |
|---|---|
| `ReadOnly` | Reads only. Creates nothing, changes nothing. |
| `Reversible` | Writes, then restores the prior state within the scenario. |
| `Creating` | Creates a real cloud resource. Recorded in the ledger and deleted during cleanup. |

`dotnet run --project . -- plan` prints the full list with each scenario's tier — it is the
authoritative index, and it never executes anything.

---

## Setup

### 1. Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| .NET 10 SDK | everything | the harness packs `msagent` from source on each run |
| A signed-in `msagent` session | everything | run `msagent auth login` before each run |
| A disposable Copilot Studio environment | the 78 `mcs-agent` scenarios | agents are created **and deleted** here |
| An Entra object id to share with | the 7 grant/revoke scenarios | `mcs.shareUserObjectId`; an object id only, never a sign-in address |
| Copilot Credits in that environment | the `eval` pack | `agent eval run` and `agent eval generate` are billed against them |
| Azure CLI (`az`) on `PATH` | the `azure` pack | cleanup shells out to it, because `msagent` cannot delete a bot |
| An Azure subscription and resource group | the `azure` pack | bots are created here when `allowCreate` is true |

**Interactive consent comes first.** ARM consent is granted through a separate client and cannot be
pre-authorized, so run one `abs` command by hand before the first `azure` run — otherwise scenarios
stop with `BLOCKED` rather than failing. The `identity` scenarios are `--dry-run` only and need no
Graph admin consent.

### 2. Create your config

```pwsh
cd src/Microsoft.Agents.Hosting.Cli.E2E
copy e2e.config.sample.json e2e.config.json
```

`e2e.config.json` is gitignored; the sample is committed and fully commented. Edit the copy — never
put your own tenant or subscription ids in the sample, or the next person to clone the repository
would silently target your environment.

Only `tenantId` and `mcs.devEnvironmentId` are needed to get started. **Every missing value becomes a
`SKIP` with a stated reason**, so the fastest way to learn what you are missing is to run `plan`.

### 3. Check what you have

```pwsh
dotnet run --project . -- plan
```

This resolves your capabilities and describes what a run *would* do. It executes no scenario.

---

## Running

Sign in first, every time:

```pwsh
msagent auth login
```

```pwsh
# Everything that needs no Azure resources and pays for no evaluation
dotnet run --project . -- run

# Include the pack that creates Azure resources
dotnet run --project . -- run --packs core,azure

# Include the pack that spends Copilot Credits on scored evaluations
dotnet run --project . -- run --packs core,eval

# A fast subset: one deploy per fixture shape, plus the previously-shipped-bug checks
dotnet run --project . -- run --packs core,azure --smoke

# One feature, or one scenario, or a prefix
dotnet run --project . -- run --feature mcs-agent
dotnet run --project . -- run --packs core,eval --feature mcs-eval
dotnet run --project . -- run --only MA-34
dotnet run --project . -- run --only MA-2*
```

| Option | Effect |
|---|---|
| `--packs core,azure,eval` | include the scenarios that create Azure resources, that spend Copilot Credits, or both. Default is `core`. `swf` adds the standalone-workflow suite |
| `--feature <names>` | `mcs-agent`, `mcs-workflow`, `mcs-standaloneworkflow`, `code-agent`, `abs`, `identity`, `mcs-eval` |
| `--only <patterns>` | `MA-18`, `MA-2*`, `AB-07`, `EV-01`, `SW-B0*` |
| `--smoke` | the fast subset |
| `--msagent-path <file>` | drive an existing binary instead of packing one (`--ah-path` is still accepted) |
| `--yes` | skip the confirmation prompt |
| `--no-color` | plain output |

A run that schedules any `Creating` scenario prompts before starting:

```
17 scenarios create real resources. Continue? [y/N]
```

---

## What you should see

`plan` reports your capabilities, then what would run:

```
Capabilities
  auth               yes   signed in
  mcs.dev            yes   env 11111111…
  mcs.test           yes   env 22222222…
  workspace.cli      yes   fixtures/MCSCliAgent (bundled)
  workspace.classic  yes   fixtures/MCSClassicAgent (bundled)
  workspace.workflow yes   fixtures/MCSWorkflow (bundled)
  azure.read         yes   sub 33333333… / rg-example · az …\az.cmd
  azure.create       yes   the run creates and deletes its own bot
  azure.existingBot  yes   my-existing-bot (read-only)
  ams                NO    no ams.endpoint supplied

Will run 91 scenarios  (readonly 58, reversible 6, creating 27)
Will skip 2 scenarios
  CA-19  agent create --agent-type CodeAgent    needs ams
  CA-20  agent deploy a CodeAgent               needs ams
```

`run` prints one line per scenario, then totals, then any failure in full:

```
  91 passed · 0 failed · 0 blocked · 2 skipped · 0 errors        17m 04s
```

Each scenario ends in one of five outcomes:

| Outcome | Meaning |
|---|---|
| `PASS` | asserted and correct |
| `FAIL` | the CLI did something other than what the scenario asserts — a real defect |
| `SKIP` | a capability was missing. The reason names the config key |
| `BLOCKED` | the CLI needed interactive sign-in or consent. **Not** a product defect; grant consent and re-run |
| `ERROR` | the harness itself failed |

A failure block names the exact command, the expectation, what actually happened, and the transcript
holding the full stdout/stderr:

```
FAIL  CA-17  records are project-scoped

  msagent deployment show --agent-id … --deployment-name … --non-interactive

  expected  a non-zero exit
  actual    exit 0

  transcript  …\.runs\20260101-120000-abcd\CA-17\transcript.log
```

### Exit codes

| Code | Meaning |
|---|---|
| `0` | every scheduled scenario passed |
| `1` | at least one scenario failed |
| `2` | configuration problem, or the confirmation was declined |
| `3` | passed, but cleanup could not remove something — **an orphaned cloud resource needs attention** |
| `4` | a harness error, or a scenario was blocked |

### Artifacts

Each run writes a directory under `.runs/<timestamp>-<id>/`:

| File | Contents |
|---|---|
| `report.json` | the machine-readable result |
| `report.md` | the same run as a readable summary |
| `<scenario>/transcript.log` | every command, its exit code, and its output, with secrets redacted |
| `dumps/msagent.<pid>.dmp` | a heap dump, written only if an `msagent` process faults |
| `bin/msagent.exe` | the exact binary that was exercised |

Workspaces for passing scenarios are removed; a failing scenario's workspace is kept so you can
inspect it.

---

## Cleanup

`run` cleans up after itself. Every created resource is recorded in a ledger as it is created, and
released at the end of the run.

If a run is interrupted — or exits `3` — remove what it left behind:

```pwsh
dotnet run --project . -- cleanup --run-id 20260101-120000-abcd
dotnet run --project . -- report  --run-id 20260101-120000-abcd
```

Anything cleanup cannot delete is listed with the manual command to remove it, so nothing is left
silently behind.

To reduce cost the suite **pools** its cloud resources: a full run uses 5 Copilot Studio agents, 2
Azure bots, and 2 app registrations, rather than one per scenario. Scenarios that delete consume from
the pool instead of creating something purely to destroy it. The trade-off is that scenarios are not
independently runnable in arbitrary order — use `--only` for a slice, not for a single scenario in
isolation.

The `eval` pack adds evaluation test sets to the ledger — recorded by name, before the call that
creates them, and deleted with `agent eval testset delete --force`.

---

## Fixtures

Two agent workspaces are bundled under `fixtures/` and used by default:

| Fixture | Shape |
|---|---|
| `MCSCliAgent` | a CLI-shaped agent — `settings.mcs.yml` and `agent.sync.yaml` |
| `MCSClassicAgent` | a classic agent — topics, knowledge files, prompts, and connection references |

They are checked in and known to deploy cleanly. Point `mcs.workspaces` at your own only when you are
specifically testing your own content — schema names are rewritten per run, so a fixture is never
deployed under a name that could collide with another operator's run.

---

## Standalone-workflow suite (`swf`)

`mcs-standaloneworkflow` drives standalone workflows (`--agent-type MCSWorkflow`) hard: happy paths,
expected failures, and messy orders, against one or two environments. It is opt-in because it deploys
and deletes dozens of flows:

```pwsh
dotnet run --project . -- run --packs swf --feature mcs-standaloneworkflow
dotnet run --project . -- run --packs swf --only SW-B0*
```

It needs `tenantId` and `mcs.devEnvironmentId`; `mcs.testEnvironmentId` unlocks the cross-environment
scenarios. `az` signed in to the same tenant unlocks the DV read — without it, the scenarios that
need it are skipped.

| Group | Ids | What it drives |
|---|---|---|
| A | `SW-A01`–`SW-A10` | scaffold, `--project`, awkward names, duplicates, `agent init` refusals, multi-record projects, slot rules |
| B | `SW-B01`–`SW-B21` | the deploy matrix — shape × draft/activated × `--publish` × full/partial/missing connections — each deployed, redeployed unchanged and redeployed edited; a missing connection or connector still deploys, as a draft; transitions, external deactivation, option validation, a cross-environment slot, inflated definitions, and a real run |
| D | `SW-D01`–`SW-D12` | clone parity and refusals (a managed flow included), a clone of a flow made outside the CLI, pull against a second operator's edit, rename and delete, a dev deploy refusing a second operator's unpulled edit, corrupt JSON |
| E | `SW-E01`–`SW-E05` | listing states, the record join, `--filter` injection safety, `--limit`, every unmanaged flow including agent flows (never managed flows), `--tenant-wide` |
| F | `SW-F01`–`SW-F12` | delete of each state, typed-confirmation variants, already-deleted flows, per-environment delete, slot-first delete, and `--non-interactive` |
| X | `SW-X01`–`SW-X03` | agent-only and code-agent-only commands and options aimed at a workflow |

**Fixtures** live under `fixtures/StandaloneWorkflows/`: `SWComplex` (35 actions, five connectors),
`SWCliNative` (three connectors), `SWClassicAgentFlow` (one), `SWDataverse` (one with no connection in
a typical environment), and `SWButtonNoop` (no connector; the only flow the suite runs), plus the bundled
`MCSWorkflow`. Every copy is re-identified — a new workflow id, an `ahe2e-SW-<id>-<run>` name, and
connection reference names unique to the run.
