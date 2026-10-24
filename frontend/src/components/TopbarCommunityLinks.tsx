import { memo, useCallback } from 'react'
import { useT } from '../i18n'
import { useGameStore } from '../game/gameStore'
import { FloatingTooltip } from './FloatingPopover'

/** 官方 QQ 群号 */
export const OFFICIAL_QQ_GROUP = '940886387'
/** 移动端 QQ 加群 DeepLink 协议（对齐父项目 daojie-yusheng） */
export const QQ_GROUP_MOBILE_DEEP_LINK = `mqqapi://card/show_pslcard?src_type=internal&version=1&uin=${OFFICIAL_QQ_GROUP}&card_type=group&source=qrcode`
/** 桌面端 QQ 加群 DeepLink 协议（对齐父项目 daojie-yusheng） */
export const QQ_GROUP_DESKTOP_DEEP_LINK = `tencent://AddContact/?fromId=45&fromSubId=1&subcmd=all&uin=${OFFICIAL_QQ_GROUP}`

/** GitHub 源码仓库地址 */
export const GITHUB_REPO_URL = 'https://github.com/fruktoguo/DaoHua'
/** GitHub 最新版本安装包直链下载地址（直接调用下载，不跳转页面） */
export const GITHUB_LATEST_APK_DOWNLOAD_URL = 'https://github.com/fruktoguo/daohua/releases/latest/download/daohua.apk'

/** 根据运行环境解析 QQ 群 DeepLink 协议地址 */
function resolveQqGroupLink(): string {
  if (typeof navigator === 'undefined') return QQ_GROUP_DESKTOP_DEEP_LINK
  const ua = navigator.userAgent.toLowerCase()
  const isMobile = /android|iphone|ipad|ipod|mobile/.test(ua)
  return isMobile ? QQ_GROUP_MOBILE_DEEP_LINK : QQ_GROUP_DESKTOP_DEEP_LINK
}

/** 异步复制群号至剪贴板（支持非安全上下文平滑降级） */
async function copyTextToClipboard(text: string): Promise<boolean> {
  try {
    if (navigator?.clipboard) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // 降级使用 textarea
  }
  try {
    const input = document.createElement('textarea')
    input.value = text
    input.style.position = 'fixed'
    input.style.opacity = '0'
    input.style.left = '-9999px'
    document.body.appendChild(input)
    input.select()
    const success = document.execCommand('copy')
    document.body.removeChild(input)
    return success
  } catch {
    return false
  }
}

/** 原地尝试唤起本地客户端协议链接，杜绝空白标签页弹出 */
function openProtocolUrl(url: string): boolean {
  try {
    const link = document.createElement('a')
    link.href = url
    link.style.display = 'none'
    document.body.appendChild(link)
    link.click()
    document.body.removeChild(link)
    return true
  } catch {
    try {
      window.location.href = url
      return true
    } catch {
      return false
    }
  }
}

/**
 * 横版顶栏社区与安装包下载外链组：
 * 包含 QQ 交流群（直接唤起 QQ 客户端 + 静默复制群号备用）、GitHub 仓库与 GitHub 最新安装包直接下载（原地下载不跳转）。
 */
export const TopbarCommunityLinks = memo(function TopbarCommunityLinks(): React.JSX.Element {
  const t = useT()

  const handleOpenQq = useCallback(async () => {
    const deepLink = resolveQqGroupLink()
    const copyPromise = copyTextToClipboard(OFFICIAL_QQ_GROUP)

    // 原地唤起本地客户端协议，不产生跳转空白页
    const opened = openProtocolUrl(deepLink)
    const copied = await copyPromise

    window.setTimeout(() => {
      // 若已切出至 QQ 客户端（浏览器进入后台），不弹出额外气泡打扰
      if (typeof document !== 'undefined' && document.visibilityState !== 'visible') {
        return
      }
      if (!opened) {
        useGameStore.getState().setNotice(t('app.topbar.qqLaunchBlocked', { group: OFFICIAL_QQ_GROUP }))
      } else if (copied) {
        useGameStore.getState().setNotice(t('app.topbar.qqLaunchedWithCopied', { group: OFFICIAL_QQ_GROUP }))
      } else {
        useGameStore.getState().setNotice(t('app.topbar.qqLaunched', { group: OFFICIAL_QQ_GROUP }))
      }
    }, 600)
  }, [t])

  const handleDownloadClick = useCallback(() => {
    useGameStore.getState().setNotice(t('app.topbar.downloadStarted'))
  }, [t])

  return (
    <>
      {/* 1. 官方 QQ 群（直接唤起客户端并复制群号备用） */}
      <FloatingTooltip
        displayContents
        placement="bottom"
        title={t('app.topbar.qqTip', { group: OFFICIAL_QQ_GROUP })}
        tone="gold"
      >
        <button
          aria-label={t('app.topbar.qqAria', { group: OFFICIAL_QQ_GROUP })}
          className="topbar-link-btn"
          onClick={handleOpenQq}
          type="button"
        >
          <svg aria-hidden="true" fill="currentColor" viewBox="0 0 24 24">
            <path d="M21.395 15.035a40 40 0 0 0-.803-2.264l-1.079-2.695c.001-.032.014-.562.014-.836C19.526 4.632 17.351 0 12 0S4.474 4.632 4.474 9.241c0 .274.013.804.014.836l-1.08 2.695a39 39 0 0 0-.802 2.264c-1.021 3.283-.69 4.643-.438 4.673.54.065 2.103-2.472 2.103-2.472 0 1.469.756 3.387 2.394 4.771-.612.188-1.363.479-1.845.835-.434.32-.379.646-.301.778.343.578 5.883.369 7.482.189 1.6.18 7.14.389 7.483-.189.078-.132.132-.458-.301-.778-.483-.356-1.233-.646-1.846-.836 1.637-1.384 2.393-3.302 2.393-4.771 0 0 1.563 2.537 2.103 2.472.251-.03.581-1.39-.438-4.673" />
          </svg>
        </button>
      </FloatingTooltip>

      {/* 2. GitHub 源码仓库 */}
      <FloatingTooltip
        displayContents
        placement="bottom"
        title={t('app.topbar.githubTip')}
        tone="gold"
      >
        <a
          aria-label={t('app.topbar.githubAria')}
          className="topbar-link-btn"
          href={GITHUB_REPO_URL}
          rel="noopener noreferrer"
          target="_blank"
        >
          <svg aria-hidden="true" fill="currentColor" viewBox="0 0 24 24">
            <path
              clipRule="evenodd"
              d="M12 2C6.477 2 2 6.484 2 12.017c0 4.425 2.865 8.18 6.839 9.504.5.092.682-.217.682-.483 0-.237-.008-.868-.013-1.703-2.782.605-3.369-1.343-3.369-1.343-.454-1.158-1.11-1.466-1.11-1.466-.908-.62.069-.608.069-.608 1.003.07 1.53 1.032 1.53 1.032.892 1.53 2.341 1.088 2.91.832.092-.647.35-1.088.636-1.338-2.22-.253-4.555-1.113-4.555-4.951 0-1.093.39-1.988 1.029-2.688-.103-.253-.446-1.272.098-2.65 0 0 .84-.27 2.75 1.026A9.564 9.564 0 0 1 12 6.844c.85.004 1.705.115 2.504.337 1.909-1.296 2.747-1.027 2.747-1.027.546 1.379.202 2.398.1 2.651.64.7 1.028 1.595 1.028 2.688 0 3.848-2.339 4.695-4.566 4.943.359.309.678.92.678 1.855 0 1.338-.012 2.419-.012 2.747 0 .268.18.58.688.482A10.019 10.019 0 0 0 22 12.017C22 6.484 17.522 2 12 2z"
              fillRule="evenodd"
            />
          </svg>
        </a>
      </FloatingTooltip>

      {/* 3. GitHub 游戏安装包直接下载（原地下载不跳转页面） */}
      <FloatingTooltip
        displayContents
        placement="bottom"
        title={t('app.topbar.downloadTip')}
        tone="gold"
      >
        <a
          aria-label={t('app.topbar.downloadAria')}
          className="topbar-link-btn"
          download="daohua.apk"
          href={GITHUB_LATEST_APK_DOWNLOAD_URL}
          onClick={handleDownloadClick}
        >
          <svg
            aria-hidden="true"
            fill="none"
            stroke="currentColor"
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth="1.8"
            viewBox="0 0 24 24"
          >
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
            <polyline points="7 10 12 15 17 10" />
            <line x1="12" x2="12" y1="15" y2="3" />
          </svg>
        </a>
      </FloatingTooltip>
    </>
  )
})
