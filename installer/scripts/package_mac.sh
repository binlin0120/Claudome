#!/usr/bin/env bash
set -euo pipefail

# Packages Claudome.Mac as a native macOS .app bundle (.zip + SHA256SUMS.txt)
# for arm64 (Apple Silicon) and/or x64 (Intel Mac).
# Requires: macOS host (sips, iconutil, ditto), Go >= 1.26, .NET 10 SDK.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSION="${1:-0.4.8}"
ARCH_LIST="${2:-arm64 x64}"
OUT_DIR="$ROOT_DIR/installer/artifacts/mac-release"

mkdir -p "$OUT_DIR"

ICON_SRC="$ROOT_DIR/installer/ClaudeSetup/Brand/Claudome.png"
ICONSET_DIR="$OUT_DIR/Claudome.iconset"
ICNS_FILE="$OUT_DIR/Claudome.icns"

rm -rf "$ICONSET_DIR"
mkdir -p "$ICONSET_DIR"
sips -z 16 16     "$ICON_SRC" --out "$ICONSET_DIR/icon_16x16.png" >/dev/null
sips -z 32 32     "$ICON_SRC" --out "$ICONSET_DIR/icon_16x16@2x.png" >/dev/null
sips -z 32 32     "$ICON_SRC" --out "$ICONSET_DIR/icon_32x32.png" >/dev/null
sips -z 64 64     "$ICON_SRC" --out "$ICONSET_DIR/icon_32x32@2x.png" >/dev/null
sips -z 128 128   "$ICON_SRC" --out "$ICONSET_DIR/icon_128x128.png" >/dev/null
sips -z 256 256   "$ICON_SRC" --out "$ICONSET_DIR/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "$ICON_SRC" --out "$ICONSET_DIR/icon_256x256.png" >/dev/null
sips -z 512 512   "$ICON_SRC" --out "$ICONSET_DIR/icon_256x256@2x.png" >/dev/null
sips -z 512 512   "$ICON_SRC" --out "$ICONSET_DIR/icon_512x512.png" >/dev/null
sips -z 1024 1024 "$ICON_SRC" --out "$ICONSET_DIR/icon_512x512@2x.png" >/dev/null
iconutil -c icns "$ICONSET_DIR" -o "$ICNS_FILE"
rm -rf "$ICONSET_DIR"

: > "$OUT_DIR/SHA256SUMS-mac.txt"

for ARCH in $ARCH_LIST; do
  if [[ "$ARCH" == "arm64" ]]; then
    GOARCH="arm64"
    RID="osx-arm64"
  elif [[ "$ARCH" == "x64" ]]; then
    GOARCH="amd64"
    RID="osx-x64"
  else
    echo "Unsupported Mac architecture: $ARCH" >&2
    exit 1
  fi

  PKG_NAME="Claudome_${VERSION}_macOS-${ARCH}"
  STAGE_DIR="$OUT_DIR/$PKG_NAME"
  APP_DIR="$STAGE_DIR/Claudome.app"
  PUB_DIR="$OUT_DIR/publish-${RID}"

  rm -rf "$STAGE_DIR" "$PUB_DIR"
  mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

  (cd "$ROOT_DIR/installer/network-gateway" && GOOS=darwin GOARCH="$GOARCH" go build -trimpath -buildvcs=false -ldflags="-s -w" -o "$APP_DIR/Contents/MacOS/ClaudeNetworkGateway" .)

  dotnet publish "$ROOT_DIR/installer/Claudome.Mac/Claudome.Mac.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:Version="$VERSION" \
    --nologo \
    -o "$PUB_DIR"

  cp -R "$PUB_DIR/"* "$APP_DIR/Contents/MacOS/"
  chmod +x "$APP_DIR/Contents/MacOS/Claudome.Mac" "$APP_DIR/Contents/MacOS/ClaudeNetworkGateway"
  cp "$ICNS_FILE" "$APP_DIR/Contents/Resources/Claudome.icns"

  cat > "$APP_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>zh_CN</string>
  <key>CFBundleDisplayName</key>
  <string>Claudome</string>
  <key>CFBundleExecutable</key>
  <string>Claudome.Mac</string>
  <key>CFBundleIconFile</key>
  <string>Claudome</string>
  <key>CFBundleIdentifier</key>
  <string>com.claudome.app</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Claudome</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION}</string>
  <key>CFBundleVersion</key>
  <string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF

  cp "$ROOT_DIR/LICENSE" "$STAGE_DIR/PROJECT-LICENSE.txt"
  cp "$ROOT_DIR/installer/快速开始-Mac.txt" "$STAGE_DIR/快速开始-Mac.txt"
  if [[ -f "$ROOT_DIR/installer/artifacts/Gateway-THIRD-PARTY-NOTICES.txt" ]]; then
    cp "$ROOT_DIR/installer/artifacts/Gateway-THIRD-PARTY-NOTICES.txt" "$STAGE_DIR/Gateway-THIRD-PARTY-NOTICES.txt"
  fi

  ZIP_PATH="$OUT_DIR/${PKG_NAME}.zip"
  rm -f "$ZIP_PATH"
  ditto -c -k --sequesterRsrc --keepParent "$STAGE_DIR" "$ZIP_PATH"

  (cd "$OUT_DIR" && shasum -a 256 "${PKG_NAME}.zip" >> "$OUT_DIR/SHA256SUMS-mac.txt")
  rm -rf "$PUB_DIR"
done

echo "Packaged macOS bundles in $OUT_DIR:"
cat "$OUT_DIR/SHA256SUMS-mac.txt"
