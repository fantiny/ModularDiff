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

# ─── merge 命令:退出码契约 / --output 同输入保护 / 冲突标记落盘(2026-11 审查补) ───
$mergeRoot = Join-Path $repoRoot ('artifacts/merge-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $mergeRoot -Force | Out-Null
try {
    $base = Join-Path $mergeRoot 'base.txt'; $localF = Join-Path $mergeRoot 'local.txt'; $remoteF = Join-Path $mergeRoot 'remote.txt'
    Set-Content -Path $base -Value "common`nkeep`n" -NoNewline
    Set-Content -Path $localF -Value "common`nmine`n" -NoNewline
    Set-Content -Path $remoteF -Value "common`ntheirs`n" -NoNewline
    $cleanBase = Join-Path $mergeRoot 'cb.txt'; $cleanLocal = Join-Path $mergeRoot 'cl.txt'; $cleanRemote = Join-Path $mergeRoot 'cr.txt'
    Set-Content -Path $cleanBase -Value "a`nb`n" -NoNewline
    Set-Content -Path $cleanLocal -Value "A`nb`n" -NoNewline
    Set-Content -Path $cleanRemote -Value "a`nB`n" -NoNewline
    $null = Invoke-Case 'Merge clean (disjoint edits)' 0 @('merge', $cleanBase, $cleanLocal, $cleanRemote)
    $conflictOut = Join-Path $mergeRoot 'conflict-merged.txt'
    $conflictText = Invoke-Case 'Merge conflicts exit 1' 1 @('merge', $base, $localF, $remoteF, '--output', $conflictOut)
    $mergedPayload = Get-Content -Path $conflictOut -Raw
    if ($mergedPayload -notmatch '<<<<<<<' -or $mergedPayload -notmatch '>>>>>>>') { throw "conflict markers missing: $mergedPayload" }
    Write-Output 'PASS conflict markers written to --output'
    $checks++
    $null = Invoke-Case 'Merge output never overwrites inputs' 2 @('merge', $base, $localF, $remoteF, '--output', $localF)
    if ((Get-Content -Path $localF -Raw) -ne "common`nmine`n") { throw 'merge --output overwrote LEFT input!' }
    $null = Invoke-Case 'Merge missing input' 2 @('merge', (Join-Path $mergeRoot 'nope.txt'), $localF, $remoteF)
    Write-Output 'PASS merge input guard left LEFT untouched'
    $checks++

    # git setup:用 GIT_CONFIG_GLOBAL 把"全局配置"隔离到临时文件,断言键与 trustExitCode;坏 --gui 必须失败。
    $gitConfigPath = Join-Path $mergeRoot 'gitconfig-test'
    $env:GIT_CONFIG_GLOBAL = $gitConfigPath
    try {
        $guiStub = Join-Path $mergeRoot 'ModularDiff.Gui.exe'
        Set-Content -Path $guiStub -Value 'stub'
        $null = Invoke-Case 'git setup writes mergetool config' 0 @('git', 'setup', '--global', '--gui', $guiStub)
        $configured = & git config --global --get-regexp '^(diff|merge)\.' 2>$null
        if (-not $configured) { throw 'git config file has no diff/merge keys' }
        $trustExit = & git config --global --get 'mergetool.modulardiff.trustExitCode'
        if ($trustExit -ne 'true') { throw "trustExitCode = $trustExit" }
        $mergeCmd = & git config --global --get 'mergetool.modulardiff.cmd'
        if ($mergeCmd -notmatch '--git-merge' -or $mergeCmd -notmatch '\$MERGED') { throw "mergetool cmd: $mergeCmd" }
        Write-Output 'PASS git setup: difftool+mergetool keys, trustExitCode, --git-merge contract'
        $checks++
        $null = Invoke-Case 'git setup rejects missing --gui path' 2 @('git', 'setup', '--global', '--gui', (Join-Path $mergeRoot 'missing.exe'))
    }
    finally { Remove-Item Env:GIT_CONFIG_GLOBAL -ErrorAction SilentlyContinue }
}
finally { Remove-Item -LiteralPath $mergeRoot -Recurse -Force -ErrorAction SilentlyContinue }
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
    $filterArguments = @('"' + $guiDll + '"', '--filter-smoke-test')
    $process = Start-Process -FilePath $dotnetExe -ArgumentList $filterArguments -WindowStyle Hidden -PassThru -Wait
    $filterReceipt = Join-Path $repoRoot 'artifacts/gui/filter-smoke-result.json'
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $filterReceipt)) { throw 'Filter-as-you-type smoke failed; see artifacts/gui/filter-smoke-error.txt' }
    Write-Output 'PASS filter-as-you-type: pooled folder rows, scroll, keyboard, selection, header, keystroke budget'
    $checks++
}
Write-Output "$checks end-to-end checks passed."
