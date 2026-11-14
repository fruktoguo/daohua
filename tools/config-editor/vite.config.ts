// ==============================================================================
// 文件名称: vite.config.ts
// 主要职责: 配置编辑器 Vite 架构与 Node.js 本地文件读写 / 领域校验 API 中间件
// ==============================================================================

import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import * as fs from 'fs/promises';
import * as path from 'path';
import { exec } from 'child_process';
import { promisify } from 'util';

const execAsync = promisify(exec);

const CONFIG_DIR = path.resolve(__dirname, '../../config/game-data');
const BACKUP_DIR = path.resolve(__dirname, '../../config/game-data/.backup');
const ALLOWED_ROOT_FILES = new Set(['manifest.json', 'expected-attributes.json']);
const ALLOWED_CONFIG_SUBDIRS = new Set([
  'actions',
  'items',
  'abilities',
  'equipment',
  'monsters',
  'encounters',
  'maps',
  'buildings',
  'shop',
]);

class ApiValidationError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

function isLoopbackHostName(hostname: string | undefined): boolean {
  if (!hostname) return false;
  const h = hostname.toLowerCase();
  return h === 'localhost' || h === '127.0.0.1' || h === '::1' || h === '[::1]';
}

function isAllowedHostHeader(hostHeader: string | undefined): boolean {
  if (!hostHeader) return false;
  return /^(?:localhost|127\.0\.0\.1|\[::1\])(?::\d{1,5})?$/i.test(hostHeader.trim());
}

function isAllowedLocalUrlHeader(rawUrl: string | undefined): boolean {
  if (!rawUrl || rawUrl === 'null') return false;
  try {
    const parsed = new URL(rawUrl);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false;
    return isLoopbackHostName(parsed.hostname);
  } catch {
    return false;
  }
}

function isLoopbackRemoteAddress(remoteAddress: string | undefined): boolean {
  if (!remoteAddress) return false;
  return (
    remoteAddress === '127.0.0.1' ||
    remoteAddress === '::1' ||
    remoteAddress === '::ffff:127.0.0.1'
  );
}

/**
 * 校验并归约配置写入相对路径，强制约束位于 config/game-data/ 目录内、仅 .json 后缀且归属白名单分类目录。
 */
function resolveSafeConfigWritePath(relInput: string, expectedSubdir?: string): string {
  if (typeof relInput !== 'string' || relInput.trim().length === 0 || relInput.includes('\0')) {
    throw new ApiValidationError(400, '配置文件路径不能为空或包含非法字符');
  }
  const normalized = relInput.replace(/\\/g, '/').replace(/^\/+/, '');
  if (path.isAbsolute(relInput)) {
    throw new ApiValidationError(400, `禁止使用绝对路径写入配置: ${relInput}`);
  }
  const segments = normalized.split('/');
  if (segments.some(seg => seg.length === 0 || seg === '.' || seg === '..' || seg.startsWith('.'))) {
    throw new ApiValidationError(400, `禁止路径穿越或隐藏目录写入: ${relInput}`);
  }
  if (!normalized.toLowerCase().endsWith('.json')) {
    throw new ApiValidationError(400, `仅允许写入 .json 配置文件: ${relInput}`);
  }

  if (segments.length === 1) {
    if (!ALLOWED_ROOT_FILES.has(segments[0])) {
      throw new ApiValidationError(403, `禁止写入非白名单根目录文件: ${relInput}`);
    }
    if (expectedSubdir) {
      throw new ApiValidationError(400, `分类 ${expectedSubdir} 不允许写入根目录文件 ${relInput}`);
    }
  } else {
    const topDir = segments[0];
    if (!ALLOWED_CONFIG_SUBDIRS.has(topDir)) {
      throw new ApiValidationError(403, `禁止写入非白名单配置子目录: ${relInput}`);
    }
    if (expectedSubdir && topDir !== expectedSubdir) {
      throw new ApiValidationError(400, `文件 ${relInput} 不属于预期目录 ${expectedSubdir}/`);
    }
  }

  const resolved = path.resolve(CONFIG_DIR, normalized);
  const configPrefix = CONFIG_DIR.endsWith(path.sep) ? CONFIG_DIR : CONFIG_DIR + path.sep;
  if (!resolved.startsWith(configPrefix)) {
    throw new ApiValidationError(403, `目标路径越界 config/game-data: ${relInput}`);
  }
  return resolved;
}

// 递归查找目录下的所有 json 文件（相对路径）
async function getAllJsonFiles(dir: string, baseDir: string = dir): Promise<string[]> {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  const files: string[] = [];
  for (const entry of entries) {
    if (entry.name.startsWith('.')) continue;
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await getAllJsonFiles(fullPath, baseDir)));
    } else if (entry.isFile() && entry.name.endsWith('.json')) {
      files.push(path.relative(baseDir, fullPath).replace(/\\/g, '/'));
    }
  }
  return files;
}

function configStoragePlugin(): Plugin {
  return {
    name: 'daohua-config-storage-plugin',
    configureServer(server) {
      server.middlewares.use(async (req, res, next) => {
        const rawPath = (req.url || '').split('?')[0];
        if (!rawPath.startsWith('/api/config')) {
          return next();
        }

        const sendJson = (data: any, status = 200) => {
          res.statusCode = status;
          res.setHeader('Content-Type', 'application/json; charset=utf-8');
          res.end(JSON.stringify(data));
        };

        const sendError = (msg: string, status = 400) => {
          res.statusCode = status;
          res.setHeader('Content-Type', 'application/json; charset=utf-8');
          res.end(JSON.stringify({ error: msg }));
        };

        // 严格本机隔离校验：防止局域网暴露或跨站网页通过 localhost CSRF 读写仓库配表
        if (!isLoopbackRemoteAddress(req.socket?.remoteAddress)) {
          return sendError('禁止非本机回环连接访问配表编辑器接口', 403);
        }
        if (!isAllowedHostHeader(req.headers.host)) {
          return sendError('Host 请求头不在 localhost/127.0.0.1 白名单内', 403);
        }
        const fetchSite = String(req.headers['sec-fetch-site'] || '').toLowerCase();
        if (fetchSite === 'cross-site') {
          return sendError('禁止跨站请求访问配表编辑器接口', 403);
        }
        const origin = typeof req.headers.origin === 'string' ? req.headers.origin : undefined;
        if (origin !== undefined && !isAllowedLocalUrlHeader(origin)) {
          return sendError('Origin 请求头不在本机编辑器白名单内', 403);
        }
        const referer = typeof req.headers.referer === 'string' ? req.headers.referer : undefined;
        if (referer !== undefined && !isAllowedLocalUrlHeader(referer)) {
          return sendError('Referer 请求头不在本机编辑器白名单内', 403);
        }
        if (req.method !== 'GET' && req.method !== 'HEAD' && origin === undefined && referer === undefined) {
          return sendError('写接口必须携带本机 Origin 或 Referer', 403);
        }

        const url = new URL(req.url || '', 'http://127.0.0.1');

        const readJsonBody = async () => {
          return new Promise<any>((resolve, reject) => {
            let body = '';
            req.on('data', chunk => { body += chunk; });
            req.on('end', () => {
              try {
                resolve(body ? JSON.parse(body) : {});
              } catch (err) {
                reject(err);
              }
            });
            req.on('error', reject);
          });
        };

        try {
          // 1. GET /api/config/all - 读取全部配置文件及分类汇总
          if (req.method === 'GET' && url.pathname === '/api/config/all') {
            const relativeFiles = await getAllJsonFiles(CONFIG_DIR);
            const rawFiles: Record<string, any> = {};

            const categoryMap: Record<string, any[]> = {
              'actions.json': [],
              'items.json': [],
              'abilities.json': [],
              'equipment.json': [],
              'monsters.json': [],
              'encounters.json': [],
              'maps.json': []
            };

            let manifestData: any = null;
            let expectedData: any = null;

            for (const rel of relativeFiles) {
              const fullPath = path.join(CONFIG_DIR, rel);
              const content = await fs.readFile(fullPath, 'utf-8');
              try {
                const parsed = JSON.parse(content);
                rawFiles[rel] = parsed;

                if (rel === 'manifest.json') {
                  manifestData = parsed;
                } else if (rel === 'expected-attributes.json') {
                  expectedData = parsed;
                } else {
                  // 子目录分类: actions/, items/, abilities/, equipment/, monsters/, encounters/, maps/
                  const topFolder = rel.split('/')[0];
                  const catKey = `${topFolder}.json`;
                  if (catKey in categoryMap) {
                    if (Array.isArray(parsed)) {
                      parsed.forEach(item => {
                        categoryMap[catKey].push({ ...item, _sourceFile: rel });
                      });
                    } else if (typeof parsed === 'object' && parsed !== null) {
                      categoryMap[catKey].push({ ...parsed, _sourceFile: rel });
                    }
                  }
                }
              } catch (e: any) {
                rawFiles[rel] = { __parseError: e.message, raw: content };
              }
            }

            const aggregated: Record<string, any> = {
              'manifest.json': manifestData || { schemaVersion: 1, contentVersion: '2026.08.24.1' },
              'expected-attributes.json': expectedData || {},
              ...categoryMap
            };

            return sendJson({
              ok: true,
              data: aggregated,
              rawFiles,
              relativeFileList: relativeFiles,
              directory: CONFIG_DIR
            });
          }

          // 2. POST /api/config/save-all - 批量写回全量配置文件（保持原目录树结构与文件归属）
          if (req.method === 'POST' && url.pathname === '/api/config/save-all') {
            const body = await readJsonBody();
            const filesMap = body.files as Record<string, any>;
            if (!filesMap || typeof filesMap !== 'object') {
              return sendError('缺少 files 字典数据');
            }

            // 自动快照备份
            await fs.mkdir(BACKUP_DIR, { recursive: true });
            const timestamp = new Date().toISOString().replace(/[:.]/g, '-');

            const relativeFiles = await getAllJsonFiles(CONFIG_DIR);
            for (const rel of relativeFiles) {
              const src = path.join(CONFIG_DIR, rel);
              const dstDir = path.join(BACKUP_DIR, path.dirname(rel));
              await fs.mkdir(dstDir, { recursive: true });
              const dst = path.join(BACKUP_DIR, `${rel}.${timestamp}.bak`);
              try {
                const content = await fs.readFile(src, 'utf-8');
                await fs.writeFile(dst, content, 'utf-8');
              } catch {
                // 忽略未就绪文件
              }
            }

            // 1. 写回根目录单个大文件
            if (filesMap['manifest.json']) {
              const target = resolveSafeConfigWritePath('manifest.json');
              await fs.writeFile(target, JSON.stringify(filesMap['manifest.json'], null, 2) + '\n', 'utf-8');
            }
            if (filesMap['expected-attributes.json']) {
              const target = resolveSafeConfigWritePath('expected-attributes.json');
              await fs.writeFile(target, JSON.stringify(filesMap['expected-attributes.json'], null, 2) + '\n', 'utf-8');
            }

            // 2. 写回各个分类子目录文件 (严格校验 _sourceFile 落于白名单分类目录内)
            const categories = ['actions', 'items', 'abilities', 'equipment', 'monsters', 'encounters', 'maps', 'buildings', 'shop'];
            for (const cat of categories) {
              const catKey = `${cat}.json`;
              const items = filesMap[catKey];
              if (Array.isArray(items)) {
                // 按 _sourceFile 分组
                const groups: Record<string, any[]> = {};
                items.forEach(item => {
                  const source = typeof item._sourceFile === 'string' && item._sourceFile.trim()
                    ? item._sourceFile.trim()
                    : `${cat}/default.json`;
                  // 预先校验每条记录的写入路径，防止部分写盘后才报错
                  resolveSafeConfigWritePath(source, cat);
                  if (!groups[source]) groups[source] = [];
                  const cleanItem = { ...item };
                  delete cleanItem._sourceFile;
                  groups[source].push(cleanItem);
                });

                for (const [sourceRel, groupItems] of Object.entries(groups)) {
                  const targetPath = resolveSafeConfigWritePath(sourceRel, cat);
                  await fs.mkdir(path.dirname(targetPath), { recursive: true });
                  const formatted = JSON.stringify(groupItems, null, 2) + '\n';
                  await fs.writeFile(targetPath, formatted, 'utf-8');
                }
              }
            }

            return sendJson({ ok: true, message: '全量配置文件已成功按目录结构写回磁盘并建立快照备份' });
          }

          // 3. POST /api/config/file/:filename - 保存单个文件
          if (req.method === 'POST' && url.pathname.startsWith('/api/config/file/')) {
            const filename = decodeURIComponent(url.pathname.replace('/api/config/file/', ''));
            if (!filename || filename.includes('/') || filename.includes('\\') || !filename.endsWith('.json')) {
              return sendError('非法的配置分类文件名', 400);
            }
            const body = await readJsonBody();
            const data = body.data;

            if (filename === 'manifest.json' || filename === 'expected-attributes.json') {
              const filePath = resolveSafeConfigWritePath(filename);
              const formatted = JSON.stringify(data, null, 2) + '\n';
              await fs.writeFile(filePath, formatted, 'utf-8');
            } else if (Array.isArray(data)) {
              const cat = filename.replace(/\.json$/, '');
              if (!ALLOWED_CONFIG_SUBDIRS.has(cat)) {
                return sendError(`禁止写入非白名单配置分类: ${filename}`, 403);
              }
              const groups: Record<string, any[]> = {};
              data.forEach(item => {
                const source = typeof item._sourceFile === 'string' && item._sourceFile.trim()
                  ? item._sourceFile.trim()
                  : `${cat}/default.json`;
                resolveSafeConfigWritePath(source, cat);
                if (!groups[source]) groups[source] = [];
                const cleanItem = { ...item };
                delete cleanItem._sourceFile;
                groups[source].push(cleanItem);
              });

              for (const [sourceRel, groupItems] of Object.entries(groups)) {
                const targetPath = resolveSafeConfigWritePath(sourceRel, cat);
                await fs.mkdir(path.dirname(targetPath), { recursive: true });
                const formatted = JSON.stringify(groupItems, null, 2) + '\n';
                await fs.writeFile(targetPath, formatted, 'utf-8');
              }
            } else {
              return sendError('保存单类配置时 data 必须为数组', 400);
            }

            return sendJson({ ok: true, filename, message: `已成功保存 ${filename}` });
          }

          // 4. POST /api/config/validate - 调用领域层 DataTool 严格校验
          if (req.method === 'POST' && url.pathname === '/api/config/validate') {
            try {
              const rootDir = path.resolve(__dirname, '../..');
              const { stdout, stderr } = await execAsync('dotnet run --project src/DataTool -- validate config/game-data --strict', {
                cwd: rootDir
              });
              let parsed: any = null;
              try {
                parsed = JSON.parse(stdout);
              } catch {
                parsed = { raw: stdout };
              }
              return sendJson({ ok: true, output: parsed, rawStdout: stdout, rawStderr: stderr });
            } catch (err: any) {
              return sendJson({ ok: false, error: err.message, stderr: err.stderr, stdout: err.stdout }, 200);
            }
          }

          // 5. POST /api/config/backup - 手动创建备份
          if (req.method === 'POST' && url.pathname === '/api/config/backup') {
            await fs.mkdir(BACKUP_DIR, { recursive: true });
            const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
            const relativeFiles = await getAllJsonFiles(CONFIG_DIR);
            for (const rel of relativeFiles) {
              const src = path.join(CONFIG_DIR, rel);
              const dstDir = path.join(BACKUP_DIR, path.dirname(rel));
              await fs.mkdir(dstDir, { recursive: true });
              const dst = path.join(BACKUP_DIR, `${rel}.${timestamp}.bak`);
              await fs.copyFile(src, dst);
            }
            return sendJson({ ok: true, count: relativeFiles.length, timestamp, message: `已备份 ${relativeFiles.length} 个配置文件` });
          }

          return sendError('未知 API 端点: ' + url.pathname, 404);
        } catch (err: any) {
          if (err instanceof ApiValidationError) {
            return sendError(err.message, err.status);
          }
          console.error('Config API error:', err);
          return sendError(err.message || '内部处理错误', 500);
        }
      });
    }
  };
}

export default defineConfig({
  plugins: [react(), tailwindcss(), configStoragePlugin()],
  server: {
    port: 5199,
    strictPort: true,
    host: '127.0.0.1',
    watch: {
      ignored: ['**/node_modules/**', '**/.git/**', '**/vendor/**', '**/bin/**', '**/obj/**', '**/dist/**']
    }
  }
});
