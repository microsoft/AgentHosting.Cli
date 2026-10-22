# AgentHost CLI — ABS conformance oracle (provisional)

This directory freezes the **scoped** shipped TypeScript **Azure Bot Service (ABS)** behavior of the
AgentHost CLI as a **language-neutral, provisional** conformance baseline — captured *before* any C#
ABS behavior is implemented (plan node **cN1**).

It contains three things:

| Path | What it is |
|------|------------|
| `fixture-set.json` | Set-level manifest: pinned TypeScript revision, `provisional` status, scope, and the fixture index. |
| `fixtures/*.json` | The 10 scoped, **language-neutral** ABS fixtures. Each carries its own source metadata, the CLI `argv`, declarative fake Azure state, the normative `assert` block, and a captured `golden` (exact JSON envelope + normalized human output). |
| `ts-oracle/runOracle.ts` | The **TypeScript oracle runner** that exercises the fixtures against the pinned production code with injected fakes. |

The C# contract runner (from plan node cN5 onward) consumes the **same** `fixtures/*.json` documents.

**C# error-code extension:** C# JSON failure envelopes additionally include an integer `errorCode`.
The C# contract runners assert the expected numeric code for each failure, then exclude only that
field when comparing with the frozen TypeScript golden. Success envelopes and all other failure
fields, human output, exit codes, and mutation assertions remain unchanged.

---

## What the oracle proves

For each fixture the runner exercises the **real** pinned production command core
(`absPreviewAsync` / `absApplyAsync`) with **injected fake** Azure dependencies, then compares the
observable result:

- **exit code** (normative),
- the exact **JSON envelope** emitted under `--json` (normative),
- the **normalized human output** (ordered lines, ANSI/newline-normalized),
- which **mutation ports** were invoked (proving preview/no-force never mutate, and that the scoped
  path performs at most one ARM upsert and **never** issues a Microsoft Graph call).

It runs each fixture **twice** — once for human output and once for `--json` — and fails if the
output format changes the exit code, the thrown outcome, the error kind, or the recorded mutations.

## Faithfulness and non-intrusion

- The pinned TypeScript worktree is imported **read-only** and is **never modified**.
- Instead of the real `buildProvisionCommandContext`, the oracle injects a fake
  `ProvisionCommandContext` — the same seam the pinned unit tests use. Therefore **no MSAL provider
  is constructed, no browser/WAM/device-code prompt can open, and no ARM / Graph / Azure / identity
  network request is ever made.** The runner is fully **offline**.
- Every ABS fixture supplies `--msa-app-id`, so the pinned core takes the **zero-Graph** path. The
  fake Graph reads **throw**, so any regression that issued a Graph read on this path would fail the
  oracle immediately.
- The only production behavior *reproduced* here (rather than imported) is the thin, un-injectable
  shell mapping from a thrown error to a process exit code + JSON/stderr envelope. That mapping
  mirrors `boot()` in the pinned `src/Cli.ts` and **must be kept in sync with it**. Everything else
  — argument validation, desired-state derivation, the review planner, the output contract, the
  redaction guard, and the human/JSON renderers — is the genuine pinned production code.

---

## Pinned source

| | |
|---|---|
| Repo | `bic/agents-managed-hosting` |
| Branch | `ah-start` |
| Commit | `5624d5459d09c67a584364d4bfd3b8496aeb9dc4` |
| Package | `src/AgentHostCli` |
| Default read-only worktree | `C:/CliAffordances/amh/src/AgentHostCli` |

The pinned worktree must contain both `src/` and an installed `node_modules/` (the oracle imports
the pinned module graph, which references `commander`, `@azure/msal-node`, etc.). No Azure or
network access is required — only the ability to load those modules.

## Pointing the runner at the pinned worktree

Resolution order (highest precedence first):

1. `--pinned <dir>` command-line argument,
2. `AH_PINNED_TS_DIR` environment variable,
3. the built-in default `C:/CliAffordances/amh/src/AgentHostCli`.

`<dir>` is the **AgentHostCli package directory** (the one containing `src/` and `node_modules/`),
not the repository root. Use the pinned `ah-start` worktree, or create a dedicated read-only
worktree at the same commit:

```powershell
# Optional: a dedicated read-only worktree pinned at the frozen revision
git -C C:\repos\agents-managed-hosting worktree add --detach C:\CliAffordances\amh-abs-oracle 5624d54
# then point the runner at its AgentHostCli package
bun runOracle.ts --pinned C:\CliAffordances\amh-abs-oracle\src\AgentHostCli
```

---

## Running

From this `ts-oracle/` directory (requires `bun`):

```powershell
# Verify: run every fixture and compare against its stored golden. Exit 0 iff all match.
bun runOracle.ts

# Verify against a specific pinned worktree
bun runOracle.ts --pinned C:\CliAffordances\amh\src\AgentHostCli

# Update: (re)capture each fixture's `golden` from the live run. Only writes when the live run
# agrees with the fixture's hand-authored `assert` block.
bun runOracle.ts --update
```

Equivalent package scripts: `bun run oracle`, `bun run oracle:update`.

### Determinism

The runner fixes the clock (per fixture), forces non-TTY / `NO_COLOR` output, and uses fixed fake
ids, so repeated runs produce **byte-identical** goldens. Re-running `verify` after `update` (and
running `update` twice) must produce no change. Any nondeterminism is a failure.

---

## The 10 scoped fixtures

| # | Fixture | Verb | Outcome |
|---|---------|------|---------|
| 1 | Supplied-app greenfield preview | `abs preview` | exit 0, plans a bot create, no mutation |
| 2 | Supplied-app confirmed create | `abs apply --force` | exit 0, one `arm.upsert`, no Graph |
| 3 | Apply without `--force` | `abs apply` | exit 0, previews only, no mutation |
| 4 | Matching-state no-op | `abs apply --force` | exit 0, `unchanged`, no mutation |
| 5 | Endpoint update | `abs apply --force` | exit 0, one `arm.upsert`, `updated` |
| 6 | Existing bot bound to a different app id | `abs apply --force` | exit 2, `naming-conflict`, no mutation |
| 7 | Missing required context | `abs preview` | exit 2, `missing-context` |
| 8 | Provider not registered | `abs apply --force` | exit 1, `provider-not-registered`, no mutation |
| 9 | Target-tenant silent-auth failure | `abs preview` | exit 3, interaction-required, no mutation |
| 10 | Secret-shaped output rejection | `abs preview` | exit 1, redaction guard, no mutation |

**Fixture 10 note:** its `--msa-app-id` is a **synthetic, non-functional, JWT-shaped test vector**
(not a real token). It exists solely to exercise the by-construction secret-material guard. The
captured golden reports the offending property **paths only** and contains **no secret value**.

## Provisional status and reconciliation

These fixtures are **provisional**: they are captured from the pinned shipped TypeScript
implementation, not from the (as-yet-unbuilt) shared `agenthost_cli_amh` mN3 oracle. When mN3 becomes
available, the fixtures are reconciled against it (plan nodes cN7 / cN11) and any unresolved
difference is recorded explicitly in the evaluation. The demonstrator does **not** block on mN3.

---

## Cross-language conformance closure (cN7)

As of plan node **cN7**, the **full** neutral fixture set (all 10 fixtures — not a subset) is
verified against **both** implementations, from clean outputs, and every observable difference is
driven to zero or recorded as an explicit contract change. The two runners share the **same**
`fixtures/*.json` documents, so agreement is transitive: if the live TypeScript run equals a
fixture's `golden` **and** the live C# run equals the same `golden`, the two implementations agree
on that fixture's exact JSON envelope, exit code, mutation calls, and normalized human output.

### The two runners

| Implementation | Runner | Command | Coverage |
|---|---|---|---|
| TypeScript (pinned, read-only) | `ts-oracle/runOracle.ts` (via `bun`) | `bun runOracle.ts` | all 10 fixtures, each in human + `--json` mode |
| C# demonstrator | `Microsoft.Agents.Hosting.Cli.Tests` contract runners | `dotnet test Microsoft.Agents.Hosting.Cli.Tests` | preview subset `{01,07,09,10}` + apply subset `{02-06,08}` = all 10, each ×2 modes = 20 cases |

The C# side is split across `AbsPreviewContractTests` and `AbsApplyContractTests`, which
auto-enumerate `fixtures/*.json` and partition by `argv[1]` (`preview` / `apply`); together they
cover the complete set. The two runners are **deliberately separate processes**: the C# `dotnet
test` suite carries no `bun`/Node dependency, so the host offline-guarantee and the default C#
validation stay hermetic. Closure is reproduced by running both commands, not by embedding one
runner inside the other.

### Reproduce the closure

```powershell
# TypeScript side (offline: fakes injected, no Azure/identity/network; requires bun + the pinned worktree's node_modules)
cd src/AgentHostCli.Conformance/ts-oracle
bun runOracle.ts                 # expect: "RESULT: all 10 fixtures passed."

# C# side (offline)
cd ../../.. ; cd src
dotnet test Microsoft.Agents.Hosting.Cli.Tests --filter "FullyQualifiedName~ContractTests"   # expect: 20 passed
```

### Normalization rules (enumerated and justified)

Comparison is **value-exact**; normalization is limited to presentation that is volatile or
platform-specific and cannot mask a behavioral divergence. Both runners apply the identical set:

| Rule | TS (`normalizeLines` / `deepEqual`) | C# (`NormalizeLines` / `JsonDeepEquals`) | Why it cannot hide a divergence |
|---|---|---|---|
| Strip ANSI color | `/\x1B\[[0-9;]*m/g` + `NO_COLOR=1`, non-TTY | `\u001b\[[0-9;]*m` regex | Color is pure terminal presentation; the color *decision* is tested separately and never appears in `--json`. |
| Trim trailing whitespace | `.replace(/\s+$/, '')` per line | `line.TrimEnd()` per line | Trailing whitespace carries no semantics and is editor/platform-volatile. |
| Normalize newlines | `.split(/\r?\n/)` | `.Split('\n')` (then `TrimEnd` removes a stray `\r`) | CRLF vs LF is a platform artifact, not behavior. |
| Drop blank lines | `.filter(l => l.length > 0)` | `.Where(l => l.Length > 0)` | Blank-line spacing is layout, not content; **ordered** non-blank lines are still compared exactly. |
| JSON key order | `canonical()` recursive key-sort before compare | `JsonDeepEquals` order-insensitive | Object key order is not semantically meaningful in JSON; every key and **value** is still compared exactly. |

There is **no value-level normalization**: every fixture declares `"volatileFields": []`, so no
timestamp, id, hostname, or correlation value is masked. Determinism is instead achieved by
*fixing* volatile inputs — the clock is pinned per fixture (`now: () => fixture.clock`), fake ids
are fixed, and color/TTY are forced off — so repeated runs are byte-identical rather than normalized
after the fact. Both runners were executed **twice** and produced no diff (and the TypeScript
`verify` mode writes nothing, leaving the fixtures unchanged).

### Reconciliation notes and contract-change records

- **Result:** all 10 fixtures pass byte/line/exit/mutation parity in both implementations,
  deterministically. **No unexplained observable ABS divergence exists.**
- **Auth is excluded from the ABS oracle by design.** Operator authentication behavior is judged
  against PAC (not these fixtures); every fixture supplies `--msa-app-id` and takes the zero-Graph
  ARM path. Auth-related differences are therefore *out of scope* here, not normalized away.
- **Explicit contract-change records** (intended C#-vs-TS deltas, none of which is observable on any
  shared fixture, so none is a defect):
  - *Missing-context requires `--msa-app-id` (C#).* The C# scoped path is supplied-app-only, so its
    missing-context aggregate additionally requires `--msa-app-id`; the pinned TS `requireProvisionArgs`
    omits it because TS also has a create path. No fixture omits `--msa-app-id` (fixture 07 supplies
    it and omits the other four), so the emitted `missing-context` message is byte-identical on both
    sides. Recorded in `TDD.md` (cN5).
  - *Connection metadata omits `tags` (C#).* The pinned TS `isValidMetadata` does not validate `tags`;
    the C# store carries none. Not observable in any fixture (metadata is a side artifact, not part of
    the envelope). Recorded in `TDD.md` (cN6).
- **mN3 reconciliation still pending.** These fixtures remain *provisional* against the future shared
  `agenthost_cli_amh` mN3 oracle. cN7 closes cross-language parity against the **pinned TS**; the mN3
  reconciliation (and any resulting fixture re-provenance) is completed in **cN11**. The demonstrator
  does not block on mN3.
