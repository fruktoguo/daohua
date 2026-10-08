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
const colors = {
  main: "\x1b[36m",
  backend: "\x1b[35m",
  frontend: "\x1b[33m",
};
const reset = "\x1b[0m";
const maxEvents = 1_000;
const offsets = new Map();
const pendingBytes = new Map();
const events = [];

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
      const line = combined.subarray(lineStart, lineEnd).toString("utf8");
      if (line.length > 0) events.push({ source, line });
      lineStart = index + 1;
    }
    pendingBytes.set(source, combined.subarray(lineStart));
  }

  if (events.length > maxEvents) {
    events.splice(0, events.length - maxEvents);
  }
}

function cellWidth(value) {
  let width = 0;
  for (const character of value) {
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
  for (const character of String(text)) {
    const nextWidth = cellWidth(character);
    if (used + nextWidth > width) return `${output}…`;
    output += character;
    used += nextWidth;
  }
  return output;
}

function horizontal(width) {
  return "─".repeat(Math.max(1, width));
}

function statusLine(name, pid) {
  const state = isAlive(pid) ? "运行中" : "未运行";
  return `${name.padEnd(4, " ")} ${state.padEnd(3, " ")} ${pid ? `PID ${pid}` : ""}`;
}

function render() {
  if (shuttingDown) return;
  collectEvents();

  const width = Math.max(40, process.stdout.columns || 100);
  const height = Math.max(12, process.stdout.rows || 30);
  const backendPid = readPid("backend");
  const frontendPid = readPid("frontend");
  const logLines = events.filter((event) => view === "all" || event.source === view);
  const visible = logLines.slice(-Math.max(1, height - 9));
  const lines = [
    `${colors.main}道化太初 本地开发${reset}  ${readState()}  ${new Date().toLocaleTimeString("zh-CN", { hour12: false })}`,
    horizontal(width),
    `${statusLine("后端", backendPid)}    ${statusLine("前端", frontendPid)}    日志：${view === "all" ? "全部" : labels[view]}`,
    "快捷键  q/Ctrl+C 停止  a 全部  b 后端  f 前端  r 刷新",
    horizontal(width),
  ];

  if (visible.length === 0) {
    lines.push("等待日志……");
  } else {
    for (const event of visible) {
      const label = `[${labels[event.source]}]`;
      const prefix = `${colors[event.source]}${label}${reset}`;
      lines.push(`${prefix} ${fit(event.line, width - cellWidth(`${label} `))}`);
    }
  }

  while (lines.length < height - 1) lines.push("");
  lines.push(`${colors.main}日志窗口${reset}  ${logDir}`);
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
