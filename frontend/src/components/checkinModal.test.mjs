import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import path from 'node:path'

const repoRoot = process.cwd()

test('CheckinModal 复用公共 Modal/Icon/ItemTile 且正确接入 App 顶层弹窗与自动调度', async () => {
  const [modalSource, appSource, cssSource] = await Promise.all([
    readFile(path.resolve(repoRoot, 'frontend/src/components/CheckinModal.tsx'), 'utf8'),
    readFile(path.resolve(repoRoot, 'frontend/src/App.tsx'), 'utf8'),
    readFile(path.resolve(repoRoot, 'frontend/src/styles/checkin.css'), 'utf8'),
  ])

  assert.match(modalSource, /<Modal[\s\S]*?\bclassName="checkin-modal"/, 'CheckinModal 必须复用公共 Modal 且挂载 .checkin-modal')

  const iconTags = [...modalSource.matchAll(/<Icon\b[\s\S]*?\/>/g)].map(m => m[0])
  assert.ok(iconTags.length >= 2, 'CheckinModal 应包含标题与按钮/倒计时 Icon')
  for (const tag of iconTags) {
    assert.match(tag, /\bsize=\{/, `Icon 必须显式传入 size 属性防止撑爆排版：\n${tag}`)
  }

  assert.match(modalSource, /<ItemTile[\s\S]*?\bitemId=\{CHECKIN_REWARD_ITEM_ID\}/, 'CheckinModal 必须复用 ItemTile 渲染绑定功德')
  assert.match(modalSource, /<ItemTooltipTarget[\s\S]*?\bitemId=\{CHECKIN_REWARD_ITEM_ID\}/, 'CheckinModal 必须包裹 ItemTooltipTarget 支持悬浮详情')

  assert.match(appSource, /SIDEBAR_OTHER_TABS[\s\S]*?'checkin'/, '桌面端侧边栏「其他」分组必须包含 checkin')
  assert.match(appSource, /<CheckinModal\s+open=\{checkinModalOpen\}\s+onClose=\{handleCloseCheckinModal\}/, 'App.tsx 必须在顶层弹窗层挂载独立 CheckinModal')
  assert.match(appSource, /computeMsUntilNextChinaMidnight/, 'App.tsx 必须调度 UTC+8 跨零点自动检测')
  assert.match(appSource, /visibilitychange/, 'App.tsx 必须在页签切回可见时核对跨日签到状态')

  assert.match(cssSource, /@media\s*\(max-width:\s*768px\)/, 'checkin.css 必须提供竖屏/窄屏单栏自适应样式')
  assert.match(cssSource, /@media\s*\(orientation:\s*landscape\)\s*and\s*\(max-height:\s*540px\)/, 'checkin.css 必须适配矮横屏布局')
})
