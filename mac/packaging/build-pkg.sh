#!/bin/bash
# 파란전화기 macOS 설치 파일 만들기 (macOS 에서 실행)
#   mac/packaging/build-pkg.sh 2.5.0
# 결과: dist/BluePhone-mac.pkg (+ .sha256)  — Apple Silicon · Intel 판을 함께 담고 설치하는 Mac 에 맞는 것만 설치
#
# 선택 (없으면 ad-hoc 서명 · 서명 없는 설치 파일):
#   MAC_APP_IDENTITY        "Developer ID Application: …"   앱 서명
#   MAC_INSTALLER_IDENTITY  "Developer ID Installer: …"     설치 파일 서명
#   APPLE_ID / APPLE_TEAM_ID / APPLE_APP_PASSWORD           공증(notarize)
set -euo pipefail

VERSION="${1:?버전을 지정하세요. 예) build-pkg.sh 2.5.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="$ROOT/dist"
WORK="$ROOT/mac/obj/pkg"
APP_NAME="파란전화기.app"
PKG="$OUT/BluePhone-mac.pkg"

rm -rf "$WORK"
mkdir -p "$WORK" "$OUT"

# ── 아이콘 (.icns) ──
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z "$s" "$s" "$HERE/AppIcon-1024.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2))
  sips -z "$d" "$d" "$HERE/AppIcon-1024.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$WORK/AppIcon.icns"

sign_app() {
  # Avalonia 배포 안내대로 Contents/MacOS 안의 파일을 하나씩 서명한 뒤 번들을 서명한다
  local app="$1" id="-" opts=()
  if [ -n "${MAC_APP_IDENTITY:-}" ]; then
    id="$MAC_APP_IDENTITY"
    opts=(--timestamp --options runtime --entitlements "$HERE/entitlements.plist")
  fi
  find "$app/Contents/MacOS" -type f -print0 | while IFS= read -r -d '' f; do
    codesign --force ${opts[@]+"${opts[@]}"} -s "$id" "$f"
  done
  codesign --force ${opts[@]+"${opts[@]}"} -s "$id" "$app"
  codesign --verify --deep --strict --verbose=2 "$app" || echo "::warning::codesign --verify 경고 (계속 진행)"
}

chmod +x "$HERE/scripts/postinstall"

for ARCH in arm64 x64; do
  echo "== $ARCH =="
  PUB="$WORK/publish-$ARCH"
  dotnet publish "$ROOT/mac/BluePhone.Mac.csproj" -c Release -r "osx-$ARCH" --self-contained true \
    -p:Version="$VERSION" -p:UseAppHost=true -o "$PUB" -nologo

  STAGE="$WORK/stage-$ARCH"
  APP="$STAGE/$APP_NAME"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$PUB/." "$APP/Contents/MacOS/"
  sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
  plutil -lint "$APP/Contents/Info.plist"
  cp "$WORK/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
  printf 'APPL????' > "$APP/Contents/PkgInfo"
  chmod +x "$APP/Contents/MacOS/BluePhone"
  xattr -cr "$APP"
  sign_app "$APP"

  # 설치 위치를 /Applications 로 고정 (다른 곳으로 옮긴 사본을 덮어쓰지 않도록)
  pkgbuild --analyze --root "$STAGE" "$WORK/comp-$ARCH.plist"
  plutil -replace 0.BundleIsRelocatable -bool NO "$WORK/comp-$ARCH.plist"
  pkgbuild --root "$STAGE" --component-plist "$WORK/comp-$ARCH.plist" \
    --identifier "com.bluephone.messenger.$ARCH" --version "$VERSION" \
    --install-location /Applications --scripts "$HERE/scripts" \
    "$WORK/BluePhone-$ARCH.pkg"
done

sed "s/__VERSION__/$VERSION/g" "$HERE/distribution.xml" > "$WORK/distribution.xml"
SIGN_ARGS=()
if [ -n "${MAC_INSTALLER_IDENTITY:-}" ]; then SIGN_ARGS=(--sign "$MAC_INSTALLER_IDENTITY" --timestamp); fi
rm -f "$PKG"
productbuild --distribution "$WORK/distribution.xml" --package-path "$WORK" --resources "$HERE/resources" \
  ${SIGN_ARGS[@]+"${SIGN_ARGS[@]}"} "$PKG"

if [ -n "${MAC_INSTALLER_IDENTITY:-}" ] && [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ]; then
  echo "== 공증 =="
  xcrun notarytool submit "$PKG" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait
  xcrun stapler staple "$PKG"
fi

( cd "$OUT" && shasum -a 256 BluePhone-mac.pkg > BluePhone-mac.pkg.sha256 && cat BluePhone-mac.pkg.sha256 )
pkgutil --check-signature "$PKG" || true
ls -l "$PKG"
