import test from 'node:test';
import assert from 'node:assert/strict';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs/promises';
import {
  buildPublishPayload,
  markdownToHtml,
  parseCliArgs,
  parseEnvText,
  resolveCookieHeader,
} from './gityx-publish.mjs';

test('parseEnvText 解析键值、注释和引号', () => {
  const parsed = parseEnvText(`
# comment
GITYX_PHPSESSID=abc123
GITYX_DID="did456"
GITYX_EVER_LOGIN='1'
`);
  assert.deepEqual(parsed, {
    GITYX_PHPSESSID: 'abc123',
    GITYX_DID: 'did456',
    GITYX_EVER_LOGIN: '1',
  });
});

test('resolveCookieHeader 支持环境变量与 .env.gityx 回退并拒绝缺失凭证', async () => {
  const direct = await resolveCookieHeader({ cookie: 'PHPSESSID=override' }, {});
  assert.equal(direct, 'PHPSESSID=override');

  const tmpDir = await fs.mkdtemp(path.join(os.tmpdir(), 'gityx-test-'));
  const envFile = path.join(tmpDir, '.env.gityx');
  await fs.writeFile(envFile, 'GITYX_PHPSESSID=sess999\nGITYX_DID=did888\n', 'utf8');

  try {
    const fromFile = await resolveCookieHeader({ envFile }, {});
    assert.equal(fromFile, 'PHPSESSID=sess999; gtyx_did=did888');

    const emptyFile = path.join(tmpDir, '.env.empty');
    await fs.writeFile(emptyFile, '# empty\n', 'utf8');
    await assert.rejects(
      () => resolveCookieHeader({ envFile: emptyFile }, {}),
      /缺少 Gityx 登录态 Cookie/
    );
  } finally {
    await fs.rm(tmpDir, { recursive: true, force: true });
  }
});

test('markdownToHtml 转换标题、列表、引用、链接与代码块', () => {
  const md = [
    '## v0.3.2 更新日志',
    '',
    '新增 **凝气归元** 行动，详见 [官网](https://daohua.yuohira.com/)。',
    '',
    '- 修复组队消耗品冻结',
    '- 优化 WASM 战斗模拟器',
    '',
    '> 欢迎加群交流',
    '',
    '```bash',
    'echo "<ok>"',
    '```',
  ].join('\n');

  const html = markdownToHtml(md);
  assert.match(html, /<h2>v0\.3\.2 更新日志<\/h2>/);
  assert.match(html, /<strong>凝气归元<\/strong>/);
  assert.match(html, /<a href="https:\/\/daohua\.yuohira\.com\/" target="_blank">官网<\/a>/);
  assert.match(html, /<ul><li>修复组队消耗品冻结<\/li><li>优化 WASM 战斗模拟器<\/li><\/ul>/);
  assert.match(html, /<blockquote><p>欢迎加群交流<\/p><\/blockquote>/);
  assert.match(html, /<pre><code>echo &quot;&lt;ok&gt;&quot;<\/code><\/pre>/);
});

test('buildPublishPayload 套用 daohua-changelog 预设并正确序列化数组字段', () => {
  const { params, payloadObject } = buildPublishPayload({
    preset: 'daohua-changelog',
    title: '道化太初 v0.3.2 更新日志',
    keywords: '道化太初,更新日志,修仙放置',
    description: '修复组队消耗品触发器并新增凝气归元行动',
    bodyHtml: '<p>正文</p>',
    updatetime: '2026-11-13 15:00:00',
    fields: {
      game_platform: '1,3',
      zuozhe: '尐妖',
    },
  });

  assert.equal(payloadObject.tid, '65');
  assert.equal(payloadObject.short_title, '道化太初');
  assert.equal(payloadObject.zuozhe, '尐妖');
  assert.equal(payloadObject.qqun, '940886387');
  assert.equal(payloadObject.isshow, '');
  assert.deepEqual(payloadObject['game_only[]'], ['3']);
  assert.deepEqual(payloadObject['game_platform[]'], ['1', '3']);
  assert.deepEqual(params.getAll('game_platform[]'), ['1', '3']);
  assert.equal(params.get('ajax'), '1');
  assert.equal(params.get('title'), '道化太初 v0.3.2 更新日志');
});

test('buildPublishPayload 在传入 id 时附带 id 与 isshow=1 用于编辑既有文章', () => {
  const { params, payloadObject } = buildPublishPayload({
    id: '1576',
    preset: 'daohua-changelog',
    title: '【更新日志】道化太初，版本更新 v0.3.3',
    bodyHtml: '<p>内容</p>',
  });
  assert.equal(payloadObject.id, '1576');
  assert.equal(payloadObject.isshow, '1');
  assert.equal(params.get('id'), '1576');
  assert.equal(params.get('isshow'), '1');
});

test('parseCliArgs 解析 publish 参数与动态 --field', () => {
  const parsed = parseCliArgs([
    'publish',
    '--preset', 'daohua-changelog',
    '--title', '测试标题',
    '--field', 'game_only=1',
    '--field', 'game_only=3',
    '--dry-run',
  ]);
  assert.equal(parsed.command, 'publish');
  assert.equal(parsed.opts.preset, 'daohua-changelog');
  assert.equal(parsed.opts.title, '测试标题');
  assert.equal(parsed.opts.dryRun, true);
  assert.deepEqual(parsed.opts.fields.game_only, ['1', '3']);
});
