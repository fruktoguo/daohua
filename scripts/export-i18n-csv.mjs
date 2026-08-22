// ==============================================================================
// 脚本名称: export-i18n-csv.mjs
// 主要职责: 国际化词典导出与同步工具，从中文 TS 源码导出扁平 key-value CSV 语言包并校验完整性。
// 核心逻辑:
//   1. 利用 esbuild 静态解析 frontend/src/i18n/locales/zh-CN.ts
//   2. 递归展平嵌套对象为点路径键 (e.g. game.tab.cultivation)
//   3. 增量校验与更新 frontend/public/locales/en-US.csv 及对照模板 template.csv
// ==============================================================================
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const rootDir = path.resolve(__dirname, '..');
const frontendDir = path.join(rootDir, 'frontend');
const localesOutDir = path.join(frontendDir, 'public', 'locales');

// 递归展平嵌套字典对象
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

// 解析 RFC 4180 CSV 为 Record<string, string>
export function parseCsvToMap(csvText) {
  const result = {};
  const length = csvText.length;
  let cursor = 0;

  function readField() {
    if (cursor >= length) return { value: '', isLastInRecord: true, isEof: true };
    let field = '';
    if (csvText[cursor] === '"') {
      cursor++;
      while (cursor < length) {
        if (csvText[cursor] === '"') {
          if (cursor + 1 < length && csvText[cursor + 1] === '"') {
            field += '"';
            cursor += 2;
          } else {
            cursor++;
            break;
          }
        } else {
          field += csvText[cursor];
          cursor++;
        }
      }
      while (cursor < length && (csvText[cursor] === ' ' || csvText[cursor] === '\t')) cursor++;
    } else {
      while (cursor < length && csvText[cursor] !== ',' && csvText[cursor] !== '\r' && csvText[cursor] !== '\n') {
        field += csvText[cursor];
        cursor++;
      }
    }

    let isLastInRecord = false;
    if (cursor >= length) {
      isLastInRecord = true;
    } else if (csvText[cursor] === ',') {
      cursor++;
    } else if (csvText[cursor] === '\r' || csvText[cursor] === '\n') {
      isLastInRecord = true;
      if (csvText[cursor] === '\r' && cursor + 1 < length && csvText[cursor + 1] === '\n') {
        cursor += 2;
      } else {
        cursor++;
      }
    }
    return { value: field, isLastInRecord, isEof: cursor >= length };
  }

  let isHeader = true;
  while (cursor < length) {
    const keyField = readField();
    if (keyField.isEof && keyField.value.trim() === '') break;
    let valField = { value: '', isLastInRecord: true, isEof: true };
    if (!keyField.isLastInRecord) {
      valField = readField();
    }
    while (!valField.isLastInRecord && !valField.isEof) {
      valField = readField();
    }
    if (isHeader) {
      isHeader = false;
      if (keyField.value.trim().toLowerCase() === 'key') continue;
    }
    const k = keyField.value.trim();
    if (k) result[k] = valField.value;
  }
  return result;
}

async function loadTsModule(entryPath) {
  const esbuild = await import(path.join(frontendDir, 'node_modules', 'esbuild', 'lib', 'main.js'));
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

async function main() {
  fs.mkdirSync(localesOutDir, { recursive: true });

  // 1. 读取 zh-CN 作为基准
  const zhModule = await loadTsModule(path.join(frontendDir, 'src', 'i18n', 'locales', 'zh-CN.ts'));
  const flatZh = flattenObject(zhModule.zhCN);
  const zhKeys = Object.keys(flatZh).sort();
  const zhKeyCount = zhKeys.length;
  console.log(`[i18n:export] 成功解析 zh-CN 基准词典，共 ${zhKeyCount} 条词条`);

  // 2. 处理 en-US.csv (若存在则做增量同步与空缺检测)
  const enCsvPath = path.join(localesOutDir, 'en-US.csv');
  let currentEnMap = {};
  if (fs.existsSync(enCsvPath)) {
    const existingContent = fs.readFileSync(enCsvPath, 'utf-8');
    currentEnMap = parseCsvToMap(existingContent);
  }

  // 检查缺失词条与冗余词条
  const missingKeys = [];
  const mergedEn = {};
  for (const k of zhKeys) {
    if (currentEnMap[k] !== undefined && currentEnMap[k] !== '') {
      mergedEn[k] = currentEnMap[k];
    } else {
      missingKeys.push(k);
      mergedEn[k] = currentEnMap[k] ?? '';
    }
  }

  const obsoleteKeys = Object.keys(currentEnMap).filter(k => flatZh[k] === undefined);

  fs.writeFileSync(enCsvPath, toCsv(mergedEn), 'utf-8');
  console.log(`[i18n:export] 已同步 en-US.csv (${Object.keys(mergedEn).length} 条词条)`);
  if (missingKeys.length > 0) {
    console.warn(`[i18n:export] ⚠️ en-US.csv 尚有 ${missingKeys.length} 条词条未提供英文翻译（将自动回退中文）`);
  } else {
    console.log(`[i18n:export] ✅ en-US.csv 翻译覆盖率 100%`);
  }
  if (obsoleteKeys.length > 0) {
    console.warn(`[i18n:export] ℹ️ 已清理 ${obsoleteKeys.length} 条已在中文源中删除的废弃键`);
  }

  // 3. 导出 template.csv（供翻译人员使用的最新中文对照模板）
  const templateLines = ['key,zh_source,translation'];
  for (const k of zhKeys) {
    const zhVal = `"${flatZh[k].replace(/"/g, '""')}"`;
    templateLines.push(`${k},${zhVal},""`);
  }
  const templatePath = path.join(localesOutDir, 'template.csv');
  fs.writeFileSync(templatePath, templateLines.join('\n') + '\n', 'utf-8');
  console.log(`[i18n:export] 成功生成对照翻译模板 template.csv (${zhKeyCount} 条词条)`);
}

// 仅在直接运行脚本时调用 main
if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  main().catch(err => {
    console.error('[i18n:export] 导出失败:', err);
    process.exit(1);
  });
}
