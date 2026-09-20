#!/usr/bin/env bash
# 安装 glacc-auto Linux CLI，并可选启用每日定时领取。
# 用法：
#   ./scripts/install-linux.sh              # 发布并安装到 ~/.local/bin
#   ./scripts/install-linux.sh --time 08:30 # 安装后启用 systemd 用户定时器
#   PREFIX=/opt/glacc-auto ./scripts/install-linux.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PREFIX="${PREFIX:-$HOME/.local}"
BIN_DIR="$PREFIX/bin"
SHARE_DIR="$PREFIX/share/glacc-auto"
TIME=""
RID=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --time) TIME="${2:-}"; shift 2 ;;
    --rid) RID="${2:-}"; shift 2 ;;
    -h|--help)
      sed -n '2,8p' "$0"
      exit 0
      ;;
    *) echo "未知参数：$1" >&2; exit 1 ;;
  esac
done

if [[ -z "$RID" ]]; then
  arch="$(uname -m)"
  case "$arch" in
    x86_64|amd64) RID=linux-x64 ;;
    aarch64|arm64) RID=linux-arm64 ;;
    *) echo "不支持的架构：$arch（请用 --rid linux-x64|linux-arm64）" >&2; exit 1 ;;
  esac
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "需要 .NET 10 SDK：https://dotnet.microsoft.com/download" >&2
  exit 1
fi

PUBLISH="$ROOT/dist/linux/$RID"
mkdir -p "$PUBLISH" "$BIN_DIR" "$SHARE_DIR"

echo "==> 发布 GlaccAuto.Cli ($RID)"
dotnet publish "$ROOT/src/GlaccAuto.Cli/GlaccAuto.Cli.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false \
  -o "$PUBLISH"

install -m 0755 "$PUBLISH/glacc-auto" "$SHARE_DIR/glacc-auto"
# 原生库与附属文件一并安装
find "$PUBLISH" -maxdepth 1 -type f ! -name 'glacc-auto' -exec install -m 0644 {} "$SHARE_DIR/" \;
if [[ -d "$PUBLISH/runtimes" ]]; then
  rm -rf "$SHARE_DIR/runtimes"
  cp -a "$PUBLISH/runtimes" "$SHARE_DIR/"
fi

ln -sfn "$SHARE_DIR/glacc-auto" "$BIN_DIR/glacc-auto"

# 保证 tls-client.so 在可执行文件同目录
if [[ ! -f "$SHARE_DIR/tls-client.so" ]]; then
  so="$(find "$SHARE_DIR" -name 'tls-client.so' | head -n1 || true)"
  if [[ -n "$so" ]]; then
    ln -sfn "$so" "$SHARE_DIR/tls-client.so"
  else
    echo "警告：未找到 tls-client.so，领取时会因 TLS 指纹库缺失失败" >&2
  fi
fi

echo "==> 已安装 $BIN_DIR/glacc-auto"
echo "    数据目录：\${XDG_CONFIG_HOME:-\$HOME/.config}/glacc-auto"
echo
echo "下一步："
echo "  1) 把 $BIN_DIR 加入 PATH（若尚未）"
echo "  2) glacc-auto login"
echo "  3) glacc-auto claim            # 立刻试领"
echo "  4) glacc-auto schedule on 08:00  # 每天自动领取"

if [[ -n "$TIME" ]]; then
  "$BIN_DIR/glacc-auto" schedule on "$TIME"
fi
