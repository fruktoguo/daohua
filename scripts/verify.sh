#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: verify.sh
# 主要职责: 全量代码构建、领域规则、数据配置与容量性能门禁本地全自动验证
#
# 核心验证流程:
#   1. 校验 YuoCore Git submodule 是否已正确初始化；
#   2. 执行 npm ci 严格安装前端锁定依赖；
#   3. 执行 .NET 解决方案 Release 配置完整构建（slnx）；
#   4. 运行领域规则与协议门禁测试（tests/Domain.Tests）；
#   5. 运行服务端测试（tests/Server.Tests）；
#   6. 运行游戏配置数据完整性校验工具（DataTool validate）；
#   7. 运行 2c4g/40g 生产容量与性能门禁压测（LoadTests capacity-target）；
#   8. 执行前端 TypeScript 静态类型检查与 Vite 生产打包构建。
#
# 使用场景:
#   - 提交代码前或 CI 阶段作为最小必要完整验证门禁；
#   - 任何涉及领域、协议、配置或前端重大改动后的回归测试。
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [[ ! -f vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj ]]; then
  printf 'YuoCore submodule 未初始化，请执行 git submodule update --init --recursive。\n' >&2
  exit 1
fi

printf '==> 安装前端锁定依赖\n'
npm --prefix frontend ci

printf '==> Release 构建\n'
dotnet build Game.slnx --configuration Release --disable-build-servers --nologo

printf '==> 领域测试\n'
dotnet test tests/Domain.Tests/Domain.Tests.csproj \
  --configuration Release \
  --no-build \
  --disable-build-servers \
  --nologo

printf '==> 服务端测试\n'
dotnet test tests/Server.Tests/Server.Tests.csproj \
  --configuration Release \
  --no-build \
  --disable-build-servers \
  --nologo

printf '==> 游戏数据校验\n'
dotnet run --project src/DataTool/DataTool.csproj \
  --configuration Release \
  --no-build \
  -- \
  validate config/game-data

printf '==> 2c4g/40g 容量目标门禁\n'
dotnet run --project src/LoadTests/LoadTests.csproj \
  --configuration Release \
  --no-build \
  -- \
  --scenario capacity-target

printf '==> 前端类型检查与构建\n'
npm --prefix frontend run build

printf '全部验证完成。\n'
