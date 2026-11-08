param([switch]$Gui)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($command) { $command.Source } else { Join-Path $env:LOCALAPPDATA 'ModularDiff/dotnet/dotnet.exe' }
$cli = Join-Path $repoRoot 'artifacts/cli/ModularDiff.Cli.dll'
$left = Join-Path $repoRoot 'samples/left/Calculator.cs'
$right = Join-Path $repoRoot 'samples/right/Calculator.cs'
$checks = 0
function Invoke-Case([string]$Name, [int]$Expected, [string[]]$Arguments) {
    $output = & $dotnetExe $cli @Arguments 2>&1
    if ($LASTEXITCODE -ne $Expected) { throw "$Name : expected $Expected, received $LASTEXITCODE`n$output" }
    Write-Output "PASS $Name (exit $Expected)"
    $script:checks++
    return ($output -join "`n")
}
$null = Invoke-Case 'Plugin discovery' 0 @('plugins')
$null = Invoke-Case 'Equal files' 0 @('compare', $left, $left)
$null = Invoke-Case 'Changed files' 1 @('compare', $left, $right)
$json = Invoke-Case 'JSON / Patience' 1 @('compare', $left, $right, '--algorithm', 'patience', '--format', 'json')
# Invoke-Case writes a PASS line before its return value; select the JSON payload.
$payload = @($json)[-1] | ConvertFrom-Json
if (-not $payload.HasDifferences -or $payload.AlgorithmId -ne 'patience') { throw 'Unexpected JSON result' }
$null = Invoke-Case 'Unknown algorithm' 2 @('compare', $left, $right, '--algorithm', 'missing')
$null = Invoke-Case 'Unknown option' 2 @('compare', $left, $right, '--typo')
$null = Invoke-Case 'Protect input from report overwrite' 2 @('compare', $left, $right, '--output', $left)
$null = Invoke-Case 'Recursive directories' 1 @('compare', (Join-Path $repoRoot 'samples/left'), (Join-Path $repoRoot 'samples/right'))

# Add an independently built package to a separate plugin root; neither host is rebuilt.
$extensionRoot = Join-Path $repoRoot ('artifacts/extension-smoke-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $extensionRoot 'plugins'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'artifacts/plugins/*') -Destination $packageRoot -Recurse
& $dotnetExe build (Join-Path $repoRoot 'examples/ModularDiff.Algorithms.Position') -c Release -o (Join-Path $packageRoot 'Position') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Extension build failed' }
$extensionJson = Invoke-Case 'New plugin without rebuilding host' 1 @('compare', $left, $right, '--plugins', $packageRoot, '--algorithm', 'position', '--format', 'json')
$extensionResult = @($extensionJson)[-1] | ConvertFrom-Json
if ($extensionResult.AlgorithmId -ne 'position') { throw 'Extension not used' }
if ($Gui) {
    $guiDll = Join-Path $repoRoot 'artifacts/gui/ModularDiff.Gui.dll'
    $imagePath = Join-Path $repoRoot 'artifacts/gui-smoke.png'
    $arguments = @('"' + $guiDll + '"', '--smoke-test', '--left', '"' + $left + '"', '--right', '"' + $right + '"', '--screenshot', '"' + $imagePath + '"')
    $process = Start-Process -FilePath $dotnetExe -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $imagePath)) { throw 'GUI smoke failed' }
    Write-Output 'PASS WPF window / real comparison / render'
    $checks++
    $folderImage = Join-Path $repoRoot 'artifacts/folder-compare.png'
    $folderLeft = Join-Path $repoRoot 'samples/workspace/left'
    $folderRight = Join-Path $repoRoot 'samples/workspace/right'
    $folderArguments = @('"' + $guiDll + '"', '--smoke-test', '--left', '"' + $folderLeft + '"', '--right', '"' + $folderRight + '"', '--screenshot', '"' + $folderImage + '"')
    $process = Start-Process -FilePath $dotnetExe -ArgumentList $folderArguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw 'Folder-to-text UI smoke failed' }
    $receipt = Get-Content (Join-Path $repoRoot 'artifacts/gui/smoke-result.json') -Raw | ConvertFrom-Json
    if (-not $receipt.success -or $receipt.folderRows -lt 12 -or $receipt.hunks -ne 4) { throw 'Folder UI receipt mismatch' }
    Write-Output 'PASS paired trees / collapse / filters / open text / return / scroll / hunk navigation / context'
    $checks++
    $editImage = Join-Path $repoRoot 'artifacts/editing.png'
    $editArguments = @('"' + $guiDll + '"', '--edit-smoke-test', '--screenshot', '"' + $editImage + '"')
    $process = Start-Process -FilePath $dotnetExe -ArgumentList $editArguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw 'Editing UI smoke failed; see artifacts/gui/edit-smoke-error.txt' }
    Write-Output 'PASS both-side editing / block merge / undo / redo / save / empty-buffer input'
    $checks++
    $syncImage = Join-Path $repoRoot 'artifacts/sync.png'
    $syncArguments = @('"' + $guiDll + '"', '--sync-smoke-test', '--screenshot', '"' + $syncImage + '"')
    $process = Start-Process -FilePath $dotnetExe -ArgumentList $syncArguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw 'Sync UI smoke failed; see artifacts/gui/sync-smoke-error.txt' }
    Write-Output 'PASS directory multiselection / batch / preview / backup / journal / history / recovery / refresh'
    $checks++
}
Write-Output "$checks end-to-end checks passed."
