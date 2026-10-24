#!/usr/bin/env bash
# 仅操作固定本地开发容器；不读取远程部署配置或生产凭据。
set -Eeuo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"
docker exec -i daohua-local-db psql -X -v ON_ERROR_STOP=1 -U daohua -d daohua < deploy/postgres-local-roles.sql
printf '本地权限组已配置。请为各进程创建独立登录角色并授予对应权限组，关闭运行时 schema bootstrap。\n'
