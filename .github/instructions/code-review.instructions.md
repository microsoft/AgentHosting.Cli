---
applyTo: "**"
---

# Code review rules (AgentHosting.Cli)

These are the repository-wide rules for **GitHub Copilot code review**. Because they live in
`.github/instructions/`, GitHub Copilot applies them automatically when reviewing pull requests,
and the local `review` agent (`.github/agents/review.agent.md`) loads the same file. This is the
**single source of truth** for review rules — edit this file to change what both GitHub and the
local review enforce, so the two never diverge.

Use `.github/copilot-instructions.md` for repository architecture and conventions. This
repository builds the `msagent` CLI, not the Agents SDK or an agent-hosting runtime.
Additional `*.instructions.md` files under `.github/instructions/` apply only when a
changed file matches their `applyTo` globs; discover existing files rather than assuming
SDK-specific instruction files are present.

## What NOT to comment on

- Do **not** comment on style, formatting, naming, or documentation wording.
- Do **not** comment on "best practices" that do not prevent an actual problem.
- Do **not** raise a finding you cannot tie to an exact `path:line` with a concrete failure.
- If uncertain, stay silent. Only report high-confidence findings.

## Anti-false-positive checks (perform ALL before raising any finding)

1. **Check for guards before flagging complexity.** Look for size limits, depth caps, early
   returns, validation, `CancellationToken` checks, and bounded collections. Drop the finding
   if an existing guard actually prevents the claimed failure.
2. **Trace the command and its callers.** Check parsing, validation, operation dispatch,
   injected ports, and the command's effective feature controls. Distinguish startup/help
   paths from per-item work in synchronization, packaging, and evaluations.
3. **Check existing resilience before proposing retries.** Inspect adapters, service
   registration, and HTTP policies. Do not recommend replaying a mutating request or
   silently falling back to a different account, cloud, environment, agent, or deployment.
4. **Distinguish sequential from nested parallelism.** Two awaited async calls in one method are
   not nested if the first completes before the second starts.
5. **Estimate proportional impact.** Do not flag small startup-only work as a performance
   problem. Give a defensible size, call-count, or cost argument; do not invent timings.
6. **Check the actual build configuration.** Use `global.json`, `src/Directory.Build.props`,
   and the affected `.csproj`. Projects currently target .NET 10 with nullable reference
   types enabled; do not import `netstandard2.0` or SDK multi-target assumptions.
7. **Keep findings change-specific.** Review the requested snapshot, trace existing callers,
   and identify a concrete regression or failure introduced or exposed by the change.
   Do not report unrelated pre-existing problems.

## Review lenses

- **Correctness and CLI contracts:** reachable logic errors, unsafe casts, races with
  evidence of shared state, and incorrect async/cancellation behavior. Preserve command
  discovery, help, exit codes, JSON shapes, stderr diagnostics, confirmation, and explicit
  account/cloud/environment/agent/deployment targeting. `--json` alone does not mean
  non-interactive; check the command's actual behavior.
- **Architecture:** Core must stay independent of the CLI host, MSAL, and the Copilot Studio
  Sync library, without new runtime package dependencies. Keep MSAL in Auth, Sync adapters
  in CopilotStudio, and process-environment resolution at the host composition root.
  Preserve injected ports and the isolated Core embedding tests.
- **Authentication and security:** `auth login` owns interactive sign-in; status/account
  switching must not acquire tokens or open identity UI. Check token/cache handling,
  input validation, injection, and secret-bearing output. Preserve redaction and telemetry
  opt-out/identifier handling; telemetry-control commands must not measure themselves.
- **Serialization and state:** use existing `System.Text.Json` options and converters.
  Check JSON contracts, workspace/deployment state, conformance fixtures, and byte or
  normalization guarantees. Do not introduce another serialization policy.
- **XML documentation references:** For `<see>` and `<seealso>` references added or modified by the
  pull request, verify that each `cref` uses a fully qualified symbol name and resolves to the
  current type, member, and signature. Do not report unchanged references elsewhere, including
  unchanged references in a changed file.
- **Performance and resilience:** unbounded work at realistic workspace sizes, repeated
  network calls, missing cancellation propagation, swallowed errors, success-shaped
  fallbacks, or unsafe retries. Trace existing guards, adapters, and error translation first.
- **Feature and test safety:** preserve shipped defaults in
  `src/Microsoft.Agents.Hosting.Cli/featureflags.json`. Offline tests must not sign in,
  depend on cached credentials, invoke live services, or spend credits. E2E is a live
  executable, not a `dotnet test` project. Do not run live E2E, provisioning, deployments,
  playground sessions, or evaluations as part of a review.
- **Build and packaging:** package versions belong in `src/Directory.Packages.props`,
  but inspect existing exceptions before flagging a version (the host telemetry package
  uses `VersionOverride`). Preserve warnings-as-errors, `src/version.json` discovery,
  Windows/Linux behavior, and both GitHub and OneBranch build inputs. Only the CLI is
  packable; it is a framework-dependent tool requiring .NET and ASP.NET Core runtimes.
  Reflection-based command discovery must survive packaging; do not enable trimming
  without preserving and verifying it.

## Severity

Classify every finding as **Critical**, **High**, or **Medium** based on demonstrated
impact: Critical for severe credential/data compromise or destructive cross-target
behavior, High for major command failure or data loss, and Medium for a concrete,
limited functional regression. Do not pad reviews with lower-impact nits or compliments.
