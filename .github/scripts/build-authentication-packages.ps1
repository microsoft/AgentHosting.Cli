param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = [IO.Path]::Combine($repo, 'src', 'Microsoft.Agents.Hosting.Cli', 'Microsoft.Agents.Hosting.Cli.csproj')
$solution = [IO.Path]::Combine($repo, 'src', 'Microsoft.Agents.Hosting.Cli.slnx')
Push-Location $repo
try {
    # A RID-agnostic solution build does not produce the .NET 10 tool's RID inputs.
    foreach ($rid in 'osx-x64', 'osx-arm64', 'win-x86', 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64') {
        & dotnet build $project --no-restore -c $Configuration -r $rid --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "RID package-input build failed: $rid" }
    }
    & dotnet pack $solution --no-build --no-restore -c $Configuration --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Tool packaging failed.' }
} finally { Pop-Location }
