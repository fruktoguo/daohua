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
NPM_CONFIG_REGISTRY="${NPM_CONFIG_REGISTRY:-https://registry.npmjs.org}"
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
    '  --release-tag TAG      指定不可变版本标签' \
    '  --image-prefix PREFIX  覆盖镜像仓库前缀' \
    '  --moving-tag TAG       覆盖移动标签，默认 prod' \
    '  --allow-dirty          允许未提交工作区（仅排障）' \
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
for command in git docker date; do
  command -v "$command" >/dev/null 2>&1 || { printf '缺少命令: %s\n' "$command" >&2; exit 1; }
done
docker info >/dev/null 2>&1 || { printf 'Docker 当前不可用。\n' >&2; exit 1; }

cd "$ROOT_DIR"
VCS_REF="$(git rev-parse HEAD 2>/dev/null || printf 'uncommitted')"
if [[ -z "$RELEASE_TAG" && "$VCS_REF" != "uncommitted" ]]; then
  RELEASE_TAG="sha-${VCS_REF:0:12}"
fi
if [[ -n "$(git status --porcelain --untracked-files=normal)" && "$ALLOW_DIRTY" != "1" ]]; then
  printf '工作区存在未提交改动，拒绝正式镜像构建。\n' >&2
  exit 1
fi
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
  # 生产镜像固定完整口径：必须存在兄弟私有仓库（反多开等扩展模块，真源 docs/backend/private-modules.md）。
  PRIVATE_ROOT="$ROOT_DIR/../DaoHua-Private"
  if [[ ! -d "$PRIVATE_ROOT/server" ]]; then
    printf '错误: 生产镜像必须为完整口径，但未发现兄弟私有仓库 %s/server。\n' "$PRIVATE_ROOT" >&2
    exit 1
  fi

  # staging 构建上下文 = git archive 干净快照 + 私有模块拼装：
  #   src/Private                 -> MSBuild 按 DaoHuaPrivateRoot 注入 Server.Clean 程序集
  #   frontend/src/private-impl   -> Vite 完整版构建挂载点（真实目录优先于链接）
  # 私有源码只进入构建上下文与镜像层，绝不落入主仓库工作区；--allow-dirty 下镜像内容
  # 仍固定为 HEAD 快照 + 私有仓库当前内容，保证可追溯。
  BUILD_DATE="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/daohua-prod-build-XXXXXX")"
  trap 'rm -rf "$STAGING_DIR"' EXIT
  git archive HEAD | tar -x -C "$STAGING_DIR"
  mkdir -p "$STAGING_DIR/src/Private" "$STAGING_DIR/frontend/src/private-impl"
  cp -R "$PRIVATE_ROOT/server/." "$STAGING_DIR/src/Private/"
  cp -R "$PRIVATE_ROOT/frontend/antiMulti/." "$STAGING_DIR/frontend/src/private-impl/"

  NODE_IMAGE_REF="${NODE_IMAGE:-ccr.ccs.tencentyun.com/yuohira/node:22-alpine}"
  common_args=(
    --platform "$PLATFORM"
    --load
    --build-arg "BUILD_DATE=${BUILD_DATE}"
    --build-arg "BUILD_VERSION=${RELEASE_TAG}"
    --build-arg "VCS_REF=${VCS_REF}"
    --build-arg "NPM_CONFIG_REGISTRY=${NPM_CONFIG_REGISTRY}"
    --build-arg "NODE_IMAGE=${NODE_IMAGE_REF}"
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

if [[ "$PUSH" == "1" ]]; then
  for image in "$RELEASE_IMAGE" "$MOVING_IMAGE" "$RELEASE_COMPUTE_IMAGE" "$MOVING_COMPUTE_IMAGE"; do
    printf '推送镜像: %s\n' "$image"
    docker push "$image"
  done
else
  printf '本地构建完成；未传入 --push，因此没有修改 CCR。\n'
fi
