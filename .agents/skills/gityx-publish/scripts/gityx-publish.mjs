#!/usr/bin/env node

/**
 * Git游戏（gityx.com）用户中心发帖 CLI 工具
 * 基于 https://gityx.com/user/release.html 的真实表单与接口契约封装，
 * 支持鉴权探测、专栏/分类拉取、动态字段查询、封面上传、Markdown 转 HTML 与发布。
 */

import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { pathToFileURL } from 'node:url';

const BASE_URL = 'https://gityx.com';

const ENDPOINTS = {
  posts: `${BASE_URL}/user/posts.html`,
  release: `${BASE_URL}/user/release.html`,
  getClass: `${BASE_URL}/user/getclass.html`,
  getFields: `${BASE_URL}/common/get_fields.html`,
  upload: `${BASE_URL}/common/uploads.html`,
};

/** 需按 PHP 数组格式 `name[]` 序列化的表单字段 */
export const ARRAY_FIELDS = new Set(['game_platform', 'game_only', 'types']);

/** 常用项目发帖预设（可被命令行显式参数覆盖） */
export const PRESETS = {
  'daohua-changelog': {
    molds: 'article',
    tid: '65',
    short_title: '道化太初',
    keywords: '更新日志,道化太初',
    litpic: 'https://cdn.g8hh.cn/static/images/game/tg-daohuataichu/face.jpg',
    game_only: ['3'],
    zuozhe: '尐妖',
    qqun: '940886387',
    gameurl: 'https://gityx.com/web/online/1575.html',
  },
  'daohua-guide': {
    molds: 'article',
    tid: '9',
    short_title: '道化太初',
    keywords: '教程攻略,道化太初',
    litpic: 'https://cdn.g8hh.cn/static/images/game/tg-daohuataichu/face.jpg',
    game_only: ['3'],
    zuozhe: '尐妖',
    qqun: '940886387',
    gameurl: 'https://gityx.com/web/online/1575.html',
  },
};

const MIME_BY_EXT = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.gif': 'image/gif',
};

/**
 * 解析 `.env` 格式文本为键值对象
 * @param {string} content
 * @returns {Record<string, string>}
 */
export function parseEnvText(content) {
  const result = {};
  for (const rawLine of content.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;
    const eqIndex = line.indexOf('=');
    if (eqIndex <= 0) continue;
    const key = line.slice(0, eqIndex).trim();
    let value = line.slice(eqIndex + 1).trim();
    if (
      (value.startsWith('"') && value.endsWith('"')) ||
      (value.startsWith("'") && value.endsWith("'"))
    ) {
      value = value.slice(1, -1);
    }
    result[key] = value;
  }
  return result;
}

/**
 * 按优先级解析 Gityx 请求 Cookie 头：
 * 1. `--cookie` 参数
 * 2. `GITYX_COOKIE` 环境变量
 * 3. `GITYX_PHPSESSID` 等分项环境变量
 * 4. 仓库根目录 `.env.gityx`（或 `--env-file` 指定路径）
 * @param {object} options
 * @param {Record<string, string | undefined>} [env]
 * @returns {Promise<string>}
 */
export async function resolveCookieHeader(options = {}, env = process.env) {
  if (options.cookie) {
    return String(options.cookie).trim();
  }
  if (env.GITYX_COOKIE) {
    return String(env.GITYX_COOKIE).trim();
  }

  let fileVars = {};
  const envFile = options.envFile || path.resolve(process.cwd(), '.env.gityx');
  try {
    const raw = await fs.readFile(envFile, 'utf8');
    fileVars = parseEnvText(raw);
  } catch (err) {
    if (err?.code !== 'ENOENT' || options.envFile) {
      throw new Error(`读取 Cookie 配置文件失败 (${envFile}): ${err.message}`);
    }
  }

  if (fileVars.GITYX_COOKIE) {
    return fileVars.GITYX_COOKIE.trim();
  }

  const phpsessid = env.GITYX_PHPSESSID || fileVars.GITYX_PHPSESSID;
  const did = env.GITYX_DID || fileVars.GITYX_DID;
  const everLogin = env.GITYX_EVER_LOGIN || fileVars.GITYX_EVER_LOGIN;
  const homeVer = env.GITYX_HOME_VER || fileVars.GITYX_HOME_VER;

  if (!phpsessid) {
    throw new Error(
      '缺少 Gityx 登录态 Cookie：请在 .env.gityx 中配置 GITYX_PHPSESSID（及可选 GITYX_DID），或通过 GITYX_COOKIE / --cookie 传入。'
    );
  }

  const parts = [`PHPSESSID=${phpsessid}`];
  if (did) parts.push(`gtyx_did=${did}`);
  if (everLogin) parts.push(`gtyx_ever_login=${everLogin}`);
  if (homeVer) parts.push(`gityx_home_ver=${homeVer}`);
  return parts.join('; ');
}

/**
 * 格式化本地时间为 `YYYY-MM-DD HH:mm:ss`
 * @param {Date} [date]
 * @returns {string}
 */
export function formatLocalDateTime(date = new Date()) {
  const pad = (n) => String(n).padStart(2, '0');
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
  );
}

/**
 * HTML 特殊字符转义
 * @param {string} text
 * @returns {string}
 */
function escapeHtml(text) {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/**
 * 轻量内联 Markdown 转 HTML（粗体、斜体、行内代码、链接、图片）
 * @param {string} text
 * @returns {string}
 */
function renderInlineMarkdown(text) {
  const codeSpans = [];
  let work = text.replace(/`([^`]+)`/g, (_, code) => {
    const token = `\u0000CODE${codeSpans.length}\u0000`;
    codeSpans.push(`<code>${escapeHtml(code)}</code>`);
    return token;
  });

  work = escapeHtml(work);
  work = work.replace(/!\[([^\]]*)\]\((https?:\/\/[^\s)]+)\)/g, '<img src="$2" alt="$1" />');
  work = work.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank">$1</a>');
  work = work.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  work = work.replace(/__([^_]+)__/g, '<strong>$1</strong>');
  work = work.replace(/\*([^*]+)\*/g, '<em>$1</em>');
  work = work.replace(/\u0000CODE(\d+)\u0000/g, (_, idx) => codeSpans[Number(idx)]);
  return work;
}

/**
 * 将 Markdown 文本转换为兼容 UEditor 的简洁 HTML
 * @param {string} markdown
 * @returns {string}
 */
export function markdownToHtml(markdown) {
  const lines = String(markdown || '').replace(/\r\n/g, '\n').split('\n');
  const out = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];
    const trimmed = line.trim();

    if (!trimmed) {
      i++;
      continue;
    }

    // 围栏代码块
    if (trimmed.startsWith('```')) {
      const codeLines = [];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith('```')) {
        codeLines.push(lines[i]);
        i++;
      }
      if (i < lines.length) i++;
      out.push(`<pre><code>${escapeHtml(codeLines.join('\n'))}</code></pre>`);
      continue;
    }

    // 标题 # ~ ######
    const headingMatch = trimmed.match(/^(#{1,6})\s+(.+)$/);
    if (headingMatch) {
      const level = headingMatch[1].length;
      out.push(`<h${level}>${renderInlineMarkdown(headingMatch[2].trim())}</h${level}>`);
      i++;
      continue;
    }

    // 分隔线
    if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)) {
      out.push('<hr />');
      i++;
      continue;
    }

    // 引用块
    if (trimmed.startsWith('>')) {
      const quoteLines = [];
      while (i < lines.length && lines[i].trim().startsWith('>')) {
        quoteLines.push(lines[i].trim().replace(/^>\s?/, ''));
        i++;
      }
      out.push(`<blockquote><p>${quoteLines.map(renderInlineMarkdown).join('<br />')}</p></blockquote>`);
      continue;
    }

    // 无序列表
    if (/^[-*+]\s+/.test(trimmed)) {
      const items = [];
      while (i < lines.length && /^[-*+]\s+/.test(lines[i].trim())) {
        items.push(`<li>${renderInlineMarkdown(lines[i].trim().replace(/^[-*+]\s+/, ''))}</li>`);
        i++;
      }
      out.push(`<ul>${items.join('')}</ul>`);
      continue;
    }

    // 有序列表
    if (/^\d+\.\s+/.test(trimmed)) {
      const items = [];
      while (i < lines.length && /^\d+\.\s+/.test(lines[i].trim())) {
        items.push(`<li>${renderInlineMarkdown(lines[i].trim().replace(/^\d+\.\s+/, ''))}</li>`);
        i++;
      }
      out.push(`<ol>${items.join('')}</ol>`);
      continue;
    }

    // 普通段落（连续非空且非块级语法的行合并）
    const paraLines = [];
    while (i < lines.length) {
      const cur = lines[i].trim();
      if (
        !cur ||
        cur.startsWith('```') ||
        /^(#{1,6})\s+/.test(cur) ||
        /^(-{3,}|\*{3,}|_{3,})$/.test(cur) ||
        cur.startsWith('>') ||
        /^[-*+]\s+/.test(cur) ||
        /^\d+\.\s+/.test(cur)
      ) {
        break;
      }
      paraLines.push(renderInlineMarkdown(cur));
      i++;
    }
    out.push(`<p>${paraLines.join('<br />')}</p>`);
  }

  return out.join('\n');
}

/**
 * 按 Gityx `#jizhiform` 表单结构构建 `URLSearchParams` 与结构化摘要
 * @param {object} options
 * @returns {{ params: URLSearchParams, payloadObject: Record<string, string | string[]> }}
 */
export function buildPublishPayload(options) {
  const preset = options.preset ? PRESETS[options.preset] : null;
  if (options.preset && !preset) {
    throw new Error(`未知 preset "${options.preset}"，可选值：${Object.keys(PRESETS).join(', ')}`);
  }

  const merged = {
    molds: 'article',
    ...(preset || {}),
    ...(options.fields || {}),
  };

  if (options.molds) merged.molds = options.molds;
  if (options.id) merged.id = String(options.id);
  if (options.isshow !== undefined) merged.isshow = String(options.isshow);
  if (options.tid) merged.tid = String(options.tid);
  if (options.title !== undefined) merged.title = options.title;
  if (options.keywords !== undefined) merged.keywords = options.keywords;
  if (options.litpic !== undefined) merged.litpic = options.litpic;
  if (options.description !== undefined) merged.description = options.description;
  if (options.bodyHtml !== undefined) merged.body = options.bodyHtml;
  if (options.updatetime !== undefined) merged.updatetime = options.updatetime;

  if (!merged.tid) {
    throw new Error('缺少分类 ID（--tid），请先执行 categories 查看可选 tid 或指定 --preset。');
  }
  if (!merged.title || !String(merged.title).trim()) {
    throw new Error('缺少文章标题（--title），该字段为站内必填项。');
  }

  const params = new URLSearchParams();
  const payloadObject = {};

  const appendField = (key, value) => {
    if (value === undefined || value === null) return;
    if (Array.isArray(value)) {
      const keyName = ARRAY_FIELDS.has(key) ? `${key}[]` : key;
      payloadObject[keyName] = value.map(String);
      for (const item of value) {
        params.append(keyName, String(item));
      }
      return;
    }
    const strVal = String(value);
    if (ARRAY_FIELDS.has(key)) {
      const list = strVal
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean);
      payloadObject[`${key}[]`] = list;
      for (const item of list) {
        params.append(`${key}[]`, item);
      }
      return;
    }
    payloadObject[key] = strVal;
    params.append(key, strVal);
  };

  appendField('ajax', '1');
  if (merged.id) {
    appendField('id', merged.id);
    appendField('isshow', merged.isshow ?? '1');
  } else {
    appendField('isshow', merged.isshow ?? '');
  }
  appendField('molds', merged.molds || 'article');
  appendField('tid', merged.tid);
  appendField('title', String(merged.title).trim());
  appendField('keywords', merged.keywords ?? '');
  appendField('litpic', merged.litpic ?? '');
  appendField('description', merged.description ?? '');
  appendField('body', merged.body ?? '');
  appendField('updatetime', merged.updatetime || formatLocalDateTime());

  const reserved = new Set([
    'ajax',
    'id',
    'isshow',
    'molds',
    'tid',
    'title',
    'keywords',
    'litpic',
    'description',
    'body',
    'updatetime',
  ]);
  for (const [k, v] of Object.entries(merged)) {
    if (!reserved.has(k)) {
      appendField(k, v);
    }
  }

  return { params, payloadObject };
}

/**
 * 发起带 Cookie 的 Gityx HTTP 请求
 * @param {string} url
 * @param {object} init
 * @param {string} cookie
 */
async function requestGityx(url, init, cookie) {
  const headers = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) GityxPublishSkill/1.0',
    Referer: ENDPOINTS.release,
    Origin: BASE_URL,
    Cookie: cookie,
    ...(init.headers || {}),
  };
  return fetch(url, { ...init, headers });
}

/**
 * 校验响应是否为未登录跳转（302 到 login 或返回登录页面）
 * @param {Response} res
 * @param {string} [bodyText]
 */
function ensureAuthenticatedResponse(res, bodyText = '') {
  const loc = res.headers.get('location') || '';
  if (
    (res.status >= 300 && res.status < 400 && loc.includes('login')) ||
    res.url.includes('login/index') ||
    bodyText.includes('login_vercode')
  ) {
    throw new Error('Gityx Cookie 已失效或未登录（被重定向至登录页），请更新 .env.gityx 中的 PHPSESSID。');
  }
}

/**
 * 检查当前登录态并解析已发布文章列表
 * @param {string} cookie
 */
async function checkSession(cookie) {
  const res = await requestGityx(ENDPOINTS.posts, { method: 'GET', redirect: 'manual' }, cookie);
  const html = await res.text();
  ensureAuthenticatedResponse(res, html);
  if (!res.ok) {
    throw new Error(`访问个人中心文章列表失败: HTTP ${res.status}`);
  }

  const recentPosts = [];
  const tbodyMatch = html.match(/<tbody>([\s\S]*?)<\/tbody>/i);
  if (tbodyMatch) {
    const rowMatches = tbodyMatch[1].matchAll(/<tr>([\s\S]*?)<\/tr>/gi);
    for (const row of rowMatches) {
      const cells = [...row[1].matchAll(/<td[^>]*>([\s\S]*?)<\/t[dh]>/gi)].map((m) => m[1]);
      if (cells.length >= 4) {
        const linkMatch = cells[0].match(/href="([^"]+)"[^>]*>([\s\S]*?)<\/a>/i);
        const strip = (s) => s.replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();
        recentPosts.push({
          title: linkMatch ? strip(linkMatch[2]) : strip(cells[0]),
          url: linkMatch ? linkMatch[1] : null,
          category: strip(cells[1]),
          status: strip(cells[2]),
          publishedAt: strip(cells[3]),
          role: cells[4] ? strip(cells[4]) : null,
        });
      }
    }
  }

  return {
    ok: true,
    authenticated: true,
    postCount: recentPosts.length,
    recentPosts: recentPosts.slice(0, 15),
  };
}

/**
 * 拉取专栏下的分类列表
 * @param {string} cookie
 * @param {string} molds
 */
async function fetchCategories(cookie, molds = 'article') {
  const res = await requestGityx(
    ENDPOINTS.getClass,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
      body: new URLSearchParams({ molds }).toString(),
    },
    cookie
  );
  const text = await res.text();
  ensureAuthenticatedResponse(res, text);
  const json = JSON.parse(text);
  if (json.code !== 0) {
    throw new Error(`获取分类失败: ${json.msg || text}`);
  }
  return (json.data || []).map((c) => ({
    id: String(c.id),
    name: c.classname,
    level: Number(c.level || 0),
    pid: String(c.pid || '0'),
    htmlurl: c.htmlurl,
  }));
}

/**
 * 拉取指定分类下的动态扩展字段定义
 * @param {string} cookie
 * @param {string} molds
 * @param {string} tid
 */
async function fetchFields(cookie, molds, tid) {
  const res = await requestGityx(
    ENDPOINTS.getFields,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
      body: new URLSearchParams({ molds, tid: String(tid), id: '0' }).toString(),
    },
    cookie
  );
  const text = await res.text();
  ensureAuthenticatedResponse(res, text);
  const json = JSON.parse(text);
  if (json.code !== 0) {
    throw new Error(`获取字段失败: ${json.msg || text}`);
  }

  const commonFields = [
    { field: 'title', label: '文章标题', required: true },
    { field: 'keywords', label: '关键词（英文逗号分隔）', required: false },
    { field: 'litpic', label: '文章主图 URL', required: false },
    { field: 'description', label: '文章简介（建议 150 字以内）', required: false },
    { field: 'body', label: '文章内容 HTML（UEditor，提示 1500 字以内）', required: false },
  ];

  const dynamicFields = (json.fields_list || []).map((f) => ({
    field: ARRAY_FIELDS.has(f.field) ? `${f.field}[]` : f.field,
    label: f.fieldname,
    required: f.ismust === '1',
    tips: f.tips || null,
    options: f.body || null,
    defaultValue: f.vdata || null,
  }));

  return { ok: true, molds, tid: String(tid), commonFields, dynamicFields };
}

/**
 * 上传本地封面图片至 Gityx
 * @param {string} cookie
 * @param {string} filePath
 * @returns {Promise<string>}
 */
async function uploadCoverImage(cookie, filePath) {
  const absPath = path.resolve(filePath);
  const ext = path.extname(absPath).toLowerCase();
  const mime = MIME_BY_EXT[ext];
  if (!mime) {
    throw new Error(`不支持的图片扩展名 "${ext}"，仅支持 ${Object.keys(MIME_BY_EXT).join(', ')}`);
  }
  const bytes = await fs.readFile(absPath);
  const form = new FormData();
  form.append('ajax', '1');
  form.append('filename', 'file_litpic');
  form.append('file_litpic', new Blob([bytes], { type: mime }), path.basename(absPath));

  const res = await requestGityx(ENDPOINTS.upload, { method: 'POST', body: form }, cookie);
  const text = await res.text();
  ensureAuthenticatedResponse(res, text);
  const json = JSON.parse(text);
  if (json.code !== 0 || !json.url) {
    throw new Error(`图片上传失败: ${json.error || json.msg || text}`);
  }
  return json.url;
}

/**
 * 解析 CLI 参数
 * @param {string[]} argv
 */
export function parseCliArgs(argv) {
  const command = argv[0] || 'help';
  const positionals = [];
  const opts = {
    fields: {},
    dryRun: false,
    confirm: false,
    skipCategoryCheck: false,
    molds: undefined,
    format: undefined,
  };

  const flagToField = {
    '--short-title': 'short_title',
    '--zuozhe': 'zuozhe',
    '--tougaozhe': 'tougaozhe',
    '--hanhuazhe': 'hanhuazhe',
    '--version': 'version',
    '--qqun': 'qqun',
    '--qqun-url': 'qqun_url',
    '--gameurl': 'gameurl',
    '--url-ifdian': 'url_ifdian',
    '--url-changelog': 'url_changelog',
    '--video': 'video',
    '--game-only': 'game_only',
    '--game-platform': 'game_platform',
    '--game-price': 'game_price',
    '--game-life': 'game_life',
    '--url1': 'url1',
    '--url1-tit': 'url1_tit',
    '--url2': 'url2',
    '--url2-tit': 'url2_tit',
    '--url-osc': 'url_osc',
  };

  for (let i = 1; i < argv.length; i++) {
    const arg = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`参数 ${arg} 缺少取值`);
      return argv[++i];
    };

    if (arg === '--help' || arg === '-h') return { command: 'help', positionals, opts };
    if (arg === '--dry-run') { opts.dryRun = true; continue; }
    if (arg === '--confirm') { opts.confirm = true; continue; }
    if (arg === '--skip-category-check') { opts.skipCategoryCheck = true; continue; }
    if (arg === '--cookie') { opts.cookie = next(); continue; }
    if (arg === '--env-file') { opts.envFile = next(); continue; }
    if (arg === '--molds') { opts.molds = next(); continue; }
    if (arg === '--id') { opts.id = next(); continue; }
    if (arg === '--isshow') { opts.isshow = next(); continue; }
    if (arg === '--tid') { opts.tid = next(); continue; }
    if (arg === '--preset') { opts.preset = next(); continue; }
    if (arg === '--title') { opts.title = next(); continue; }
    if (arg === '--keywords') { opts.keywords = next(); continue; }
    if (arg === '--litpic') { opts.litpic = next(); continue; }
    if (arg === '--cover-file') { opts.coverFile = next(); continue; }
    if (arg === '--description') { opts.description = next(); continue; }
    if (arg === '--body') { opts.body = next(); continue; }
    if (arg === '--body-file') { opts.bodyFile = next(); continue; }
    if (arg === '--format') { opts.format = next(); continue; }
    if (arg === '--updatetime') { opts.updatetime = next(); continue; }
    if (arg === '--field') {
      const kv = next();
      const idx = kv.indexOf('=');
      if (idx <= 0) throw new Error(`--field 格式应为 key=value，收到: ${kv}`);
      const k = kv.slice(0, idx).trim();
      const v = kv.slice(idx + 1);
      if (opts.fields[k] !== undefined) {
        opts.fields[k] = Array.isArray(opts.fields[k])
          ? [...opts.fields[k], v]
          : [opts.fields[k], v];
      } else {
        opts.fields[k] = v;
      }
      continue;
    }
    if (flagToField[arg]) {
      opts.fields[flagToField[arg]] = next();
      continue;
    }
    if (arg.startsWith('--')) {
      throw new Error(`未知参数: ${arg}`);
    }
    positionals.push(arg);
  }

  return { command, positionals, opts };
}

function printHelp() {
  console.log(`
Gityx（Git游戏）发帖 CLI 工具

用法:
  node gityx-publish.mjs <command> [options]

命令:
  check                        检查 Cookie 登录态与近期已发文章
  categories                   拉取可选专栏与分类 tid 列表
  fields --tid <id>            查询指定分类的必填与扩展字段
  upload-image <filePath>      上传本地封面图并返回站内 URL
  publish [options]            发布文章（默认先用 --dry-run 预览，正式提交须传 --confirm）

常用 publish 参数:
  --preset <name>              套用预设（可选：daohua-changelog, daohua-guide）
  --id <postId>                编辑已有文章 ID（需管理员已分配编辑权限，自动附带 isshow=1）
  --molds <article|product>    专栏类型（默认 article）
  --tid <id>                   分类 ID（如 65=更新日志, 9=教程/攻略, 37=网络放置）
  --title <text>               文章标题（必填）
  --keywords <a,b,c>           关键词（英文逗号分隔）
  --description <text>         简介（建议 150 字以内）
  --body <text>                正文内容
  --body-file <path>           从文件读取正文（.md 自动转 HTML，除非显式传 --format html）
  --format <markdown|html>     正文格式（默认按文件后缀或 html 处理）
  --litpic <url>               文章主图 URL
  --cover-file <path>          自动上传本地图片并填入 litpic
  --field <key=value>          传入任意动态扩展字段（可重复）
  --dry-run                    仅构建与校验载荷，不触发上传和发帖请求
  --confirm                    正式发帖确认开关（未传 --dry-run 时必须显式传入）
  --cookie <str> / --env-file  指定 Cookie 字符串或配置文件（默认读 .env.gityx）
`.trim());
}

async function run() {
  const { command, positionals, opts } = parseCliArgs(process.argv.slice(2));
  if (command === 'help') {
    printHelp();
    return;
  }

  const cookie = await resolveCookieHeader(opts);

  if (command === 'check') {
    console.log(JSON.stringify(await checkSession(cookie), null, 2));
    return;
  }

  if (command === 'categories') {
    const molds = opts.molds || 'article';
    const list = await fetchCategories(cookie, molds);
    console.log(JSON.stringify({ ok: true, molds, count: list.length, categories: list }, null, 2));
    return;
  }

  if (command === 'fields') {
    if (!opts.tid) throw new Error('fields 命令需要指定 --tid <id>');
    const molds = opts.molds || 'article';
    console.log(JSON.stringify(await fetchFields(cookie, molds, opts.tid), null, 2));
    return;
  }

  if (command === 'upload-image') {
    const filePath = positionals[0];
    if (!filePath) throw new Error('upload-image 需要传入图片文件路径');
    const url = await uploadCoverImage(cookie, filePath);
    console.log(JSON.stringify({ ok: true, url }, null, 2));
    return;
  }

  if (command === 'publish') {
    let rawBody = opts.body ?? '';
    let format = opts.format;
    if (opts.bodyFile) {
      rawBody = await fs.readFile(path.resolve(opts.bodyFile), 'utf8');
      if (!format && opts.bodyFile.toLowerCase().endsWith('.md')) {
        format = 'markdown';
      }
    }
    const bodyHtml = format === 'markdown' ? markdownToHtml(rawBody) : rawBody;

    let litpic = opts.litpic;
    if (opts.coverFile) {
      litpic = opts.dryRun
        ? `[dry-run: 待上传封面 ${opts.coverFile}]`
        : await uploadCoverImage(cookie, opts.coverFile);
    }

    const { params, payloadObject } = buildPublishPayload({
      ...opts,
      litpic,
      bodyHtml,
    });

    const warnings = [];
    if ((payloadObject.description || '').length > 150) {
      warnings.push(`description 长度为 ${payloadObject.description.length} 字，超过表单提示的 150 字上限`);
    }
    if (!payloadObject.body) {
      warnings.push('文章正文 body 为空');
    }

    if (!opts.skipCategoryCheck) {
      const cats = await fetchCategories(cookie, payloadObject.molds);
      const matched = cats.find((c) => c.id === String(payloadObject.tid));
      if (!matched) {
        throw new Error(
          `分类 tid=${payloadObject.tid} 在 molds=${payloadObject.molds} 中不存在，请先运行 categories 检查。`
        );
      }
      payloadObject._categoryName = matched.name;
    }

    if (opts.dryRun) {
      console.log(
        JSON.stringify(
          {
            ok: true,
            mode: 'dry-run',
            endpoint: ENDPOINTS.release,
            warnings,
            payload: payloadObject,
          },
          null,
          2
        )
      );
      return;
    }

    if (!opts.confirm) {
      console.error(
        JSON.stringify(
          {
            ok: false,
            error: '安全保护：正式发布到线上社区必须显式传入 --confirm（建议先用 --dry-run 预览）。',
          },
          null,
          2
        )
      );
      process.exitCode = 2;
      return;
    }

    const res = await requestGityx(
      ENDPOINTS.release,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
        body: params.toString(),
      },
      cookie
    );
    const text = await res.text();
    ensureAuthenticatedResponse(res, text);

    let json;
    try {
      json = JSON.parse(text);
    } catch {
      throw new Error(`发布接口返回非 JSON 响应 (HTTP ${res.status}): ${text.slice(0, 300)}`);
    }

    if (json.code !== 0) {
      throw new Error(`发布失败 (code=${json.code}): ${json.msg || text}`);
    }

    console.log(
      JSON.stringify(
        {
          ok: true,
          mode: 'published',
          warnings,
          response: json,
        },
        null,
        2
      )
    );
    return;
  }

  throw new Error(`未知命令 "${command}"，请运行 help 查看用法。`);
}

const isMain =
  process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;

if (isMain) {
  run().catch((err) => {
    console.error(JSON.stringify({ ok: false, error: err.message }, null, 2));
    process.exitCode = 1;
  });
}
