#!/usr/bin/env bash
# 门禁入口：工具链、restore+Release 构建、Domain/Ecs/Server 三套测试（自动供给
# 每轮一次性集成测试库，并按 trx 实际执行数硬校验非空）、DataTool 校验、前端单测与
# 构建。
#
# 默认（无参数）= 日常快速门禁：跳过 Compose 校验与 PostgresIntegration 集成测试。
# 交付/合并/发布前必须显式跑 --full。
#
# 双口径（私有模块拼装，真源 docs/backend/private-modules.md）：
# 存在兄弟仓库 ../DaoHua-Private 时自动升级为完整口径——后端追加私有扩展模块测试套、
# 前端挂载私有实现并追加其类型检查；缺失时保持纯开源口径，开源 CI 与自建环境天然可跑。
#
# 模式：
#   （无参数）   日常快速：后端三套测试（跳过 PG 集成）+ 前端 i18n/日志标记/单测/构建。
#   --full       全量交付门禁：追加 PostgresIntegration 集成测试与 Compose 配置校验。
#   --backend    只跑后端面（在该模式下叠加 --full 即含 PG 集成）。
#   --frontend   只跑前端面：依赖安装、i18n 词条、日志标记、前端单测、契约、类型检查与构建。
#   --serial     禁用前后端并发，强制纯串行执行。
#   -h, --help   打印本用法。
#
# 环境变量：
#   SKIP_NPM_CI=1          跳过前端依赖安装（即使锁定文件已变化）
#   VERIFY_DOCKER=1        追加生产镜像构建（需 --full）
#   VERIFY_RESULTS_DIR     测试 trx 落盘目录（默认 artifacts/verify-results）

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

usage() {
  printf '用法: scripts/verify.sh [--full | --backend | --frontend]\n\n'
  printf '  （无参数）   日常快速门禁：跳过 Compose 校验与 PostgresIntegration 集成测试\n'
  printf '  --full       全量交付门禁：含 PostgresIntegration 集成测试与 Compose 配置校验\n'
  printf '  --backend    仅后端：Release 构建、三套测试、DataTool 校验（可叠加 --full）\n'
  printf '  --frontend   仅前端：依赖安装、i18n、日志标记、单测、契约、类型检查与生产构建\n'
  printf '  --serial     禁用前后端并发，强制纯串行执行\n'
  printf '\n环境变量: SKIP_NPM_CI=1 跳过前端依赖安装；VERIFY_DOCKER=1 追加生产镜像构建\n'
  printf '%s\n' '  --with-postgres -- <命令>  仅给一个测试宿主供给新库' \
    '默认使用 postgres:18-alpine 专用容器、tmpfs、随机回环端口；退出即清理。' \
    '外部供给必须显式设置 VERIFY_POSTGRES_EXTERNAL=1；只接受库名以 daohua_test_ 开头的一次性回环库，不接管或清理外部库。' \
    'DAOHUA_MARKET_TEST_CONNECTION=Host=127.0.0.1;Port=<端口>;Database=daohua_test_<标识>;Username=<用户>;Password=<临时密码>' \
    '一个库只交给一个测试宿主；外部供给者完成后自行回收。'
}

require_docker() {
  if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
    printf '门禁失败：%s需要可用的 Docker。\n' "$1" >&2
    exit 1
  fi
}

VERIFY_PG_CONTAINER=""
cleanup_verify() {
  if [[ -n "$VERIFY_PG_CONTAINER" ]]; then
    docker rm -f -v "$VERIFY_PG_CONTAINER" >/dev/null ||
      printf '警告：无法回收本轮专用容器 %s。\n' "$VERIFY_PG_CONTAINER" >&2
  fi
  if [[ -n "${VERIFY_TMP:-}" ]]; then rm -rf "$VERIFY_TMP"; fi
}
trap cleanup_verify EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

supply_verify_postgres() {
  if [[ "${VERIFY_POSTGRES_EXTERNAL:-0}" == 1 ]]; then
    : "${DAOHUA_MARKET_TEST_CONNECTION:?缺少一次性测试库连接串}"
    export DAOHUA_MARKET_TEST_CONNECTION
    return
  fi
  require_docker '供给一次性 PostgreSQL '
  unset DAOHUA_MARKET_TEST_CONNECTION
  local run_id name password address ready=0
  run_id="$(openssl rand -hex 16 | tr -d '\r')"
  password="$(openssl rand -hex 24 | tr -d '\r')"
  [[ "$run_id" =~ ^[0-9a-f]{32}$ && "$password" =~ ^[0-9a-f]{48}$ ]] || exit 1
  name="daohua_test_$run_id"
  # 只记录新建的容器 ID，不搜索已有容器、挂载用户卷或复用端口。禁止 MSYS 改写 Linux 路径。
  VERIFY_PG_CONTAINER="$(POSTGRES_DB="$name" POSTGRES_USER="$name" POSTGRES_PASSWORD="$password" \
    MSYS_NO_PATHCONV=1 docker create --rm --label daohua.verify.postgres=true \
    --publish 127.0.0.1::5432 --tmpfs=/var/lib/postgresql:rw \
    --env POSTGRES_DB --env POSTGRES_USER --env POSTGRES_PASSWORD postgres:18-alpine | tr -d '\r')"
  [[ "$VERIFY_PG_CONTAINER" =~ ^[0-9a-f]{64}$ ]] || { VERIFY_PG_CONTAINER=""; exit 1; }
  docker start "$VERIFY_PG_CONTAINER" >/dev/null
  # 探测 TCP 而不是镜像初始化时临时打开的 Unix socket。
  for _ in {1..60}; do
    if docker exec "$VERIFY_PG_CONTAINER" sh -ceu '
      PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -Xq -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT 1"
    ' >/dev/null 2>&1; then
      ready=1
      break
    fi
    sleep 1
  done
  [[ "$ready" == 1 ]] || { printf '一次性测试库未就绪。\n' >&2; exit 1; }
  address="$(docker port "$VERIFY_PG_CONTAINER" 5432/tcp)"
  address="${address%$'\r'}"
  [[ "$address" =~ ^127\.0\.0\.1:[0-9]+$ ]] || { printf '一次性测试库未绑定到独占回环端口。\n' >&2; exit 1; }
  export DAOHUA_MARKET_TEST_CONNECTION="Host=127.0.0.1;Port=${address##*:};Database=$name;Username=$name;Password=$password"
}

if [[ "${1:-}" == --with-postgres ]]; then
  if [[ "${2:-}" != -- || $# -lt 3 ]]; then usage >&2; exit 2; fi
  shift 2
  supply_verify_postgres
  "$@"
  exit
fi

FULL=0
SCOPE="all"
PARALLEL=1
for arg in "$@"; do
  case "$arg" in
    --full) FULL=1 ;;
    --backend|--frontend)
      if [[ "$SCOPE" != all && "$SCOPE" != "${arg#--}" ]]; then
        printf '%s\n' '--backend 与 --frontend 不能同时指定。' >&2
        exit 2
      fi
      SCOPE="${arg#--}" ;;
    --serial) PARALLEL=0 ;;
    -h|--help) usage; exit 0 ;;
    *)
      printf '未知参数: %s\n\n' "$arg" >&2
      usage >&2
      exit 2
      ;;
  esac
done

RUN_BACKEND=1
RUN_FRONTEND=1
RUN_INTEGRATION=$FULL
RUN_COMPOSE=$FULL
case "$SCOPE" in
  backend) RUN_FRONTEND=0; RUN_COMPOSE=0 ;;
  frontend) RUN_BACKEND=0; RUN_INTEGRATION=0; RUN_COMPOSE=0 ;;
esac

RUN_IMAGE=0
if [[ "${VERIFY_DOCKER:-0}" == 1 && "$RUN_COMPOSE" -eq 1 ]]; then RUN_IMAGE=1; fi
if [[ "$RUN_COMPOSE" -eq 1 ]]; then
  require_docker '全量 Compose 校验'
  docker compose version >/dev/null || { printf '门禁失败：缺少 Docker Compose 插件。\n' >&2; exit 1; }
fi

if [[ ! -f vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj ]]; then
  printf 'YuoCore submodule 未初始化，请执行 git submodule update --init --recursive。\n' >&2
  exit 1
fi

# 私有扩展模块（反多开等）探测：存在即完整口径（MSBuild 源码注入 + 前端挂载），
# 缺失即开源口径；开源环境与公开 CI 无该仓库，门禁语义自动收敛。
PRIVATE_ROOT="$ROOT_DIR/../DaoHua-Private"
PRIVATE_AVAILABLE=0
if [[ -d "$PRIVATE_ROOT/server" ]]; then
  PRIVATE_AVAILABLE=1
  printf '%s\n' '==> 检测到兄弟私有仓库 DaoHua-Private：门禁升级为完整口径（反多开模块拼装）'
fi

printf '%s\n' '==> 检查真实工具链'
if [[ "$RUN_BACKEND" -eq 1 ]]; then dotnet --version; fi
node --version # 后端契约测试也调用 Node。
if [[ "$RUN_FRONTEND" -eq 1 ]]; then npm --version; fi

# 前端依赖只在 node_modules 缺失或锁定文件更新时重装：`npm ci` 整目录重装要付 7~40s，
# 且会与运行中的 dev server 争抢同一份 esbuild/rollup 原生二进制。
ensure_frontend_deps() {
  if [[ "${SKIP_NPM_CI:-0}" == "1" ]]; then
    printf '%s\n' '==> 跳过前端依赖安装 (SKIP_NPM_CI=1)'
    return 0
  fi
  local stamp="frontend/node_modules/.package-lock.json"
  if [[ ! -d frontend/node_modules || ! -f "$stamp" ]]; then
    printf '%s\n' '==> 安装前端锁定依赖（node_modules 缺失）'
  elif [[ frontend/package-lock.json -nt "$stamp" ]]; then
    printf '%s\n' '==> 安装前端锁定依赖（package-lock.json 已更新）'
  else
    printf '%s\n' '==> 前端依赖已是最新，跳过安装'
    return 0
  fi
  npm --prefix frontend ci --include=dev --ignore-scripts --no-audit --no-fund
}

# `dotnet test` 在筛选器零匹配、或测试宿主根本没启动（如 obj/ 里的 NuGet 包路径
# 指向其它机器导致 IsTestProject 为空）时返回 0 且只打印空白——门禁会「全绿但一个用例都没跑」。
# 因此所有测试套件统一落 trx 并校验实际执行数；下限只用于抓「近乎零」，不钉死具体用例数，
# 避免每加一个测试就要改门禁。
RESULTS_DIR="${VERIFY_RESULTS_DIR:-artifacts/verify-results}"
run_suite() {
  local name="$1" project="$2" floor="$3"
  shift 3
  local dir="$RESULTS_DIR/$name"
  rm -rf "$dir"
  mkdir -p "$dir"
  dotnet test "$project" --configuration Release --no-build --disable-build-servers --nologo \
    --logger "trx;LogFileName=$name.trx" --results-directory "$dir" "$@"
  local executed
  executed="$(grep -o 'executed="[0-9]*"' "$dir/$name.trx" 2>/dev/null | head -1 | tr -dc '0-9' || true)"
  if [[ -z "$executed" ]] || (( executed < floor )); then
    printf '门禁失败：%s 实际执行 %s 个用例（下限 %s）。dotnet test 对零匹配返回 0，必须硬失败。\n' \
      "$name" "${executed:-0}" "$floor" >&2
    exit 1
  fi
  printf '%s 实际执行 %s 个用例。\n' "$name" "$executed"
}

run_backend_pipeline() {
  printf '%s\n' '==> Release 构建'
  # 显式 restore 后再 --no-restore 构建：实测 `dotnet build` 隐式还原在解决方案级
  # 会逐项目重算（同一状态下 9~47s 抖动），拆开后稳定 restore 约 2s + build 约 5s。
  dotnet restore Game.slnx --disable-build-servers --nologo
  dotnet build Game.slnx --configuration Release --no-restore --disable-build-servers --nologo

  if [[ -f tests/Domain.Tests/Domain.Tests.csproj ]]; then
    printf '%s\n' '==> 领域测试'
    run_suite domain tests/Domain.Tests/Domain.Tests.csproj 120
  fi
  if [[ -f tests/Ecs.Tests/Ecs.Tests.csproj ]]; then
    printf '%s\n' '==> ECS 运行时与适配边界测试'
    run_suite ecs tests/Ecs.Tests/Ecs.Tests.csproj 40
  fi

  if [[ -f tests/Server.Tests/Server.Tests.csproj ]]; then
    if [[ "$RUN_INTEGRATION" -eq 1 ]]; then
      printf '%s\n' '==> 服务端测试'
      run_suite server tests/Server.Tests/Server.Tests.csproj 150
    else
      # --fast：跳过需要真实数据库的 PostgresIntegration 门禁，只跑纯内存/契约用例。
      printf '%s\n' '==> 服务端测试（跳过 PostgresIntegration）'
      run_suite server tests/Server.Tests/Server.Tests.csproj 50 --filter "Category!=PostgresIntegration"
    fi
  fi

  if [[ -f src/DataTool/DataTool.csproj ]]; then
    printf '%s\n' '==> 游戏数据校验'
    dotnet run --project src/DataTool/DataTool.csproj --configuration Release --no-build -- validate config/game-data --strict
  fi

  if [[ "$PRIVATE_AVAILABLE" -eq 1 ]]; then
    printf '%s\n' '==> 私有扩展模块测试（完整口径）'
    # 私有测试工程引用主仓库 Server：构建时 Server.Clean 自动注入私有源码（完整版），
    # 与生产完整构建同一代码路径；先显式构建再复用 run_suite 的 --no-build 与 trx 硬校验。
    dotnet build "$PRIVATE_ROOT/tests/DaoHuaPrivate.Tests/DaoHuaPrivate.Tests.csproj" \
      --configuration Release --disable-build-servers --nologo
    run_suite private "$PRIVATE_ROOT/tests/DaoHuaPrivate.Tests/DaoHuaPrivate.Tests.csproj" 5
  fi
}

run_frontend_pipeline() {
  ensure_frontend_deps

  # 私有客户端模块挂载：存在即完整版构建（vite alias 自动切换），缺失即开源 stub。
  printf '%s\n' '==> 同步私有客户端模块挂载'
  node scripts/sync-private-modules.mjs

  printf '%s\n' '==> 生成战斗模拟器前端配表'
  node scripts/gen-simulator-catalog.mjs

  printf '%s\n' '==> 同步前端 i18n 词条模板 CSV'
  node scripts/export-i18n-csv.mjs

  printf '%s\n' '==> 前端 i18n 词条覆盖校验'
  node scripts/check-i18n-keys.mjs

  printf '%s\n' '==> 日志富文本标记转义校验'
  node scripts/check-log-markup.mjs

  # 前端纯逻辑与工具单测（node:test）：启用 --experimental-strip-types 直跑 TS 测试模块。
  printf '%s\n' '==> 前端逻辑与工具单测'
  node --experimental-strip-types --test "frontend/src/**/*.test.mjs" "scripts/gen-asset-manifest.test.mjs" ".agents/skills/gityx-publish/scripts/gityx-publish.test.mjs"

  # 契约脚本的唯一既有入口是 tests/Server.Tests/FrontendContractTests（随 --backend 跑）。
  # 全量/后端模式下它已被执行，此处再跑就是同一个脚本重复一遍；
  # 只有「单独跑前端面」时它才完全缺席，因此在那种模式下直调一次补齐。
  if [[ "$RUN_BACKEND" -eq 0 ]]; then
    printf '%s\n' '==> 前后端契约解码校验（--frontend 补充执行）'
    node scripts/check-frontend-contracts.mjs
  fi

  printf '%s\n' '==> 前端类型检查与生产构建'
  npm --prefix frontend run build:bundle

  if [[ -e frontend/src/private-impl/index.ts ]]; then
    printf '%s\n' '==> 私有前端实现类型检查（完整口径，tsc 走挂载视图）'
    (cd frontend && npx --no-install tsc -p tsconfig.private-impl.json)
  fi
}

if [[ "$RUN_INTEGRATION" -eq 1 ]]; then
  printf '%s\n' '==> 供给 PostgreSQL 集成测试一次性库'
  supply_verify_postgres
fi

if [[ "$RUN_BACKEND" -eq 1 && "$RUN_FRONTEND" -eq 1 && "$PARALLEL" -eq 1 ]]; then
  printf '%s\n' '==> 启动前后端并发双轨门禁流水线（并行加速模式）'
  VERIFY_TMP="$(mktemp -d "${TMPDIR:-/tmp}/daohua-verify.XXXXXX")"
  BACKEND_STATUS_FILE="$VERIFY_TMP/backend.status"
  FRONTEND_STATUS_FILE="$VERIFY_TMP/frontend.status"

  (
    set -Eeuo pipefail
    run_backend_pipeline
    echo "0" > "$BACKEND_STATUS_FILE"
  ) 2>&1 | sed -u 's/^/[后端] /' &
  PID_BACKEND=$!

  (
    set -Eeuo pipefail
    run_frontend_pipeline
    echo "0" > "$FRONTEND_STATUS_FILE"
  ) 2>&1 | sed -u 's/^/[前端] /' &
  PID_FRONTEND=$!

  wait "$PID_BACKEND" || {
    printf '\n错误: 后端门禁流水线执行失败！\n' >&2
    exit 1
  }
  wait "$PID_FRONTEND" || {
    printf '\n错误: 前端门禁流水线执行失败！\n' >&2
    exit 1
  }

  if [[ ! -f "$BACKEND_STATUS_FILE" || "$(cat "$BACKEND_STATUS_FILE")" != "0" ]]; then
    printf '\n错误: 后端门禁未成功到达终点。\n' >&2
    exit 1
  fi
  if [[ ! -f "$FRONTEND_STATUS_FILE" || "$(cat "$FRONTEND_STATUS_FILE")" != "0" ]]; then
    printf '\n错误: 前端门禁未成功到达终点。\n' >&2
    exit 1
  fi
  printf '\n%s\n\n' '✔ 前后端双轨流水线并发校验全部通过！'
else
  if [[ "$RUN_BACKEND" -eq 1 ]]; then
    run_backend_pipeline
  fi
  if [[ "$RUN_FRONTEND" -eq 1 ]]; then
    run_frontend_pipeline
  fi
fi

if [[ "$RUN_COMPOSE" -eq 1 ]]; then
  printf '%s\n' '==> 校验 Compose 配置'
  docker compose -f compose.yaml config >/dev/null
  DB_BOOTSTRAP_USER=daohua_bootstrap \
  DB_BOOTSTRAP_PASSWORD=verify-bootstrap-password \
  DB_APP_USER=daohua_api_login \
  DB_APP_PASSWORD=verify-app-password \
  DB_WORKER_USER=daohua_worker_login \
  DB_WORKER_PASSWORD=verify-worker-password \
  APP_IMAGE=daohua:verify \
  COMPUTE_IMAGE=daohua-compute:verify \
  COMPUTE_TOKEN=verify-compute-token \
    docker compose -f deploy/compose.prod.yaml config --quiet
fi

if [[ "$RUN_IMAGE" -eq 1 ]]; then
  printf '%s\n' '==> 生产镜像构建（VERIFY_DOCKER=1 + 不限范围的 --full）'
  scripts/docker-build-prod.sh --allow-dirty --no-pull --release-tag verify
fi

summary_item() {
  local item="$1" executed="$2" reason="$3"
  if [[ "$executed" -eq 1 ]]; then
    printf '  %s：已通过\n' "$item"
  else
    printf '  %s：未执行（%s）\n' "$item" "$reason"
  fi
}

printf '\n门禁执行摘要（scope=%s，full=%s）：\n' "$SCOPE" "$FULL"
summary_item '后端构建、测试与数据校验' "$RUN_BACKEND" '限定为前端'
summary_item '前端门禁（含契约）' "$RUN_FRONTEND" '限定为后端'
summary_item 'PostgresIntegration' "$RUN_INTEGRATION" '快速口径或限定为前端'
summary_item 'Compose 配置' "$RUN_COMPOSE" '仅不限范围的 --full 执行'
summary_item '生产镜像构建' "$RUN_IMAGE" '仅不限范围的 --full 加 VERIFY_DOCKER=1 执行'
if [[ "$RUN_COMPOSE" -eq 1 ]]; then
  printf '%s\n' '全量门禁完成。'
else
  printf '%s\n' '所选范围门禁完成；未覆盖交付全量，请另跑 scripts/verify.sh --full。'
fi
