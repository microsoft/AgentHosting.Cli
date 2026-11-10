param(
    [Parameter(Mandatory)][string]$ExpectedRid,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $PackageDirectory) {
    $PackageDirectory = [IO.Path]::Combine($repo, 'src', 'Microsoft.Agents.Hosting.Cli', 'bin', $Configuration)
}
$harness = [IO.Path]::Combine($repo, 'src', 'Microsoft.Agents.Hosting.Cli.AuthenticationChecks', 'bin', $Configuration,
    'net10.0', 'Microsoft.Agents.Hosting.Cli.AuthenticationChecks.dll')
Push-Location $repo
try {
    & dotnet $harness package --expected-rid $ExpectedRid --package-directory $PackageDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Isolated installed-package acceptance failed.' }
} finally { Pop-Location }
