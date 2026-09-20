#!/usr/bin/env bash
# 发布 Linux CLI 压缩包：dist/glacc-auto-v<版本>-linux-<arch>.tar.gz
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-}"
if [[ -z "$VERSION" ]]; then
  VERSION="$(grep -oP '(?<=<Version>)[^<]+' "$ROOT/src/GlaccAuto.Cli/GlaccAuto.Cli.csproj" | head -n1)"
fi
RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -m)" in
    x86_64|amd64) RID=linux-x64 ;;
    aarch64|arm64) RID=linux-arm64 ;;
    *) echo "用法: $0 linux-x64|linux-arm64" >&2; exit 1 ;;
  esac
fi
OUT="$ROOT/dist/glacc-auto-v${VERSION}-${RID}"
rm -rf "$OUT"
mkdir -p "$OUT"
dotnet publish "$ROOT/src/GlaccAuto.Cli/GlaccAuto.Cli.csproj" \
  -c Release -r "$RID" --self-contained true -p:Version="$VERSION" -o "$OUT"
cp "$ROOT/LICENSE" "$ROOT/NOTICE" "$ROOT/DISCLAIMER.md" "$OUT/"
# 保证根目录有 tls-client.so
if [[ ! -f "$OUT/tls-client.so" ]]; then
  so="$(find "$OUT" -name 'tls-client.so' | head -n1 || true)"
  [[ -n "$so" ]] && cp "$so" "$OUT/tls-client.so"
fi
mkdir -p "$ROOT/dist"
tar -C "$(dirname "$OUT")" -czf "$ROOT/dist/glacc-auto-v${VERSION}-${RID}.tar.gz" "$(basename "$OUT")"
echo "打包完成：$ROOT/dist/glacc-auto-v${VERSION}-${RID}.tar.gz"
