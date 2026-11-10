# Microsoft Agent Hosting CLI developer reference

This repository builds `Microsoft.Agents.Hosting.Cli`, a framework-dependent
.NET tool installed as `msagent`. The default experience supports authentication,
environment selection, Copilot Studio agents and workflows, deployment slots,
connections, and evaluations. Code-agent hosting, Azure Bot Service, identity,
and live testing are implemented behind feature controls.

**For contributors and coding agents changing the CLI:** follow the root
[CONTRIBUTING.md](../../CONTRIBUTING.md), the authoritative guide for architecture
boundaries, build/test commands, live-service safety, and documentation maintenance.
This file is a technical reference, not a second contributor policy.

**For users and coding agents using the CLI:** start with the packaged
[Readme.md](Readme.md), the [user guides](../../docs/README.md), and the
[command reference](../Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md).
The [coding-agent usage guide](../../docs/agent.md) is for authoring agent projects,
not changing the CLI implementation.

## Developer documents

| Topic | Reference |
|---|---|
| Architecture, contribution rules, build, and offline tests | [CONTRIBUTING.md](../../CONTRIBUTING.md) |
| Telemetry schema, identity/privacy, routing, and counting | [Telemetry details](../../docs/developer/Telemetry.md) |
| Scoped TypeScript comparison and fixture contracts | [Comparison](../../docs/developer/Comparison.md) |
| Live testing, Playground behavior, and validation records | [Manual testing and records](../../docs/developer/ManualTesting.md) |
| Live executable E2E harness | [E2E setup](../Microsoft.Agents.Hosting.Cli.E2E/README.md) |
| Command options and error recovery | [Command reference](../Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md) and [error codes](../Microsoft.Agents.Hosting.Cli.Docs/ErrorCodes.md) |

## Configuration (host environment)

The host resolves these settings at its composition root. Core receives resolved
values or injected readers; it does not read process environment variables.

| Variable | Purpose |
|---|---|
| `AH_CLI_CLIENT_ID` | AgentHost public-client application ID. Sign-in uses the platform broker reply URL: Windows `ms-appx-web://microsoft.aad.brokerplugin/{clientId}`, macOS `msauth.com.msauth.unsignedapp://auth`, Linux `https://login.microsoftonline.com/common/oauth2/nativeclient`. |
| `AH_CLI_GRAPH_CLIENT_ID` | Consent-capable public client for identity provisioning. Defaults to Microsoft Graph Command Line Tools; it is separate from the CLI first-party client. |
| `AH_CLI_GRAPH_REDIRECT_URI` | Registered HTTP loopback redirect for a Graph client override. |
| `AH_CLI_ARM_CLIENT_ID` | Consent-capable public client for managed-identity provisioning. Defaults to the Azure CLI public client. |
| `AH_CLI_ARM_REDIRECT_URI` | Registered HTTP loopback redirect for an ARM client override. |
| `AH_CLI_CONFIG_DIR` | Override the profile/config directory, normally under `LocalApplicationData`. |
| `AH_CLI_SKIP_UPDATE_CHECK` | `true` (case-insensitive) or `1` disables version checks and notices. |
| `AH_CLI_ALLOW_UNPROTECTED_CACHE` | Allow an unprotected token cache when protected storage is unavailable. |
| `AH_CLI_FEATURES` | Semicolon-separated, case-insensitive, enable-only feature overrides; see [feature controls](#feature-controls). |
| `AH_CLI_TELEMETRY_OPTOUT` | `1`/`true` opts out, `0`/`false` requests collection; unset defers to the remembered preference and host policy. |
| `AH_CLI_AMS_ENDPOINT` | AMS control-plane endpoint used by deployment. Absolute HTTPS; not settable per command. |
| `AH_CLI_AMSDP_ENDPOINT` | AMS data-plane endpoint used by upload and as a direct Dev-island override for creation. Absolute HTTPS. |
| `NO_COLOR` | Disable colored output, like `--no-color`. |
| `CI` | Suppress progress and version checks when nonempty and not `false` or `0`. |

The CLI supports TLS 1.3 and TLS 1.2. Production HTTP clients use the shared
transport defaults; telemetry's SDK transport has its own integration boundary
described in the [telemetry reference](../../docs/developer/Telemetry.md#delivery-and-diagnostics).

### Automatic version notices

The executable checks listed `Microsoft.Agents.Hosting.Cli` versions on nuget.org
before an eligible leaf command's first output or prompt, or before displaying
help (including bare `msagent`), writing notices to spinner-aware stderr.
It compares NuGet versions with
`ThisAssembly.AssemblyInformationalVersion` after removing `+` metadata.
It shows the highest newer stable version and a newer prerelease only when it
also exceeds the highest stable version. Suggested commands use `--global`,
with `--prerelease` for a preview; local and tool-path installations must use
their own scope. No update is executed automatically.

`Hosting/NuGetUpdateClient.cs` discovers an approved public search endpoint from
the service index and requests prereleases and SemVer 2 versions anonymously.
Discovery and body reads share a three-second deadline. There are no retries,
redirects, credential providers, custom publisher feeds, or identity calls.
Expected failures stay silent, including under `--debug`; caller cancellation
propagates.

`Hosting/UpdateCheckService.cs` caches metadata at
`<config directory>/updates/version-check.json` for 24 hours and backs failed
checks off for one hour. The cache is independent of the installed version.
Invalid, future, oversized, or expired data is refreshed rather than used for
stale notices. Writes use unique temporary files and atomic replacement without
a process lock; cache I/O is best effort.

Checks are skipped for version output, JSON, parser-rejected invocations,
navigation groups without help, CI, redirected stdout/stderr, and explicit
opt-out. Playground child processes
return before composition. `--non-interactive` alone does not suppress checks.
Direct command-host construction remains offline unless the internal update
seam is explicitly supplied. Offline tests inject HTTP handlers, temporary
directories, and a manual clock.

## Authentication profiles, clouds and environments

Authentication profiles capture the selected identity and registered cloud
coordinates. Use `auth create` to authenticate and create or replace a profile,
and `auth select` to activate a stored profile without authenticating.
`auth list`, `auth who`, and `auth status` inspect metadata without renewing
credentials. `auth login` and `auth logout` are legacy commands gated by
`LegacyAuth`, which is off by default. See the [authentication command
reference](../Microsoft.Agents.Hosting.Cli.Docs/Command_Auth.md) for credential,
storage, and lifecycle details.

Authentication has one `--cloud` selector: `Public`, `Preprod`, `Test`, `UsGov`,
`UsGovHigh`, `UsGovDod`, `China`, `UsNat`, or `UsSec`. Profile creation defaults
to `Public`; explicitly select the intended cloud when creating a sovereign or
nonproduction profile. A registered cloud does not guarantee every downstream
service route is available.

Environment targeting follows explicit command environment, compatible owned
override, profile default, then a missing-target error. `env select` updates the
selected profile's default; select the intended profile first when changing a
different profile. Foreign or incompatible overrides are not silently adopted.
See the [environment command reference](../Microsoft.Agents.Hosting.Cli.Docs/Command_Env.md).

## Feature controls

`featureflags.json` is copied beside the CLI assembly and packed with the tool.
Only `on` enables a key, case-insensitively; missing keys and other values are
disabled. A missing or malformed file stops startup. Environment overrides do
not replace the required file; a missing-file diagnostic directs the user to
reinstall the tool.

`AH_CLI_FEATURES` is an enable-only override: listed features turn on, while
unlisted features retain their file defaults. It cannot turn a shipped-on
feature off. Names are case-insensitive, semicolon-separated, and trimmed;
unknown entries are ignored. Feature controls are rollout controls, not
authorization or security boundaries.

The source of truth is [featureflags.json](featureflags.json):

| Feature | Default | Controlled surface |
|---|---|---|
| `AmsFeatures` | `off` | CodeAgent creation, AMS package upload, and package-revision deployment. |
| `AzureBotServiceFeatures` | `off` | `abs` and resolved CodeAgent deployment routes backed by Azure Bot Service. |
| `IdentityFeatures` | `off` | The user-facing `identity` command group. |
| `LegacyAuth` | `off` | Only `auth login` and `auth logout`; profile commands and `auth status` remain available. |
| `LiveTestFeatures` | `off` | `test ask` and `test playground`. |
| `EvaluationFeatures` | `on` | `agent eval` and its `testset` subgroup. |

For an explicitly approved developer session, enable only the needed features:

```powershell
$env:AH_CLI_FEATURES = "AmsFeatures;AzureBotServiceFeatures"
```

Multiple `[RequiredFeatureFlag]` attributes are ANDed. A disabled parent removes
all descendants before help, parsing, and dispatch. Feature-controlled options
are removed too. Mixed commands enforce the resolved route immediately before
handler dispatch, so a stored CodeAgent record cannot bypass a disabled route.

The shared MCS, authentication, environment, and local deployment surfaces remain
available. A bare `agent create` retains the CodeAgent route and reports AMS as
disabled; it is not silently rerouted. Use `--agent-type MCSAgent` for the
available MCS route. Identity services remain registered even when their
user-facing group is disabled because other operations can depend on them.

### Adding command or group controls

1. Add the feature to `Microsoft.Agents.Hosting.Cli.Commands/Features/FeatureName.cs`.
2. Set its shipped default in `featureflags.json`.
3. Attach `[RequiredFeatureFlag(FeatureName.YourFeature)]` to the command or
   command-group class.
4. For a route-only option, set `RequiredFeatures` on its `CommandSymbol`.
   Call `FeatureGate.RequireEnabled` after route resolution and before dispatch.

`CommandReflector` recursively filters cached metadata before System.CommandLine
materializes help, parser symbols, and aliases. An enabled leaf cannot bypass a
disabled ancestor. Do not maintain a second central command-path list.

Public documentation and maintenance requirements are defined in
[CONTRIBUTING.md](../../CONTRIBUTING.md#documentation-maintenance).

## AMS package upload and deployment

These routes require `AmsFeatures`. Package upload and deployment are separate
effects; neither is a local CodeAgent build command.

```powershell
msagent agent package upload --agent-id <agent-id> --package <agent.zip> --environment-id <home-environment-id>
```

Upload hashes, sizes, and uploads the exact supplied bytes without opening the
archive. Relative paths are resolved against the process working directory.
The same read-only stream is hashed, rewound, and uploaded. Operator-selected
files may be outside the repository; path normalization is not a symlink,
junction, or sandbox boundary.

The response identifies the durable Package Upload Operation and, when confirmed,
the immutable Package Revision. A non-success response with a validated durable
`Accepted` or `Failed` operation exits nonzero; JSON preserves
`partialResult.packageOperationId` and `partialResult.operationStatus`. No
revision is confirmed by that failure. Upload does not poll, retry, or create
an AMS deployment.

```powershell
msagent agent deploy --agent-id <agent-id> --package-revision <revision-id> --environment-id <home-environment-id> --target-environment-id <target-environment-id> --deployment-name <name> --deployment-type <dev|test|prod> --entra-id <application-id> --entra-id-type <app-reg|blueprint|default>
```

`--environment-id` defaults to the active environment. The target environment
is mandatory even for same-home deployment. AMS-specific inputs cannot be mixed
with MCS-only inputs such as `--project`, `--agent-name`, `--deployment-id`, or
`--publish`. `--agent-id` and `--deployment-name` are shared selectors.
MCS deploy keeps its own workspace, registry, and backend.

Deploy reads the CodeAgent through AMS data plane and sends identifiers and
explicit desired state through AMS control plane. It does not upload bytes.
An acknowledged target-routed projection is not proof of runtime readiness,
recovery, or successful cross-island operation. Do not automatically replay a
mutating request after ambiguous failure.

For current identity, deployment, channel, and OAuth command options, use the
[command reference](../Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md) and command
help rather than historical examples. Live use requires the approval described
in [CONTRIBUTING.md](../../CONTRIBUTING.md#live-service-testing).
