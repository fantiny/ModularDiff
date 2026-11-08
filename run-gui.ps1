$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:LOCALAPPDATA 'ModularDiff/dotnet/dotnet.exe' }
& $dotnetExe (Join-Path $PSScriptRoot 'artifacts/gui/ModularDiff.Gui.dll') @args
exit $LASTEXITCODE
