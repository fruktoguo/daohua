// ==============================================================================
// 脚本名称: export-i18n-csv.mjs
// 主要职责: 国际化模板自动生成工具，从中文 TS 源码全自动提取点路径并生成 template.csv。
// 核心逻辑:
//   1. 利用 esbuild 静态解析 frontend/src/i18n/locales/zh-CN.ts
//   2. 递归展平嵌套对象为点路径键 (e.g. game.tab.cultivation)
//   3. 导出标准 RFC 4180 CSV 翻译模板至 frontend/public/locales/template.csv
// ==============================================================================
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const rootDir = path.resolve(__dirname, '..');
const frontendDir = path.join(rootDir, 'frontend');
const localesOutDir = path.join(frontendDir, 'public', 'locales');

// 递归展平嵌套字典对象为点路径映射
export function flattenObject(obj, prefix = '') {
  const result = {};
  for (const [key, value] of Object.entries(obj)) {
    const fullKey = prefix ? `${prefix}.${key}` : key;
    if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      Object.assign(result, flattenObject(value, fullKey));
    } else if (value !== undefined) {
      result[fullKey] = String(value);
    }
  }
  return result;
}

// 格式化为标准 RFC 4180 CSV
export function toCsv(record) {
  const lines = ['key,value'];
  const sortedKeys = Object.keys(record).sort();
  for (const k of sortedKeys) {
    const v = record[k] ?? '';
    const escaped = `"${v.replace(/"/g, '""')}"`;
    lines.push(`${k},${escaped}`);
  }
  return lines.join('\n') + '\n';
}

async function loadTsModule(entryPath) {
  const esbuild = await import(pathToFileURL(path.join(frontendDir, 'node_modules', 'esbuild', 'lib', 'main.js')).href);
  const buildResult = await esbuild.build({
    entryPoints: [entryPath],
    bundle: true,
    write: false,
    format: 'esm',
    target: 'node22',
    platform: 'node',
  });
  const code = buildResult.outputFiles[0].text;
  const dataUri = `data:text/javascript;base64,${Buffer.from(code).toString('base64')}`;
  return import(dataUri);
}

/** 全自动生成 template.csv 对照翻译模板 */
export async function generateTemplateCsv() {
  fs.mkdirSync(localesOutDir, { recursive: true });

  const zhModule = await loadTsModule(path.join(frontendDir, 'src', 'i18n', 'locales', 'zh-CN.ts'));
  const flatZh = flattenObject(zhModule.zhCN);
  const zhKeys = Object.keys(flatZh).sort();
  const zhKeyCount = zhKeys.length;

  const templateLines = ['key,zh_source,translation'];
  for (const k of zhKeys) {
    const zhVal = `"${flatZh[k].replace(/"/g, '""')}"`;
    templateLines.push(`${k},${zhVal},""`);
  }
  const templatePath = path.join(localesOutDir, 'template.csv');
  fs.writeFileSync(templatePath, templateLines.join('\n') + '\n', 'utf-8');
  return { zhKeyCount, templatePath };
}

async function main() {
  const { zhKeyCount, templatePath } = await generateTemplateCsv();
  console.log(`[i18n:template] 成功自动生成翻译模板 ${templatePath}（共 ${zhKeyCount} 条中文词条）`);
}

// 仅在直接运行脚本时执行 main
if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  main().catch(err => {
    console.error('[i18n:template] 生成失败:', err);
    process.exit(1);
  });
}
