# Testing native msagent against a personal DEV stamp

The first question is not whether a package upload succeeds. It is whether the
upload and the deployment reach the services you meant to test.

An upload goes to AMS-DP, the Agent Management Service data plane. A deployment
goes to AMS-CMP, the control plane, which then reads the source package and
arranges delivery to the target environment. Pointing only one of those client
calls at DEV can leave the other on its normal route. Even pointing both at DEV
does not select the control plane's downstream services.

This branch makes the **native msagent executable** accept a deliberately narrow
pair of personal-DEV service origins. It supplies the tenant-qualified paths
those services require, without replacing native authentication or environment
discovery:

```text
Repository: microsoft/AgentHosting.Cli
Branch:     users/stevesteiner/msagent-dev-stamp-testing
Base:       e5098faaf412ca5e4ae554e26526bc9a31f3d2aa
```

This is a source-build preview, not an installed release or a REST test client.
The ordinary Copilot Studio bug-bash instructions do not describe this gated
CodeAgent path. With the endpoint variables unset, normal routing is unchanged.

## Start with an agreed test boundary

Before building or signing in, identify the personal alias, both service origins,
the test tenant, the agent's home environment, and the target environment.
Confirm the account and approval for each network or mutation step. GitHub access
and internal package-feed access usually use corporate identities; native agent
operations use the approved account in the test tenant. They are not
interchangeable.

The backend services must already be deployed and authorized for this scenario.
This branch does not deploy AMS-CMP, AMS-DP, or AHRG, change their feature gates,
or grant environment access. Use the RP repository's current
`.github\skills\deploy-dev\SKILL.md` and
`src\tools\DevClusterSetup\DeployToDev.ps1` guidance for those changes, with
separate approval. Its EV2 deployment flow requires a real Windows PowerShell
5.1 console and the approved corporate SFDev identity; it is not part of the
PowerShell 7 CLI walkthrough below.

The finalization validation behind this guide used RP fix
`a80398377c3160ddc375aa527078dc5b062eee34` with separately approved DEV
configuration/feature-gate changes. Those server changes are not in this CLI
branch. Record the actual running service versions rather than assuming that
checking out the client selects that backend build.

The walkthrough below uses a synthetic Node package, a fresh runtime application,
and a same-home target environment. It exercises package delivery and deployment
publication. It does **not** launch a runtime or establish that the synthetic
package is a functioning hosted agent.

You will need **PowerShell 7 on Windows** for these examples, a .NET SDK satisfying
`global.json` (currently 10.0.400 with `latestMajor` roll-forward and prereleases
disabled), and the organization's configured authentication for the internal
NuGet feed. A .NET runtime alone cannot build this branch.

## Build the branch, not whichever msagent is on PATH

From an existing clone, fetch the branch and use a new worktree directory. Adjust
these two local paths; do not reuse a worktree that contains someone else's work.
Git may request the GitHub identity authorized for this private repository.

```powershell
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AH_CLI_TELEMETRY_OPTOUT = '1'
$clone = 'C:\repos\AgentHosting.Cli'
$worktree = 'C:\worktrees\msagent-dev-stamp'
$branch = 'users/stevesteiner/msagent-dev-stamp-testing'

git -C $clone fetch origin $branch
if ($LASTEXITCODE -ne 0) { throw 'Fetch failed; stop here.' }
git -C $clone worktree add --detach $worktree FETCH_HEAD
if ($LASTEXITCODE -ne 0) { throw 'Worktree creation failed; stop here.' }
Set-Location $worktree
git rev-parse HEAD
dotnet --version
```

The detached worktree pins the fetched commit for testing. It does not change the
branch checked out in your original clone.

This branch intentionally selects the **internal DPX-Tools-Upstream feed** in its
root `nuget.config`. The explicit restore configuration below also uses that
feed, and keeps downloaded packages within this worktree:

```powershell
$testProject = '.\src\Microsoft.Agents.Hosting.Cli.Tests\Microsoft.Agents.Hosting.Cli.Tests.csproj'

dotnet restore $testProject --configfile .\.azdo\nuget.config `
    --packages .\artifacts\internal-nuget --disable-parallel --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Internal-feed restore failed; stop here.' }

dotnet build $testProject -c Release --no-restore --nologo -v:minimal `
    -m:1 /nr:false /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed; stop here.' }

dotnet test $testProject -c Release --no-build --no-restore --nologo `
    --filter 'FullyQualifiedName~Microsoft.Agents.Hosting.Cli.Tests.Ams.'
if ($LASTEXITCODE -ne 0) { throw 'Offline AMS checks failed; stop here.' }

$cli = Join-Path $worktree 'src\Microsoft.Agents.Hosting.Cli\bin\Release\net10.0\Microsoft.Agents.Hosting.Cli.exe'
& $cli --version
Get-FileHash -Algorithm SHA256 -LiteralPath $cli, `
    (Join-Path (Split-Path $cli) 'Microsoft.Agents.Hosting.Cli.dll'), `
    (Join-Path (Split-Path $cli) 'Microsoft.Agents.Hosting.Cli.Core.dll')
```

Keep the source commit and binary hashes with the run's evidence. The offline
checks exercise native composition and request shape; they are not a live DEV
result. If restore fails with a feed-authentication error, resolve access through
the approved corporate mechanism. Do not substitute `api.nuget.org`, add a public
fallback, clear shared caches, or run a bare tool install/update. Invoke `$cli`
directly throughout; `dotnet run` can introduce another build or restore.

## Establish the native identity before setting DEV origins

There are two application identities to keep apart. The **tool** obtains native
delegated tokens as msagent's client, `21fadfba-178a-47f0-8ce6-07c05eb346ca`.
The **runtime application** is the Entra application identified by `--entra-id`
when you create a deployment. Supplying that runtime ID does not change the
tool's identity.

Choose an isolated profile outside the repository. First leave both endpoint
overrides unset: a new profile has not yet selected the Test ring, and the branch
rejects personal-DEV origins outside that ring.

```powershell
$devAlias = '<approved-personal-alias>'
$tenantId = '<test-tenant-guid>'
$account = '<approved-test-account-UPN>'
$homeEnvironmentId = '<approved-home-environment-guid>'
$targetEnvironmentId = $homeEnvironmentId

$env:AH_CLI_TELEMETRY_OPTOUT = '1'
$env:AH_CLI_CONFIG_DIR = Join-Path $env:LOCALAPPDATA "agenthost-cli-csharp\devstamp-$devAlias-$tenantId"
$env:AH_CLI_FEATURES = 'AmsFeatures'
Remove-Item Env:\AH_CLI_AMS_ENDPOINT, Env:\AH_CLI_AMSDP_ENDPOINT -ErrorAction SilentlyContinue

if ($env:AH_CLI_CLIENT_ID) { throw 'Remove the custom client override before this native-client test.' }
if ($env:AH_CLI_ALLOW_UNPROTECTED_CACHE -in @('1','true')) {
    throw 'This walkthrough requires protected credential storage.'
}
```

The next command is an **authentication boundary**. With approval from the test
account's owner, it may open broker/browser sign-in or ask for consent. Check the
account and tenant in that UI. Do not substitute a corporate account because it
happens to be signed into the browser.

```powershell
& $cli auth login --cloud test --tenant $tenantId --account $account
if ($LASTEXITCODE -ne 0) { throw 'Sign-in failed; resolve it before proceeding.' }
```

If the local broker cannot work in the terminal, the separately approved explicit
alternative is the same command with `--device-code`. It is still native msagent
authentication, not a helper application's token. Do not copy tokens into the
CLI, override the resource to a DEV hostname, or enable an unprotected cache.

Now inspect inventory and select the home environment:

```powershell
& $cli env list --json --non-interactive --no-color
if ($LASTEXITCODE -ne 0) { throw 'Environment inventory failed.' }
& $cli env select $homeEnvironmentId --json --non-interactive --no-color
if ($LASTEXITCODE -ne 0) { throw 'Environment selection failed.' }
$auth = & $cli auth status --json --non-interactive --no-color | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $auth.success -or -not $auth.signedIn -or
    $auth.account -ine $account -or $auth.tenantId -ine $tenantId -or
    $auth.cloud -ne 'test' -or $auth.environmentId -ine $homeEnvironmentId -or
    $auth.environmentTenantId -ine $tenantId -or -not $auth.environmentTenantBound) {
    throw 'Native account, ring, or environment binding differs from the agreed test.'
}
```

Inventory still uses the normal trusted Power Platform API. Its verified tenant
and environment IDs are what make the subsequent direct service routes valid.
This branch does not turn arbitrary endpoints returned by discovery into trusted
ones.

## Select both client destinations, then check the downstream path

Use origins taken from the approved DEV deployment, not hosts inferred from a
successful sign-in. This example shows the supported WUS `cm100`/`il100` shape;
substitute your approved region and cluster numbers.

```powershell
$region = 'wus'
$cmpCluster = '100'
$dpCluster = '100'
$env:AH_CLI_AMS_ENDPOINT = "https://parp-agentmanagementservice-${devAlias}.${region}-cm${cmpCluster}.gateway.test.cm.powerapps.com"
$env:AH_CLI_AMSDP_ENDPOINT = "https://parp-agentmanagementservicedataplane-${devAlias}.${region}-il${dpCluster}.gateway.test.island.powerapps.com"
& $cli auth status --json --non-interactive --no-color
if ($LASTEXITCODE -ne 0) { throw 'Resolve startup/profile configuration before any mutation.' }
```

Both variables are required, and their personal alias and region must agree.
They accept HTTPS origins only, optionally ending in `/`: no explicit port,
credentials, extra path, query, or fragment. Shared unaliased services, arbitrary
hosts, and non-Test profiles are rejected. Endpoint validation is not a health
check, so successful `auth status` does not prove that either service is running.

Agent creation, reads, and package upload now use AMS-DP. Deployment creation and
the read/PATCH pair used by `deployment update` use AMS-CMP. The direct paths are:

```text
AMS-DP:  /agenthosting/tenants/{tenant}/environments/{home}/agents...
AMS-CMP: /agenthosting/tenants/{tenant}/environments/{home}/agents/{agent}/deployments...
```

The target environment stays in the deployment request body. It does not replace
the home environment in those paths. Likewise, `--deployment-type test` is a
deployment stage; it does not choose the stamp. The Test authentication/discovery
ring and a personal DEV service deployment are different choices.

There is one more routing boundary, and it is easy to miss. AMS-CMP chooses its
**source DP** through server-side `DataPlaneDirectHostOverride`; it does not read
your shell's `AH_CLI_AMSDP_ENDPOINT`. Confirm that the server will read from the
same DP to which the CLI uploads. For the target, AHRG (Agent Host Runtime
Gateway) allocation uses environment discovery with `directHostOverride:null`.
The CLI never calls AHRG directly.

Before uploading, establish the running full service versions and routing using
the approved deployment records and sanctioned observation. If personal AHRG
selection needs the DEV-only `LocalDev.ServiceClient.DevAliasHostRewriteRules`,
have that exact server configuration reviewed and approved separately. Merely
setting these two CLI variables does not configure that rewrite. Correlate actual
requests with the selected personal AMS-CMP, source AMS-DP, and target AHRG
versions; do not treat an EV2 shell exit or an endpoint string as that proof.

## Prepare a fresh package and an unoccupied runtime application

For a first deployment, arrange an approved **new single-tenant Entra application
and service principal**, and record the application's client ID. The minimal
fixture needs no credentials or permission grants for this publication-only
test. Provisioning that fixture is a separate, approved operation, not something
these examples perform.

Do not reuse an application already owned by another active deployment in this
tenant/target environment. Its routing key is the runtime application ID within
that tenant/environment, not the agent name or deployment stage. A new agent with
an occupied application can finalize its package and then fail publication with
`RoutingConflict`, leaving partial deployment state.

After approving local fixture creation, this produces a fresh immutable ZIP
without downloading or running agent code. The random padding makes it slightly
larger than 8 MiB, exercising three blocks with the validated 4 MiB block writer.
Confirm the writer configuration if testing a different backend build.

```powershell
$runId = [Guid]::NewGuid().ToString('D')
$fixtureDirectory = Join-Path $worktree "artifacts\devstamp-fixture-$runId"
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
$manifest = @{
    name = "devstamp-$runId"; version = '0.0.1'; private = $true
    main = 'index.js'; scripts = @{ start = 'node index.js' }
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $fixtureDirectory 'package.json') -Encoding utf8NoBOM
'process.exitCode = 0;' | Set-Content (Join-Path $fixtureDirectory 'index.js') -Encoding utf8NoBOM
$padding = [byte[]]::new(8 * 1024 * 1024 + 257)
[Random]::new().NextBytes($padding)
[IO.File]::WriteAllBytes((Join-Path $fixtureDirectory 'synthetic-padding.bin'), $padding)
$padding = $null
$inputs = @('package.json','index.js','synthetic-padding.bin') |
    ForEach-Object { Join-Path $fixtureDirectory $_ }
$packageV1 = Join-Path $fixtureDirectory 'package-v1.zip'
Compress-Archive -LiteralPath $inputs -DestinationPath $packageV1 -CompressionLevel NoCompression
$shaV1 = (Get-FileHash $packageV1 -Algorithm SHA256).Hash.ToLowerInvariant()
$sizeV1 = (Get-Item $packageV1).Length
$runtimeApplicationId = '<approved-new-runtime-application-client-id>'
```

The ZIP, not the source directory, is the immutable input. Do not change it after
recording its digest and size. The native upload command sends those exact bytes;
it does not build or inspect their contents.

## Create, upload, and deploy once

Each block below is a **separate approved mutation** under `$account`. Execute
and inspect one at a time. `--non-interactive` prevents an unexpected sign-in
prompt; it does not remove the need for approval. Stop on any failed or ambiguous
result. Preserve sanitized operation IDs, correlations, timestamps, and partial
state instead of rerunning the block.

```powershell
$createKey = [Guid]::NewGuid().ToString('D')
$created = & $cli agent create --agent-type CodeAgent --agent-name "devstamp-$runId" `
    --language node --language-version 22.15.0 --environment-id $homeEnvironmentId `
    --idempotency-key $createKey --json --non-interactive --no-color | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $created.success -or $created.status -ne 'agent-created' -or
    $created.tenantId -ine $tenantId -or $created.environmentId -ine $homeEnvironmentId -or
    -not $created.agentId -or -not $created.repositoryId) {
    throw 'Agent creation did not confirm success; preserve the result.'
}
$agentId = $created.agentId
```

Confirm its tenant/home coordinates and repository ID, and corroborate the source
DP route before uploading.

```powershell
$uploaded = & $cli agent package upload --agent-id $agentId --package $packageV1 `
    --environment-id $homeEnvironmentId --json --non-interactive --no-color | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $uploaded.success -or $uploaded.status -ne 'package-uploaded' -or
    $uploaded.agentId -ine $agentId -or $uploaded.packageDigest -ine $shaV1 -or
    $uploaded.packageSizeInBytes -ne $sizeV1 -or $uploaded.tenantId -ine $tenantId -or
    $uploaded.environmentId -ine $homeEnvironmentId -or -not $uploaded.packageRevisionId) {
    throw 'Source publication or integrity is unconfirmed; preserve the result.'
}
$revisionV1 = $uploaded.packageRevisionId
```

`package-uploaded` establishes a source revision, not a deployed agent.
Deployment is the next operation:

```powershell
$deploymentName = "devstamp-$($runId.Substring(0,8))"
$deployed = & $cli agent deploy --agent-id $agentId --package-revision $revisionV1 `
    --environment-id $homeEnvironmentId --target-environment-id $targetEnvironmentId `
    --deployment-name $deploymentName --deployment-type test `
    --entra-id $runtimeApplicationId --entra-id-type app-reg `
    --json --non-interactive --no-color | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $deployed.success -or
    $deployed.status -ne 'deployment-projection-acknowledged' -or -not $deployed.projectionAcknowledged -or
    $deployed.agentId -ine $agentId -or $deployed.packageRevisionId -ine $revisionV1 -or
    $deployed.packageDigest -ine $shaV1 -or $deployed.packageSizeInBytes -ne $sizeV1 -or
    $deployed.entraId -ine $runtimeApplicationId -or $deployed.tenantId -ine $tenantId -or
    $deployed.entraIdType -ne 'AppReg' -or $deployed.deploymentType -ne 'Test' -or
    $deployed.deploymentName -cne $deploymentName -or -not $deployed.deploymentId -or
    $deployed.homeEnvironmentId -ine $homeEnvironmentId -or $deployed.targetEnvironmentId -ine $targetEnvironmentId) {
    throw 'Deployment outcome or identity differs; preserve the result.'
}
$deploymentId = $deployed.deploymentId
```

This terminal response is necessary, but it is not the complete finalization
proof. Establish the backend evidence described below before proceeding to an
upgrade.

## Give the same deployment a new package revision

An upgrade is not another `agent deploy` with a new application. Keep the agent,
deployment, runtime application, and environment coordinates fixed.

First change the synthetic package's declared version and content, and create a
second ZIP. The first ZIP remains untouched:

```powershell
$manifest.version = '0.0.2'
$manifest | ConvertTo-Json | Set-Content (Join-Path $fixtureDirectory 'package.json') -Encoding utf8NoBOM
'const revision = 2; process.exitCode = revision === 2 ? 0 : 1;' |
    Set-Content (Join-Path $fixtureDirectory 'index.js') -Encoding utf8NoBOM
$packageV2 = Join-Path $fixtureDirectory 'package-v2.zip'
Compress-Archive -LiteralPath $inputs -DestinationPath $packageV2 -CompressionLevel NoCompression
$shaV2 = (Get-FileHash $packageV2 -Algorithm SHA256).Hash.ToLowerInvariant()
$sizeV2 = (Get-Item $packageV2).Length
if ($shaV1 -eq $shaV2) { throw 'The upgrade must contain distinct bytes.' }
```

With approval for the new source revision:

```powershell
$uploadedV2 = & $cli agent package upload --agent-id $agentId --package $packageV2 `
    --environment-id $homeEnvironmentId --json --non-interactive --no-color | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $uploadedV2.success -or $uploadedV2.status -ne 'package-uploaded' -or
    $uploadedV2.agentId -ine $agentId -or $uploadedV2.packageDigest -ine $shaV2 -or
    $uploadedV2.packageSizeInBytes -ne $sizeV2 -or $uploadedV2.tenantId -ine $tenantId -or
    $uploadedV2.environmentId -ine $homeEnvironmentId -or -not $uploadedV2.packageRevisionId -or
    $uploadedV2.packageRevisionId -ieq $revisionV1) {
    throw 'The distinct new source revision is unconfirmed; preserve the result.'
}
$revisionV2 = $uploadedV2.packageRevisionId
```

Then approve the existing-deployment update:

```powershell
$updated = & $cli deployment update --agent-id $agentId --deployment-id $deploymentId `
    --package-revision $revisionV2 --environment-id $homeEnvironmentId `
    --json --non-interactive --no-color | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $updated.success -or $updated.status -ne 'agent-deployment-updated' -or
    -not $updated.projectionAcknowledged -or $updated.agentId -ine $agentId -or
    $updated.deploymentId -ine $deploymentId -or $updated.entraId -ine $runtimeApplicationId -or
    $updated.tenantId -ine $tenantId -or $updated.entraIdType -ne 'AppReg' -or $updated.deploymentType -ne 'Test' -or
    $updated.packageRevisionId -ine $revisionV2 -or $updated.packageDigest -ine $shaV2 -or
    $updated.packageSizeInBytes -ne $sizeV2 -or $updated.deploymentName -cne $deploymentName -or
    $updated.homeEnvironmentId -ine $homeEnvironmentId -or $updated.targetEnvironmentId -ine $targetEnvironmentId) {
    throw 'Same-deployment upgrade is unconfirmed; preserve the result.'
}
```

`--package-revision` selects the AMS update path. Do not combine this form with
`--project`, `--deployment-name`, `--tenant`, `--deployment-type`, or ABS options.
The native implementation reads the current deployment, validates its identity,
and PATCHes the new revision with that read's `ExpectedPackageRevisionId`. It
does not silently fall back to creating a deployment.

## Decide what the run actually proved

Use sanctioned backend observation, scoped to the operation window and the
agent, **deployment**, and revision IDs. Include the deployment ID when finding
correlations: an update need not log the same identity fields as agent creation.
Telemetry can arrive separately for each service. Wait or requery the evidence,
not the mutation.

For each fresh revision, retain the expected digest/size, source operation and
revision IDs, native terminal result, and correlated full versions of personal
AMS-CMP, source AMS-DP, and target AHRG. Establish this order:

1. A fresh target allocation, followed by `GenerateBlobWriteSas` and block commit.
2. Successful `CompleteAdlsUpload`, then both finalized verification reads of
   **that same allocation**, with integrity established before availability.
3. Matching persisted availability, deployment publication, and native terminal
   success selecting the expected revision, digest, size, and stable identities.

For an existing deployment, index replay and routing update are expected
convergence paths; requiring create-only events would be wrong. Observed update
events include `EspAgentDeploymentStore.CanonicalReplaySuccess`,
`AgentIndexReplaySuccess`, and `RoutingUpdateSuccess`. Index replay alone says
nothing about the selected package revision. A stale or superseded result is
not proof that the requested new revision was published.

Separate live observations from conclusions based on the exact running code.
For example, matching availability after the patched integrity guards supports
the finalization claim, but it does not expose raw consistency-token lineage.
The CLI's transitional capability booleans, such as
`targetLocalPackageStagingPerformed`, are not a substitute for backend evidence.
Do not claim a separate post-update read unless one was actually performed.

Retain metadata only: no tokens, SAS values, raw consistency tokens, credential
caches, or customer payloads. Do not enable broad debug capture or a transcript
of authentication. On failure, distinguish a rejected completion, stale readback,
wrong identity/version, publication conflict, and subsequent mutation. Preserve
the state; do not bypass pending-upload guards, add sleeps to the deployment,
blindly re-upload, or redeploy services until the cause is understood.

Fresh bootstrap and same-deployment package upgrade were both observed to pass
through this native routing implementation with approved personal DEV backends.
That is evidence for those two scenarios, not for arbitrary server versions or
independent rollout safety. Full native `identity create app` provisioning,
credentials/consent, runtime launch, replay/idempotency, concurrent updates,
interruption recovery, and rollback remain separate test work.

## Leave the overrides behind when leaving DEV

These settings belong to the current shell. Reapply them deliberately in a new
one; do not put personal DEV destinations into a shared shell profile. Before
switching rings or repairing sign-in, remove both overrides:

```powershell
Remove-Item Env:\AH_CLI_AMS_ENDPOINT, Env:\AH_CLI_AMSDP_ENDPOINT -ErrorAction SilentlyContinue
```

This restores normal client routing. It does not delete agents, deployments,
runtime applications, or published packages. Any cleanup of those resources is a
separately approved operation.
