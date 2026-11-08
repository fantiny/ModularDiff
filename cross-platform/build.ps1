param(
    [ValidateSet('win-x64','osx-arm64','osx-x64','linux-x64','linux-arm64')][string]$Runtime,
    [string]$Dotnet = 'dotnet',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $PSScriptRoot ('artifacts/' + $(if ($Runtime) { $Runtime } else { 'dev' }))
function Run-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
foreach ($plugin in Get-ChildItem (Join-Path $repo 'shared/plugins') -Directory) {
    Run-Dotnet @('build', (Join-Path $plugin.FullName ($plugin.Name + '.csproj')), '-c', 'Release', '-o', (Join-Path $output ('plugins/' + $plugin.Name)), '--nologo')
}
$desktop = Join-Path $PSScriptRoot 'ModularDiff.Desktop/ModularDiff.Desktop.csproj'
$cli = Join-Path $repo 'shared/ModularDiff.Cli/ModularDiff.Cli.csproj'
foreach ($entry in @(@($desktop, 'app'), @($cli, 'cli'))) {
    $arguments = @('publish', $entry[0], '-c', 'Release', '-o', (Join-Path $output $entry[1]), '--nologo', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false')
    if ($Runtime) { $arguments += @('-r', $Runtime, '--self-contained', 'true') }
    else { $arguments += @('--self-contained', 'false') }
    Run-Dotnet $arguments
}
if (-not $SkipTests) {
    Run-Dotnet @('run', '--project', (Join-Path $PSScriptRoot 'ModularDiff.Desktop.Tests'), '-c', 'Release', '--', (Join-Path $output 'plugins'))
}
Write-Output "Ready: $output"
