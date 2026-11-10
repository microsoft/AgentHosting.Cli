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

**233 scenarios** across eight features. Every one drives the packaged binary through a real command
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
| `mcs-ams` | `AM-01`–`AM-35` | 27 | `ams` | MCS on AMS, ported check by check from the live checks: the flag off, `agent create`, clones, workflows and `agent init` into a new or an existing GRS repository, a teammate's init, deployments in Dev, Test and Prod, local and remote deploys with their capture and the Dev guard, connections, `agent delete`, `agent pull` with AMS unreachable or after a remote deploy, a copy of `.mcs` per commit, the create order, and the version 7 fix's checks: `.mcs` in local history, a bot AMS doesn't know, and Dev edits to a custom connector or a standalone workflow. See [MCS on AMS](#mcs-on-ams-ams) |

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
| An AMS deployment and three test-ring environments | the `ams` pack | see [MCS on AMS](#mcs-on-ams-ams); it signs in differently and runs alone |

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
| `--packs core,azure,eval` | include the scenarios that create Azure resources, that spend Copilot Credits, or both. Default is `core`. `swf` adds the standalone-workflow suite. `ams` runs MCS on AMS, alone |
| `--feature <names>` | `mcs-agent`, `mcs-workflow`, `mcs-standaloneworkflow`, `code-agent`, `abs`, `identity`, `mcs-eval`, `mcs-ams` |
| `--only <patterns>` | `MA-18`, `MA-2*`, `AB-07`, `EV-01`, `SW-B0*`, `AM-12` |
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
| `3` | passed, but cleanup could not remove something — **an orphaned cloud resource needs attention**. A resource kept on purpose (`Retained`) doesn't count |
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
inspect it. A passing scenario's workspace is kept too when a resource the run didn't remove, a
ledger entry still `Created` or `Orphaned`, points into it, and the summary lists the workspaces kept.

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

---

## MCS on AMS (`ams`)

`mcs-ams` drives `msagent` with the `AmsMcsAgents` flag on, for MCS agents and standalone workflows:
repositories on GRS, AMS records, local and remote deploys to Dev, Test and Prod, capture and pull,
and the flag off. It is opt-in, and it runs **alone**: msagent keeps one Power Platform ring per
account, the other packs use the prod ring, and this one uses the test ring. `plan` and `run` refuse
`--packs` naming `ams` with any other pack.

```pwsh
msagent auth login --cloud test
dotnet run --project . -- plan --packs ams
dotnet run --project . -- run --packs ams
```

### Prerequisites

| Requirement | Needed for | Notes |
|---|---|---|
| An AMS deployment: data plane, on-behalf-of gateway and control plane, each with its audience, and GRS's git host | every call | `amsMcs.dataPlane`, `amsMcs.onBehalfOf`, `amsMcs.controlPlane`, `amsMcs.gitHost` |
| Three test-ring environments, Dev, Test and Prod, each with its Dataverse organization | deploys, promotions, captures and the Dataverse checks | `amsMcs.devEnvironmentId`, `testEnvironmentId`, `prodEnvironmentId` and `organizations` |
| `msagent auth login --cloud test`, signed in with a test-ring account | every call, and msagent's git credential helper for GRS | the other packs need `msagent auth login` on the prod ring, so run them separately |
| `az login` to the tenant | the Dataverse reads, the simulated portal edits, and the cleanup of leftover bots, flows, custom connectors and connection references | |
| A writable publisher in Dev whose prefix isn't `new` | AM-33's custom connector, whose name starts with that prefix | read from Dataverse; any customization prefix a maker uses will do |
| `git` on `PATH`, with `user.name` and `user.email` | clones, commits, branches and pushes | |
| An MSN Weather connection in Test, made once by hand | the connection scenario | msagent can't make a connection non-interactively, and the run never tries. Make it at `https://make.test.powerapps.com/environments/<testEnvironmentId>/connections/available?apiName=shared_msnweather`, signed in as msagent's user |
| A workspace root outside any git working tree | every project the pack makes | AMS mode refuses a project folder inside another repository, and `.runs/<run>/ws` sits inside this one. For example `%TEMP%\msagent-e2e-ams` |

### The `amsMcs` block

Every key also reads an environment variable, which wins over the file. The legacy `AH_E2E_` prefix
still works.

| Key | Meaning | Passed to msagent as | Environment variable |
|---|---|---|---|
| `features` | the flag value for AMS-on calls. It must hold `AmsMcsAgents`, the default | `AH_CLI_FEATURES` | `MSAGENT_E2E_AMS_MCS_FEATURES` |
| `dataPlane.endpoint`, `dataPlane.audience` | AMS's data plane | `MSAGENT_AMS_DATAPLANE_ENDPOINT`, `MSAGENT_AMS_DATAPLANE_AUDIENCE` | `MSAGENT_E2E_AMS_MCS_DATA_PLANE_ENDPOINT`, `MSAGENT_E2E_AMS_MCS_DATA_PLANE_AUDIENCE` |
| `onBehalfOf.endpoint`, `onBehalfOf.audience` | the on-behalf-of gateway | `MSAGENT_AMS_ONBEHALFOF_ENDPOINT`, `MSAGENT_AMS_ONBEHALFOF_AUDIENCE` | `MSAGENT_E2E_AMS_MCS_ON_BEHALF_OF_ENDPOINT`, `MSAGENT_E2E_AMS_MCS_ON_BEHALF_OF_AUDIENCE` |
| `controlPlane.endpoint`, `controlPlane.audience` | AMS's control plane | `MSAGENT_AMS_CONTROLPLANE_ENDPOINT`, `MSAGENT_AMS_CONTROLPLANE_AUDIENCE` | `MSAGENT_E2E_AMS_MCS_CONTROL_PLANE_ENDPOINT`, `MSAGENT_E2E_AMS_MCS_CONTROL_PLANE_AUDIENCE` |
| `gitHost` | GRS's git host | `MSAGENT_GIT_HOST` | `MSAGENT_E2E_AMS_MCS_GIT_HOST` |
| `devEnvironmentId`, `testEnvironmentId`, `prodEnvironmentId` | the three test-ring environments, all different | command options | `MSAGENT_E2E_AMS_MCS_DEV_ENVIRONMENT_ID`, `_TEST_ENVIRONMENT_ID`, `_PROD_ENVIRONMENT_ID` |
| `organizations.dev`, `.test`, `.prod` | each environment's Dataverse URL. The lookup the other packs use goes through the prod-ring BAP, which can't see test-ring environments | not passed | `MSAGENT_E2E_AMS_MCS_ORGANIZATIONS_DEV`, `_TEST`, `_PROD` |
| `workspaceRoot` | where the pack's projects go. Environment variables in it are expanded | not passed | `MSAGENT_E2E_AMS_MCS_WORKSPACE_ROOT` |
| `forbiddenEnvironmentIds` | environments the run must never touch, such as one holding real agents | not passed | `MSAGENT_E2E_AMS_MCS_FORBIDDEN_ENVIRONMENT_IDS`, separated by commas or semicolons |

The code agents' `ams` block is separate, and unchanged.

### Capabilities

`plan --packs ams` adds seven lines. Each missing value is a `SKIP` that names its key.

| `plan` name | Available when |
|---|---|
| `amsMcs` | `features` holds `AmsMcsAgents`, and every endpoint, audience and the git host are set |
| `amsMcs.ring` | three different environment GUIDs, each with an https organization, and none of them forbidden |
| `amsMcs.root` | `workspaceRoot` is set and outside every git working tree, and the run's folder in it is empty or absent |
| `git` | `git` runs from `PATH`, with `user.name` and `user.email` |
| `amsMcs.oracle` | `az` gives a token that each organization accepts. Probed only when an `mcs-ams` scenario is scheduled |
| `amsMcs.connection` | Test holds an MSN Weather connection the signed-in user can use. It is listed with msagent's git credential token, which the test ring's Power Platform API accepts and `az`'s isn't. Probed only when a scheduled scenario needs it; the `SKIP` gives the link to make one |
| `amsMcs.endpoints` | the data plane, the on-behalf-of gateway, the control plane and GRS's git host each answer one plain HTTPS request. Any HTTP status counts, 401, 403 and 404 included. Probed only when an `mcs-ams` scenario is scheduled. When one fails on DNS, TLS, the connection or a 30-second time limit, the line names it, and every `mcs-ams` scenario ends `BLOCKED` before the test-ring guard, so the run changes nothing |

### Scenarios and the home project

| Id | Drives | Needs | Tier |
|---|---|---|---|
| AM-01 | the flag off: the AMS-only forms are unknown; create, deploy, pull, init and delete are main's | ring, oracle | Creating |
| AM-02 | `agent create` into an empty folder: P | ring, git | Creating, smoke |
| AM-03 | a second agent in P | ring, git | Creating |
| AM-04 | `agent create --repository-id` into a new folder, then answering `n` to the clone question | ring, git | Creating |
| AM-05 | a repository GRS doesn't have | ring | ReadOnly |
| AM-06 | a clone of a Dev bot inside P, which joins R; its capture and a remote Dev deploy; a second clone of the bot fails with `BotClaimed` | ring, git, oracle | Creating |
| AM-07 | a workflow scaffolded into W, and a clone of a Dev workflow that joins W; AMS lists both, and a remote deploy connects no folder | ring, git, oracle | Creating |
| AM-08 | `agent init`: a new repository, an attach with `--repository-id`, refused attaches that leave the folder as it was, and a working tree with history | ring, git | Creating |
| AM-10 | `deployment create` in Dev, Test and Prod, refused creates, a rename and a delete | ring, git | Creating, smoke |
| AM-11 | A's local Dev deploy, then a portal rename captured on `main` | ring, git, oracle | Creating, smoke |
| AM-12 | remote Dev deploys and the Dev guard: a clean head connects the folder, other heads leave it alone with a notice, 19050 and 19051; a bound bot with no cloud base gives 19038 and 19064 | ring, git, oracle | Creating, smoke |
| AM-13 | remote Test: a commit Dev deployed is accepted, one it never did is refused (19002), and the default is Dev's newest | ring, git, oracle | Creating |
| AM-14 | remote Prod takes Test's newest published commit; `--commit-id` is refused (19063); nothing published to Test fails (19004), then works once it is | ring, git, oracle | Creating |
| AM-15 | a standalone workflow attached to W with `agent init --repository-id`, then its connections on Test: a draft, a bind that turns it on, and a redeploy that keeps it | ring, git, oracle, connection | Creating |
| AM-09 | a teammate, two ways: `git clone` then `agent init`, or `agent init --repository-id` alone; P4 and an empty repository fail and remove the clone | ring, git | Creating |
| AM-16 | `agent delete`, and again from the restored `.config`; then a teammate's `agent init --repository-id` of R without `--agent-name` fails, naming the deleted agent's folder, and takes its clone back (U16) | ring, git, oracle | Creating, smoke |
| AM-18 | `agent pull` with every `MSAGENT_AMS_*_ENDPOINT` set to an `.invalid` host | ring, git, oracle | Creating |
| AM-19 | pull after a remote deploy, on X: a deploy of the commit a clean folder holds connects it, another branch's commit leaves a teammate's folder alone (10011), the connected folder pulls the change, and a downloaded knowledge file keeps its CRLF bytes | ring, git, oracle | Creating |
| AM-20 | a copy of `.mcs` per commit, on Z: pull uses the commit's copy, a folder behind is put back with `git restore`, an uncommitted change or a diverged HEAD is refused, and `agent delete` forgets the copies | ring, git, oracle | Creating |
| AM-21 | the create order: usage errors before any AMS work, without `--non-interactive`, with input closed and a 120-second limit | ring, git | ReadOnly |
| AM-25 | the other remote-deploy cases, on X: `--async`, a commit that isn't pushed, a copy of the project without `.git`, and git off `PATH`; each leaves the folder's connection as it was | ring, git | Creating |
| AM-26 | the other copy cases, in Z's repository: a clone and a local Dev deploy each save a copy for HEAD, a HEAD with no copy pulls with its nearest ancestor's, and a folder behind with an uncommitted change is refused | ring, git | Creating |
| AM-28 | agents and workflows in R: one workflow joins through P's remote, another with `--repository-id`; AMS lists both kinds, and a teammate's init restores them all | ring, git | Creating |
| AM-29 | `agent init` of a working tree whose second commit adds `wf/.mcs/workflow-state.json` is refused with 19059's history wording before any question, and AMS lists no new agent; after the history is squashed it succeeds, and a fresh clone holds no `.mcs` in its tree or its history (BF1) | ring, git | Creating |
| AM-32 | after an AMS-off deploy, a Dev rename and `agent init`, a remote Dev deploy fails with `CaptureBaseUnknown` (19064) and the rename stays; after a local deploy the next remote Dev deploy captures first; on a second such project, `--overwrite` takes the bot over (BF4) | ring, git, oracle | Creating |
| AM-33 | a classic agent with a custom connector, pushed to Dev; a Dev edit of the connector's description is captured by the next remote Dev deploy, whose captured commit holds it, and Dev keeps it (BF5) | ring, git, oracle | Creating |
| AM-35 | W's workflow deployed to Dev: a Dev rename of its flow is captured by the next remote Dev deploy and deployed; once `main` changes the workflow, the deploy is refused without `--overwrite` (19008, with a remediation that names `--overwrite`), and with it overwrites the rename (BF9) | ring, git, oracle | Creating |

### Cleanup

| Ledger kind | Cleanup |
|---|---|
| `AmsAgent` | `deployment delete` for each Test and Prod deployment, with `--delete-deployed-mcs-agent` for an agent deployed there and never for a workflow, then `agent delete --deployment-id <dev>`, with AMS on, or off for an agent made with the flag off. If that fails, it removes the agent's `.mcs` and tries once more |
| `AmsFlow` | a workflow's flow that `agent delete` doesn't reach, in Test or Prod, or in Dev under a name a scenario gave it: turned off and deleted through Dataverse by its exact name, then checked gone |
| `LeftoverBot` | each schema name the run used, in each of the three organizations: a bot still there is deleted with its components, after its schema name is checked |
| `GrsRepository` | soft-deleted after the run's `agent delete` calls, once `agent list --view ams --repository-id` lists no agent in it: a `DELETE` on its git URL with msagent's git credential token. GRS then reads it as `Deleted`, and `POST <git url>/recover` brings it back |
| `AmsConnectionReference` | AM-33's connection reference in Dev, which `agent delete` leaves: deleted through Dataverse by its exact logical name after the bots, then checked gone |
| `AmsConnector` | AM-33's custom connector in Dev, which `agent delete` leaves: deleted through Dataverse by its exact name after its connection reference, then checked gone |
