#!/usr/bin/env node
/**
 * ==============================================================================
 * 脚本名称: dev-tui.mjs
 * 主要职责: 为 scripts/dev.sh 提供无第三方依赖的本地开发 TUI 日志观察器
 *
 * 启动方式:
 *   node scripts/dev-tui.mjs --log-dir <临时日志目录> --parent-pid <dev.sh PID>
 *
 * 快捷键:
 *   q / Ctrl+C  停止 dev.sh 及其前后端进程
 *   a           显示全部日志
 *   b           只显示后端日志
 *   f           只显示前端日志
 *   r           立即刷新
 *
 * 该脚本只负责展示和发送停止信号，服务生命周期仍由 dev.sh 统一管理。
 * ==============================================================================
 */

import { closeSync, existsSync, openSync, readFileSync, readSync, statSync } from "node:fs";
import process from "node:process";

const args = parseArgs(process.argv.slice(2));
const logDir = args.get("log-dir");
const parentPid = Number.parseInt(args.get("parent-pid") ?? "", 10);

if (!logDir || !Number.isInteger(parentPid) || parentPid <= 0) {
  process.stderr.write("dev-tui.mjs 需要 --log-dir 和有效的 --parent-pid\n");
  process.exit(2);
}

const files = {
  main: `${logDir}/main.log`,
  backend: `${logDir}/backend.log`,
  frontend: `${logDir}/frontend.log`,
};
const labels = {
  main: "启动",
  backend: "后端",
  frontend: "前端",
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
const maxEvents = 1_000;
const offsets = new Map();
const pendingBytes = new Map();
const events = [];
const startedAt = Date.now();

let view = "all";
let shuttingDown = false;
let rendered = false;

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

function collectEvents() {
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
      if (line.length > 0) events.push({ source, line });
      lineStart = index + 1;
    }
    pendingBytes.set(source, combined.subarray(lineStart));
  }

  if (events.length > maxEvents) {
    events.splice(0, events.length - maxEvents);
  }
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
  return `${paint(ansi.muted, "│")} ${content}${" ".repeat(Math.max(0, innerWidth - cellWidth(content)))} ${paint(ansi.muted, "│")}`;
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

function eventLevel(line) {
  const normalized = line.toLowerCase();
  if (/error|exception|failed|失败|错误|fatal/.test(normalized)) return "ERR";
  if (/warn|warning|警告/.test(normalized)) return "WARN";
  if (/ready|listening|started|成功|就绪|启动/.test(normalized)) return "OK";
  return "LOG";
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
  return ansi.cyan;
}

function logRow(width, event) {
  const source = padCell(labels[event.source], 4);
  const level = padCell(eventLevel(event.line), 4);
  const prefixWidth = 4 + 1 + 4 + 1;
  const message = fit(event.line, Math.max(1, width - prefixWidth - 4));
  return frameRow(
    width,
    `${paint(sourceColor(event.source), source)} ${paint(levelColor(eventLevel(event.line)), level)} ${message}`,
  );
}

function viewTab(label, key, count, active) {
  const text = `${key} ${label} ${count}`;
  return active ? paint(`${ansi.bold}${ansi.cyan}`, `[${text}]`) : paint(ansi.muted, ` ${text} `);
}

function buildViewTabs() {
  const counts = { all: events.length, main: 0, backend: 0, frontend: 0 };
  for (const event of events) counts[event.source] += 1;
  return [
    viewTab("全部", "A", counts.all, view === "all"),
    viewTab("启动", "S", counts.main, view === "main"),
    viewTab("后端", "B", counts.backend, view === "backend"),
    viewTab("前端", "F", counts.frontend, view === "frontend"),
  ].join("  ");
}

function render() {
  if (shuttingDown) return;
  collectEvents();

  const width = Math.max(40, process.stdout.columns || 100);
  const height = Math.max(12, process.stdout.rows || 30);
  const state = readState();
  const backendPid = readPid("backend");
  const frontendPid = readPid("frontend");
  const stateText = paint(statusColor(state), `${ansi.bold}${state}`);
  const narrow = width < 76;
  const header = [
    borderTop(width, narrow ? "道化太初 · DEV" : "道化太初 · 本地开发控制台", ansi.cyan, formatClock()),
    frameRow(width, `${paint(ansi.bold, "DEV")}${paint(ansi.muted, "  ·  ")}阶段 ${stateText}${paint(ansi.muted, "  ·  ")}运行 ${paint(ansi.text, formatElapsed())}`),
    borderBottom(width),
  ];

  const cardWidth = Math.floor((width - 1) / 2);
  const backendCard = serviceCard(cardWidth, "后端服务", backendPid, "127.0.0.1:5128  ·  ws /ws", ansi.blue);
  const frontendCard = serviceCard(width - cardWidth - 1, "前端 Vite", frontendPid, "127.0.0.1:15172  ·  /ops", ansi.yellow);
  const cards = combineColumns(backendCard, frontendCard, width).lines;

  const logEvents = events.filter((event) => view === "all" || event.source === view);
  const logHeight = Math.max(2, height - header.length - cards.length - 5);
  const visible = logEvents.slice(-logHeight);
  const logs = [
    borderTop(width, "实时日志", ansi.cyan, narrow ? "" : buildViewTabs()),
    ...(visible.length === 0
      ? [frameRow(width, paint(ansi.dim, "等待服务输出 · 启动阶段日志会显示在这里"))]
      : visible.map((event) => logRow(width, event))),
    borderBottom(width),
  ];

  const footer = [
    frameRow(width, narrow
      ? `${paint(ansi.bold + ansi.cyan, "Q")} 退出  ${paint(ansi.bold + ansi.cyan, "A/S/B/F")} 日志  ${paint(ansi.bold + ansi.cyan, "R")} 刷新`
      : `${paint(ansi.muted, "快捷键")}  ${paint(ansi.bold + ansi.cyan, "Q")} 退出  ${paint(ansi.bold + ansi.cyan, "A/S/B/F")} 切换日志  ${paint(ansi.bold + ansi.cyan, "R")} 刷新`),
    paint(ansi.muted, `╰─ ${narrow ? "日志" : "日志目录"} ${fit(logDir, Math.max(1, width - 8))} ${"─".repeat(Math.max(1, width - cellWidth(logDir) - 10))}╯`),
  ];

  const lines = [...header, ...cards, ...logs, ...footer];
  while (lines.length < height) lines.push("");
  process.stdout.write(`\x1b[?25l\x1b[H\x1b[2J${lines.slice(0, height).join("\n")}`);
  rendered = true;
}


function stopParent(signal = "SIGINT") {
  if (shuttingDown) return;
  shuttingDown = true;
  try {
    process.kill(parentPid, signal);
  } catch {
    // dev.sh 可能已经退出；此时只需恢复终端。
  }
}

function restoreTerminal() {
  if (!rendered) return;
  if (process.stdin.isTTY) {
    process.stdin.setRawMode(false);
    process.stdin.pause();
  }
  process.stdout.write("\x1b[?25h\x1b[0m\x1b[?1049l");
}

function shutdown() {
  shuttingDown = true;
  restoreTerminal();
  process.exit(0);
}

if (process.stdin.isTTY) {
  process.stdin.setRawMode(true);
  process.stdin.resume();
  process.stdin.on("data", (buffer) => {
    for (const byte of buffer) {
      if (byte === 3 || byte === 113 || byte === 81) {
        stopParent();
      } else if (byte === 97 || byte === 65) {
        view = "all";
        render();
      } else if (byte === 115 || byte === 83) {
        view = "main";
        render();
      } else if (byte === 98 || byte === 66) {
        view = "backend";
        render();
      } else if (byte === 102 || byte === 70) {
        view = "frontend";
        render();
      } else if (byte === 114 || byte === 82) {
        render();
      }
    }
  });
  process.stdin.on("end", () => stopParent("SIGTERM"));
} else {
  process.stderr.write("dev-tui.mjs 需要在 TTY 中运行\n");
  process.exit(2);
}

process.stdout.on("resize", render);
process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

const timer = setInterval(() => {
  if (!existsSync(logDir)) {
    shutdown();
    return;
  }
  render();
}, 250);
timer.unref();

process.stdout.write("\x1b[?1049h\x1b[2J\x1b[H");
render();
