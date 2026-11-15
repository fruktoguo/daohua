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

const MOCK_MAILS = [
  {
    mailKey: 'p:1',
    kind: 'personal',
    title: '未读有奖传书',
    senderName: '太初执事司',
    createdAt: '2026-11-15T10:00:00Z',
    expiresAt: '2026-12-15T10:00:00Z',
    hasAttachments: true,
    isRead: false,
    isClaimed: false,
    onlyExistingPlayers: true,
    attachments: [{ itemId: 'currency.spirit-stone', quantity: 100 }],
  },
  {
    mailKey: 'p:2',
    kind: 'personal',
    title: '已读无奖传书',
    senderName: '太初执事司',
    createdAt: '2026-11-15T09:00:00Z',
    expiresAt: '2026-12-15T09:00:00Z',
    hasAttachments: false,
    isRead: true,
    isClaimed: false,
    onlyExistingPlayers: true,
    attachments: [],
  },
]

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
              let currentMails = ${JSON.stringify(MOCK_MAILS)};
              export const fetchMailList = async () => ({
                mails: currentMails,
                summary: { unreadCount: 1, claimableCount: 1, forceRedDot: false },
              });
              export const fetchMailDetail = async (mailKey) => ({
                mail: { ...(currentMails.find(m => m.mailKey === mailKey) ?? currentMails[0]), isRead: true, body: '正文内容' },
                summary: { unreadCount: 0, claimableCount: 1, forceRedDot: false },
              });
              export const markMailRead = async () => ({ mailKey: '', summary: { unreadCount: 0, claimableCount: 0 } });
              export const claimMailAttachments = async () => ({ claimedMailKeys: [], grantedAttachments: [], summary: { unreadCount: 0, claimableCount: 0 } });
              export const claimAllMailAttachments = async () => ({ claimedMailKeys: [], grantedAttachments: [], summary: { unreadCount: 0, claimableCount: 0 } });
              export const deleteMail = async (mailKey) => ({ deletedMailKeys: [mailKey], skippedUnclaimedCount: 0, summary: { unreadCount: 1, claimableCount: 1 } });
              export const deleteReadMails = async () => ({ deletedMailKeys: ['p:2'], skippedUnclaimedCount: 0, summary: { unreadCount: 1, claimableCount: 1 } });
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

const { hasMailRedDot, filterMailItems, useMailStore } = await import(pathToFileURL(bundleMailStorePath).href)
const { useUiStore } = await import(pathToFileURL(bundleUiStorePath).href)

test.after(async () => {
  await Promise.all([
    rm(bundleMailStorePath, { force: true }),
    rm(bundleUiStorePath, { force: true }),
  ])
})

test('MailModal 独立弹窗契约、Icon 显式尺寸及公共组件规范', async () => {
  const [mailModalSource, appSource] = await Promise.all([
    readFile(path.resolve(repoRoot, 'frontend/src/components/MailModal.tsx'), 'utf8'),
    readFile(path.resolve(repoRoot, 'frontend/src/App.tsx'), 'utf8'),
  ])

  assert.match(mailModalSource, /<Modal[\s\S]*?\bclassName="mail-modal"/, 'MailModal 必须复用公共 Modal 且挂载 .mail-modal 类名')

  const iconTags = [...mailModalSource.matchAll(/<Icon\b[\s\S]*?\/>/g)].map(m => m[0])
  assert.ok(iconTags.length >= 3, 'MailModal 应包含标题、刷新与空态 Icon')
  for (const tag of iconTags) {
    assert.match(tag, /\bsize=\{/, `Icon 必须显式传入 size 属性防止撑爆标题排版：\n${tag}`)
  }

  const tileTags = [...mailModalSource.matchAll(/<ItemTile\b[\s\S]*?\/>/g)].map(m => m[0])
  assert.ok(tileTags.length >= 1, 'MailModal 必须使用 ItemTile 渲染邮件附件')
  for (const tag of tileTags) {
    assert.match(tag, /\bitemId=\{/, `ItemTile 缺少显式 itemId 属性：\n${tag}`)
  }

  assert.match(mailModalSource, /<RichTemplateText[\s\S]*?\btemplate=\{/, 'MailModal 必须向 RichTemplateText 传入 template 属性')
  assert.match(mailModalSource, /app\.mail\.neverExpires/, 'MailModal 必须支持永不过期邮件文案显示')
  assert.match(appSource, /<MailModal\s+open=\{mailOpen\}\s+onClose=\{/, 'App.tsx 必须在顶层弹窗层挂载独立 MailModal')
  assert.doesNotMatch(appSource, /hidden=\{mailOpen \|\|/, '独立弹窗化后 App.tsx 不得用 mailOpen 隐藏底层主界面页签')
})

test('顶栏角标排布与横竖屏响应式规则完整', async () => {
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

  assert.match(mailCssSource, /\.mail-modal\.modal-shell[\s\S]*?overflow:\s*hidden/,
    '弹窗外框必须锁死 overflow: hidden，避免内外双滚动条')
  assert.match(mailCssSource, /@media\s*\(orientation:\s*landscape\)\s*and\s*\(max-height:\s*540px\)/,
    '必须覆盖手机矮横屏布局以保持双栏独立滚动可用')
  assert.match(mailCssSource, /@media\s*\(max-width:\s*760px\),\s*\(orientation:\s*portrait\)\s*and\s*\(max-width:\s*900px\)/,
    '必须覆盖竖屏与窄屏单栏全高钻取布局')
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

test('useMailStore 刷新或切换筛选不擅自回退选中未点击信件', async () => {
  const mailStore = useMailStore.getState()
  mailStore.reset()

  await mailStore.refreshList()
  assert.equal(useMailStore.getState().mails.length, 2)
  assert.equal(useMailStore.getState().selectedMailKey, null, '初次加载列表不得在用户未点击前自动选中首封邮件')

  mailStore.setFilter('unread')
  assert.equal(useMailStore.getState().selectedMailKey, null, '切换至未读筛选亦不得自动回退选中未点击邮件')

  mailStore.setFilter('all')
  await mailStore.selectMail('p:2')
  assert.equal(useMailStore.getState().selectedMailKey, 'p:2')

  // p:2 为已读无奖，切至 unread 筛选不可见时应清空选中而非切到 p:1
  mailStore.setFilter('unread')
  assert.equal(useMailStore.getState().selectedMailKey, null)

  mailStore.setFilter('all')
  await mailStore.selectMail('p:2')
  mailStore.clearSelection()
  assert.equal(useMailStore.getState().selectedMailKey, null)

  await mailStore.selectMail('p:2')
  await mailStore.deleteOneMail('p:2')
  assert.equal(useMailStore.getState().selectedMailKey, null, '删除当前选中邮件后应清空选中而不跳选未点击信件')
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
