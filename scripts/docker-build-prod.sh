#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: docker-build-prod.sh
# 主要职责: 生产 Docker 容器镜像的规范化构建、元数据注入与 CCR 镜像仓库推送
#
# 核心功能与安全保障:
#   1. 工作区状态检查: 默认拒绝 dirty worktree 构建正式镜像，确保镜像与 Git Commit 严格对应（可通过 --allow-dirty 覆盖排障）；
#   2. 自动生成标签: 根据当前 Git Commit 生成不可变唯一版本标签（sha-<hash:12>）与移动标签（默认为 prod）；
#   3. OCI 元数据注入: 注入构建时间 (BUILD_DATE)、版本号 (BUILD_VERSION)、Git Commit (VCS_REF)；
#   4. 平台白名单构建: 使用 Docker Buildx 构建 linux/amd64 架构的最小化轻量镜像，DOCKER_PLATFORM 覆盖为其他平台时立即报错退出；
#   5. 镜像推送控制: 默认仅本机构建，传入 --push 时才会同步推送至腾讯云 CCR (ccr.ccs.tencentyun.com)；
#   6. 幂等复用推送: 支持 --push-existing 参数，直接推送本机已通过烟测验收的现有镜像，避免二次构建引起的内容漂移。
#
# 参数说明:
#   --push                 构建完成后推送不可变标签和移动标签到 CCR
#   --push-existing        不重新构建，直接推送本机已验证且一致的镜像
#   --release-tag TAG      显式指定不可变版本标签
#   --image-prefix PREFIX  覆盖 CCR 命名空间前缀
#   --moving-tag TAG       覆盖移动标签（默认 prod）
#   --allow-dirty          允许未提交工作区构建（仅用于排障）
#   --no-pull              构建时不主动刷新基础镜像
#   -h, --help             显示帮助信息
# ==============================================================================

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE_PREFIX="${IMAGE_PREFIX:-${TENCENT_IMAGE_PREFIX:-ccr.ccs.tencentyun.com/yuohira}}"
IMAGE_NAME="${IMAGE_NAME:-daohua}"
MOVING_TAG="${MOVING_TAG:-prod}"
RELEASE_TAG="${RELEASE_TAG:-}"
PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"
NPM_CONFIG_REGISTRY="${NPM_CONFIG_REGISTRY:-https://registry.npmjs.org}"
PUSH=0
BUILD=1
PULL=1
ALLOW_DIRTY=0

if [[ "$PLATFORM" != "linux/amd64" ]]; then
  printf '错误: DOCKER_PLATFORM 仅支持 linux/amd64（当前值: %s），生产镜像统一构建 amd64 架构。\n' "$PLATFORM" >&2
  exit 1
fi

usage() {
  printf '%s\n' \
    '用法：scripts/docker-build-prod.sh [选项]' \
    '' \
    '选项：' \
    '  --push                 构建后推送不可变标签和移动标签' \
    '  --push-existing        推送本机已验收且两个标签内容一致的镜像' \
    '  --release-tag TAG      指定不可变版本标签' \
    '  --image-prefix PREFIX  覆盖 CCR 命名空间' \
    '  --moving-tag TAG       覆盖移动标签，默认 prod' \
    '  --allow-dirty          允许从未提交工作区构建，仅用于首次引导或排障' \
    '  --no-pull              不主动刷新基础镜像' \
    '  -h, --help             显示帮助' \
    '' \
    '默认行为只在本机构建。正式发布必须显式传入 --push，且工作区必须已提交。'
}

while (($# > 0)); do
  case "$1" in
    --push)
      PUSH=1
      shift
      ;;
    --push-existing)
      PUSH=1
      BUILD=0
      shift
      ;;
    --release-tag)
      RELEASE_TAG="${2:?--release-tag 缺少值}"
      shift 2
      ;;
    --image-prefix)
      IMAGE_PREFIX="${2:?--image-prefix 缺少值}"
      shift 2
      ;;
    --moving-tag)
      MOVING_TAG="${2:?--moving-tag 缺少值}"
      shift 2
      ;;
    --allow-dirty)
      ALLOW_DIRTY=1
      shift
      ;;
    --no-pull)
      PULL=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf '未知参数: %s\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

for command in git docker date; do
  if ! command -v "$command" >/dev/null 2>&1; then
    printf '缺少命令: %s\n' "$command" >&2
    exit 1
  fi
done
if ! docker info >/dev/null 2>&1; then
  printf 'Docker 当前不可用。\n' >&2
  exit 1
fi

cd "$ROOT_DIR"
VCS_REF="uncommitted"
if git rev-parse --verify HEAD >/dev/null 2>&1; then
  VCS_REF="$(git rev-parse HEAD)"
  if [[ -z "$RELEASE_TAG" ]]; then
    RELEASE_TAG="sha-${VCS_REF:0:12}"
  fi
fi

if [[ -n "$(git status --porcelain --untracked-files=normal)" && "$ALLOW_DIRTY" != "1" ]]; then
  printf '工作区存在未提交改动，拒绝生成正式镜像。排障时可显式传入 --allow-dirty。\n' >&2
  exit 1
fi
if [[ -z "$RELEASE_TAG" ]]; then
  printf '当前仓库还没有提交，必须通过 --release-tag 指定不可变标签。\n' >&2
  exit 1
fi
if [[ ! "$RELEASE_TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]]; then
  printf '非法 Docker 标签: %s\n' "$RELEASE_TAG" >&2
  exit 1
fi
if [[ ! "$MOVING_TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]]; then
  printf '非法 Docker 移动标签: %s\n' "$MOVING_TAG" >&2
  exit 1
fi

IMAGE_REPOSITORY="${IMAGE_PREFIX%/}/${IMAGE_NAME}"
RELEASE_IMAGE="${IMAGE_REPOSITORY}:${RELEASE_TAG}"
MOVING_IMAGE="${IMAGE_REPOSITORY}:${MOVING_TAG}"
if [[ "$BUILD" == "1" ]]; then
  BUILD_DATE="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  BUILD_ARGS=(
    --platform "$PLATFORM"
    --load
    --build-arg "BUILD_DATE=${BUILD_DATE}"
    --build-arg "BUILD_VERSION=${RELEASE_TAG}"
    --build-arg "VCS_REF=${VCS_REF}"
    --build-arg "NPM_CONFIG_REGISTRY=${NPM_CONFIG_REGISTRY}"
    --tag "$RELEASE_IMAGE"
    --tag "$MOVING_IMAGE"
  )
  if [[ "$PULL" == "1" ]]; then
    BUILD_ARGS+=(--pull)
  fi

  printf '构建镜像: %s\n' "$RELEASE_IMAGE"
  printf '移动标签: %s\n' "$MOVING_IMAGE"
  docker buildx build "${BUILD_ARGS[@]}" .
else
  release_id="$(docker image inspect --format '{{.Id}}' "$RELEASE_IMAGE" 2>/dev/null || true)"
  moving_id="$(docker image inspect --format '{{.Id}}' "$MOVING_IMAGE" 2>/dev/null || true)"
  if [[ -z "$release_id" || -z "$moving_id" ]]; then
    printf '本机缺少待推送镜像，请先完成构建和运行验收。\n' >&2
    exit 1
  fi
  if [[ "$release_id" != "$moving_id" ]]; then
    printf '不可变标签和移动标签内容不一致，拒绝推送: %s != %s\n' "$release_id" "$moving_id" >&2
    exit 1
  fi
  printf '复用已验收镜像: %s\n' "$release_id"
fi

if [[ "$PUSH" == "1" ]]; then
  printf '推送不可变镜像: %s\n' "$RELEASE_IMAGE"
  docker push "$RELEASE_IMAGE"
  printf '推送移动标签: %s\n' "$MOVING_IMAGE"
  docker push "$MOVING_IMAGE"
  printf 'CCR 清单:\n'
  docker buildx imagetools inspect "$MOVING_IMAGE" --format '{{json .Manifest}}'
else
  printf '本地构建完成；未传入 --push，因此没有修改 CCR。\n'
fi
