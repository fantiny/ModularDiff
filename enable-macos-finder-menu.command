#!/bin/zsh
# ModularDiff Finder 一级菜单 · 激活/公证脚本
# 现状(2026-11-08 验证):本机 macOS 27 的 pluginkit 仅收录「已公证(Notarized)」的
# FinderSync 扩展;Apple Development(免费)签名已验证不够。本脚本分两种情况工作。
APP="$HOME/Applications/ModularDiff.app"
APPEX="$APP/Contents/PlugIns/ModularDiffFinderSync.appex"

DEVID=$(security find-identity -v -p codesigning 2>/dev/null | grep "Developer ID Application" | head -1 | sed -E 's/.*"([^"]+)".*/\1/')
if [ -z "$DEVID" ]; then
  echo "未检测到 Developer ID Application 证书。"
  echo ""
  echo "当前免费方案下,一级菜单无法注册(macOS 27 要求公证)。可继续使用:"
  echo "  右键 → 快速操作 → 「ModularDiff 设为左侧」+「用 ModularDiff 比较」"
  echo ""
  echo "若将来开通付费 Apple Developer 账号(¥688/年):"
  echo "  1. 申请 Developer ID Application 证书并安装到钥匙串"
  echo "  2. 运行 xcrun notarytool store-credentials MD-NOTARY --apple-id <你的ID> --team-id <团队ID>"
  echo "  3. 重新运行本脚本,它会完成签名+公证+装订+注册全流程"
  read "?按回车退出..."
  exit 0
fi

echo "✓ 使用 Developer ID: $DEVID"
echo "→ 签名扩展与宿主..."
codesign --force --sign "$DEVID" --timestamp=none --options runtime "$APPEX" || exit 1
codesign --force --sign "$DEVID" --timestamp=none --options runtime "$APP" || exit 1

echo "→ 打包并提交公证(等待 Apple 审核,数分钟到一小时)..."
DITTO=/tmp/ModularDiff-notary.zip
ditto -c -k --keepParent "$APP" "$DITTO"
xcrun notarytool submit "$DITTO" --keychain-profile MD-NOTARY --wait || exit 1
echo "→ 装订公证票据..."
xcrun stapler staple "$APP" || exit 1
spctl -a -vv "$APP"

echo "→ 注册 FinderSync 扩展..."
killall pluginkit 2>/dev/null; sleep 1
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP"
sleep 2
pluginkit -a "$APPEX" 2>/dev/null || true
sleep 1
echo "===== FinderSync 注册状态 ====="
pluginkit -m -p com.apple.FinderSync
echo "如出现 com.modulardiff 条目即成功;若 Finder 右键暂未出现,请到"
echo "系统设置 → 通用 → 登录项与扩展 → ModularDiff → 开启「访达」扩展。"
read "?按回车退出..."
