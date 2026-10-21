#!/usr/bin/env bash
set -Eeuo pipefail

# 基准专用门禁：独立发布生产宿主（优化版 ECS）并核验产物 provenance。
# 该脚本不启动服务，也不会清理正在运行的产物（运行中产物的守卫在 bench-service.sh
# 的 artifact_process_ids 里，按 artifact 目录的进程 cwd 比对，与本脚本无关）。
# Ecs.Tests 回归不在此重复执行：合并必跑的 scripts/verify.sh 已跑同一 csproj 与配置。
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo '==> Bench Release 构建'
dotnet build src/Bench/Bench.csproj -c Release --no-restore --disable-build-servers --nologo

variant="ecs"
echo "==> 独立发布：$variant"
scripts/bench-service.sh --variant "$variant" --build --no-restore >/tmp/daohua-bench-"$variant"-build.log
dotnet src/Bench/bin/Release/net10.0/DaoHua.Bench.dll provenance \
  --variant "$variant" --output "artifacts/runtime/$variant" \
  >"artifacts/runtime/$variant/provenance.json"

echo '基准构建/provenance 门禁通过；真实服务 workload 与长稳结果需由 bench-matrix.sh 另行生成。'
