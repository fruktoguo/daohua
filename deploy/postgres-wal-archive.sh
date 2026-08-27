#!/bin/sh
# PostgreSQL archive_command：把单个 WAL 段以原子方式写入挂载的归档目录。
# 参数由 PostgreSQL 传入：%p（数据目录内的相对路径）和 %f（WAL 文件名）。

set -eu

source_path=${1:?缺少 WAL 源文件路径}
wal_name=${2:?缺少 WAL 文件名}
archive_dir=${WAL_ARCHIVE_DIR:-/var/lib/postgresql/wal-archive}

case "$wal_name" in
  ''|*[!A-Fa-f0-9]*)
    echo "非法 WAL 文件名: $wal_name" >&2
    exit 2
    ;;
esac

if [ ! -f "$source_path" ]; then
  echo "WAL 源文件不存在: $source_path" >&2
  exit 1
fi

mkdir -p "$archive_dir"
destination="$archive_dir/$wal_name"
if [ -f "$destination" ]; then
  # PostgreSQL 可能因重试再次调用 archive_command；已有同内容文件可直接视为成功。
  cmp -s "$source_path" "$destination" || {
    echo "WAL 归档冲突：目标文件内容不同: $destination" >&2
    exit 1
  }
  exit 0
fi

temporary="$archive_dir/.${wal_name}.tmp.$$"
rm -f "$temporary"
cp "$source_path" "$temporary"
chmod 0600 "$temporary"
if command -v sync >/dev/null 2>&1; then
  sync -f "$temporary" 2>/dev/null || sync
fi
if [ -e "$destination" ]; then
  cmp -s "$temporary" "$destination" || {
    rm -f "$temporary"
    echo "WAL 归档并发冲突：目标文件内容不同: $destination" >&2
    exit 1
  }
  rm -f "$temporary"
else
  mv "$temporary" "$destination"
fi
