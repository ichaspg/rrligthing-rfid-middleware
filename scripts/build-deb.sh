#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${1:-0.1.0}"
stage_root="$(mktemp -d)"
trap 'rm -rf "$stage_root"' EXIT
stage="$stage_root/rfid-middleware_${version}_amd64"
command -v patchelf >/dev/null 2>&1 || { echo "patchelf is required to package the vendor SDK safely." >&2; exit 2; }
install -d -m 0755 "$stage/DEBIAN" "$stage/usr/lib/rfid-middleware" "$stage/etc/rfid-middleware" "$stage/lib/systemd/system"
dotnet publish "$root/src/RfidMiddleware.Host/RfidMiddleware.Host.csproj" -c Release -r linux-x64 --self-contained true -p:Version="$version" -o "$stage/usr/lib/rfid-middleware"
patchelf --set-rpath '$ORIGIN' "$stage/usr/lib/rfid-middleware/native/librfid.so"
cp "$root/packaging/appsettings.production.json" "$stage/etc/rfid-middleware/appsettings.json"
cp "$root/packaging/environment.example" "$stage/etc/rfid-middleware/environment.example"
cp "$root/packaging/rfid-middleware.service" "$stage/lib/systemd/system/rfid-middleware.service"
cp "$root/packaging/postinst" "$stage/DEBIAN/postinst"
cp "$root/packaging/prerm" "$stage/DEBIAN/prerm"
cat > "$stage/DEBIAN/control" <<EOF
Package: rfid-middleware
Version: $version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: WIT
Description: RFID transport validation edge middleware
EOF
find "$stage" -type d -exec chmod 0755 {} +
find "$stage" -type f -exec chmod 0644 {} +
chmod 0755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/prerm" "$stage/usr/lib/rfid-middleware/rfid-middleware"
mkdir -p "$root/artifacts"
dpkg-deb --build --root-owner-group "$stage" "$root/artifacts/rfid-middleware_${version}_amd64.deb"
