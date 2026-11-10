# CLI comparison and conformance

The maintained comparison is an offline Azure Bot Service contract check, not a
competition between implementation proposals or proof of complete CLI parity.
Follow [CONTRIBUTING.md](../../CONTRIBUTING.md) for contributor requirements.

## Scope and evidence

[`src/AgentHostCli.Conformance`](../../src/AgentHostCli.Conformance/README.md)
contains a language-neutral fixture manifest, ten scoped ABS fixtures, and a
TypeScript oracle runner. Each fixture supplies fake Azure state, command
arguments, normative assertions, and captured JSON/human-output goldens.

The TypeScript input is pinned by
[`fixture-set.json`](../../src/AgentHostCli.Conformance/fixture-set.json) to
commit `5624d5459d09c67a584364d4bfd3b8496aeb9dc4` of
`bic/agents-managed-hosting`. That is comparison provenance, not this
repository's location or an instruction to fetch PR builds from it.
Keep the pinned implementation read-only. The runner needs its package source
and installed dependencies; the normal .NET tests do not require Bun or Node.

The C# `AbsPreviewContractTests` and `AbsApplyContractTests` consume the same
fixtures. Both implementations compare exit codes, JSON values, ordered
normalized human output, and mutation-port calls in human and `--json` modes.
The supplied-app path must not issue Microsoft Graph calls. Preview and apply
without force must not mutate; the confirmed scoped apply performs at most one
ARM upsert.

These are deterministic fake-backed checks, not live authentication, service
compatibility, cross-island deployment, or runtime-readiness evidence. The
fixture manifest marks the baseline provisional; do not expand that into a
claim that every current command is equivalent to TypeScript.

## Reproduce the comparison

Build the matching configuration first using the root contributor guide.
Run the C# selection from the repository root:

```powershell
dotnet test src\Microsoft.Agents.Hosting.Cli.Tests\Microsoft.Agents.Hosting.Cli.Tests.csproj --no-build --no-restore -c Debug --filter "FullyQualifiedName~AbsPreviewContractTests|FullyQualifiedName~AbsApplyContractTests"
```

With Bun and an already-prepared pinned package directory, run the oracle from
the repository root, replacing the example path with that directory:

```powershell
bun src\AgentHostCli.Conformance\ts-oracle\runOracle.ts --pinned C:\repos\pinned-agenthost-cli
```

The pinned directory is the package containing `src` and `node_modules`, not its
repository root. These commands use injected fakes, not live cloud services.
Running one runner does not execute or validate the other. Record the repository
revision, pinned revision, exact commands, and outcomes when reporting results;
do not treat old successful runs as evidence for a new revision.

## Normalization and intentional differences

Human-output comparison strips ANSI color, trims trailing whitespace, normalizes
newlines, and drops blank lines while preserving the order and content of
nonblank lines. JSON object-key order is insignificant. There is no value-level
normalization: `volatileFields` is empty, with clocks and fake IDs fixed instead.
Do not mask timestamps, identifiers, hosts, or correlations to hide divergence.

C# failure envelopes add integer `errorCode`. The C# runners assert that code
and exclude only that field when comparing with the TypeScript golden. Other
JSON fields, success envelopes, exit codes, and mutation assertions remain
subject to comparison.

The scoped C# ABS path requires a supplied app ID and does not carry connection
metadata tags. These differences are outside the current fixtures' observable
comparison; they are not permission to normalize other differences away.
Authentication behavior is outside this ABS oracle's scope.

When a contract intentionally changes, update the normative assertions, goldens,
runner expectations, and explanation together. Keep the detailed
[fixture and normalization reference](../../src/AgentHostCli.Conformance/README.md)
aligned, and do not recapture goldens merely to make a regression pass.
