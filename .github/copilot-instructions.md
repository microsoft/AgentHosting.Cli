# Copilot Instructions for AgentHosting.Cli

## Project Overview

This repository builds **Microsoft.Agents.Hosting.Cli**, a framework-dependent
.NET tool whose installed command is **`msagent`**. It connects local agent
projects and source-control workflows with Microsoft Copilot Studio and the
Microsoft Agent Hosting platform.

The default experience supports authentication, environment selection, Copilot
Studio agent and workflow development, deployment slots, connector connections,
and evaluations. Additional code-agent hosting, Azure Bot Service, identity, and
live-testing capabilities are implemented behind feature controls.

This is a preview CLI, not the Microsoft 365 Agents SDK, an ASP.NET Core agent
application, or the agent-hosting runtime. Do not import SDK-specific project
layouts, agent patterns, sample paths, or release assumptions into this repo.

## Build and Test

Run commands from the repository root. Use the .NET SDK selected by `global.json`;
projects currently target `net10.0` through `src/Directory.Build.props`.
The examples below use PowerShell paths; use the corresponding native paths on
Linux and macOS.

```powershell
# Restore and build the complete CLI solution through the root traversal project
dotnet restore Microsoft.Agents.Hosting.Cli.proj
dotnet build Microsoft.Agents.Hosting.Cli.proj --no-restore -c Debug

# Alternatively, build the solution directly
dotnet build src\Microsoft.Agents.Hosting.Cli.slnx -c Debug

# Run the main offline suite
dotnet test src\Microsoft.Agents.Hosting.Cli.Tests\Microsoft.Agents.Hosting.Cli.Tests.csproj --no-build -c Debug

# Run the isolated Core embeddability suite
dotnet test src\Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests\Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests.csproj --no-build -c Debug

# Run selected tests (after building the matching configuration)
dotnet test src\Microsoft.Agents.Hosting.Cli.Tests\Microsoft.Agents.Hosting.Cli.Tests.csproj --no-build -c Debug --filter "FullyQualifiedName~Namespace.ClassName"

# Package the CLI after building the same configuration
dotnet pack src\Microsoft.Agents.Hosting.Cli.slnx --no-build --no-restore -c Debug

# Run the local command host without installing the tool
dotnet run --project src\Microsoft.Agents.Hosting.Cli --no-build -c Debug -- --help
```

Run the smallest relevant offline test selection first. A complete offline pass
includes both test projects above. `Microsoft.Agents.Hosting.Cli.E2E` is a
separate executable, not a test suite that `dotnet test` executes.

## Architecture and Dependency Boundaries

All projects are directly under `src/`; there is no `src/libraries/` or
`src/tests/` layout.

| Project | Responsibility |
|---|---|
| `Microsoft.Agents.Hosting.Cli` | Executable, composition root (`Program.cs`), command-line hosting, rendering adapters, feature controls, and telemetry |
| `Microsoft.Agents.Hosting.Cli.Commands` | Command groups, reflected command definitions, options, validation, and dispatch into Core operations |
| `Microsoft.Agents.Hosting.Cli.Core` | Embeddable operations, contracts, ports, project/deployment state, and service adapters that do not require MSAL or the Copilot Studio Sync library |
| `Microsoft.Agents.Hosting.Cli.Auth` | MSAL, broker integration, token caching, and authentication dependency registration |
| `Microsoft.Agents.Hosting.Cli.CopilotStudio` | Copilot Studio Sync, Dataverse, workspace synchronization, templates, connections, and evaluation integrations |
| `Microsoft.Agents.Hosting.Cli.Tests` | Offline unit, contract, command-host, and adapter tests |
| `Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests` | Isolated proof that Core can be hosted without the CLI shell or authentication adapter |
| `Microsoft.Agents.Hosting.Cli.E2E` | Explicitly invoked live scenario harness |
| `AgentHostCli.Conformance` | Contract fixtures and a TypeScript oracle, not another .NET application |

- Keep Core independent of the command host, MSAL, and the Sync library. Do not
  add runtime package dependencies to Core; its inherited build-time versioning
  reference is separate from its runtime dependency boundary.
- Keep MSAL usage in `Microsoft.Agents.Hosting.Cli.Auth`. Wire concrete services
  in the host rather than moving identity-library types into Commands or Core.
- Keep Sync-specific workspace behavior behind Core ports and implement it in
  `Microsoft.Agents.Hosting.Cli.CopilotStudio`. Do not add YamlDotNet; existing
  dependency-boundary tests explicitly prohibit it.
- Resolve process environment settings at the host composition root. Pass
  resolved values or the existing `Func<string, string?> readEnvironment` seam
  into helpers; Core must not read process environment variables.
- Preserve injected clocks, transports, stores, and other ports so offline tests
  and embedded hosts do not need real sign-in, network services, or CLI globals.

## Commands, Features, and Output

- Follow the existing command attributes, group structure, and reflection model
  under `Microsoft.Agents.Hosting.Cli.Commands/Model`, `Groups`, and `Commands`.
  Keep parsing and presentation separate from operation logic.
- Treat `src/Microsoft.Agents.Hosting.Cli/featureflags.json` as the source of
  shipped feature defaults. Currently evaluations are on; AMS, Azure Bot
  Service, identity, and live testing are off. Do not enable a feature merely to
  make a test or documentation example work.
- Preserve command help, exit codes, machine-readable `--json` contracts,
  diagnostics on stderr, cancellation, and the command-specific interaction and
  confirmation behavior. `--json` alone does not mean non-interactive.
- `auth login` owns interactive identity sign-in; status and account switching
  must not acquire tokens or open identity UI.
- Preserve explicit account, cloud/ring, environment, agent, and deployment
  targeting. Do not silently fall back to another destination or replay a
  potentially mutating request.
- Reuse existing error translation and response-redaction helpers. Never write
  access tokens, credentials, or unredacted secret-bearing responses to output,
  logs, fixtures, or telemetry.
- Preserve telemetry opt-out behavior and identifier handling when changing
  command execution. Telemetry-control commands must not measure themselves.

## Build and Code Conventions

- Package versions are managed in `src/Directory.Packages.props`. Check existing
  exceptions before assuming every reference uses the central version; the CLI
  telemetry package currently has a `VersionOverride`.
- Shared framework, compiler, analyzer, and warning settings are in
  `src/Directory.Build.props`; warnings are treated as errors. Nullable reference
  types and implicit usings are enabled in the projects.
- Nerdbank.GitVersioning uses `src/version.json`; local tool versions live in
  `.config/dotnet-tools.json`. Release branch naming is configured in
  `src/version.json`, not inherited from the SDK.
- Shared package metadata is in `src/NugetPackage.Shared.props`, and assembly
  metadata is in `src/AssemblyInfo.Shared.props`. Only the CLI executable is
  currently packable as the `msagent` .NET tool.
- The command tree is reflection-discovered. Do not enable trimming without
  explicitly preserving and verifying command discovery.
- Use existing `System.Text.Json` options, converters, and registration helpers;
  do not introduce Newtonsoft.Json or a second serialization policy.
- Preserve asynchronous cancellation and use synchronization primitives or
  existing test hooks instead of timing-based sleeps in tests.
- Use fully qualified symbols in XML `<see cref="...">` and
  `<seealso cref="...">` references. Verify that references resolve; use fully
  qualified `<c>` text when assembly or framework boundaries prevent a `cref`.

## Testing and Live-Service Safety

- Offline tests use xUnit and injected fakes. They must not prompt for sign-in,
  depend on cached user credentials, invoke live cloud services, or spend credits.
- Preserve the separate Core-only embedding test project; adding references to
  the CLI host or Auth would defeat its isolation guarantee.
- Keep conformance fixtures and their byte/normalization contracts aligned with
  `src/AgentHostCli.Conformance/README.md`. The pinned TypeScript implementation
  is a read-only comparison input.
- Never run the live E2E harness, `test ask`, playground sessions, provisioning,
  deployments, or evaluations against a real tenant without explicit approval.
  These operations can create/delete resources, perform agent actions, or incur
  charges. Follow `src/Microsoft.Agents.Hosting.Cli.E2E/README.md` for setup.
- Never commit local E2E configuration, token caches, credentials, or generated
  workspace state. Keep local agent plans and worktrees out of tracked files;
  `.github/scripts/verify-no-local-agent-artifacts.ps1` rejects tracked files
  under `docs/superpowers/`, `.superpowers/`, and `.worktrees/`.

## Documentation and Automation

- `README.md` and `docs/` explain user workflows;
  `src/Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md` is the command reference.
- `src/Microsoft.Agents.Hosting.Cli/Tool_Readme.md` is the packaged public guide.
  Document only default-on features there, and do not add a feature-controls
  section or link. Internal feature-control guidance belongs in the adjacent
  developer `README.md`.
- Update relevant help, command reference, and user/developer documentation when
  changing command behavior. Some docs retain demonstrator or older preview
  wording; verify examples against current command definitions and feature flags.
- GitHub Windows/Linux CI and CodeQL workflows are in `.github/workflows/`.
  OneBranch PR/nightly pipelines are in `.azdo/` and consume an external
  build/sign/package template. Changes to build inputs must account for both.
- Local/GitHub restore uses root `nuget.config`; OneBranch uses
  `.azdo/nuget.config`. Do not assume their feeds or authentication are
  interchangeable, and never add credentials to either file.
