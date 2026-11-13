# Authentication platform validation (developer guide)

This guide describes acceptance tooling for `msagent`, not the Agents SDK or
the agent-hosting runtime. A configured job, successful cross-RID build, or
packaged library is **not** evidence of native execution or successful sign-in.
The existing [authentication command reference](../src/Microsoft.Agents.Hosting.Cli.Docs/Command_Auth.md)
remains the source for commands, clouds, targeting, JSON, and error contracts.

## Three separate tiers

| Tier | Allowed work | Entry point |
| --- | --- | --- |
| Offline | Injected credential/native/transport fakes; profile, targeting, cancellation, JSON, exit and privacy contracts. No credentials, UI, network or credits. | Main xUnit project **and** Core embedding project explicitly. |
| Dedicated native-nonsecret | Prepared test-owned store/session; generated values and PFX keys; native API/ABI and installed library checks. No sign-in, OS account enumeration, token acquisition, browser UI, services or credits. | Separate nonpacked `Microsoft.Agents.Hosting.Cli.AuthenticationChecks` console, explicit `native --dedicated --prepared`. |
| Live | Real browser/broker/device/workload authentication, conditional access, registered redirects, service/cloud acceptance and tenant actions. | Separately authorized acceptance, never these CI jobs. E2E is a live executable, not an offline `dotnet test` suite. |

The same console's `package` mode is offline-safe: it checks package manifests
and an actual local installation with empty test-owned configuration. It does
not open native storage or load the broker runtime. `platform` only validates
the actual .NET process/OS architecture. Dedicated Linux `control` mode prepares
the owned daemon directory; `control --prepared` verifies its published selection
without changing permissions. Both require `--dedicated` and a matching native
Linux process, and neither is part of the ordinary offline suites.
These are test tooling commands, not
new `msagent` commands. No disabled native fixture silently passes or skips:
missing opt-in/preparation, unsupported architecture, missing prerequisites or
failed cleanup fail the invocation. A dispatch without explicit authorization
fails its authorization job.

After the matching solution build, run both offline projects explicitly:

```powershell
dotnet test src\Microsoft.Agents.Hosting.Cli.Tests\Microsoft.Agents.Hosting.Cli.Tests.csproj --no-build --no-restore -c Debug
dotnet test src\Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests\Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests.csproj --no-build --no-restore -c Debug
```

## Runtime, dependencies and support boundaries

The CLI remains a framework-dependent, untrimmed .NET tool, with reflection
command discovery. Installation needs the .NET SDK. Execution needs **both**
`Microsoft.NETCore.App 10.x` and `Microsoft.AspNetCore.App 10.x`; the telemetry
SDK contributes the latter requirement. The .NET 10 SDK includes both.
Runtime-only installation must supply both shared frameworks. Use
`dotnet --list-runtimes` in the architecture-matching .NET installation.

`global.json` selects SDK `10.0.403`, `rollForward: latestMajor`, without
prerelease SDKs. CI retains `10.0.x` setup and the repository's SDK resolution,
versioning and warnings-as-errors settings. Current package acceptance pins:

| Dependency | Version |
| --- | --- |
| Microsoft.Identity.Client / Broker / Extensions.Msal | 4.92.0 |
| Azure.Identity | 1.21.0 |
| Azure.Core | 1.53.0 |
| Microsoft.Agents.CopilotStudio.Sync | 1.10.9-prerelease |

The seven RIDs below are packaging targets, **not minimum OS declarations**.
Product native minimum Windows/macOS versions, Linux distributions/libc
baselines, and full OS/architecture support remain **UNVERIFIED** until native
acceptance. Consult the official [.NET Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows),
[macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos), and
[Ubuntu](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install)
support/dependency tables for .NET 10 itself. For example the macOS table lists
macOS 14/15/26 for .NET 10; that does not certify our native Keychain/broker path.
Ubuntu feeds and architecture coverage differ; do not extrapolate to every
Linux distribution or to musl. We do not declare a `linux-musl-*` RID.

## Native store and session preparation

Never run the native tier on a workstation, shared runner, shared login
Keychain/default collection, cached user credential store, or live tenant.
The supplied preparation driver refuses anything other than an explicitly
dedicated ephemeral GitHub-hosted job. It cannot be used to make a shared
machine safe by merely passing `-Dedicated`.

- **Windows:** current-user DPAPI in the dedicated test process, an exact
  generated checkout-local store root, and matching .NET/native libraries.
  There are no Windows Credential Manager, broker-account or user-cache probes.
- **macOS:** a generated, unlocked **test-owned** legacy Keychain is prepared
  as the ephemeral CI user's default (inside the disposable job, never a
  workstation's login Keychain). The harness verifies the exact default
  Keychain path before any storage operation. The supported legacy
  [`SecKeychainSetUserInteractionAllowed(false)`](https://developer.apple.com/documentation/security/seckeychainsetuserinteractionallowed(_:))
  capability is explicitly established for the harness process lifetime,
  before Keychain work. It is never toggled/restored, and does not change a
  machine interaction setting. No signing helper, entitlements, unlock or
  ACL UI are added to production authentication. Other native clients must
  not reenable UI in that process.
- **Linux:** GLib/GObject/GIO **2.36 or newer**, with loadable
  `libglib-2.0.so.0`, `libgobject-2.0.so.0`, `libgio-2.0.so.0`. The harness checks
  the runtime GLib version and uses the actual raw GIO/Secret Service adapter,
  **not** an auto-unlocking libsecret convenience API. Production requires an
  already-existing local Unix session bus and unlocked default Secret Service
  collection; host-resolved settings enter through existing runtime seams.
  `G_DBUS_DEBUG` must be absent/empty.

The Linux **test driver**, not production, starts its own nonforking bus and
`gnome-keyring-daemon --foreground --components=secrets --unlock`, with a generated
fixture password on stdin. HOME/XDG state lives in its owned leaf. A unique
abstract Unix socket avoids Unix socket pathname limits in long CI checkouts.
EXTERNAL authentication admits only the job's Unix UID. The private bus has no
service-activation directories, so it cannot auto-start another daemon. The
driver waits for its bus and default collection using nonmutating private-bus
calls, never enumerating credentials. Before daemon launch, the exact ordinary
`control` directory is checked for ownership by the job's effective Unix UID,
explicitly changed to **0700**, and its Unix mode read back; the driver does not
rely on umask. A redirected, unowned, or incorrectly permissioned directory fails
before launch. Its native socket argument is relative to the exact owned cwd:
`control/control` is **15 UTF-8 bytes**, below Linux's 107-byte pathname payload
limit irrespective of checkout length.

[GNOME 46.1 directory validation](https://github.com/GNOME/gnome-keyring/blob/46.1/daemon/gkd-util.c)
rejects an existing 0755 directory and selects a fallback; its
[control socket binding](https://github.com/GNOME/gnome-keyring/blob/46.1/daemon/control/gkd-control-server.c)
truncates long native socket paths. The driver therefore waits at most 30 seconds
for the foreground daemon's complete startup publication (it flushes stdout,
then redirects stdout to stderr after initialization in
[`gkd-main.c`](https://github.com/GNOME/gnome-keyring/blob/46.1/daemon/gkd-main.c)).
It requires exactly `GNOME_KEYRING_CONTROL=control`, checks ownership/mode again
**without repairing permissions after launch**, and rejects missing, duplicate,
fallback, absolute, or unexpected selection. Inherited control/activation
environment variables are removed only from child environments. Failure stops
the gate; cleanup terminates only owned processes and deletes only the named
owned leaf, never an unexpected published path. Its prepared-session behavior
still requires execution on the dedicated Linux runners; it has not been
validated by a Windows build.

Ubuntu 24.04 setup installs `libglib2.0-0t64`, `dbus`, `gnome-keyring`, and
MSAL loader prerequisites `libx11-6`, `libc++1`, `libc++abi1`, `libsecret-1-0`,
`libwebkit2gtk-4.1-0`. These are **dedicated CI prerequisites only**, not a
production auto-install or a promise that a headless browser/broker is ready.
Package names are distro-specific; see [Ubuntu GLib](https://packages.ubuntu.com/noble/libglib2.0-0t64)
and [WebKit](https://packages.ubuntu.com/noble/libwebkit2gtk-4.1-0),
[GIO no-auto-start flags](https://docs.gtk.org/gio/flags.DBusCallFlags.html),
and the [daemon manual](https://manpages.ubuntu.com/manpages/noble/man1/gnome-keyring-daemon.1.html).

Fixtures exercise protect/read/delete for credential and token-cache kinds,
and preserve sibling references. Issued mutations enter an exact inventory
before writes; cleanup uses cancellation-independent deletion and verifies
absence, including a write that commits before failing. Returned buffers are
wiped. Fixture PFX keys are generated for the run and loaded by the actual
non-persistent private-key loader, with signing verification and source-file
retention until fixture cleanup. This does not modify production retention:
ROPC passwords/federation assertions are not persisted, protected owned
credentials/caches retain their existing semantics, and user source
certificates are never deleted.

### Mandatory native negatives

After positive storage checks, the harness seeds **existing** credential and
token-cache references, applies a real prepared-store access denial, and requires
all six read/write/delete attempts to raise protection-unavailable errors.
A null/missing read or a successful operation cannot satisfy this gate.

| OS | Dedicated test-owned denial | Explicit fixture restoration |
| --- | --- | --- |
| Windows | Deny read/write/delete on the two generated files, and deny create-files/delete-child on their exact owned directory. This avoids parent DELETE_CHILD bypassing an item's DELETE denial. Owner/ACL-change access remains available. | Restore captured ACLs only on that directory and those generated files. |
| macOS | Lock the exact generated Keychain with `SecKeychainLock`; verify it remains locked and the existing process-lifetime no-UI policy remains established after each rejected operation. | Unlock only that generated Keychain using its test-generated password supplied on dedicated child stdin. Never toggle the process UI policy. |
| Linux | Reload the already-owned private D-Bus configuration with a denial targeting only `org.freedesktop.secrets`. Keep checking that the existing service still owns its name, so loss/unavailability cannot masquerade as the denial. This is **access-policy denial**, not a collection-lock claim. | Restore the original owned bus configuration and reload it. No collection unlock, prompt, daemon replacement or private control protocol. |

Linux configuration rejects shared endpoints, includes and service-activation
directories. Bounded `gdbus` children contact only the private bus control
interface. The [D-Bus daemon reference](https://dbus.freedesktop.org/doc/dbus-daemon.1.html)
describes destination policy and reloading; actual effectiveness must still be
demonstrated on dedicated runners. `gnome-keyring-daemon --unlock` is used only
for initial private preparation, not restoration of an already-running daemon.

After restoration, both original protected values must remain unchanged;
cleanup deletes and verifies only the issued references. Failed restoration
fails the gate and prevents further backend access in an unsafe state; the
outer driver still owns exact Keychain/path/process cleanup.

Generated PFX cases require signing with a password-protected fixture and
passwordless fixtures with null/empty passwords. Passwordless PFX remains
supported, not newly prohibited. They reject missing/wrong passwords on the
protected fixture, an incorrect password on the passwordless fixture, malformed
PFX and a certificate without a private key. Every source fixture must remain
unchanged until exact fixture cleanup. These **real native negatives are
configured but unexecuted**; passing injected orchestration tests is not
native-negative execution evidence.

### macOS PFX private-key lifetime

.NET 10.0.14's [macOS certificate loader](https://github.com/dotnet/runtime/blob/v10.0.14/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/X509Certificates/X509CertificateLoader.macOS.cs)
rejects `EphemeralKeySet`. The CLI therefore uses `DefaultKeySet` **only on
macOS**, without `PersistKeySet`; Windows/Linux retain `EphemeralKeySet`.
The pinned runtime chooses `CreateTemporaryKeychain`, not the user's default
certificate Keychain, whenever `PersistKeySet` is absent.

This is a **framework-owned temporary disk Keychain**, not memory-only
ephemeral storage. Its [runtime lifetime implementation](https://github.com/dotnet/runtime/blob/v10.0.14/src/libraries/Common/src/Interop/OSX/System.Security.Cryptography.Native.Apple/Interop.Keychain.macOS.cs)
uses a GUID path under `Path.GetTempPath`, a generated password which it wipes,
and settings on that temporary Keychain only. Tracked certificate/key handles
keep it alive; the last release **attempts** `SecKeychainDelete` without throwing
on deletion errors. Cleanup is not guaranteed after every OS failure or crash.
Callers retain their existing certificate/MSAL ownership and dispose derived
private keys and certificates; source PFX files are never deleted by Auth.

The dedicated harness requires its own scratch directory as the process temp
directory before generating/importing keys. It observes the new temporary
Keychain during signing and requires its disappearance after disposing the
imported certificate/key handles. Any residual entry fails that fixture gate;
there is no SDK-private reflection, global temp sweep, certificate-store
enumeration, signing helper or machine-preference change. Normal offline
tests do not run this native fixture.

Only exact generated references, named owned leaves/Keychain paths and
process handles/PIDs created by that invocation are cleaned. There is no broad
root, wildcard user-store, broker/PAC/Azure cache or source-certificate cleanup.
Fixture/private daemon state is never uploaded. Child diagnostics are consumed,
not exported; harness output contains only tier/check status, OS/process/hardware
architecture and package/framework/library-version metadata.

## Browser and broker evidence is narrower than sign-in readiness

Native checks build MSAL client configuration with HTTP handlers that reject
network use. They check the system-browser loopback redirect and, where the
pinned native asset exists, broker options
with OS-account enumeration disabled; **no `GetAccounts` or token API is called**.
Installed MSAL runtime library loading is an ABI/dependency check, not evidence
of an installed/working broker, desktop UI or account. Browser/broker readiness
and actual sign-in are explicitly reported **UNVERIFIED**.

- [System browser](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers):
  registered `http://localhost` redirect, a usable default browser and permitted
  loopback listener are needed for actual interactive work.
- [Windows WAM](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam):
  its documented Windows 10 1703+/Server 2019+ baseline is a broker API prerequisite,
  not the CLI/.NET 10 minimum OS or a readiness probe. A suitable user session,
  UI parent and application registration remain live acceptance prerequisites.
- [macOS broker](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/macos-broker-dotnet-sdk):
  Company Portal/device enrollment and functioning SSO extension are required;
  interactive work has main/UI-thread requirements. A dylib or OSX broker option
  alone proves none of those conditions.
- [Linux broker](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/linux-dotnet-sdk):
  the Microsoft identity broker is separately installed/configured, with desktop/
  parent-window and registered broker-redirect prerequisites. The guide contains
  SDK sample setup, not a universal .NET 10 or CLI distro support promise.
  No broker is installed, enrolled or contacted by these checks.
  **The pinned package has no Linux ARM64 MSAL runtime asset**; that loader check
  and Linux ARM64 broker availability remain UNVERIFIED, not a fabricated pass.

Client IDs and the nine-cloud registry remain those of `msagent`; acceptance
does not substitute Azure CLI/PAC clients, modify cloud mapping, or prove every
cloud's downstream service support.

## Packaging and safe installed-tool checks

Both `RuntimeIdentifiers` and `PackAsToolShimRuntimeIdentifiers` retain exactly:

```text
osx-x64;osx-arm64;win-x86;win-x64;win-arm64;linux-x64;linux-arm64
```

With .NET 10 this tool produces **eight nupkgs**: one `DotnetTool` routing
package and seven same-version `DotnetToolRidPackage` implementations. Each
implementation retains every declared shim, the executable/assemblies,
framework references, dependency versions and shipped feature defaults.
Only the CLI is packable. The acceptance console is neither packed nor
discovered as an xUnit test project.

From the repository root, after restore and matching solution build:

```powershell
dotnet build src\Microsoft.Agents.Hosting.Cli.slnx --no-restore -c Debug --verbosity quiet
.\.github\scripts\build-authentication-packages.ps1 -Configuration Debug
.\.github\scripts\verify-authentication-package.ps1 -ExpectedRid win-x64 -Configuration Debug
```

Use the actual native RID on other platforms. The build script prepares all
seven RID inputs without restore, then runs `pack --no-build --no-restore`.
A RID-agnostic build alone does **not** prepare all .NET 10 tool inputs.
Cross-building these inputs on Windows proves packaging only.

The verifier rejects missing/ambiguous/stale families, copies only the validated
family to a generated local-only feed, and runs `dotnet tool install --tool-path`
with a generated NuGet configuration/cache. It uses the architecture-matching
SDK's default shim generation, not a cross-architecture `--arch` download.
The installed implementation RID must match the explicit expected RID.
It invokes the **actual installed command shim** (including .NET 10's Windows
`msagent.cmd` wrapper) from an empty owned project/configuration. No global
tool/feed settings are changed. NuGet HTTP/scratch caches and child TMP/TEMP/TMPDIR
also resolve inside the named owned leaf. Credential-source/feature overrides are cleared
in child environments only; recognized `AH_CLI_CONFIG_DIR` and
`AH_CLI_TELEMETRY_OPTOUT=1` isolate host state and disable telemetry.

Checks cover reflected auth help/aliases, removed switch/token/kind/url surfaces,
shipped feature defaults, empty auth status/list/who JSON, telemetry opt-out,
and early NI12007/exit 3 for disallowed interaction. Existing fake-host tests
prove those early gates precede native/cache/source acquisition; absence of
native-cache artifacts is also checked in the installation's owned config.
There is no real auth mutation or acquisition.

## CI inputs and evidence ledger

Windows and Ubuntu workflows now run both offline suites explicitly; macOS
adds Intel and ARM64 jobs with the same pinned checkout/setup-dotnet/nbgv actions,
artifact guard, SDK, versioning and root NuGet configuration. Normal jobs perform
only offline and safe installed-package checks.

`authentication-native.yml` is dispatch-only, requires explicit dedicated
authorization, and prepares stores only on disposable hosted runners.
Runner labels and setup-dotnet's architecture input are sourced from
[GitHub's runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
and the [pinned action definition](https://github.com/actions/setup-dotnet/blob/a98b56852c35b8e3190ac28c8c2271da59106c68/action.yml).
Availability/access to those runners is still a CI execution prerequisite.

| RID | Dedicated native configuration | Native/installed execution evidence |
| --- | --- | --- |
| win-x86 | windows-2025, x86 SDK/process on x64 hardware | UNVERIFIED; x64 .NET is not x86 evidence |
| win-x64 | windows-2025, x64 SDK/process | Local **offline/package** checks only on Windows x64; native UNVERIFIED |
| win-arm64 | windows-11-arm, ARM64 SDK/process | UNVERIFIED |
| osx-x64 | macos-15-intel, x64 SDK/process | UNVERIFIED |
| osx-arm64 | macos-15, ARM64 SDK/process | UNVERIFIED |
| linux-x64 | ubuntu-24.04, x64 SDK/process, private session | UNVERIFIED |
| linux-arm64 | ubuntu-24.04-arm, ARM64 SDK/process, private session | UNVERIFIED; broker runtime asset absent |

The harness requires native OS/process/hardware agreement (Windows x86 on
x64 Windows is allowed only with an **x86 .NET process**). ARM64 emulation and
an x64 process labeled x86 cannot pass. New job definitions are configured,
**not executed/passed**. No native store was probed on the shared Windows
development host. All live/cloud/service/UI acceptance remains UNVERIFIED.

OneBranch PR/nightly retain their governed Windows container, authenticated
`.azdo/nuget.config`, SDK/tool setup and **external**
`Agent-Hosting-CLI/templates/build-sign-package.yaml@tools` inputs. Added steps
explicitly build/run both offline projects without restore and check the
actual externally emitted package family under `src`, matching existing package
copy inputs. They do not repack or change signing. Integration requires the
external template to restore the registered solution and emit a single complete
eight-package family; missing/ambiguous outputs fail rather than selecting a
different destination or pretending acceptance. External template behavior,
PowerShell 7 availability, container execution, authenticated feed prerequisites,
and governed macOS/ARM64 pools have not been demonstrated here. No unsupported
OneBranch pool or external template change is invented.
