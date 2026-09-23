#!/usr/bin/env node
// 总纲查看器同步脚本
// 扫描 docs/design/总纲/ 下 NN-*.md 篇目 →
//   1. 重写 manifest.json（HTTP 访问时查看器自动逐篇 fetch 的清单）；
//   2. 重写 index.html 内嵌快照块（file:// 直开兜底，篇目间以 <!-- FILE:名 --> 哨兵拼接）。
// 用法：node scripts/sync-master-viewer.mjs（仓库根目录执行）
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const DIR = 'docs/design/总纲';
const HTML = join(DIR, 'index.html');
const CHAPTER_RE = /^\d{2}-.*\.md$/;

const files = readdirSync(DIR).filter((f) => CHAPTER_RE.test(f)).sort();
if (files.length === 0) {
  console.error(`未在 ${DIR} 找到 NN-*.md 篇目`);
  process.exit(1);
}

writeFileSync(join(DIR, 'manifest.json'), JSON.stringify({ files }, null, 2) + '\n');

const combined = files
  .map((f) => `<!-- FILE:${f} -->\n${readFileSync(join(DIR, f), 'utf8').trim()}`)
  .join('\n\n');
if (combined.includes('</script>')) {
  console.error('篇目内容包含 </script>，无法内嵌');
  process.exit(1);
}

const html = readFileSync(HTML, 'utf8');
const replaced = html.replace(
  /(<script type="text\/markdown" id="default-markdown">)[\s\S]*?(<\/script>)/,
  `$1\n${combined}\n$2`,
);
if (replaced === html) {
  console.error('index.html 未找到 default-markdown 内嵌块');
  process.exit(1);
}
writeFileSync(HTML, replaced);
console.log(`已同步：manifest.json + 内嵌快照（${files.length} 篇，${combined.length} 字）`);
