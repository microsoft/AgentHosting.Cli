# Contributing

#### Note for Microsoft internal developers:
- Internal Microsoft Developers should join the Core identity group [Agents SDK Contrib](https://coreidentity.microsoft.com/manage/Entitlement/entitlement/agentssdkint-upyj)

This repository builds the Microsoft Agent Hosting CLI (`msagent`), a .NET tool
for local Copilot Studio and agent-hosting development workflows. See the
[README](README.md) for product usage and [the documentation index](docs/README.md)
for user guides.

This is the authoritative contributor guide for humans and coding agents working
on this repository. Agent-specific instructions and developer references must
remain consistent with it. This project is a preview CLI, not the Microsoft 365
Agents SDK or the agent-hosting runtime.

## Before you start

For larger features or behavior changes, discuss the proposal with the maintainers
in an issue before starting implementation. Keep pull requests focused and avoid
unrelated refactoring.

Microsoft contributors who need internal build or service access should contact
the repository maintainers for the appropriate access process. Internal access
and a cloud account are not required for the normal offline build and tests.

## Development prerequisites

- Git, with the repository history available for Nerdbank.GitVersioning.
- The .NET SDK selected by [global.json](global.json). Projects currently target
  .NET 10; install the SDK, not just the runtime.
- The .NET 10 and ASP.NET Core 10 shared runtimes to run the CLI. Both are
  included with the .NET 10 SDK.

Run the following commands from the repository root. These examples use
PowerShell paths; use the corresponding native paths on Linux and macOS.

## Restore and build

```powershell
dotnet restore Microsoft.Agents.Hosting.Cli.proj
dotnet build Microsoft.Agents.Hosting.Cli.proj --no-restore -c Debug
```

The root project builds `src/Microsoft.Agents.Hosting.Cli.slnx`. Shared build
settings are in `src/Directory.Build.props`, package versions are in
`src/Directory.Packages.props`, and versioning is configured in `src/version.json`.
Warnings are treated as errors.

Restore uses the root `nuget.config`. Do not copy internal feed credentials into
it; OneBranch has its own configuration in `.azdo/nuget.config`.

If you need the repository's local versioning or coverage-report tools, restore
their pinned versions with `dotnet tool restore`.

## Run offline tests

After building, run both offline test projects:

```powershell
dotnet test src\Microsoft.Agents.Hosting.Cli.Tests\Microsoft.Agents.Hosting.Cli.Tests.csproj --no-build --no-restore -c Debug
dotnet test src\Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests\Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests.csproj --no-build --no-restore -c Debug
```

During development, run the smallest relevant selection first:

```powershell
dotnet test src\Microsoft.Agents.Hosting.Cli.Tests\Microsoft.Agents.Hosting.Cli.Tests.csproj --no-build --no-restore -c Debug --filter "FullyQualifiedName~SolutionBoundaryTests"
```

Rebuild after changing code, and keep the configuration consistent between build
and test commands. Offline tests must not require sign-in, cached credentials,
cloud resources, or paid service access. The separate Core embedding project
must remain independent of the CLI host and authentication adapter.

## Run and package the CLI

Display local command help without installing the tool or signing in:

```powershell
dotnet run --project src\Microsoft.Agents.Hosting.Cli --no-build --no-restore -c Debug -- --help
```

Create the .NET tool package after building the same configuration:

```powershell
dotnet pack src\Microsoft.Agents.Hosting.Cli.slnx --no-build --no-restore -c Debug
```

Only the CLI executable is packable. Package metadata is shared through
`src/NugetPackage.Shared.props`; the packaged user guide is
`src/Microsoft.Agents.Hosting.Cli/Readme.md`.

The project declares runtime-specific tool packages as well as the top-level
package. Keep the packages from a build together when using their directory as
a local source; do not assume there is exactly one `.nupkg`. Use an isolated
`--tool-path` when testing a package rather than replacing a global installation.
For a PR build, use artifacts from a successful run of
`microsoft/AgentHosting.Cli` matching the requested revision, and read the package
ID and version from its manifest. Artifact availability alone is not proof of a
successful build.

## Architecture and dependency boundaries

All projects are directly under `src/`. The solution is
`src/Microsoft.Agents.Hosting.Cli.slnx`; this repository does not contain the
agent-hosting runtime projects.

| Project | Responsibility |
|---|---|
| `Microsoft.Agents.Hosting.Cli` | Executable, composition root, command host, rendering, feature controls, and telemetry. |
| `Microsoft.Agents.Hosting.Cli.Commands` | Command metadata, options, validation, and dispatch into Core operations. |
| `Microsoft.Agents.Hosting.Cli.Core` | Embeddable operations, contracts, ports, project/deployment state, and service adapters. |
| `Microsoft.Agents.Hosting.Cli.Auth` | MSAL, broker integration, token caching, and authentication registration. |
| `Microsoft.Agents.Hosting.Cli.CopilotStudio` | Sync, Dataverse, workspace, connection, template, and evaluation adapters. |
| `Microsoft.Agents.Hosting.Cli.Tests` | Offline unit, contract, command-host, and adapter tests. |
| `Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests` | Isolated Core-only hosting tests. |
| `Microsoft.Agents.Hosting.Cli.E2E` | Explicitly invoked live scenario harness. |
| `AgentHostCli.Conformance` | Language-neutral contract fixtures and a TypeScript oracle, not a .NET application. |

- Keep Core independent of the CLI host, MSAL, and the Copilot Studio Sync
  library. Do not add runtime package dependencies to Core; inherited build-time
  versioning is separate from that boundary.
- Keep MSAL in Auth and Sync-specific behavior behind Core ports implemented in
  CopilotStudio. Do not introduce YamlDotNet.
- Resolve process-environment settings at the host composition root. Pass
  resolved values or the existing `Func<string, string?> readEnvironment` seam
  into helpers; Core must not read process environment variables.
- Preserve injected clocks, transports, stores, cancellation, and other ports so
  offline tests and embedded hosts do not require CLI globals or real services.

## Command and code changes

Follow the reflected command model under
`src/Microsoft.Agents.Hosting.Cli.Commands/Model`, `Groups`, and `Commands`.
Keep command parsing and presentation separate from operation logic.

When adding a command:

1. Define its command class and attribute under `Commands/<Group>/`. Reuse
   `OptionCatalog` entries and add any new option with a unique alias.
2. Define its request, result, and handler in Core. Follow the existing
   `ICommandHandler<TRequest, TResult>` and `ITextGridRenderable` contracts.
3. Register the handler in the host composition root and any affected test
   composition. `CompositeHandlerProvider` can resolve registered handlers
   without adding them to the operations facade.
4. Register result types in `Rendering/CliJsonContext.cs` and update command
   surface/usage assertions, focused tests, and relevant documentation.

Preserve help, exit codes, JSON shapes, stderr diagnostics, cancellation, and
command-specific confirmation behavior. `--json` alone does not mean
non-interactive. `auth login` owns interactive identity sign-in; status and
account switching must not acquire tokens or open identity UI.

Preserve explicit account, ring, environment, agent, and deployment targeting.
Never silently fall back to another destination or automatically replay a
potentially mutating request. Reuse error translation and response redaction;
never write tokens, credentials, or unredacted secret-bearing responses to
output, logs, fixtures, or telemetry. Preserve telemetry opt-out and identifier
handling; telemetry-control commands must not measure themselves.

Use the existing `System.Text.Json` options, converters, and registration helpers,
not a second serialization policy. Keep package versions in
`src/Directory.Packages.props`, accounting for existing `VersionOverride`
exceptions. Preserve nullable analysis, warnings-as-errors, and
reflection-based command discovery; do not enable trimming without preserving
and verifying that discovery. Use fully qualified, resolvable symbols in XML
`<see cref="...">` and `<seealso cref="...">` references.

## Feature controls

The source of shipped defaults is
[`featureflags.json`](src/Microsoft.Agents.Hosting.Cli/featureflags.json).
Evaluations and live testing ship on; AMS, Azure Bot Service, and identity ship off.
Do not enable a feature merely to make a documentation example or test work.
Feature controls are rollout controls, not authorization boundaries.

For loading rules, overrides, and metadata-based command/route controls, see the
[developer feature reference](src/Microsoft.Agents.Hosting.Cli/agent.md#feature-controls).

## Live-service testing

`Microsoft.Agents.Hosting.Cli.E2E` is an explicitly invoked console harness, not
a test project executed by `dotnet test`. It can create and delete real resources.
Live agent queries, deployments, provisioning, and evaluations can also perform
real actions or incur charges.

Use only approved, disposable environments and follow the
[E2E setup and safety guidance](src/Microsoft.Agents.Hosting.Cli.E2E/README.md).
Never commit local E2E configuration, credentials, token caches, or tenant-specific
workspace state. Do not enable shipped-off feature flags as part of an unrelated
change.

Use xUnit and injected fakes for offline tests, and use synchronization primitives
or existing hooks rather than timing-based sleeps. Keep the conformance fixtures
and normalization contracts aligned with their
[comparison reference](docs/developer/Comparison.md). The pinned TypeScript
implementation is read-only comparison input, not a dependency of the .NET tests.

Live E2E, `test ask`, playground sessions, provisioning, deployments, and
evaluations require explicit approval for the intended tenant and effects.
See [manual testing and records](docs/developer/ManualTesting.md) for checks and
recording requirements. Never infer successful live validation from offline
tests, a feature override, or a child process starting.

## Documentation maintenance

- Update relevant help, command reference, and user/developer documentation when
  changing command behavior.
- Keep the packaged
  [`Readme.md`](src/Microsoft.Agents.Hosting.Cli/Readme.md) limited to features
  that ship on in `featureflags.json`. Do not add a feature-controls section or
  link there. Add public documentation when a feature becomes default-on and
  remove it when the feature becomes default-off.
- Keep internal feature-control and host-configuration details in the
  [developer reference](src/Microsoft.Agents.Hosting.Cli/agent.md), not the
  packaged public guide.
- Any addition or change to an error code, its meaning, triggering conditions,
  or recovery guidance must update the
  [error code directory](src/Microsoft.Agents.Hosting.Cli.Docs/ErrorCodes.md)
  and affected
  [per-code troubleshooting pages](src/Microsoft.Agents.Hosting.Cli.Docs/ErrorCodes/)
  in the same change.
- Maintain the [telemetry details](docs/developer/Telemetry.md),
  [comparison contracts](docs/developer/Comparison.md), and
  [manual testing records](docs/developer/ManualTesting.md) when changing their
  corresponding behavior. Record evidence with its revision and scope, and
  remove obsolete history rather than presenting it as current guidance.

## Pull requests

- Add or update offline tests for behavior changes, and describe the relevant
  verification in the pull request. Documentation-only changes do not require
  running the .NET test suites.
- Preserve command help, exit codes, JSON output contracts, cancellation,
  confirmation behavior, and explicit account/environment/deployment targeting.
- Do not commit local agent plans or worktrees. CI rejects tracked files under
  `docs/superpowers/`, `.superpowers/`, and `.worktrees/`.

GitHub CI and CodeQL workflows live under `.github/workflows/`; OneBranch
PR/nightly pipelines live under `.azdo/` and consume an external
build/sign/package template. Account for both when changing build inputs.

Pull requests are labeled by changed paths using
[.github/labeler.yml](.github/labeler.yml). Keep those mappings aligned with new
projects and build inputs. A change can receive both an area label and a docs,
build, or package label; that overlap is intentional.

## Contributor License Agreement

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
