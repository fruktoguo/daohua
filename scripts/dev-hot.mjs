#!/usr/bin/env node
/**
 * ==============================================================================
 * 脚本名称: dev-hot.mjs
 * 主要职责: 服务端本地开发环境增量监听与平滑热重启守护进程
 *
 * 核心机制与特性:
 *   1. 源码轮询监控: 只扫描服务端工程的真实编译闭包——`src/Domain`、`src/Ecs`、`src/Protocol`、
 *      `src/Server`（含 `Clean/`，实际由 Server.Clean 工程编译）、`config/game-data/` 与 `vendor/YuoCore`；
 *      `src/Bench`、`src/ComputeNode`、`src/DataTool`、`src/LoadTests` 不参与 `src/Server/Server.csproj`
 *      构建，改动它们不触发重启（避免无关改动打断调试会话）；
 *   2. 防抖冷却机制: 每次文件变动刷新防抖计时器（默认静默 3s，可通过 SERVER_DEV_RESTART_DEBOUNCE_MS 调整，0 为立即触发）；
 *   3. 零中断构建保护: 防抖期满先在后台执行 dotnet build；若编译报错则保持旧服务端实例继续对外提供服务，避免调试中断；
 *   4. 优雅停止与生命周期: 编译成功后向旧服务发送 SIGTERM 触发存档持久化与市场写者租约释放，确认退出后再启动新实例；
 *   5. 崩溃自动拉起: 监听子进程生命周期，进程异常崩溃退出时采用退避策略（1s~5s）自动重启拉起；
 *   6. 级联信号清理: 捕获 SIGINT / SIGTERM 信号，确保守护进程与服务端子进程树全部安全优雅退出。
 *
 * 环境变量支持:
 *   - SERVER_PROJECT: 服务端工程文件路径（默认 src/Server/Server.csproj）
 *   - SERVER_DEV_RESTART_DEBOUNCE_MS: 变更防抖毫秒数（默认 3000）
 *   - SERVER_DEV_STOP_TIMEOUT_MS: 优雅停止超时毫秒数（默认 30000；生产 stop_grace_period 为 90s，dev 取 1/3 折中——存档与释放市场写者租约通常远快于此，又避免卡死拖慢迭代）
 * ==============================================================================
 */


import { spawn } from "node:child_process";

import { existsSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const project = process.env.SERVER_PROJECT
  ?? "src/Server/Server.csproj";

const debounceMs = parseNonNegativeInteger(
  process.env.SERVER_DEV_RESTART_DEBOUNCE_MS,
  3_000,
);
const gracefulStopTimeoutMs = parseNonNegativeInteger(
  process.env.SERVER_DEV_STOP_TIMEOUT_MS,
  30_000,
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

const IGNORED_DIR_NAMES = new Set([
  "bin",
  "obj",
  "node_modules",
  ".git",
  ".vs",
  ".idea",
]);

/** 递归收集目录下全部文件相对路径。 */
function listFilesRecursive(dir, baseDir = dir, out = []) {
  for (const entry of readdirSafe(dir)) {
    if (entry.isDirectory()) {
      if (IGNORED_DIR_NAMES.has(entry.name) || entry.name.startsWith(".")) {
        continue;
      }
      const full = path.join(dir, entry.name);
      listFilesRecursive(full, baseDir, out);
    } else {
      out.push(path.relative(baseDir, path.join(dir, entry.name)));
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

/**
 * 监听根：只覆盖 `src/Server/Server.csproj` 的真实编译闭包（Domain/Ecs/Protocol/Server.Clean/Server）
 * 加配表与内核。`src/Bench`、`src/ComputeNode`、`src/DataTool`、`src/LoadTests` 不在该闭包内，
 * 改动它们不会改变服务端产物，因此不触发重建重启。
 * 私有扩展模块（反多开等）位于兄弟仓库 `../DaoHua-Private/server`：存在时经 MSBuild
 * 源码注入 Server.Clean 程序集，属于完整版闭包的一部分，一并纳入监听；缺失时自动跳过。
 */
const WATCH_ROOTS = [
  "src/Domain",
  "src/Ecs",
  "src/Protocol",
  "src/Server",
  "src/Server.Clean",
  "config/game-data",
  "vendor/YuoCore",
  "../DaoHua-Private/server",
];

/** 轮询快照对比：跨平台、无 inotify 句柄限制，天然合并短时间内的多次写入。 */
function snapshotSourceFiles() {
  const map = new Map();
  for (const dir of WATCH_ROOTS) {
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
  if (process.platform === "win32") {
    spawn("taskkill", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore" }).on("exit", () => {
      spawn("taskkill", ["/IM", "Server.exe", "/F"], { stdio: "ignore" }).on("exit", done);
    });
  } else {
    child.kill("SIGTERM");
    setTimeout(() => {
      if (finished) return;
      log(`服务端未在 ${gracefulStopTimeoutMs}ms 内优雅退出，改为强制结束`);
      child.kill("SIGKILL");
    }, gracefulStopTimeoutMs).unref();
  }
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
    if (shuttingDown || wasExpected || building) return;
    const delayMs = computeBackoffDelayMs();
    log(`服务端异常退出 (code=${code ?? "null"}, signal=${signal ?? "none"})，${delayMs}ms 后自动拉起`);
    setTimeout(() => startServer(), delayMs);
  });
}

function buildAndRestart(reason) {
  if (shuttingDown) return;
  building = true;
  log(`${reason}，开始重新构建...`);

  // Windows 上产物文件被运行中的实例占用，必须先停进程再构建；此时旧进程已不可用，
  // 构建失败只能把上一次的可执行产物重新拉起。其他平台构建期旧进程照常服务。
  let stoppedBeforeBuild = false;

  const onBuildExit = (code) => {
    building = false;
    if (shuttingDown) return;
    if (code !== 0) {
      log(`构建失败 (code=${code})，${stoppedBeforeBuild ? "拉起上一次产物继续运行" : "保持旧进程继续运行"}`);
      if (stoppedBeforeBuild) startServer();
      drainQueuedRestart();
      return;
    }
    if (stoppedBeforeBuild) {
      // 进程已在构建前停掉，startServer 自身会输出启动日志，此处不再重复。
      startServer();
      drainQueuedRestart();
      return;
    }
    log("构建成功，重启服务端进程...");
    stopServer(() => {
      startServer();
      drainQueuedRestart();
    }, reason);
  };

  const runBuild = () => {
    spawn("dotnet", ["build", project], {
      cwd: rootDir,
      env: { ...process.env },
      stdio: ["ignore", "inherit", "inherit"],
    }).on("exit", onBuildExit);
  };

  if (process.platform === "win32" && serverProcess) {
    stoppedBeforeBuild = true;
    stopServer(runBuild, reason);
  } else {
    runBuild();
  }
}

/** 构建期间收到过变更时补跑一轮，避免“变更落在构建窗口里就永远不生效”。 */
function drainQueuedRestart() {
  if (!restartPending || shuttingDown) return;
  restartPending = false;
  if (building) return;
  buildAndRestart("补跑构建期间的变更");
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

function initialBuildAndStart() {
  building = true;
  log("正在执行初次构建...");
  const build = spawn("dotnet", ["build", project], {
    cwd: rootDir,
    env: { ...process.env },
    stdio: ["ignore", "inherit", "inherit"],
  });

  build.on("exit", (code) => {
    building = false;
    if (shuttingDown) return;
    if (code !== 0) {
      log(`初次构建失败 (code=${code})，等待源码变更修复后重试`);
      drainQueuedRestart();
      return;
    }
    startServer();
    if (process.stdout.isTTY) {
      log(`已就绪。防抖 ${debounceMs / 1_000}s，Ctrl+C 停止全部服务。`);
    }
    drainQueuedRestart();
  });
}

initialBuildAndStart();
