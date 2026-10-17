# Contributing

#### Note for Microsoft internal developers: 
- Internal Microsoft Developers should join the Core identity group **TBD**

This repository builds the Microsoft Agent Hosting CLI (`msagent`), a .NET tool
for local Copilot Studio and agent-hosting development workflows. See the
[README](README.md) for product usage and [the documentation index](docs/README.md)
for user guides.

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
`src/Microsoft.Agents.Hosting.Cli/Tool_Readme.md`.

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

## Pull requests

- Add or update offline tests for behavior changes, and describe the relevant
  verification in the pull request. Documentation-only changes do not require
  running the .NET test suites.
- Preserve command help, exit codes, JSON output contracts, cancellation,
  confirmation behavior, and explicit account/environment/deployment targeting.
- Update related user guides, command reference, and help when changing commands.
  Keep the packaged `Tool_Readme.md` limited to default-on features; developer
  feature-control documentation belongs in the adjacent `README.md`.
- Keep Core independent of MSAL, the CLI host, and the Copilot Studio Sync
  library. Follow the project boundaries in
  [.github/copilot-instructions.md](.github/copilot-instructions.md).
- Do not commit local agent plans or worktrees. CI rejects tracked files under
  `docs/superpowers/`, `.superpowers/`, and `.worktrees/`.

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
