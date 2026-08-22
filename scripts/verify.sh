#!/usr/bin/env bash

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
dotnet build DaoHua.slnx --configuration Release --disable-build-servers --nologo

printf '==> 领域测试\n'
dotnet test tests/DaoHua.Domain.Tests/DaoHua.Domain.Tests.csproj \
  --configuration Release \
  --no-build \
  --disable-build-servers \
  --nologo

printf '==> 游戏数据校验\n'
dotnet run --project src/DaoHua.DataTool/DaoHua.DataTool.csproj \
  --configuration Release \
  --no-build \
  -- \
  validate config/game-data

printf '==> 2c4g/40g 容量目标门禁\n'
dotnet run --project src/DaoHua.LoadTests/DaoHua.LoadTests.csproj \
  --configuration Release \
  --no-build \
  -- \
  --scenario capacity-target

printf '==> 前端类型检查与构建\n'
npm --prefix frontend run build

printf '全部验证完成。\n'
