#!/bin/zsh
# codeusagemonit macOS 打包脚本：发布自包含构建 + 生成 .app 包 + CLI。
# 用法：./build-macos.sh   （产出在 dist/osx-arm64/）
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:$PATH"

RID=osx-arm64
OUT=dist/$RID

echo "== publish desktop & cli ($RID, self-contained)"
dotnet publish src/CodeUsageMonit.Desktop -c Release -r $RID --self-contained true -o $OUT/app
dotnet publish src/CodeUsageMonit.Cli    -c Release -r $RID --self-contained true -o $OUT/cli

echo "== app icon"
rm -rf /tmp/codeusagemonit.iconset
mkdir -p /tmp/codeusagemonit.iconset
swift packaging/makeicon.swift /tmp/codeusagemonit.iconset
iconutil -c icns /tmp/codeusagemonit.iconset -o $OUT/app.icns

echo "== bundle CodeUsageMonit.app"
APP=$OUT/CodeUsageMonit.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R $OUT/app/ "$APP/Contents/MacOS/"
cp packaging/Info.plist "$APP/Contents/Info.plist"
cp $OUT/app.icns "$APP/Contents/Resources/app.icns"

echo "== ad-hoc 签名（未签名/无签名属性的 .app 可能被 App Translocation 挪进只读随机目录，导致数据目录不可写）"
# 失败不阻断打包，只警告（机器上没有 codesign 或证书链问题的场合仍可手动运行）。
codesign --force --deep --sign - "$APP" || echo "warn: ad-hoc codesign 失败，继续打包"

echo "== dmg 安装镜像（macOS 习惯的安装形态：拖进 Applications）"
# hdiutil 是系统自带工具，无第三方依赖；UDZO 压缩。暂存目录里放 .app + Applications
# 软链，挂载后用户把图标拖过去即完成安装。路径均为脚本内固定常量，无外部输入。
DMG=$OUT/CodeUsageMonit-macos-arm64.dmg
STAGE=$OUT/dmg-staging
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "CodeUsageMonit" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

echo "== cli zip"
rm -f $OUT/codeusage-cli-macos-arm64.zip
ditto -c -k --sequesterRsrc --keepParent $OUT/cli $OUT/codeusage-cli-macos-arm64.zip

echo "== done"
echo "  app: $APP"
echo "  dmg: $DMG"
echo "  cli: $OUT/cli/codeusage（zip: $OUT/codeusage-cli-macos-arm64.zip）"
