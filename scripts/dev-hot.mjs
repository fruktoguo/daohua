#!/usr/bin/env node
/**
 * 服务端开发热重启守护进程（参考 mud_mmo_next 的 dev-hot.ts 行为）：
 *
 * - 监听 src/ 全部源码变更；每次变更刷新防抖计时器，默认静默 60 秒后才开始重建
 *   （SERVER_DEV_RESTART_DEBOUNCE_MS 可调，设为 0 表示立即重启）。
 * - 防抖期满先执行 dotnet build；编译失败时保持旧进程继续运行并等待下一次变更。
 * - 构建成功才优雅停止旧进程（SIGTERM 走存档与市场租约释放），再启动新进程。
 * - 新进程异常退出时按退避自动拉起：30 秒内连续 3 次失败则从 1s 放慢到 5s。
 * - Ctrl+C / SIGTERM 时整树优雅退出。
 */

import { spawn } from "node:child_process";

import { existsSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const project = process.env.DAOXUAN_SERVER_PROJECT
  ?? "src/Daoxuan.Server/Daoxuan.Server.csproj";

const debounceMs = parseNonNegativeInteger(
  process.env.SERVER_DEV_RESTART_DEBOUNCE_MS,
  60_000,
);
const gracefulStopTimeoutMs = parseNonNegativeInteger(
  process.env.SERVER_DEV_STOP_TIMEOUT_MS,
  20_000,
);

let shuttingDown = false;
let building = false;
let restartPending = false;
let debounceTimer = null;
let serverProcess = null;
let expectedExitPid = null;
const recentUnexpectedExits = [];

function log(message) {
  const now = new Date();
  const hh = String(now.getHours()).padStart(2, "0");
  const mm = String(now.getMinutes()).padStart(2, "0");
  const ss = String(now.getSeconds()).padStart(2, "0");
  process.stdout.write(`[${hh}:${mm}:${ss}] [server dev] ${message}\n`);
}

function parseNonNegativeInteger(value, fallback) {
  if (value === undefined || value === "") return fallback;
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback;
}

function isWatchableFile(relativePath) {
  return /\.(cs|csproj|json)$/.test(relativePath);
}

/** 递归收集目录下全部文件相对路径。 */
function listFilesRecursive(dir, baseDir = dir, out = []) {
  for (const entry of readdirSafe(dir)) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      listFilesRecursive(full, baseDir, out);
    } else {
      out.push(path.relative(baseDir, full));
    }
  }
  return out;
}

function readdirSafe(dir) {
  try {
    return readdirWithTypes(dir);
  } catch {
    // 目录可能刚被删除（如 bin/obj 清理），忽略即可。
    return [];
  }
}

function readdirWithTypes(dir) {
  return readdirSync(dir, { withFileTypes: true });
}

/** 轮询快照对比：跨平台、无 inotify 句柄限制，天然合并短时间内的多次写入。 */
function snapshotSourceFiles() {
  const map = new Map();
  for (const dir of ["src", "vendor/YuoCore"]) {
    const base = path.join(rootDir, dir);
    if (!existsSync(base)) continue;
    for (const relative of listFilesRecursive(base)) {
      if (!isWatchableFile(relative)) continue;
      const full = path.join(base, relative);
      try {
        const stat = statSync(full);
        map.set(`${dir}/${relative}`, `${stat.mtimeMs}:${stat.size}`);
      } catch {
        // 文件在扫描间隙被删除：跳过，下次扫描按删除处理。
      }
    }
  }
  return map;
}

function computeBackoffDelayMs() {
  const now = Date.now();
  while (
    recentUnexpectedExits.length > 0
    && now - recentUnexpectedExits[0] > 30_000
  ) {
    recentUnexpectedExits.shift();
  }
  recentUnexpectedExits.push(now);
  return recentUnexpectedExits.length >= 3 ? 5_000 : 1_000;
}

function stopServer(onStopped, reason) {
  const child = serverProcess;
  if (!child || child.exitCode !== null) {
    serverProcess = null;
    onStopped?.();
    return;
  }

  expectedExitPid = child.pid ?? null;
  let finished = false;
  const done = () => {
    if (finished) return;
    finished = true;
    if (serverProcess === child) serverProcess = null;
    onStopped?.();
  };

  child.once("exit", done);
  child.kill("SIGTERM");

  setTimeout(() => {
    if (finished) return;
    log(`服务端未在 ${gracefulStopTimeoutMs}ms 内优雅退出，改为强制结束`);
    child.kill("SIGKILL");
  }, gracefulStopTimeoutMs).unref();
}

function startServer() {
  if (shuttingDown) return;
  log("启动服务端进程...");
  const child = spawn("dotnet", [
    "run",
    "--no-build",
    "--project",
    project,
  ], {
    cwd: rootDir,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" },
    stdio: ["ignore", "inherit", "inherit"],
  });
  serverProcess = child;

  child.on("exit", (code, signal) => {
    const wasExpected = expectedExitPid !== null && expectedExitPid === (child.pid ?? null);
    if (expectedExitPid === (child.pid ?? null)) expectedExitPid = null;
    if (serverProcess === child) serverProcess = null;
    if (shuttingDown || wasExpected) return;
    const delayMs = computeBackoffDelayMs();
    log(`服务端异常退出 (code=${code ?? "null"}, signal=${signal ?? "none"})，${delayMs}ms 后自动拉起`);
    setTimeout(() => startServer(), delayMs).unref();
  });
}

function buildAndRestart(reason) {
  if (shuttingDown) return;
  building = true;
  log(`${reason}，开始重新构建...`);
  const build = spawn("dotnet", ["build", project], {
    cwd: rootDir,
    env: { ...process.env },
    stdio: ["ignore", "inherit", "inherit"],
  });

  build.on("exit", (code) => {
    building = false;
    if (shuttingDown) return;
    if (code !== 0) {
      log(`构建失败 (code=${code})，保持旧进程继续运行，等待下一次源码变更`);
      return;
    }
    stopServer(() => startServer(), reason);
  });
}

function scheduleDebouncedRestart(reason) {
  if (shuttingDown) return;
  if (debounceMs <= 0) {
    if (!building) buildAndRestart(reason);
    else restartPending = true;
    return;
  }

  if (debounceTimer !== null) clearTimeout(debounceTimer);
  const seconds = Math.round(debounceMs / 1_000);
  log(`${reason}，静默 ${seconds}s 后重建重启（期间有新变更将刷新等待）`);
  debounceTimer = setTimeout(() => {
    debounceTimer = null;
    if (building) {
      restartPending = true;
      return;
    }
    buildAndRestart("静默期结束");
  }, debounceMs);
  debounceTimer.unref();
}

function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  clearTimeout(debounceTimer);
  log("正在停止热重启守护进程...");
}

process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);

// 主循环：每秒轮询一次源码快照；发现差异即进入防抖等待。
let previous = snapshotSourceFiles();
setInterval(() => {
  if (shuttingDown) return;
  const current = snapshotSourceFiles();
  const changed = [];
  for (const [key, value] of current) {
    if (previous.get(key) !== value) changed.push(key);
  }
  for (const key of previous.keys()) {
    if (!current.has(key)) changed.push(key);
  }
  previous = current;
  if (changed.length > 0) {
    const names = changed.slice(0, 3).join(", ");
    const suffix = changed.length > 3 ? ` 等 ${changed.length} 个文件` : "";
    scheduleDebouncedRestart(`检测到变更 ${names}${suffix}`);
  }
}, 1_000).unref();

startServer();

if (process.stdout.isTTY) {
  log(`已就绪。防抖 ${debounceMs / 1_000}s，Ctrl+C 停止全部服务。`);
}
