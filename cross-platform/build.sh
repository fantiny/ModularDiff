#!/usr/bin/env bash
set -euo pipefail
here="$(cd -- "$(dirname -- "$0")" && pwd)"
repo="$(dirname "$here")"
rid="${1:-dev}"
case "$rid" in dev|osx-arm64|osx-x64|linux-x64|linux-arm64|win-x64) ;; *) echo "Unsupported RID: $rid" >&2; exit 2;; esac
out="$here/artifacts/$rid"
for project in "$repo"/shared/plugins/*/*.csproj; do
  name="$(basename "${project%.csproj}")"
  dotnet build "$project" -c Release -o "$out/plugins/$name" --nologo
done
args=(--self-contained false)
if [[ "$rid" != dev ]]; then args=(-r "$rid" --self-contained true); fi
dotnet publish "$here/ModularDiff.Desktop" -c Release -o "$out/app" "${args[@]}" -p:PublishTrimmed=false -p:PublishSingleFile=false
dotnet publish "$repo/shared/ModularDiff.Cli" -c Release -o "$out/cli" "${args[@]}" -p:PublishTrimmed=false -p:PublishSingleFile=false
dotnet run --project "$here/ModularDiff.Desktop.Tests" -c Release -- "$out/plugins"
if [[ "$rid" == osx-* ]]; then
  bundle="$out/ModularDiff.app/Contents"
  mkdir -p "$bundle/MacOS" "$bundle/plugins"
  cp -R "$out/app/." "$bundle/MacOS/"
  cp -R "$out/plugins/." "$bundle/plugins/"
  cp "$here/Info.plist" "$bundle/Info.plist"
  chmod +x "$bundle/MacOS/ModularDiff.Desktop"
  echo "Unsigned application bundle: $out/ModularDiff.app"
fi
printf 'Ready: %s\n' "$out"
