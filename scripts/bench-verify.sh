#!/usr/bin/env bash
set -Eeuo pipefail

# 基准专用门禁：独立发布生产宿主（优化版 ECS）并核验产物 provenance。
# 该脚本不启动服务，也不会清理正在运行的产物。
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

for port in 15140; do
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    echo "基准端口 $port 正在使用；为避免删除运行中产物，先停止对应基准进程。" >&2
    exit 5
  fi
done

echo '==> Bench Release 构建'
dotnet build src/Bench/Bench.csproj -c Release --no-restore --disable-build-servers --nologo

variant="ecs-optimized"
echo "==> 独立发布：$variant"
scripts/bench-service.sh --variant "$variant" --build --no-restore >/tmp/daohua-bench-"$variant"-build.log
dotnet src/Bench/bin/Release/net10.0/DaoHua.Bench.dll provenance \
  --variant "$variant" --output "artifacts/runtime/$variant" \
  >"artifacts/runtime/$variant/provenance.json"

echo '==> ECS 集成回归测试'
dotnet test tests/ServerEcs.Tests/ServerEcs.Tests.csproj -c Release --no-restore --disable-build-servers --nologo

echo '基准构建/provenance 门禁通过；真实服务 workload 与长稳结果需由 bench-matrix.sh 另行生成。'
