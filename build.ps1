param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipTests, [string]$Dotnet)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($Dotnet) { $Dotnet } elseif ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:LOCALAPPDATA 'ModularDiff/dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetExe)) { throw 'Install .NET 10 SDK, then run build.ps1 again.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
Push-Location $repoRoot
try {
    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'shared/plugins') -Directory) {
        Invoke-Dotnet @('build', (Join-Path $project.FullName ($project.Name + '.csproj')), '-c', $Configuration, '-o', (Join-Path $repoRoot ('artifacts/plugins/' + $project.Name)), '--nologo')
    }
    Invoke-Dotnet @('build', 'shared/ModularDiff.Cli/ModularDiff.Cli.csproj', '-c', $Configuration, '-o', 'artifacts/cli', '--nologo')
    Invoke-Dotnet @('build', 'windows/ModularDiff.Gui/ModularDiff.Gui.csproj', '-c', $Configuration, '-o', 'artifacts/gui', '--nologo')
    if (-not $SkipTests) {
        Invoke-Dotnet @('run', '--project', 'tests/ModularDiff.Tests/ModularDiff.Tests.csproj', '-c', $Configuration, '--', (Join-Path $repoRoot 'artifacts/plugins'))
    }
    Write-Output 'Build complete: artifacts/cli, artifacts/gui, artifacts/plugins'
} finally { Pop-Location }
