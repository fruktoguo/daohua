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
        const url = new URL(req.url || '', `http://${req.headers.host}`);
        if (!url.pathname.startsWith('/api/config')) {
          return next();
        }

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
              const target = path.join(CONFIG_DIR, 'manifest.json');
              await fs.writeFile(target, JSON.stringify(filesMap['manifest.json'], null, 2) + '\n', 'utf-8');
            }
            if (filesMap['expected-attributes.json']) {
              const target = path.join(CONFIG_DIR, 'expected-attributes.json');
              await fs.writeFile(target, JSON.stringify(filesMap['expected-attributes.json'], null, 2) + '\n', 'utf-8');
            }

            // 2. 写回各个分类子目录文件 (根据 _sourceFile 分组)
            const categories = ['actions', 'items', 'abilities', 'equipment', 'monsters', 'encounters', 'maps'];
            for (const cat of categories) {
              const catKey = `${cat}.json`;
              const items = filesMap[catKey];
              if (Array.isArray(items)) {
                // 按 _sourceFile 分组
                const groups: Record<string, any[]> = {};
                items.forEach(item => {
                  const source = item._sourceFile || `${cat}/default.json`;
                  if (!groups[source]) groups[source] = [];
                  const cleanItem = { ...item };
                  delete cleanItem._sourceFile;
                  groups[source].push(cleanItem);
                });

                for (const [sourceRel, groupItems] of Object.entries(groups)) {
                  const targetPath = path.join(CONFIG_DIR, sourceRel);
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
            const body = await readJsonBody();
            const data = body.data;

            if (filename === 'manifest.json' || filename === 'expected-attributes.json') {
              const filePath = path.join(CONFIG_DIR, filename);
              const formatted = JSON.stringify(data, null, 2) + '\n';
              await fs.writeFile(filePath, formatted, 'utf-8');
            } else if (Array.isArray(data)) {
              const cat = filename.replace('.json', '');
              const groups: Record<string, any[]> = {};
              data.forEach(item => {
                const source = item._sourceFile || `${cat}/default.json`;
                if (!groups[source]) groups[source] = [];
                const cleanItem = { ...item };
                delete cleanItem._sourceFile;
                groups[source].push(cleanItem);
              });

              for (const [sourceRel, groupItems] of Object.entries(groups)) {
                const targetPath = path.join(CONFIG_DIR, sourceRel);
                await fs.mkdir(path.dirname(targetPath), { recursive: true });
                const formatted = JSON.stringify(groupItems, null, 2) + '\n';
                await fs.writeFile(targetPath, formatted, 'utf-8');
              }
            }

            return sendJson({ ok: true, filename, message: `已成功保存 ${filename}` });
          }

          // 4. POST /api/config/validate - 调用领域层 DataTool 校验
          if (req.method === 'POST' && url.pathname === '/api/config/validate') {
            try {
              const rootDir = path.resolve(__dirname, '../..');
              const { stdout, stderr } = await execAsync('dotnet run --project src/DataTool -- validate config/game-data', {
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
