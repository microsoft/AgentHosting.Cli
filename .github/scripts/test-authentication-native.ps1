param(
    [Parameter(Mandatory)][string]$ExpectedRid,
    [switch]$Dedicated,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
if (-not $Dedicated -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Native preparation is restricted to explicitly dedicated ephemeral GitHub-hosted jobs. Never run on a shared workstation.'
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$root = [IO.Path]::Combine($repo, 'artifacts', 'msagent-native-' + [guid]::NewGuid().ToString('N'))
$harness = [IO.Path]::Combine($repo, 'src', 'Microsoft.Agents.Hosting.Cli.AuthenticationChecks', 'bin', $Configuration,
    'net10.0', 'Microsoft.Agents.Hosting.Cli.AuthenticationChecks.dll')
$packages = [IO.Path]::Combine($repo, 'src', 'Microsoft.Agents.Hosting.Cli', 'bin', $Configuration)
$os = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { 'unsupported' }
$hardware = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($ExpectedRid -ne "$os-$hardware" -and -not ($ExpectedRid -eq 'win-x86' -and $os -eq 'win' -and $hardware -eq 'x64')) {
    throw 'Preparation requires matching native hardware.'
}
if (Test-Path -LiteralPath $root) { throw 'Generated native leaf already exists.' }
$parent = Split-Path -Parent $root
if (Test-Path -LiteralPath $parent) {
    if ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Native parent must not redirect outside the checkout.' }
}
New-Item -ItemType Directory -Path $root | Out-Null
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
$keychainCreated = $false
$fixturePassword = ''

function Start-OwnedChild([string]$File, [string[]]$Arguments, [string]$InputText = '', [string]$WorkingDirectory = $root) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $WorkingDirectory
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match '^(AH_CLI_|AZURE_|ACTIONS_ID_TOKEN_|MSAGENT_ADO_|NUGET_|LISTEN_)' -or $key -in 'DBUS_SESSION_BUS_ADDRESS','G_DBUS_DEBUG','GNOME_KEYRING_CONTROL') {
            $start.Environment.Remove($key) | Out-Null
        }
    }
    foreach ($key in 'HOME','XDG_CONFIG_HOME','XDG_DATA_HOME','XDG_CACHE_HOME','XDG_RUNTIME_DIR') {
        $start.Environment[$key] = (Join-Path $root 'home')
    }
    foreach ($key in 'TEMP','TMP','TMPDIR') { $start.Environment[$key] = (Join-Path $root 'scratch') }
    $start.Environment['AH_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['AH_CLI_CONFIG_DIR'] = (Join-Path $root 'config')
    if ($IsLinux) { $start.Environment['DBUS_SESSION_BUS_ADDRESS'] = 'unix:abstract=' + [IO.Path]::GetFileName($root) }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    $children.Add($child)
    $output = $child.StandardOutput.ReadToEndAsync()
    $errorOutput = $child.StandardError.ReadToEndAsync()
    if ($InputText) { $child.StandardInput.WriteLine($InputText) }
    $child.StandardInput.Close()
    return @{ Process = $child; Output = $output; Error = $errorOutput }
}

function Wait-OwnedChild($Child, [string]$Check) {
    if (-not $Child.Process.WaitForExit(180000)) { throw "Prepared check timed out: $Check" }
    if ($Child.Process.ExitCode -ne 0) {
        @($Child.Output.Result, $Child.Error.Result) -split '\r?\n' | Where-Object { $_ -match '^tier=' } | Write-Output
        throw "Prepared check failed: $Check"
    }
}

Push-Location $repo
try {
    New-Item -ItemType Directory -Path (Join-Path $root 'home') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'scratch') | Out-Null
    $platform = Start-OwnedChild (Get-Command dotnet).Source @($harness, 'platform', '--expected-rid', $ExpectedRid) -WorkingDirectory $repo
    Wait-OwnedChild $platform 'actual-dotnet-process-architecture'
    $platform.Output.Result -split '\r?\n' | Where-Object { $_ -match '^tier=' } | Write-Output
    if ($IsLinux) {
        Wait-OwnedChild (Start-OwnedChild 'chmod' @('700', $root, (Join-Path $root 'home'))) 'owned-directory-permissions'
        # No service directories: the private bus cannot activate a shared/default daemon.
        $config = Join-Path $root 'bus.conf'
        $identity = Start-OwnedChild 'id' @('-u')
        Wait-OwnedChild $identity 'private-bus-peer-identity'
        $uid = $identity.Output.Result.Trim()
        if ($uid -notmatch '^[0-9]+$') { throw 'Private bus peer identity unavailable.' }
        $address = 'unix:abstract=' + [IO.Path]::GetFileName($root)
        "<busconfig><type>session</type><listen>$address</listen><auth>EXTERNAL</auth><policy context='default'><allow user='$uid'/><allow own='*'/><allow send_destination='*'/><allow receive_sender='*'/></policy></busconfig>" |
            Set-Content -LiteralPath $config
        $bus = Start-OwnedChild 'dbus-daemon' @('--nofork', "--config-file=$config")
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ($true) {
            if ($bus.Process.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'Private session bus unavailable.' }
            $probe = Start-OwnedChild 'gdbus' @('call', '--session', '--dest', 'org.freedesktop.DBus', '--object-path',
                '/org/freedesktop/DBus', '--method', 'org.freedesktop.DBus.GetId')
            if ($probe.Process.WaitForExit(2000) -and $probe.Process.ExitCode -eq 0) { break }
            if (-not $probe.Process.HasExited) { $probe.Process.Kill($true); $probe.Process.WaitForExit() }
            Start-Sleep -Milliseconds 100
        }
        $control = Join-Path $root 'control'
        New-Item -ItemType Directory -Path $control | Out-Null
        $controlPreparation = Start-OwnedChild (Get-Command dotnet).Source @($harness, 'control', '--dedicated',
            '--root', $root, '--expected-rid', $ExpectedRid) -WorkingDirectory $repo
        Wait-OwnedChild $controlPreparation 'owned-control-permissions-before-daemon-launch'
        $fixturePassword = [guid]::NewGuid().ToString('N')
        $daemon = Start-OwnedChild 'gnome-keyring-daemon' @('--foreground', '--components=secrets', '--control-directory=control', '--unlock') $fixturePassword
        # Foreground GNOME flushes its environment, then redirects stdout to stderr after initialization.
        if (-not $daemon.Output.Wait(30000) -or $daemon.Process.HasExited) { throw 'Prepared daemon control publication unavailable.' }
        $selected = $daemon.Output.GetAwaiter().GetResult()
        if ($selected.EndsWith("`n")) { $selected = $selected.Substring(0, $selected.Length - 1) }
        $controlPublication = Start-OwnedChild (Get-Command dotnet).Source @($harness, 'control', '--dedicated', '--prepared',
            '--root', $root, '--expected-rid', $ExpectedRid) -InputText $selected -WorkingDirectory $repo
        Wait-OwnedChild $controlPublication 'exact-selected-owned-control-directory'
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $ready = $false
        while ([DateTime]::UtcNow -lt $deadline -and -not $daemon.Process.HasExited) {
            $probe = Start-OwnedChild 'gdbus' @('call', '--session', '--dest', 'org.freedesktop.secrets', '--object-path',
                '/org/freedesktop/secrets', '--method', 'org.freedesktop.Secret.Service.ReadAlias', 'default')
            if ($probe.Process.WaitForExit(2000) -and $probe.Process.ExitCode -eq 0 -and $probe.Output.Result -notmatch "objectpath '/'") {
                $ready = $true
                break
            }
            if (-not $probe.Process.HasExited) { $probe.Process.Kill($true); $probe.Process.WaitForExit() }
            Start-Sleep -Milliseconds 100
        }
        if (-not $ready) { throw 'Prepared unlocked default Secret Service collection unavailable.' }
        Write-Output 'tier=native-nonsecret check=private-session-preparation status=PASS autostart=disabled control=owned-relative mode=0700 socketBytes=15 selected=verified'
    } elseif ($IsMacOS) {
        $keychain = Join-Path $root 'test.keychain-db'
        $password = [guid]::NewGuid().ToString('N')
        $fixturePassword = $password
        # This is a disposable CI user's generated keychain, never a workstation's login keychain.
        $keychainCreated = $true
        Wait-OwnedChild (Start-OwnedChild '/usr/bin/security' @('create-keychain', '-p', $password, $keychain)) 'create-owned-keychain'
        Wait-OwnedChild (Start-OwnedChild '/usr/bin/security' @('unlock-keychain', '-p', $password, $keychain)) 'prepare-owned-unlocked-keychain'
        Wait-OwnedChild (Start-OwnedChild '/usr/bin/security' @('default-keychain', '-d', 'user', '-s', $keychain)) 'prepare-owned-default-keychain'
        Write-Output 'tier=native-nonsecret check=ephemeral-owned-keychain-preparation status=PASS'
    }
    Set-Content -LiteralPath (Join-Path $root 'prepared') -Value 'dedicated-test-owned-nonsecret'
    $nativeInput = if ($IsMacOS) { $fixturePassword } else { '' }
    $check = Start-OwnedChild (Get-Command dotnet).Source @($harness, 'native', '--dedicated', '--prepared',
        '--root', $root, '--expected-rid', $ExpectedRid, '--package-directory', $packages) -InputText $nativeInput -WorkingDirectory $repo
    Wait-OwnedChild $check 'native-nonsecret-acceptance'
    $check.Output.Result -split '\r?\n' | Where-Object { $_ -match '^tier=' } | Write-Output
} finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
    if ($keychainCreated -and (Test-Path -LiteralPath (Join-Path $root 'test.keychain-db'))) {
        $cleanup = [Diagnostics.ProcessStartInfo]::new('/usr/bin/security')
        $cleanup.UseShellExecute = $false
        $cleanup.RedirectStandardOutput = $true
        $cleanup.RedirectStandardError = $true
        foreach ($argument in 'delete-keychain', (Join-Path $root 'test.keychain-db')) { $cleanup.ArgumentList.Add($argument) }
        $child = [Diagnostics.Process]::Start($cleanup)
        $null = $child.StandardOutput.ReadToEndAsync()
        $null = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(30000)) { $child.Kill($true); $child.WaitForExit(); throw 'Owned keychain cleanup timed out.' }
        if ($child.ExitCode -ne 0) { throw 'Owned keychain cleanup failed.' }
        $child.Dispose()
    }
    Remove-Item -LiteralPath $root -Recurse -Force
    Pop-Location
}
