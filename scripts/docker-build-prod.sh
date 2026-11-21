#!/usr/bin/env bash
# 生产镜像构建：一次构建 Server 与无状态 ComputeNode 两个不可变镜像。
# 默认拒绝 dirty worktree；不会把运行数据、日志、快照或 secret 写入镜像。

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE_PREFIX="${IMAGE_PREFIX:-${TENCENT_IMAGE_PREFIX:-ccr.ccs.tencentyun.com/yuohira}}"
IMAGE_NAME="${IMAGE_NAME:-daohua}"
COMPUTE_IMAGE_NAME="${COMPUTE_IMAGE_NAME:-daohua-compute}"
MOVING_TAG="${MOVING_TAG:-prod}"
RELEASE_TAG="${RELEASE_TAG:-}"
PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"
NPM_CONFIG_REGISTRY="${NPM_CONFIG_REGISTRY:-https://registry.npmmirror.com}"
PUSH=0
BUILD=1
PULL=1
ALLOW_DIRTY=0

usage() {
  printf '%s\n' \
    '用法：scripts/docker-build-prod.sh [选项]' \
    '' \
    '选项：' \
    '  --push                 构建后推送两个不可变镜像和 prod 标签' \
    '  --push-existing        推送本机已验收的两个镜像，不重新构建' \
    '  --release-tag TAG      指定不可变版本标签（默认从 version.json 读取 v<MAJOR>.<MINOR>.<PATCH>）' \
    '  --image-prefix PREFIX  覆盖镜像仓库前缀' \
    '  --moving-tag TAG       覆盖移动标签，默认 prod' \
    '  --allow-dirty          本地排障允许主仓 dirty（仍只构建提交）' \
    '  --no-pull              不主动刷新基础镜像' \
    '  -h, --help             显示帮助'
}

while (($# > 0)); do
  case "$1" in
    --push) PUSH=1; shift ;;
    --push-existing) PUSH=1; BUILD=0; shift ;;
    --release-tag) RELEASE_TAG="${2:?--release-tag 缺少值}"; shift 2 ;;
    --image-prefix) IMAGE_PREFIX="${2:?--image-prefix 缺少值}"; shift 2 ;;
    --moving-tag) MOVING_TAG="${2:?--moving-tag 缺少值}"; shift 2 ;;
    --allow-dirty) ALLOW_DIRTY=1; shift ;;
    --no-pull) PULL=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf '未知参数: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ "$PLATFORM" != "linux/amd64" ]]; then
  printf '错误: 生产镜像只允许 linux/amd64（当前 %s）。\n' "$PLATFORM" >&2
  exit 1
fi
for command in git docker date openssl; do
  command -v "$command" >/dev/null 2>&1 || { printf '缺少命令: %s\n' "$command" >&2; exit 1; }
done
docker info >/dev/null 2>&1 || { printf 'Docker 当前不可用。\n' >&2; exit 1; }

cd "$ROOT_DIR"
source "$ROOT_DIR/deploy/release-source.sh"
if [[ "$PUSH" == 1 && "$ALLOW_DIRTY" == 1 ]]; then
  printf '正式推送不允许 --allow-dirty。\n' >&2; exit 1
fi
release_sources "$ROOT_DIR" "$ALLOW_DIRTY"
VCS_REF="$main_revision"
[[ "$VCS_REF" == "${RELEASE_MAIN_REVISION:-$VCS_REF}" && "$yuo_revision" == "${RELEASE_YUO_REVISION:-$yuo_revision}" && "$private_revision" == "${RELEASE_PRIVATE_REVISION:-$private_revision}" ]] || {
  printf '构建输入在发布预检后已变化。\n' >&2; exit 1;
}
if [[ -z "$RELEASE_TAG" ]]; then
  PKG_VERSION="$(git show "${VCS_REF}:version.json" | node -p 'JSON.parse(require("fs").readFileSync(0,"utf8")).version')"
  RELEASE_TAG="v${PKG_VERSION}"
fi
DB_SCHEMA="$(git show "${VCS_REF}:src/Server/Clean/PostgresSchema.cs" | sed -nE 's/^ *public const int Version = ([0-9]+);.*/\1/p')"
SECTION_SCHEMA="$(git show "${VCS_REF}:src/Domain/Persistence/SectionCodec.cs" | sed -nE 's/^ *public const int CurrentSchemaVersion = ([0-9]+);.*/\1/p')"
[[ "$DB_SCHEMA" =~ ^[0-9]+$ && "$SECTION_SCHEMA" =~ ^[0-9]+$ ]] || { printf '无法判定提交的 schema 目标。\n' >&2; exit 1; }
if [[ -z "$RELEASE_TAG" ]]; then
  printf '无 Git 提交时必须通过 --release-tag 指定不可变标签。\n' >&2
  exit 1
fi
if [[ ! "$RELEASE_TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ || ! "$MOVING_TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]]; then
  printf '非法 Docker 标签。\n' >&2
  exit 1
fi

IMAGE_REPOSITORY="${IMAGE_PREFIX%/}/${IMAGE_NAME}"
COMPUTE_REPOSITORY="${IMAGE_PREFIX%/}/${COMPUTE_IMAGE_NAME}"
RELEASE_IMAGE="${IMAGE_REPOSITORY}:${RELEASE_TAG}"
MOVING_IMAGE="${IMAGE_REPOSITORY}:${MOVING_TAG}"
RELEASE_COMPUTE_IMAGE="${COMPUTE_REPOSITORY}:${RELEASE_TAG}"
MOVING_COMPUTE_IMAGE="${COMPUTE_REPOSITORY}:${MOVING_TAG}"

if [[ "$BUILD" == "1" ]]; then
  # 三个输入都锁定为提交。仅从私有提交归档两个允许的子树，不复制工作目录。
  BUILD_DATE="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/daohua-prod-build-XXXXXX")"
  trap 'rm -rf -- "$STAGING_DIR"' EXIT
  git archive "$VCS_REF" | tar -x -C "$STAGING_DIR"
  mkdir -p "$STAGING_DIR/src/Private" "$STAGING_DIR/frontend/src/private-impl" "$STAGING_DIR/vendor/YuoCore"
  git -C "$ROOT_DIR/vendor/YuoCore" archive "$yuo_revision" | tar -x -C "$STAGING_DIR/vendor/YuoCore"
  git -C "$private_root" archive "$private_revision" server | tar -x -C "$STAGING_DIR/src/Private"
  git -C "$private_root" archive "$private_revision" frontend/antiMulti | tar -x --strip-components=2 -C "$STAGING_DIR/frontend/src/private-impl"
  NODE_IMAGE_REF="${NODE_IMAGE:-ccr.ccs.tencentyun.com/yuohira/node:22-alpine}"
  common_args=(
    --platform "$PLATFORM"
    --load
    --build-arg "BUILD_DATE=${BUILD_DATE}"
    --build-arg "BUILD_VERSION=${RELEASE_TAG}"
    --build-arg "VCS_REF=${VCS_REF}"
    --label "io.daohua.yuocore-revision=$yuo_revision"
    --label "io.daohua.private-revision=$private_revision"
    --label "io.daohua.db-schema=$DB_SCHEMA"
    --label "io.daohua.section-schema=$SECTION_SCHEMA"
    --build-arg "NPM_CONFIG_REGISTRY=${NPM_CONFIG_REGISTRY}"
    --build-arg "NODE_IMAGE=${NODE_IMAGE_REF}"
    --build-arg "REQUIRE_PRIVATE_MODULES=1"
  )
  if [[ "$PULL" == "1" ]]; then common_args+=(--pull); fi
  printf '构建 Server 镜像: %s（完整口径，含私有扩展模块）\n' "$RELEASE_IMAGE"
  docker buildx build "${common_args[@]}" --target server-runtime \
    --tag "$RELEASE_IMAGE" --tag "$MOVING_IMAGE" "$STAGING_DIR"
  printf '构建 ComputeNode 镜像: %s\n' "$RELEASE_COMPUTE_IMAGE"
  docker buildx build "${common_args[@]}" --target compute-runtime \
    --tag "$RELEASE_COMPUTE_IMAGE" --tag "$MOVING_COMPUTE_IMAGE" "$STAGING_DIR"
else
  release_id="$(docker image inspect --format '{{.Id}}' "$RELEASE_IMAGE" 2>/dev/null || true)"
  moving_id="$(docker image inspect --format '{{.Id}}' "$MOVING_IMAGE" 2>/dev/null || true)"
  compute_release_id="$(docker image inspect --format '{{.Id}}' "$RELEASE_COMPUTE_IMAGE" 2>/dev/null || true)"
  compute_moving_id="$(docker image inspect --format '{{.Id}}' "$MOVING_COMPUTE_IMAGE" 2>/dev/null || true)"
  if [[ -z "$release_id" || -z "$moving_id" || -z "$compute_release_id" || -z "$compute_moving_id" ]]; then
    printf '缺少已验收的 Server 或 ComputeNode 镜像。\n' >&2
    exit 1
  fi
  if [[ "$release_id" != "$moving_id" || "$compute_release_id" != "$compute_moving_id" ]]; then
    printf '不可变标签与移动标签内容不一致，拒绝推送。\n' >&2
    exit 1
  fi
fi

# 推送既有镜像也必须绑定本次固定输入；不能只凭 tag 相同或本地存在判为可发布。
for image in "$RELEASE_IMAGE" "$RELEASE_COMPUTE_IMAGE"; do
  provenance="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}} {{index .Config.Labels "io.daohua.yuocore-revision"}} {{index .Config.Labels "io.daohua.private-revision"}} {{index .Config.Labels "io.daohua.db-schema"}} {{index .Config.Labels "io.daohua.section-schema"}}' "$image")"
  [[ "$provenance" == "$VCS_REF $yuo_revision $private_revision $DB_SCHEMA $SECTION_SCHEMA" ]] || {
    printf '镜像来源或 schema 标签不匹配: %s\n' "$image" >&2; exit 1;
  }
done
printf '镜像输入: main=%s YuoCore=%s private=%s schema=%s/%s\n' "$VCS_REF" "$yuo_revision" "$private_revision" "$DB_SCHEMA" "$SECTION_SCHEMA"

if [[ "$PUSH" == "1" ]]; then
  for image in "$RELEASE_IMAGE" "$RELEASE_COMPUTE_IMAGE" "$MOVING_IMAGE" "$MOVING_COMPUTE_IMAGE"; do
    printf '推送镜像: %s\n' "$image"
    docker push "$image"
  done
else
  printf '本地构建完成；未传入 --push，因此没有修改 CCR。\n'
fi
