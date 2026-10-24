#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: redeploy-compute.sh
# 主要职责: 在本机完成 ComputeNode 的 linux-arm64 自包含发布并部署到 odin
#
# 流程: dotnet publish → scp 到 /root/daohua-compute.new → 原子换目录 →
#       杀旧进程 → Termux 层守护 10s 内自动拉起新版本。
# 注意: 运行中的二进制不可覆写（ETXTBSY），必须换目录而非就地覆盖。
# ==============================================================================
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
OUT_DIR="${OUT_DIR:-/tmp/daohua-compute-odin}"
REMOTE_HOST="${SSH_HOST:-odin}"

printf '==> 发布 linux-arm64 自包含构建\n'
rm -rf "$OUT_DIR"
dotnet publish "$ROOT_DIR/src/ComputeNode" -c Release -r linux-arm64 --self-contained -o "$OUT_DIR" --nologo

printf '==> 上传到 odin:/root/daohua-compute.new\n'
ssh -o BatchMode=yes "$REMOTE_HOST" 'rm -rf /root/daohua-compute.new && mkdir -p /root/daohua-compute.new'
scp -q -r "$OUT_DIR"/* "$REMOTE_HOST":/root/daohua-compute.new/

printf '==> 原子换目录并触发守护重启\n'
ssh -o BatchMode=yes "$REMOTE_HOST" '
  set -Eeuo pipefail
  rm -rf /root/daohua-compute.old
  [[ -d /root/daohua-compute ]] && mv /root/daohua-compute /root/daohua-compute.old
  mv /root/daohua-compute.new /root/daohua-compute
  pkill -f "DaoHua[.]ComputeNode" || true
'

printf '==> 等待守护拉起并验证\n'
sleep 15
ssh -o BatchMode=yes "$REMOTE_HOST" 'curl -fsS http://127.0.0.1:8090/healthz' && printf '\n部署完成。\n'
