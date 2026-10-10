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

echo "== done"
echo "  app: $APP"
echo "  cli: $OUT/cli/codeusage"
