import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { build } from 'esbuild'

const repoRoot = process.cwd()

const bundleMailStorePath = path.join(tmpdir(), `daohua-mail-store-${Date.now()}.mjs`)
const bundleUiStorePath = path.join(tmpdir(), `daohua-ui-store-${Date.now()}.mjs`)

await Promise.all([
  build({
    entryPoints: [path.resolve(repoRoot, 'frontend/src/game/mailStore.ts')],
    bundle: true,
    format: 'esm',
    outfile: bundleMailStorePath,
    logLevel: 'error',
    define: { 'import.meta.env': '{"VITE_APP_VERSION":"dev"}' },
    plugins: [
      {
        name: 'mock-mail-deps',
        setup(b) {
          b.onResolve({ filter: /\/i18n($|\/)/ }, args => ({ path: args.path, namespace: 'mock-i18n' }))
          b.onLoad({ filter: /.*/, namespace: 'mock-i18n' }, () => ({
            contents: 'export const getT = () => (key) => key; export const useT = () => (key) => key; export const hasTranslation = () => false; export const useI18nStore = { getState: () => ({ locale: "zh" }) };',
            loader: 'js',
          }))
          b.onResolve({ filter: /\/network\/apiClient$/ }, args => ({ path: args.path, namespace: 'mock-api' }))
          b.onLoad({ filter: /.*/, namespace: 'mock-api' }, () => ({
            contents: `
              export const fetchMailList = async () => ({ mails: [], summary: { unreadCount: 0, claimableCount: 0 } });
              export const fetchMailDetail = async () => ({ mail: null, summary: { unreadCount: 0, claimableCount: 0 } });
              export const markMailRead = async () => ({ mailKey: '', summary: { unreadCount: 0, claimableCount: 0 } });
              export const claimMailAttachments = async () => ({ claimedMailKeys: [], grantedAttachments: [], summary: { unreadCount: 0, claimableCount: 0 } });
              export const claimAllMailAttachments = async () => ({ claimedMailKeys: [], grantedAttachments: [], summary: { unreadCount: 0, claimableCount: 0 } });
              export const deleteMail = async () => ({ deletedMailKeys: [], skippedUnclaimedCount: 0, summary: { unreadCount: 0, claimableCount: 0 } });
              export const deleteReadMails = async () => ({ deletedMailKeys: [], skippedUnclaimedCount: 0, summary: { unreadCount: 0, claimableCount: 0 } });
            `,
            loader: 'js',
          }))
        },
      },
    ],
  }),
  build({
    entryPoints: [path.resolve(repoRoot, 'frontend/src/uiStore.ts')],
    bundle: true,
    format: 'esm',
    outfile: bundleUiStorePath,
    logLevel: 'error',
    define: { 'import.meta.env': '{"VITE_APP_VERSION":"dev"}' },
    plugins: [
      {
        name: 'mock-ui-deps',
        setup(b) {
          b.onResolve({ filter: /\/i18n($|\/)/ }, args => ({ path: args.path, namespace: 'mock-i18n' }))
          b.onLoad({ filter: /.*/, namespace: 'mock-i18n' }, () => ({
            contents: 'export const getT = () => (key) => key; export const useT = () => (key) => key; export const useI18nStore = { getState: () => ({ locale: "zh" }) };',
            loader: 'js',
          }))
        },
      },
    ],
  }),
])

const { hasMailRedDot, filterMailItems } = await import(pathToFileURL(bundleMailStorePath).href)
const { useUiStore } = await import(pathToFileURL(bundleUiStorePath).href)

test.after(async () => {
  await Promise.all([
    rm(bundleMailStorePath, { force: true }),
    rm(bundleUiStorePath, { force: true }),
  ])
})

test('MailPage 所有 ItemTile 必须显式传 itemId，RichTemplateText 必须使用 template 属性', async () => {
  const mailPageSource = await readFile(path.resolve(repoRoot, 'frontend/src/components/MailPage.tsx'), 'utf8')
  const tileTags = [...mailPageSource.matchAll(/<ItemTile\b[\s\S]*?\/>/g)].map(m => m[0])
  assert.ok(tileTags.length >= 1, 'MailPage 必须使用 ItemTile 渲染邮件附件')
  for (const tag of tileTags) {
    assert.match(tag, /\bitemId=\{/, `ItemTile 缺少显式 itemId 属性：\n${tag}`)
  }

  assert.match(mailPageSource, /<RichTemplateText[\s\S]*?\btemplate=\{/, 'MailPage 必须向 RichTemplateText 传入 template 属性')
})

test('顶栏角标排布于 TaijiToggle 紧邻左侧且竖屏缩短等级进度条', async () => {
  const [appSource, mailCssSource] = await Promise.all([
    readFile(path.resolve(repoRoot, 'frontend/src/App.tsx'), 'utf8'),
    readFile(path.resolve(repoRoot, 'frontend/src/styles/mail.css'), 'utf8'),
  ])

  const topbarActionsMatch = appSource.match(/<div className="topbar-actions">[\s\S]*?<\/div>/)
  assert.ok(topbarActionsMatch, 'App.tsx 必须存在 .topbar-actions 容器')
  const mailIndex = topbarActionsMatch[0].indexOf('<TopbarMailButton')
  const taijiIndex = topbarActionsMatch[0].indexOf('<TaijiToggle')
  assert.ok(mailIndex !== -1 && taijiIndex !== -1 && mailIndex < taijiIndex,
    'TopbarMailButton 必须放在 .topbar-actions 中 TaijiToggle 的紧邻左侧')

  assert.match(mailCssSource, /@media\s*\(max-width:\s*960px\)[\s\S]*?\.topbar-actions\s+\.topbar-mail-btn[\s\S]*?display:\s*inline-flex/,
    '竖屏 <=960px 下顶栏邮件按钮必须显式保持可见')
  assert.match(mailCssSource, /@media\s*\(max-width:\s*960px\)[\s\S]*?\.topbar\s+\.realm-progress[\s\S]*?max-width:\s*152px/,
    '竖屏 <=960px 下必须缩短左侧境界进度条')
  assert.match(mailCssSource, /@media\s*\(max-width:\s*520px\)[\s\S]*?\.topbar\s+\.realm-progress[\s\S]*?max-width:\s*118px/,
    '小屏 <=520px 下必须进一步缩短左侧境界进度条')
})

test('hasMailRedDot 与 filterMailItems 纯逻辑判断准确', () => {
  assert.equal(hasMailRedDot({ unreadCount: 0, claimableCount: 0 }), false)
  assert.equal(hasMailRedDot({ unreadCount: 0, claimableCount: 0, forceRedDot: false }), false)
  assert.equal(hasMailRedDot({ unreadCount: 1, claimableCount: 0 }), true)
  assert.equal(hasMailRedDot({ unreadCount: 0, claimableCount: 1 }), true)
  assert.equal(hasMailRedDot({ unreadCount: 0, claimableCount: 0, forceRedDot: true }), true)

  const sampleMails = [
    { mailKey: 'p:1', kind: 'personal', title: '未读有奖', senderName: '司', createdAt: '2026-11-15T10:00:00Z', expiresAt: '2026-12-15T10:00:00Z', hasAttachments: true, isRead: false, isClaimed: false, onlyExistingPlayers: true, attachments: [] },
    { mailKey: 'p:2', kind: 'personal', title: '已读有奖', senderName: '司', createdAt: '2026-11-15T09:00:00Z', expiresAt: '2026-12-15T09:00:00Z', hasAttachments: true, isRead: true, isClaimed: false, onlyExistingPlayers: true, attachments: [] },
    { mailKey: 'p:3', kind: 'personal', title: '已读无奖', senderName: '司', createdAt: '2026-11-15T08:00:00Z', expiresAt: '2026-12-15T08:00:00Z', hasAttachments: false, isRead: true, isClaimed: false, onlyExistingPlayers: true, attachments: [] },
    { mailKey: 'b:1', kind: 'broadcast', title: '全服已领', senderName: '司', createdAt: '2026-11-15T07:00:00Z', expiresAt: '2026-12-15T07:00:00Z', hasAttachments: true, isRead: true, isClaimed: true, onlyExistingPlayers: true, attachments: [] },
  ]

  assert.equal(filterMailItems(sampleMails, 'all').length, 4)
  assert.deepEqual(filterMailItems(sampleMails, 'unread').map(m => m.mailKey), ['p:1'])
  assert.deepEqual(filterMailItems(sampleMails, 'claimable').map(m => m.mailKey), ['p:1', 'p:2'])
})

test('uiStore mailOpen 状态切换与任意导航跳转自动关闭', () => {
  const store = useUiStore.getState()
  store.setMailOpen(false)
  assert.equal(useUiStore.getState().mailOpen, false)

  store.toggleMailOpen()
  assert.equal(useUiStore.getState().mailOpen, true)

  // 点击同一个移动分组时，即便 activeTab 还在该分组，也必须关闭 mailOpen 还原原页面
  store.activateMobileNavGroup('explore')
  assert.equal(useUiStore.getState().mailOpen, false)

  store.toggleMailOpen()
  assert.equal(useUiStore.getState().mailOpen, true)
  store.setActiveTab('market')
  assert.equal(useUiStore.getState().mailOpen, false)
  assert.equal(useUiStore.getState().activeTab, 'market')

  store.setMailOpen(true)
  store.setCraftingMode('forging')
  assert.equal(useUiStore.getState().mailOpen, false)

  store.setMailOpen(true)
  store.navigateToMapNode('node.yunxi-village')
  assert.equal(useUiStore.getState().mailOpen, false)
  assert.equal(useUiStore.getState().activeTab, 'map')
})
