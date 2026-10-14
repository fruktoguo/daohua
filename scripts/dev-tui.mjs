#!/usr/bin/env node
/**
 * ==============================================================================
 * 脚本名称: dev-tui.mjs
 * 主要职责: 为 scripts/dev.sh 提供无第三方依赖的本地开发运维控制台（TUI）
 *
 * 启动方式:
 *   node scripts/dev-tui.mjs --log-dir <临时日志目录> --parent-pid <dev.sh PID> [--input-file <按键转发文件>]
 *   TTY 模式下由 dev.sh 前台拉起并直接读 /dev/tty（零转发延迟）；
 *   --input-file 为无头测试通道，dev.sh 正常路径不使用。
 *
 * 浏览模式快捷键:
 *   q             停止 dev.sh 及其前后端进程（Ctrl+C/Ctrl+D 等效）
 *   ↑↓ / j / k    滚动 1 行；PgUp/PgDn/Space 翻页；g/G 到顶/底（在底部时自动跟随）
 *   鼠标滚轮      滚动 3 行
 *   a / s / b / f 日志来源：全部 / 启动 / 后端 / 前端
 *   e / w / i     日志级别：仅错误 / 警告及以上 / 全部
 *   : / Enter     进入指令模式（help 查看全部指令）
 *   o             打开 http://daohua.localhost 运维面板
 *   r             立即刷新
 *
 * 指令模式:
 *   Enter 执行 · Esc 取消 · ↑↓ 历史 · Tab 命令补全 · Ctrl+A/E/K/U/W 行编辑
 *   查询类指令直连后端 /ops/api（127.0.0.1:5128，可用 DAOHUA_OPS_API 覆盖）；
 *   写操作要求面板令牌——启动时自动读取 src/Server/appsettings.Development.json
 *   的 Ops:Token（或 DAOHUA_OPS_TOKEN 环境变量），token <值> 可临时覆盖；
 *   危险写操作需输入 y 二次确认。
 *
 * 该脚本只负责展示、交互与发送停止信号，服务生命周期仍由 dev.sh 统一管理。
 * ==============================================================================
 */

import { closeSync, existsSync, openSync, readFileSync, readSync, statSync } from "node:fs";
import { spawn } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import process from "node:process";

const args = parseArgs(process.argv.slice(2));
const logDir = args.get("log-dir");
const parentPid = Number.parseInt(args.get("parent-pid") ?? "", 10);
const inputFile = args.get("input-file");

if (!logDir || !Number.isInteger(parentPid) || parentPid <= 0) {
  process.stderr.write("dev-tui.mjs 需要 --log-dir 和有效的 --parent-pid\n");
  process.exit(2);
}

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const apiBase = (process.env.DAOHUA_OPS_API ?? "http://127.0.0.1:5128/ops/api").replace(/\/+$/, "");
const localEntry = "http://daohua.localhost/";

const files = {
  main: `${logDir}/main.log`,
  backend: `${logDir}/backend.log`,
  frontend: `${logDir}/frontend.log`,
};
const labels = {
  main: "启动",
  backend: "后端",
  frontend: "前端",
  cmd: "指令",
};

const ansi = {
  reset: "\x1b[0m",
  bold: "\x1b[1m",
  dim: "\x1b[2m",
  text: "\x1b[37m",
  muted: "\x1b[90m",
  cyan: "\x1b[36m",
  blue: "\x1b[34m",
  magenta: "\x1b[35m",
  green: "\x1b[32m",
  yellow: "\x1b[33m",
  red: "\x1b[31m",
};
const maxEvents = 8_000;
const offsets = new Map();
const pendingBytes = new Map();
const events = [];
const startedAt = Date.now();

let sourceFilter = "all";
let levelFilter = "all";
let scrollOffset = 0;
let prevFlatRowsLength = 0;
let prevFilterKey = "all|all";
let mode = "browse";
let inputText = "";
let inputCursor = 0;
let histIndex = -1;
let histDraft = "";
const history = [];
let pendingAction = null;
let busyCommands = 0;
let shuttingDown = false;
let rendered = false;
let opsToken = loadOpsToken();

function parseArgs(values) {
  const result = new Map();
  for (let index = 0; index < values.length; index += 1) {
    const value = values[index];
    if (!value.startsWith("--")) continue;
    result.set(value.slice(2), values[index + 1] ?? "");
    index += 1;
  }
  return result;
}

function paint(color, value) {
  return `${color}${value}${ansi.reset}`;
}

function readText(path) {
  try {
    return readFileSync(path, "utf8");
  } catch {
    return "";
  }
}

function readState() {
  return readText(`${logDir}/state`).trim() || "准备中";
}

function readPid(name) {
  const value = Number.parseInt(readText(`${logDir}/${name}.pid`).trim(), 10);
  return Number.isInteger(value) && value > 0 ? value : null;
}

function isAlive(pid) {
  if (!pid) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

function loadOpsToken() {
  if (process.env.DAOHUA_OPS_TOKEN) return process.env.DAOHUA_OPS_TOKEN;
  try {
    const settings = JSON.parse(readFileSync(path.join(rootDir, "src/Server/appsettings.Development.json"), "utf8"));
    return settings?.Ops?.Token ?? "";
  } catch {
    return "";
  }
}

function eventLevel(line) {
  const normalized = line.toLowerCase();
  if (/\bfail:|\bcrit:|\berror\b|\bfatal\b|exception|traceback|失败|错误|unhandled|uncaught/.test(normalized)) return "ERR";
  if (/\bwarn:|\bwarning\b|\bwarn\b|警告/.test(normalized)) return "WARN";
  if (/\binfo:|ready|listening|started|成功|就绪|启动/.test(normalized)) return "OK";
  return "LOG";
}

function collectEvents() {
  let added = 0;
  for (const [source, path] of Object.entries(files)) {
    let size;
    try {
      size = statSync(path).size;
    } catch {
      continue;
    }

    let offset = offsets.get(source) ?? 0;
    if (size < offset) {
      offset = 0;
      pendingBytes.set(source, Buffer.alloc(0));
    }
    let remaining = size - offset;
    if (remaining <= 0) continue;

    const fd = openSync(path, "r");
    const chunks = [];
    try {
      while (remaining > 0) {
        const chunk = Buffer.allocUnsafe(Math.min(remaining, 65_536));
        const bytesRead = readSync(fd, chunk, 0, chunk.length, offset);
        if (bytesRead === 0) break;
        chunks.push(chunk.subarray(0, bytesRead));
        offset += bytesRead;
        remaining -= bytesRead;
      }
    } finally {
      closeSync(fd);
    }
    offsets.set(source, offset);

    const appended = chunks.length === 1 ? chunks[0] : Buffer.concat(chunks);
    const combined = Buffer.concat([pendingBytes.get(source) ?? Buffer.alloc(0), appended]);
    let lineStart = 0;
    for (let index = 0; index < combined.length; index += 1) {
      if (combined[index] !== 10) continue;
      let lineEnd = index;
      if (lineEnd > lineStart && combined[lineEnd - 1] === 13) lineEnd -= 1;
      const line = stripAnsi(combined.subarray(lineStart, lineEnd).toString("utf8"));
      if (line.length > 0) {
        events.push({ source, level: eventLevel(line), line, time: new Date(), wrapped: null });
        added += 1;
      }
      lineStart = index + 1;
    }
    pendingBytes.set(source, combined.subarray(lineStart));
  }

  if (events.length > maxEvents) {
    events.splice(0, events.length - maxEvents);
  }
  return added;
}

function emitCmd(lines, level = "LOG") {
  for (const entry of [].concat(lines)) {
    for (const part of String(entry).split("\n")) {
      events.push({ source: "cmd", level, line: part, time: new Date(), wrapped: null });
    }
  }
  if (events.length > maxEvents) events.splice(0, events.length - maxEvents);
  requestRender();
}

function stripAnsi(value) {
  return value.replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, "");
}

function cellWidth(value) {
  let width = 0;
  for (const character of stripAnsi(value)) {
    const code = character.codePointAt(0);
    const wide = code >= 0x1100 && (
      code <= 0x115f || code === 0x2329 || code === 0x232a
      || (code >= 0x2e80 && code <= 0xa4cf && code !== 0x303f)
      || (code >= 0xac00 && code <= 0xd7a3)
      || (code >= 0xf900 && code <= 0xfaff)
      || (code >= 0xfe10 && code <= 0xfe19)
      || (code >= 0xfe30 && code <= 0xfe6f)
      || (code >= 0xff00 && code <= 0xff60)
      || (code >= 0xffe0 && code <= 0xffe6)
      || (code >= 0x1f300 && code <= 0x1faff)
      || (code >= 0x20000 && code <= 0x3fffd)
    );
    width += wide ? 2 : 1;
  }
  return width;
}

function fit(text, width) {
  if (width <= 0) return "";
  let output = "";
  let used = 0;
  for (const character of stripAnsi(String(text))) {
    const nextWidth = cellWidth(character);
    if (used + nextWidth > width) return `${output}…`;
    output += character;
    used += nextWidth;
  }
  return output;
}

function wrapContent(text, width) {
  const rows = [];
  let current = "";
  let used = 0;
  for (const character of String(text)) {
    const nextWidth = cellWidth(character);
    if (used + nextWidth > width && current.length > 0) {
      rows.push(current);
      current = "";
      used = 0;
    }
    current += character;
    used += nextWidth;
  }
  rows.push(current);
  return rows;
}

function eventRows(event, width) {
  if (!event.wrapped || event.wrapped.width !== width) {
    event.wrapped = { width, rows: wrapContent(event.line, Math.max(8, width)) };
  }
  return event.wrapped.rows;
}

function padCell(text, width, align = "left") {
  const value = fit(text, width);
  const padding = Math.max(0, width - cellWidth(value));
  if (align === "right") return `${" ".repeat(padding)}${value}`;
  if (align === "center") {
    const left = Math.floor(padding / 2);
    return `${" ".repeat(left)}${value}${" ".repeat(padding - left)}`;
  }
  return `${value}${" ".repeat(padding)}`;
}

function borderTop(width, title, color = ansi.cyan, right = "") {
  const innerWidth = Math.max(4, width - 2);
  const left = `─ ${title} `;
  const rightPart = right ? ` ${right} ─` : "─";
  const fill = Math.max(1, innerWidth - cellWidth(left) - cellWidth(rightPart));
  return `╭${paint(color, left)}${paint(ansi.muted, "─".repeat(fill))}${paint(ansi.muted, rightPart)}╮`;
}

function borderBottom(width) {
  return paint(ansi.muted, `╰${"─".repeat(Math.max(1, width - 2))}╯`);
}

function frameRow(width, content) {
  const innerWidth = Math.max(1, width - 4);
  const fitted = fitAnsi(content, innerWidth);
  return `${paint(ansi.muted, "│")} ${fitted}${" ".repeat(Math.max(0, innerWidth - cellWidth(fitted)))} ${paint(ansi.muted, "│")}`;
}

// ANSI 安全的截断：保留转义序列，按可见宽度截断并在结尾补 reset
function fitAnsi(text, width) {
  const source = String(text);
  let output = "";
  let used = 0;
  let index = 0;
  while (index < source.length) {
    if (source[index] === "\x1b") {
      const match = source.slice(index).match(/^\x1b\[[0-?]*[ -/]*[@-~]/);
      if (match) {
        output += match[0];
        index += match[0].length;
        continue;
      }
    }
    const char = String.fromCodePoint(source.codePointAt(index));
    const charWidth = cellWidth(char);
    if (used + charWidth > width) return `${output}${ansi.reset}…`;
    output += char;
    used += charWidth;
    index += char.length;
  }
  return output;
}

function statusColor(state) {
  if (state === "运行中") return ansi.green;
  if (state === "正在停止") return ansi.yellow;
  if (state.includes("错误") || state.includes("失败")) return ansi.red;
  return ansi.cyan;
}

function formatElapsed() {
  const seconds = Math.floor((Date.now() - startedAt) / 1_000);
  const hours = Math.floor(seconds / 3_600);
  const minutes = Math.floor((seconds % 3_600) / 60);
  const remainder = seconds % 60;
  return hours > 0
    ? `${hours}h ${String(minutes).padStart(2, "0")}m`
    : `${minutes}m ${String(remainder).padStart(2, "0")}s`;
}

function formatClock() {
  return new Date().toLocaleTimeString("zh-CN", { hour12: false });
}

function formatHms(date) {
  return date.toLocaleTimeString("zh-CN", { hour12: false });
}

function formatIso(value) {
  if (!value) return "-";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? String(value) : formatHms(date);
}

function formatDurationSec(value) {
  if (value === null || value === undefined || !Number.isFinite(value)) return "-";
  const seconds = Math.round(value);
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function formatBytes(value) {
  if (!Number.isFinite(value)) return "-";
  const units = ["B", "KB", "MB", "GB"];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size.toFixed(unit === 0 ? 0 : 1)}${units[unit]}`;
}

function formatNum(value, digits = 0) {
  if (value === null || value === undefined || !Number.isFinite(value)) return "-";
  return Number(value).toLocaleString("zh-CN", { maximumFractionDigits: digits });
}

function serviceStatus(pid) {
  return pid && isAlive(pid) ? "运行中" : "等待中";
}

function serviceCard(width, title, pid, endpoint, color) {
  const status = serviceStatus(pid);
  const running = status === "运行中";
  const dot = running ? "●" : "○";
  const top = borderTop(width, title, color, status);
  const statusText = `${paint(running ? ansi.green : ansi.yellow, dot)} ${paint(running ? ansi.green : ansi.yellow, status)}`;
  const pidText = pid ? `PID ${pid}` : "尚未拉起";
  const row = frameRow(width, `${statusText}  ${paint(ansi.muted, padCell(pidText, Math.max(1, width - 20), "right"))}`);
  const endpointRow = frameRow(width, paint(ansi.dim, fit(endpoint, Math.max(1, width - 4))));
  return [top, row, endpointRow, borderBottom(width)];
}

function combineColumns(left, right, totalWidth) {
  const gap = 1;
  const leftWidth = Math.floor((totalWidth - gap) / 2);
  const rightWidth = totalWidth - gap - leftWidth;
  const lines = [];
  for (let index = 0; index < Math.max(left.length, right.length); index += 1) {
    const leftLine = left[index] ?? "";
    const rightLine = right[index] ?? "";
    lines.push(`${leftLine}${" ".repeat(gap)}${rightLine}`);
  }
  return { lines, leftWidth, rightWidth };
}

function levelColor(level) {
  if (level === "ERR") return ansi.red;
  if (level === "WARN") return ansi.yellow;
  if (level === "OK") return ansi.green;
  return ansi.muted;
}

function sourceColor(source) {
  if (source === "backend") return ansi.magenta;
  if (source === "frontend") return ansi.yellow;
  if (source === "cmd") return ansi.cyan;
  return ansi.cyan;
}

function levelRank(level) {
  if (level === "ERR") return 3;
  if (level === "WARN") return 2;
  return level === "OK" ? 1 : 0;
}

function passesFilter(event) {
  if (event.source === "cmd") return true;
  if (sourceFilter !== "all" && event.source !== sourceFilter) return false;
  if (levelFilter === "err") return event.level === "ERR";
  if (levelFilter === "warn") return levelRank(event.level) >= 2;
  return true;
}

function viewTab(label, key, active) {
  const text = `${key} ${label}`;
  return active ? paint(`${ansi.bold}${ansi.cyan}`, `[${text}]`) : paint(ansi.muted, ` ${text} `);
}

function buildFilterTabs() {
  const source = [
    viewTab("全部", "A", sourceFilter === "all"),
    viewTab("启动", "S", sourceFilter === "main"),
    viewTab("后端", "B", sourceFilter === "backend"),
    viewTab("前端", "F", sourceFilter === "frontend"),
  ].join(" ");
  const level = [
    viewTab("错误", "E", levelFilter === "err"),
    viewTab("警告+", "W", levelFilter === "warn"),
    viewTab("全级", "I", levelFilter === "all"),
  ].join(" ");
  return `${source} ${paint(ansi.muted, "│")} ${level}`;
}

function inputDisplay(maxCells) {
  const chars = [...inputText];
  const cursorCells = cellWidth(chars.slice(0, inputCursor).join(""));
  if (cellWidth(inputText) <= maxCells) return { text: inputText, cursorCells };
  let start = inputCursor;
  let used = 0;
  while (start > 0) {
    const charWidth = cellWidth(chars[start - 1]);
    if (used + charWidth > maxCells) break;
    used += charWidth;
    start -= 1;
  }
  return { text: chars.slice(start).join(""), cursorCells: used };
}

// ---------------------------------------------------------------------------
// /ops/api 指令层
// ---------------------------------------------------------------------------

class ApiError extends Error {
  constructor(status, code, message) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

async function apiGet(apiPath, params) {
  const query = params
    ? `?${new URLSearchParams(Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== ""))}`
    : "";
  let response;
  try {
    response = await fetch(`${apiBase}${apiPath}${query}`, {
      signal: AbortSignal.timeout(6_000),
      headers: { Accept: "application/json", ...(opsToken ? { "X-Ops-Token": opsToken } : {}) },
    });
  } catch {
    throw new ApiError(0, null, "后端不可达（服务未就绪或已停止）");
  }
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new ApiError(response.status, body?.code ?? null, body?.message ?? `HTTP ${response.status}`);
  return body;
}

async function apiPost(apiPath, payload) {
  if (!opsToken) {
    throw new ApiError(0, null, "未设置写操作令牌（token <值>，或配置 Ops:Token 后重启）");
  }
  let response;
  try {
    response = await fetch(`${apiBase}${apiPath}`, {
      method: "POST",
      signal: AbortSignal.timeout(60_000),
      headers: { "Content-Type": "application/json", "X-Ops-Token": opsToken },
      body: JSON.stringify(payload),
    });
  } catch {
    throw new ApiError(0, null, "后端不可达（服务未就绪或已停止）");
  }
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new ApiError(response.status, body?.code ?? null, body?.message ?? `HTTP ${response.status}`);
  return body;
}

function table(headers, rows, maxTotal = 118) {
  const widths = headers.map((header, index) => Math.min(
    36,
    Math.max(cellWidth(header), ...rows.map((row) => cellWidth(String(row[index] ?? "")))),
  ));
  // 终端宽度内放不下时从最宽列开始逐列收缩，保证整表不越界折行
  while (widths.reduce((sum, w) => sum + w, 0) + 2 * (widths.length - 1) > maxTotal) {
    const widest = widths.indexOf(Math.max(...widths));
    if (widths[widest] <= 6) break;
    widths[widest] -= 1;
  }
  const renderRow = (row) => row.map((cell, index) => padCell(String(cell ?? ""), widths[index])).join("  ");
  return [renderRow(headers), widths.map((width) => "─".repeat(width)).join("  "), ...rows.map(renderRow)];
}

function shortId(value) {
  return String(value ?? "").slice(0, 8);
}

const levelNameMap = {
  error: "Error", err: "Error", critical: "Critical", crit: "Critical", fatal: "Critical",
  warning: "Warning", warn: "Warning",
  information: "Information", info: "Information", log: "Information",
  debug: "Debug", dbug: "Debug", trace: "Trace", trce: "Trace",
};

async function resolvePlayerId(token) {
  const players = await apiGet("/players");
  const needle = String(token).toLowerCase();
  const exact = players.filter((p) => String(p.playerId).toLowerCase() === needle);
  const prefix = players.filter((p) => String(p.playerId).toLowerCase().startsWith(needle));
  const byName = players.filter((p) => String(p.displayName).includes(token));
  const matched = exact[0] ?? prefix[0] ?? byName[0];
  if (matched) return { player: matched, candidates: prefix.length + byName.length };
  return { player: null, candidates: 0 };
}

const commands = {
  help: {
    usage: "help",
    desc: "显示全部指令与快捷键",
    run: () => [
      "查询：overview | health | players | player <id|名> | sessions | scheduler | storage | compute | config",
      "      logs [级别] [条数] [关键词]（级别 error/warn/info/debug/trace，如 logs error 50 market）",
      "      errors | catalog [关键词] | market book <品种> | market orders <玩家> | market trades [n] | market metrics",
      "写操作（y 确认）：kick <玩家> | flush [玩家] | unload <玩家> | drain [节点] | sell/buy item|category|all … | cancel [品种]",
      "本地：token <值> | clear | open | quit（或浏览模式按 q）",
      "浏览模式：a/s/b/f 来源 · e/w/i 级别 · ↑↓/PgUp/g/G/滚轮 滚动 · : 指令 · o 面板 · q 退出",
      "指令模式：↑↓ 历史 · Tab 补全 · Esc 取消 · Ctrl+A/E/K/U/W 行编辑",
    ],
  },
  overview: {
    usage: "overview", alias: ["ov"], desc: "服务概览",
    run: async () => {
      const d = await apiGet("/overview");
      return [
        `服务 ${d.service} · 版本 ${d.version} · ${d.environment} · ${d.machineName}`,
        `运行 ${formatDurationSec(d.uptimeSeconds)} · PID ${d.processId} · 线程 ${d.threadCount} · CPU ${d.processorCount}`,
        `内存 workingSet ${formatBytes(d.workingSetBytes)} · GC ${formatBytes(d.gcTotalMemoryBytes)}`,
        `协议 v${d.protocolVersion} · schema v${d.schemaVersion} · 配表 ${d.gameDataVersion} (${String(d.gameDataFingerprint).slice(0, 12)})`,
        `已加载玩家 ${d.loadedPlayers} · 会话 ${d.sessions}`,
        `市场 挂单 ${d.market.activeOrderCount} · 下单 ${d.market.placementCount} · 撮合 ${d.market.matchCount} · 成交量 ${formatNum(d.market.tradedQuantity)}`,
      ];
    },
  },
  health: {
    usage: "health", desc: "健康检查",
    run: async () => {
      const d = await apiGet("/health");
      const lines = [`总体 ${d.status} · ${formatNum(d.totalDurationMs, 1)}ms`];
      for (const check of d.checks ?? []) {
        lines.push(`  ${padCell(check.status, 10)} ${padCell(`${formatNum(check.durationMs, 1)}ms`, 9, "right")} ${check.name}${check.description ? ` · ${check.description}` : ""}${check.exception ? ` · 异常 ${check.exception}` : ""}`);
      }
      return lines;
    },
  },
  players: {
    usage: "players", alias: ["ls"], desc: "已加载玩家",
    run: async () => {
      const players = await apiGet("/players");
      if (players.length === 0) return ["当前没有已加载玩家。"];
      return table(
        ["玩家ID", "道号", "状态", "动作", "资产", "上次模拟"],
        players.map((p) => [shortId(p.playerId), p.displayName, p.presence, p.actionCount, p.assetCount, formatIso(p.lastSimulatedAt)]),
      );
    },
  },
  player: {
    usage: "player <玩家ID前缀|道号>", desc: "玩家详情",
    run: async (a) => {
      if (!a[0]) throw new ApiError(0, null, "用法：player <玩家ID前缀|道号>");
      const { player, candidates } = await resolvePlayerId(a[0]);
      if (!player) throw new ApiError(0, null, `未找到玩家「${a[0]}」（仅匹配已加载玩家）`);
      const d = await apiGet(`/players/${player.playerId}`);
      const lines = [
        `${d.displayName} (${d.playerId}) · ${d.presence} · stateVersion ${d.stateVersion} · 账号 ${shortId(d.accountId)}`,
        `境界 Lv${d.progression.realmLevel} · 经验 ${formatNum(d.progression.experience)} · 修炼 ${formatDurationSec(d.progression.cultivationSeconds)} · 击杀 普${d.progression.normalKills}/精${d.progression.eliteKills}/王${d.progression.bossKills} · 败北 ${d.progression.totalDefeats}`,
        `气血 ${formatNum(d.combat.hitpoints)}/${formatNum(d.combat.maxHitpoints)} · 灵力 ${formatNum(d.combat.manapoints)}/${formatNum(d.combat.maxManapoints)} · 攻 ${formatNum(d.combat.attackPower)} · 防 ${formatNum(d.combat.defense)}`,
        `位置 ${d.world.nodeId} (${d.world.x},${d.world.y})${d.world.edgeId ? ` · 边上 ${d.world.edgeId} ${formatNum(d.world.edgeProgress * 100)}%` : ""}`,
        `队列 ${d.counts.actions} · 资产 ${d.counts.assets} · Buff ${d.counts.buffs} · 功法 ${d.counts.learnedAbilities} · 装备 ${d.counts.equippedInstances}`,
      ];
      if (candidates > 1) lines.push(`（按前缀/道号匹配到 ${candidates} 名已加载玩家，已取首个）`);
      const logs = d.recentLogs?.slice(-8) ?? [];
      if (logs.length > 0) {
        lines.push("最近日志：");
        for (const entry of logs) {
          lines.push(`  ${formatIso(new Date(entry.timestampUnixMilliseconds).toISOString())} ${entry.channel ?? ""} ${entry.template}${entry.count > 1 ? ` ×${entry.count}` : ""}`);
        }
      }
      return lines;
    },
  },
  sessions: {
    usage: "sessions", desc: "在线会话",
    run: async () => {
      const list = await apiGet("/sessions");
      if (list.length === 0) return ["当前没有在线会话。"];
      return table(
        ["玩家ID", "设备指纹", "关闭中"],
        list.map((s) => [s.playerId, s.deviceFingerprint ?? "-", s.closing ? "是" : "否"]),
      );
    },
  },
  scheduler: {
    usage: "scheduler", alias: ["sched"], desc: "调度与推进延迟",
    run: async () => {
      const d = await apiGet("/scheduler");
      const lines = [
        `已加载 ${d.loadedPlayers} · 下次推进 ${formatIso(d.nextAdvanceBoundaryUtc)}（${formatDurationSec(d.secondsToNextBoundary)}后）`,
        `推进延迟 p50 ${formatDurationSec(d.lag.p50Seconds)} · p95 ${formatDurationSec(d.lag.p95Seconds)} · 峰值 ${formatDurationSec(d.lag.maxSeconds)}`,
        `动作队列 合计 ${d.queuedActions.total} · p50 ${formatNum(d.queuedActions.p50, 1)} · 峰值 ${d.queuedActions.max}`,
        `在线分布 ${d.byPresence.map((item) => `${item.presence}×${item.count}`).join("  ") || "-"}`,
      ];
      const slowest = d.slowest?.slice(0, 10) ?? [];
      if (slowest.length > 0) {
        lines.push("推进欠债最深：");
        lines.push(...table(
          ["玩家ID", "道号", "延迟", "队列"],
          slowest.map((p) => [shortId(p.playerId), p.displayName, formatDurationSec(p.lagSeconds), p.queuedActions]),
        ));
      }
      return lines;
    },
  },
  storage: {
    usage: "storage", desc: "存储体量与落盘队列",
    run: async () => {
      const d = await apiGet("/storage");
      const lines = [
        `账号 ${d.accountCount} · 玩家 ${d.playerCount} · stateHead ${d.stateHeadCount} · 租约 ${d.activeLeaseCount}`,
        `outbox pending ${d.outboxPending} · leased ${d.outboxLeased} · delivered ${d.outboxDelivered} · retry ${d.outboxRetrying} · 最老待投 ${formatDurationSec(d.outboxOldestPendingSeconds)}`,
        `flushQ ${d.flushQueueDepth}（retry ${d.flushQueueRetrying} · 最老到期 ${formatDurationSec(d.flushQueueOldestDueSeconds)}）`,
        `section 行 ${formatNum(d.sectionRowCount)} · 原始 ${formatBytes(d.sectionRawBytes)} · 存储 ${formatBytes(d.sectionStoredBytes)}`,
        `schema 分布 ${d.sectionSchemaVersions?.map((b) => `v${b.schemaVersion}×${b.sectionCount}`).join("  ") || "-"}`,
      ];
      const sizes = (d.tableSizes ?? []).slice(0, 8);
      if (sizes.length > 0) {
        lines.push("表体量 TOP：");
        lines.push(...table(["表", "体量"], sizes.map((t) => [t.table, formatBytes(t.totalBytes)])));
      }
      return lines;
    },
  },
  compute: {
    usage: "compute", desc: "计算节点与派发链",
    run: async () => {
      const d = await apiGet("/compute");
      if (!d.enabled) return ["冷挂机派发链未启用（ComputeNode:Enabled 与令牌均未配置）。"];
      const t = d.totals;
      const lines = [
        `契约 ${d.contractVersion}/${d.dispatchContractVersion} · 在线节点 ${t.nodeCount} · 容量 ${t.capacity} 批 · 在飞 ${t.inFlight} · 待回 ${t.pendingResults} · 出站 ${t.outboundDepth}`,
      ];
      if (d.backlog) lines.push(`任务积压 ${d.backlog.pending}（到期 ${d.backlog.pendingDue}）· 租约 ${d.backlog.leased} · dead ${d.backlog.dead} · 最老 ${formatDurationSec(d.backlog.oldestPendingAgeSeconds)}`);
      if (d.metrics) {
        const m = d.metrics;
        lines.push(`批次 发${m.batchesWritten}/成${m.batchesCompleted}（失败 写${m.batchesFailedWrite} 超时${m.batchesFailedTimeout} 断${m.batchesFailedDisconnect} 约${m.batchesFailedContract} 异${m.batchesFailedError}）`);
        lines.push(`任务 领${m.jobsClaimed}/派${m.jobsDispatched}/成${m.jobsApplied}/死${m.jobsDead} · 拒绝 令${m.tokenRejected} 额${m.nodeLimitRejected} 纹${m.fingerprintRejected} 握${m.handshakeRejected}`);
      }
      if (d.nodes?.length > 0) {
        lines.push(...table(
          ["节点", "NodeId", "容量", "在飞", "出站", "拨入", "RTT 均/峰ms"],
          d.nodes.map((n) => [n.name + (n.draining ? "(排空中)" : ""), shortId(n.nodeId), n.capacity, n.inFlight, n.outboundDepth, formatDurationSec(n.uptimeSeconds), `${formatNum(n.avgRoundTripMs, 1)}/${formatNum(n.maxRoundTripMs, 1)}`]),
        ));
      }
      return lines;
    },
  },
  config: {
    usage: "config", desc: "配置摘要",
    run: async () => {
      const d = await apiGet("/config");
      const lines = [
        `服务 ${d.service} · ${d.environment} · 角色 ${d.processRole}`,
        `协议 v${d.protocolVersion} · schema v${d.schemaVersion} · 配表 ${d.gameDataVersion} (${String(d.gameDataFingerprint).slice(0, 12)})`,
      ];
      for (const [section, values] of Object.entries({ storage: d.storage, auth: d.auth, server: d.server, market: d.market, compute: d.compute, antiMultiOpen: d.antiMultiOpen })) {
        lines.push(`${section}: ${Object.entries(values).map(([k, v]) => `${k}=${v}`).join("  ")}`);
      }
      return lines;
    },
  },
  logs: {
    usage: "logs [级别] [条数] [cat:类别] [关键词]", desc: "服务端日志缓冲",
    run: async (a) => {
      // 参数智能解析：首个 token 是级别名→级别；是数字→条数；否则全部当关键词
      let rest = [...a];
      let level = "Information";
      let limit = 50;
      if (rest.length > 0 && levelNameMap[rest[0].toLowerCase()]) {
        level = levelNameMap[rest.shift().toLowerCase()];
      }
      if (rest.length > 0 && /^\d+$/.test(rest[0])) {
        limit = Math.min(500, Number(rest.shift()));
      }
      // cat:Xxx 前缀 token 走类别过滤，其余 token 并入关键词（正文/异常）
      let category;
      const words = [];
      for (const token of rest) {
        if (token.startsWith("cat:")) category = token.slice(4);
        else words.push(token);
      }
      const q = words.join(" ") || undefined;
      const list = await apiGet("/logs", { level, limit, category, q });
      const sorted = [...list].sort((x, y) => x.sequence - y.sequence);
      if (sorted.length === 0) return ["没有符合条件的日志。"];
      const lines = [];
      for (const entry of sorted) {
        lines.push(`${formatIso(entry.timestamp)} ${padCell(entry.level, 11)} ${entry.category}: ${entry.message}`);
        if (entry.exception) for (const l of String(entry.exception).split("\n")) lines.push(`      ${l}`);
      }
      return lines;
    },
  },
  errors: {
    usage: "errors [条数]", desc: "最近错误日志",
    run: async (a) => commands.logs.run(["error", a[0] ?? "30"]),
  },
  catalog: {
    usage: "catalog [关键词]", desc: "可挂售品种目录",
    run: async (a) => {
      const d = await apiGet("/market/catalog");
      const kw = a.join(" ").toLowerCase();
      const items = (d.items ?? []).filter((item) => !kw || item.id.toLowerCase().includes(kw) || item.name.includes(kw));
      if (items.length === 0) return ["没有匹配的可挂售品种。"];
      const lines = [`共 ${items.length} 个品种（品类：${(d.categories ?? []).join("、")}）`];
      lines.push(...table(
        ["品种ID", "名称", "品类", "缺省价"],
        items.slice(0, 40).map((item) => [item.id, item.name, `${item.category}/${item.subCategory}`, formatNum(item.sellPrice)]),
      ));
      if (items.length > 40) lines.push(`（仅显示前 40 条，用 catalog <关键词> 缩小范围）`);
      return lines;
    },
  },
  market: {
    usage: "market book <品种> [深度] | market orders <玩家> | market trades [n] | market metrics",
    desc: "市场查询",
    run: async (a) => {
      const sub = a[0];
      if (sub === "book") {
        if (!a[1]) throw new ApiError(0, null, "用法：market book <品种ID> [变体] [深度]");
        const variant = a[2] && !/^\d+$/.test(a[2]) ? a[2] : undefined;
        const depth = /^\d+$/.test(a[2] ?? "") ? a[2] : a[3] ?? 12;
        const d = await apiGet("/market/book", { assetId: a[1], variant, depth });
        const lines = [`${d.assetId}${d.variant ? `+${d.variant}` : ""} · 版本 ${d.version} · ${formatIso(d.capturedAt)}`];
        const rowCount = Math.max(d.asks?.length ?? 0, d.bids?.length ?? 0);
        lines.push(...table(
          ["卖价", "卖量", "卖单", "买价", "买量", "买单"],
          Array.from({ length: rowCount }, (_, i) => {
            const ask = d.asks?.[i];
            const bid = d.bids?.[i];
            return [
              ask ? formatNum(ask.price) : "", ask ? formatNum(ask.quantity) : "", ask ? ask.orders : "",
              bid ? formatNum(bid.price) : "", bid ? formatNum(bid.quantity) : "", bid ? bid.orders : "",
            ];
          }),
        ));
        return lines;
      }
      if (sub === "orders") {
        if (!a[1]) throw new ApiError(0, null, "用法：market orders <玩家ID>");
        const { player } = await resolvePlayerId(a[1]);
        if (!player) throw new ApiError(0, null, `未找到玩家「${a[1]}」`);
        const list = await apiGet("/market/orders", { playerId: player.playerId, limit: 50 });
        if (list.length === 0) return ["该玩家没有活动挂单。"];
        return table(
          ["订单", "方向", "品种", "价", "余量/总量", "状态"],
          list.map((o) => [shortId(o.orderId), o.side, o.assetId, formatNum(o.price), `${formatNum(o.remainingQuantity)}/${formatNum(o.originalQuantity)}`, o.status]),
        );
      }
      if (sub === "trades") {
        const list = await apiGet("/market/trades", { limit: a[1] ?? 30 });
        if (list.length === 0) return ["暂无成交。"];
        return table(
          ["时间", "品种", "价", "量", "买方", "卖方"],
          list.map((t) => [formatIso(t.tradedAt), t.assetId, formatNum(t.price), formatNum(t.quantity), shortId(t.buyerId), shortId(t.sellerId)]),
        );
      }
      if (sub === "metrics") {
        const d = await apiGet("/market/metrics");
        return [
          `就绪 ${d.ready} · 分片 ${d.partitionId}/${d.partitionCount} · 算法 ${d.algorithm}`,
          `挂单 ${d.metrics.activeOrderCount} · 下单 ${d.metrics.placementCount} · 撮合 ${d.metrics.matchCount} · 撤单 ${d.metrics.cancelCount} · 拒绝 ${d.metrics.rejectedCount}`,
          `成交量 ${formatNum(d.metrics.tradedQuantity)} · 成交额 ${formatNum(d.metrics.tradedGrossAmount)}`,
        ];
      }
      throw new ApiError(0, null, commands.market.usage);
    },
  },
  kick: {
    usage: "kick <玩家>", desc: "踢下线（写）",
    confirm: async (a) => `踢下线玩家 ${a[0]}（关闭码 4010，客户端会重连）`,
    run: async (a) => {
      const playerId = await resolvePlayerTarget(a[0]);
      const d = await apiPost("/actions/kick", { playerId });
      return [`已踢下线 ${d.playerId}（closeCode ${d.closeCode}）`];
    },
  },
  flush: {
    usage: "flush [玩家]", desc: "强制存档（写）",
    confirm: async (a) => (a[0] ? `强制存档玩家 ${a[0]}` : "强制存档全部已加载玩家"),
    run: async (a) => {
      const playerId = a[0] ? await resolvePlayerTarget(a[0]) : null;
      const d = await apiPost("/actions/flush", { playerId, force: true });
      return [d.scope === "all" ? `已强制存档全部 ${d.loadedPlayers} 名玩家` : `已强制存档玩家 ${d.playerId}`];
    },
  },
  unload: {
    usage: "unload <玩家>", desc: "卸载玩家运行时（写）",
    confirm: async (a) => `卸载玩家 ${a[0]}（先落盘再卸载；在线会话会被踢出）`,
    run: async (a) => {
      const playerId = await resolvePlayerTarget(a[0]);
      const d = await apiPost("/actions/unload", { playerId, flush: true });
      return [`已卸载 ${d.playerId}（先落盘=${d.flushed}）`];
    },
  },
  drain: {
    usage: "drain [节点]", desc: "排空计算节点（写）",
    confirm: async (a) => (a[0] ? `排空计算节点 ${a[0]}` : "排空全部在线计算节点"),
    run: async (a) => {
      const d = await apiPost("/actions/compute-drain", { nodeId: a[0] ?? null });
      return [d.scope === "all" ? `已下发全组排空（在线 ${d.nodeCount} 节点）` : `已排空节点 ${d.name} (${shortId(d.nodeId)})`];
    },
  },
  sell: {
    usage: "sell item <品种> [价] [量] | sell category <品类> [价] | sell all [价]", desc: "系统挂售（写）",
    confirm: async (a) => `系统批量挂售 ${validateOrderScope(a)}`,
    run: async (a) => runSystemOrder("sell", a),
  },
  buy: {
    usage: "buy item <品种> [价] [量] | buy category <品类> [价] | buy all [价]", desc: "系统收购（写）",
    confirm: async (a) => `系统批量收购 ${validateOrderScope(a)}`,
    run: async (a) => runSystemOrder("buy", a),
  },
  cancel: {
    usage: "cancel [品种]", desc: "撤销系统挂单（写）",
    confirm: async (a) => (a[0] ? `撤销品种 ${a[0]} 的全部系统挂单` : "撤销全部系统挂单"),
    run: async (a) => {
      const d = await apiPost("/actions/market-system-cancel", { assetId: a[0] ?? null });
      const lines = [`撤单完成：成功 ${d.cancelled} · 失败 ${d.failed}（范围 ${d.scope}）`];
      for (const item of (d.results ?? []).filter((r) => r.status === "failed").slice(0, 10)) {
        lines.push(`  失败 ${item.assetId} @${formatNum(item.price)}：${item.error}`);
      }
      return lines;
    },
  },
  token: {
    usage: "token <值>", desc: "设置写操作令牌",
    run: (a) => {
      if (!a[0]) return [opsToken ? "令牌已设置（已脱敏不显示）" : "令牌未设置"];
      opsToken = a[0];
      return ["令牌已更新（仅本次 TUI 会话生效）"];
    },
  },
  clear: {
    usage: "clear", desc: "清空事件流",
    run: () => {
      events.splice(0, events.length);
      return [];
    },
  },
  open: {
    usage: "open", desc: "打开本地运维面板",
    run: () => {
      const opener = process.platform === "darwin" ? "open" : "xdg-open";
      spawn(opener, [localEntry], { detached: true, stdio: "ignore" }).unref();
      return [`已打开 ${localEntry}`];
    },
  },
  quit: {
    usage: "quit", alias: ["exit", "q"], desc: "停止全部服务",
    run: () => {
      quit();
      return ["正在停止全部服务…"];
    },
  },
};

function validateOrderScope(a) {
  if (!["item", "category", "all"].includes(a[0])) {
    throw new ApiError(0, null, "用法：sell/buy item|category|all …（见 help）");
  }
  if (a[0] !== "all" && !a[1]) {
    throw new ApiError(0, null, `用法：sell/buy ${a[0]} <目标> [价] [量]`);
  }
  return a.join(" ");
}

async function resolvePlayerTarget(token) {
  // 已加载玩家优先（支持 ID 前缀与道号模糊匹配）；未命中且本身就是 GUID 时原样上送
  const { player } = await resolvePlayerId(token);
  if (player) return player.playerId;
  if (/^[0-9a-fA-F-]{32,36}$/.test(token)) return token;
  throw new ApiError(0, null, `未找到玩家「${token}」（仅匹配已加载玩家；可给完整 UUID）`);
}

async function runSystemOrder(side, a) {
  const scope = a[0];
  const payload = { side, scope, itemId: null, category: null, price: null, quantity: null };
  if (scope === "item") {
    if (!a[1]) throw new ApiError(0, null, `用法：${side} item <品种ID> [价] [量]`);
    payload.itemId = a[1];
    payload.price = a[2] ? Number(a[2]) : null;
    payload.quantity = a[3] ? Number(a[3]) : null;
  } else if (scope === "category") {
    if (!a[1]) throw new ApiError(0, null, `用法：${side} category <品类> [价] [量]`);
    payload.category = a[1];
    payload.price = a[2] ? Number(a[2]) : null;
    payload.quantity = a[3] ? Number(a[3]) : null;
  } else if (scope === "all") {
    payload.price = a[1] ? Number(a[1]) : null;
    payload.quantity = a[2] ? Number(a[2]) : null;
  } else {
    throw new ApiError(0, null, `用法：${side} item|category|all …（见 help）`);
  }
  const d = await apiPost("/actions/market-system-orders", payload);
  const lines = [`系统${side === "sell" ? "挂售" : "收购"}完成：请求 ${d.requested} · 成功 ${d.placed} · 失败 ${d.failed}（批次 ${shortId(d.batchId)}）`];
  for (const item of (d.results ?? []).filter((r) => r.status === "failed").slice(0, 10)) {
    lines.push(`  失败 ${item.assetId}：${item.error}`);
  }
  return lines;
}

const commandNames = Object.keys(commands);
const commandLookup = new Map();
for (const [name, cmd] of Object.entries(commands)) {
  commandLookup.set(name, cmd);
  for (const alias of cmd.alias ?? []) commandLookup.set(alias, cmd);
}

async function executeCommand(line) {
  const text = line.trim();
  if (!text) return;
  emitCmd([`❯ ${text}`], "OK");
  const tokens = text.split(/\s+/);
  const command = commandLookup.get(tokens[0].toLowerCase());
  if (!command) {
    emitCmd([`未知指令「${tokens[0]}」，输入 help 查看全部指令。`], "ERR");
    return;
  }
  const args = tokens.slice(1);
  busyCommands += 1;
  try {
    if (command.confirm) {
      const requiresArg = command.usage.split(" ")[1]?.startsWith("<");
      if (requiresArg && !args[0]) {
        emitCmd([`用法：${command.usage}`], "ERR");
        return;
      }
      pendingAction = { desc: await command.confirm(args), command, args };
      mode = "confirm";
      emitCmd([`⚠ ${pendingAction.desc} —— 输入 y 确认执行，其他键取消`], "WARN");
      return;
    }
    const lines = await command.run(args);
    emitCmd(lines);
  } catch (cause) {
    emitCmd([cause instanceof ApiError ? `失败：${cause.message}${cause.code ? ` (${cause.code})` : ""}` : `异常：${cause.message ?? cause}`], "ERR");
  } finally {
    busyCommands -= 1;
  }
}

// ---------------------------------------------------------------------------
// 终端输入解析：dev.sh 转发原始字节流（ANSI 转义序列 + UTF-8 文本）
// ---------------------------------------------------------------------------

let inputPending = Buffer.alloc(0);

function feedInput(chunk) {
  inputPending = Buffer.concat([inputPending, chunk]);
  while (inputPending.length > 0) {
    const byte = inputPending[0];
    if (byte === 0x1b) {
      const consumed = consumeEscape(inputPending);
      if (consumed === 0) break;
      inputPending = inputPending.subarray(consumed);
      continue;
    }
    if (byte < 0x20 || byte === 0x7f) {
      handleKey(controlKey(byte));
      inputPending = inputPending.subarray(1);
      continue;
    }
    const length = byte < 0xc0 ? 1 : byte < 0xe0 ? 2 : byte < 0xf0 ? 3 : 4;
    if (inputPending.length < length) break;
    handleKey({ key: "char", ch: inputPending.subarray(0, length).toString("utf8") });
    inputPending = inputPending.subarray(length);
  }
  // 每个输入批次末尾合帧渲染：按键/粘贴/滚轮连续到达时只重绘一次
  requestRender();
}

function consumeEscape(buf) {
  if (buf.length < 2) return 0;
  const second = buf[1];
  if (second === 0x5b) { // CSI
    for (let index = 2; index < Math.min(buf.length, 48); index += 1) {
      const c = buf[index];
      if (c >= 0x40 && c <= 0x7e) {
        const params = buf.subarray(2, index).toString("ascii");
        handleKey(csiKey(params, String.fromCharCode(c)));
        return index + 1;
      }
      if (c < 0x30 || c > 0x3f) {
        // 非法 CSI（如 ESC [ 后紧跟又一个 ESC）：放弃 ESC 本身，重新解析后续字节
        handleKey({ key: "escape" });
        return 1;
      }
    }
    return 0;
  }
  if (second === 0x4f) { // SS3
    if (buf.length < 3) return 0;
    const map = { 0x41: "up", 0x42: "down", 0x43: "right", 0x44: "left", 0x48: "home", 0x46: "end" };
    handleKey({ key: map[buf[2]] ?? "none" });
    return 3;
  }
  // 裸 ESC：后面不是序列头 → 视为 Esc 键
  handleKey({ key: "escape" });
  return 1;
}

function csiKey(params, final) {
  if (params.startsWith("<")) {
    // SGR 鼠标：<btn;x;yM（按下）/m（释放）——只关心滚轮 64/65
    const btn = Number.parseInt(params.slice(1), 10);
    if (final === "M" && btn === 64) return { key: "wheelUp" };
    if (final === "M" && btn === 65) return { key: "wheelDown" };
    return { key: "none" };
  }
  if (final === "A") return { key: "up" };
  if (final === "B") return { key: "down" };
  if (final === "C") return { key: "right" };
  if (final === "D") return { key: "left" };
  if (final === "H") return { key: "home" };
  if (final === "F") return { key: "end" };
  if (final === "Z") return { key: "tab" };
  if (final === "~") {
    const code = params.split(";")[0];
    if (code === "1" || code === "7") return { key: "home" };
    if (code === "3") return { key: "delete" };
    if (code === "4" || code === "8") return { key: "end" };
    if (code === "5") return { key: "pgup" };
    if (code === "6") return { key: "pgdn" };
  }
  return { key: "none" };
}

function controlKey(byte) {
  switch (byte) {
    case 1: return { key: "ctrlA" };
    case 3: return { key: "ctrlC" };
    case 4: return { key: "ctrlD" };
    case 5: return { key: "ctrlE" };
    case 8: case 127: return { key: "backspace" };
    case 9: return { key: "tab" };
    case 10: case 13: return { key: "enter" };
    case 11: return { key: "ctrlK" };
    case 12: return { key: "redraw" };
    case 21: return { key: "ctrlU" };
    case 23: return { key: "ctrlW" };
    default: return { key: "none" };
  }
}

// ---------------------------------------------------------------------------
// 按键分发：browse / input / confirm 三种模式
// ---------------------------------------------------------------------------

function handleKey(k) {
  if (!k || k.key === "none") return;
  if (process.env.DAOHUA_TUI_DEBUG) emitCmd([`key=${k.key}${k.ch ? ` ch=${k.ch}` : ""} mode=${mode} scroll=${scrollOffset}`]);
  if (k.key === "wheelUp" || k.key === "wheelDown") {
    scrollBy(k.key === "wheelUp" ? 3 : -3);
    return;
  }
  if (mode === "input") return inputKey(k);
  if (mode === "confirm") return confirmKey(k);
  return browseKey(k);
}

function browseKey(k) {
  if (k.key === "up") return scrollBy(1);
  if (k.key === "down") return scrollBy(-1);
  if (k.key === "pgup") return scrollBy(pageRows());
  if (k.key === "pgdn") return scrollBy(-pageRows());
  if (k.key === "home") return scrollTop();
  if (k.key === "end") return scrollBottom();
  if (k.key === "enter") { mode = "input"; return; }
  if (k.key === "ctrlC" || k.key === "ctrlD") return quit();
  if (k.key === "redraw") return render();
  if (k.key !== "char") return;
  switch (k.ch) {
    case "q": case "Q": quit(); break;
    case "a": case "A": sourceFilter = "all"; break;
    case "s": case "S": sourceFilter = "main"; break;
    case "b": case "B": sourceFilter = "backend"; break;
    case "f": case "F": sourceFilter = "frontend"; break;
    case "e": case "E": levelFilter = "err"; break;
    case "w": case "W": levelFilter = "warn"; break;
    case "i": case "I": levelFilter = "all"; break;
    case "j": scrollBy(-1); break;
    case "k": scrollBy(1); break;
    case " ": scrollBy(-pageRows()); break;
    case "g": scrollTop(); break;
    case "G": scrollBottom(); break;
    case ":": case "/": mode = "input"; break;
    case "?": void executeCommand("help"); break;
    case "o": case "O": void executeCommand("open"); break;
    case "r": case "R": render(); break;
    default: break;
  }
}

function inputKey(k) {
  switch (k.key) {
    case "escape":
      resetInput();
      mode = "browse";
      return;
    case "enter": {
      const text = inputText;
      resetInput();
      mode = "browse";
      if (text.trim()) {
        if (history[history.length - 1] !== text) history.push(text);
        if (history.length > 100) history.shift();
      }
      void executeCommand(text);
      return;
    }
    case "up":
      if (history.length === 0) return;
      if (histIndex === -1) {
        histDraft = inputText;
        histIndex = history.length - 1;
      } else if (histIndex > 0) {
        histIndex -= 1;
      }
      setInput(history[histIndex]);
      return;
    case "down":
      if (histIndex === -1) return;
      histIndex += 1;
      if (histIndex >= history.length) {
        histIndex = -1;
        setInput(histDraft);
      } else {
        setInput(history[histIndex]);
      }
      return;
    case "left": inputCursor = Math.max(0, inputCursor - 1); return;
    case "right": inputCursor = Math.min([...inputText].length, inputCursor + 1); return;
    case "home": case "ctrlA": inputCursor = 0; return;
    case "end": case "ctrlE": inputCursor = [...inputText].length; return;
    case "backspace":
      if (inputCursor > 0) {
        inputText = spliceInput(inputCursor - 1, 1, "");
        inputCursor -= 1;
      }
      return;
    case "delete":
      inputText = spliceInput(inputCursor, 1, "");
      return;
    case "ctrlK":
      inputText = spliceInput(inputCursor, [...inputText].length - inputCursor, "");
      return;
    case "ctrlU":
      inputText = spliceInput(0, inputCursor, "");
      inputCursor = 0;
      return;
    case "ctrlW": {
      const chars = [...inputText];
      let pos = inputCursor;
      while (pos > 0 && chars[pos - 1] === " ") pos -= 1;
      while (pos > 0 && chars[pos - 1] !== " ") pos -= 1;
      inputText = spliceInput(pos, inputCursor - pos, "");
      inputCursor = pos;
      return;
    }
    case "tab": {
      const prefixText = [...inputText].slice(0, inputCursor).join("");
      if (prefixText.includes(" ")) return;
      const matched = commandNames.filter((name) => name.startsWith(prefixText.toLowerCase()));
      if (matched.length === 1) {
        setInput(`${matched[0]} `);
      } else if (matched.length > 1) {
        emitCmd([matched.join("  ")]);
        setInput(commonPrefix(matched));
      }
      return;
    }
    case "ctrlC":
      if (inputText.length > 0) {
        resetInput();
      } else {
        resetInput();
        mode = "browse";
      }
      return;
    case "pgup": scrollBy(pageRows()); return;
    case "pgdn": scrollBy(-pageRows()); return;
    case "char": {
      inputText = spliceInput(inputCursor, 0, k.ch);
      inputCursor += [...k.ch].length;
      histIndex = -1;
      return;
    }
    default: return;
  }
}

function confirmKey(k) {
  const yes = k.key === "char" && (k.ch === "y" || k.ch === "Y");
  const action = pendingAction;
  pendingAction = null;
  mode = "browse";
  if (!yes || !action) {
    emitCmd(["已取消。"]);
    return;
  }
  busyCommands += 1;
  void (async () => {
    try {
      const lines = await action.command.run(action.args);
      emitCmd(lines);
    } catch (cause) {
      emitCmd([cause instanceof ApiError ? `失败：${cause.message}${cause.code ? ` (${cause.code})` : ""}` : `异常：${cause.message ?? cause}`], "ERR");
    } finally {
      busyCommands -= 1;
    }
  })();
}

function spliceInput(pos, remove, insert) {
  const chars = [...inputText];
  chars.splice(pos, remove, ...[...insert]);
  return chars.join("");
}

function setInput(text) {
  inputText = text;
  inputCursor = [...text].length;
}

function resetInput() {
  inputText = "";
  inputCursor = 0;
  histIndex = -1;
  histDraft = "";
}

function commonPrefix(names) {
  let prefix = names[0];
  for (const name of names.slice(1)) {
    while (!name.startsWith(prefix)) prefix = prefix.slice(0, -1);
  }
  return prefix;
}

// ---------------------------------------------------------------------------
// 渲染
// ---------------------------------------------------------------------------

function pageRows() {
  return Math.max(3, (process.stdout.rows || 30) - 14);
}

function scrollBy(delta) {
  scrollOffset = Math.max(0, scrollOffset + delta);
}

function scrollTop() {
  scrollOffset = maxEvents;
}

function scrollBottom() {
  scrollOffset = 0;
}

// buildFrame 只构建纯帧数据；渲染写出由 render() 的行级 diff 完成
function buildFrame() {
  const width = Math.max(40, process.stdout.columns || 100);
  const height = Math.max(12, process.stdout.rows || 30);
  const state = readState();
  const backendPid = readPid("backend");
  const frontendPid = readPid("frontend");
  const stateText = paint(statusColor(state), `${ansi.bold}${state}`);
  const narrow = width < 76;
  const tokenText = opsToken ? paint(ansi.green, "令牌已加载") : paint(ansi.yellow, "令牌未设置");
  const busyText = busyCommands > 0 ? `${paint(ansi.muted, "  ·  ")}${paint(ansi.yellow, "指令执行中…")}` : "";
  const header = [
    borderTop(width, narrow ? "道化太初 · DEV" : "道化太初 · 本地开发控制台", ansi.cyan, formatClock()),
    frameRow(width, `${paint(ansi.bold, "DEV")}${paint(ansi.muted, "  ·  ")}阶段 ${stateText}${paint(ansi.muted, "  ·  ")}运行 ${paint(ansi.text, formatElapsed())}${paint(ansi.muted, "  ·  ")}${tokenText}${busyText}`),
    borderBottom(width),
  ];

  const cardWidth = Math.floor((width - 1) / 2);
  const backendCard = serviceCard(cardWidth, "后端服务", backendPid, "127.0.0.1:5128  ·  ws /ws", ansi.blue);
  const frontendCard = serviceCard(width - cardWidth - 1, "前端 Vite", frontendPid, "127.0.0.1:15172  ·  /ops", ansi.yellow);
  const cards = combineColumns(backendCard, frontendCard, width).lines;

  // 日志区：事件 → 过滤 → 按内容宽度折行 → 视口裁剪
  const innerWidth = Math.max(10, width - 4);
  const prefixWidth = 8 + 1 + 4 + 1 + 4 + 1; // [HH:mm:ss] + 来源 + 级别
  const contentWidth = Math.max(8, innerWidth - prefixWidth);
  const flatRows = [];
  for (const event of events) {
    if (!passesFilter(event)) continue;
    const rows = eventRows(event, contentWidth);
    for (let index = 0; index < rows.length; index += 1) {
      flatRows.push({ event, first: index === 0, content: rows[index] });
    }
  }

  const footerRows = 2;
  const logHeight = Math.max(2, height - header.length - cards.length - 2 - footerRows);
  const maxScroll = Math.max(0, flatRows.length - logHeight);
  // 向上翻阅时钉住当前内容：新日志涌入只累加偏移，不把视口顶走；跟随模式不受影响。
  // 过滤器切换导致的行数变化不算新增（prevFilterKey 不一致时重置基线）。
  const filterKey = `${sourceFilter}|${levelFilter}`;
  const rowsAdded = filterKey === prevFilterKey ? flatRows.length - prevFlatRowsLength : 0;
  prevFilterKey = filterKey;
  prevFlatRowsLength = flatRows.length;
  if (scrollOffset > 0 && rowsAdded > 0) scrollOffset += rowsAdded;
  scrollOffset = Math.min(scrollOffset, maxScroll);
  const end = flatRows.length - scrollOffset;
  const visible = flatRows.slice(Math.max(0, end - logHeight), end);

  const scrollInfo = scrollOffset > 0
    ? paint(ansi.yellow, `↑${scrollOffset} 行（G 回底）`)
    : paint(ansi.muted, "跟随");
  const logHeaderRight = narrow ? scrollInfo : `${buildFilterTabs()} ${paint(ansi.muted, "│")} ${scrollInfo}`;

  const logBody = [];
  if (visible.length === 0) {
    logBody.push(frameRow(width, paint(ansi.dim, "等待服务输出 · 启动阶段日志会显示在这里")));
  } else {
    for (const row of visible) {
      const { event, first, content } = row;
      if (first) {
        const prefix = `${paint(ansi.dim, formatHms(event.time))} ${paint(sourceColor(event.source), padCell(labels[event.source], 4))} ${paint(levelColor(event.level), padCell(event.level, 4))} `;
        const message = event.source === "cmd" && event.level === "OK" ? paint(ansi.cyan, content) : content;
        logBody.push(frameRow(width, `${prefix}${message}`));
      } else {
        logBody.push(frameRow(width, `${" ".repeat(prefixWidth)}${paint(ansi.dim, content)}`));
      }
    }
  }
  // 日志区不足一屏时用空行补齐，保证输入栏与边框始终钉在屏幕底部
  while (logBody.length < logHeight) logBody.push(frameRow(width, ""));
  const logs = [borderTop(width, `日志（${flatRows.length} 行）`, ansi.cyan, logHeaderRight), ...logBody, borderBottom(width)];

  const footer = [];
  if (mode === "input") {
    const { text, cursorCells } = inputDisplay(innerWidth - 2);
    const hint = "Enter 执行 · Esc 取消 · ↑↓ 历史 · Tab 补全 · Ctrl+A/E/K/U/W 编辑";
    footer.push(frameRow(width, `${paint(ansi.bold + ansi.green, "❯")} ${text}`));
    footer.push(paint(ansi.muted, `╰─ ${fit(hint, Math.max(1, width - 5))} ${"─".repeat(Math.max(1, width - cellWidth(hint) - 5))}╯`));
    footer.cursorCells = 3 + 2 + cursorCells;
  } else if (mode === "confirm" && pendingAction) {
    footer.push(frameRow(width, `${paint(ansi.yellow, "⚠")} ${paint(ansi.bold, "确认执行")}：${pendingAction.desc}`));
    footer.push(paint(ansi.muted, `╰─ y 确认执行 · 其他键取消 ${"─".repeat(Math.max(1, width - 24))}╯`));
  } else {
    const hints = narrow
      ? "Q 退出 · : 指令 · ↑↓ 滚动"
      : `${paint(ansi.muted, "快捷键")}  ${paint(ansi.bold + ansi.cyan, "Q")} 退出  ${paint(ansi.bold + ansi.cyan, ":")} 指令  ${paint(ansi.bold + ansi.cyan, "O")} 面板  ${paint(ansi.bold + ansi.cyan, "A/S/B/F")} 来源  ${paint(ansi.bold + ansi.cyan, "E/W/I")} 级别  ${paint(ansi.bold + ansi.cyan, "↑↓/PgUp/g/G")} 滚动`;
    footer.push(frameRow(width, hints));
    footer.push(paint(ansi.muted, `╰─ ${narrow ? "日志" : "日志目录"} ${fit(logDir, Math.max(1, width - 8))} ${"─".repeat(Math.max(1, width - cellWidth(logDir) - 10))}╯`));
  }

  const inputRowIndex = header.length + cards.length + logs.length; // 0 基，footer 首行
  const lines = [...header, ...cards, ...logs, ...footer];
  while (lines.length < height) lines.push("");
  const cursor = mode === "input"
    ? { row: inputRowIndex + 1, col: Math.max(1, Math.min(width, footer.cursorCells)) }
    : null;
  return { lines: lines.slice(0, height), cursor };
}

// 行级 diff 渲染（参照 Claude Code/Codex 类 TUI）：只重写内容变化的行，
// 无变化时零输出；滚动/输入等交互即时出帧，不再有 200ms 整屏重拍。
let prevLines = [];
let forceFullRepaint = true;

function render() {
  if (shuttingDown) return;
  collectEvents();

  const { lines, cursor } = buildFrame();
  const out = ["\x1b[?25l"];
  if (forceFullRepaint) {
    out.push("\x1b[H\x1b[2J");
    prevLines = [];
    forceFullRepaint = false;
  }
  for (let row = 0; row < lines.length; row += 1) {
    if (lines[row] === prevLines[row]) continue;
    out.push(`\x1b[${row + 1};1H${lines[row]}\x1b[0K`);
  }
  prevLines = lines;
  if (cursor) out.push(`\x1b[?25h\x1b[${cursor.row};${cursor.col}H`);
  process.stdout.write(out.join(""));
  rendered = true;
}

// 渲染合帧：同一次 tick 内多处状态变化只落一帧（setImmediate 合并突发输入/日志写入）
let renderQueued = false;
function requestRender() {
  if (renderQueued || shuttingDown) return;
  renderQueued = true;
  setImmediate(() => {
    renderQueued = false;
    render();
  });
}

// ---------------------------------------------------------------------------
// 生命周期
// ---------------------------------------------------------------------------

// 退出即结束整个开发会话：先通知 dev.sh 走清理（TTY 模式下 TUI 是前台子进程，
// dev.sh 拿不到它的 pid 无法反向 kill），随后自行恢复终端并退出。
function quit(signal = "SIGINT") {
  try {
    process.kill(parentPid, signal);
  } catch {
    // dev.sh 可能已经退出；此时只需恢复终端。
  }
  shutdown();
}

function restoreTerminal() {
  if (!rendered) return;
  if (process.stdin.isTTY) {
    process.stdin.setRawMode(false);
    process.stdin.pause();
  }
  process.stdout.write("\x1b[?25h\x1b[0m\x1b[?1006l\x1b[?1000l\x1b[?1049l");
}

function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  restoreTerminal();
  process.exit(0);
}

function parentAlive() {
  try {
    process.kill(parentPid, 0);
    return true;
  } catch {
    return false;
  }
}

let inputOffset = 0;
function readInputFile() {
  if (!inputFile) return;
  let size;
  try {
    size = statSync(inputFile).size;
  } catch {
    return;
  }
  if (size < inputOffset) inputOffset = 0;
  const remaining = size - inputOffset;
  if (remaining <= 0) return;
  const fd = openSync(inputFile, "r");
  try {
    const chunk = Buffer.allocUnsafe(remaining);
    const bytesRead = readSync(fd, chunk, 0, chunk.length, inputOffset);
    inputOffset += bytesRead;
    feedInput(chunk.subarray(0, bytesRead));
  } finally {
    closeSync(fd);
  }
}

if (inputFile) {
  process.stdin.pause();
} else if (process.stdin.isTTY) {
  process.stdin.setRawMode(true);
  process.stdin.resume();
  process.stdin.on("data", (buffer) => feedInput(buffer));
  process.stdin.on("end", () => quit("SIGTERM"));
} else {
  process.stderr.write("dev-tui.mjs 需要在 TTY 中运行，或传入 --input-file\n");
  process.exit(2);
}

process.stdout.on("resize", () => {
  forceFullRepaint = true;
  requestRender();
});
process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

// 低速轮询：日志文件尾部、--input-file 测试通道、残缺转义序列兜底、
// 父进程存活与秒级时钟。渲染本身事件驱动，此循环不直接出帧。
let pendingIdleMs = 0;
let lastClockSecond = -1;
const timer = setInterval(() => {
  readInputFile();
  // 转义序列残缺兜底：输入停在半个转义序列超过 ~500ms 视为裸 Esc；
  // 半截 UTF-8 字符等非 ESC 起始残包同样丢弃，但不触发 Esc 语义。
  if (inputPending.length > 0) {
    pendingIdleMs += 250;
    if (pendingIdleMs > 375) {
      const wasEscape = inputPending[0] === 0x1b;
      inputPending = Buffer.alloc(0);
      pendingIdleMs = 0;
      if (wasEscape) {
        handleKey({ key: "escape" });
        requestRender();
      }
    }
  } else {
    pendingIdleMs = 0;
  }
  if (!existsSync(logDir) || !parentAlive()) {
    shutdown();
    return;
  }
  if (collectEvents() > 0) requestRender();
  const second = Math.floor(Date.now() / 1000);
  if (second !== lastClockSecond) {
    lastClockSecond = second;
    requestRender(); // 顶栏时钟每秒刷新一次
  }
}, 250);
if (!inputFile) timer.unref();

process.stdout.write("\x1b[?1049h\x1b[?1000h\x1b[?1006h");
render();
