import { memo, useCallback } from 'react'
import { useT } from '../i18n'
import { useGameStore } from '../game/gameStore'
import { FloatingTooltip } from './FloatingPopover'

/** 官方 QQ 群号 */
export const OFFICIAL_QQ_GROUP = '940886387'
/** GitHub 源码仓库地址 */
export const GITHUB_REPO_URL = 'https://github.com/fruktoguo/DaoHua'
/** GitHub 安装包 Releases 下载地址 */
export const GITHUB_RELEASES_URL = 'https://github.com/fruktoguo/DaoHua/releases'

/**
 * 横版顶栏社区与安装包下载外链组：
 * 包含 QQ 交流群（点击一键复制群号）、GitHub 仓库与 GitHub 游戏安装包下载。
 */
export const TopbarCommunityLinks = memo(function TopbarCommunityLinks(): React.JSX.Element {
  const t = useT()

  const handleCopyQq = useCallback(async () => {
    let copied = false
    try {
      if (navigator.clipboard) {
        await navigator.clipboard.writeText(OFFICIAL_QQ_GROUP)
        copied = true
      }
    } catch {
      // 降级使用 textarea
    }
    if (!copied) {
      try {
        const input = document.createElement('textarea')
        input.value = OFFICIAL_QQ_GROUP
        input.style.position = 'fixed'
        input.style.opacity = '0'
        document.body.appendChild(input)
        input.select()
        copied = document.execCommand('copy')
        document.body.removeChild(input)
      } catch {
        // 容错兜底
      }
    }
    useGameStore.getState().setNotice(t('app.topbar.qqCopied', { group: OFFICIAL_QQ_GROUP }))
  }, [t])

  return (
    <>
      {/* 1. 官方 QQ 群（点击复制） */}
      <FloatingTooltip
        displayContents
        placement="bottom"
        title={t('app.topbar.qqTip', { group: OFFICIAL_QQ_GROUP })}
        tone="gold"
      >
        <button
          aria-label={t('app.topbar.qqAria', { group: OFFICIAL_QQ_GROUP })}
          className="topbar-link-btn"
          onClick={handleCopyQq}
          type="button"
        >
          <svg aria-hidden="true" fill="currentColor" viewBox="0 0 24 24">
            <path d="M21.395 15.035a4.57 4.57 0 0 0-.847-1.396c-.636-.708-1.164-1.127-1.164-2.19 0-3.328-2.22-6.527-5.59-7.234A4.6 4.6 0 0 0 12.04 4a4.6 4.6 0 0 0-1.755.215c-3.37.707-5.59 3.906-5.59 7.234 0 1.063-.528 1.482-1.164 2.19a4.57 4.57 0 0 0-.847 1.396c-.477 1.18-.328 2.37.408 2.97.746.61 1.944.57 3.01.218.423.834 1.08 1.54 1.91 2.02.164.846.887 1.757 2.068 1.757 1.18 0 1.904-.91 2.068-1.757.83-.48 1.487-1.186 1.91-2.02 1.066.352 2.264.392 3.01-.218.736-.6.885-1.79.408-2.97z" />
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

      {/* 3. GitHub 游戏安装包下载 */}
      <FloatingTooltip
        displayContents
        placement="bottom"
        title={t('app.topbar.downloadTip')}
        tone="gold"
      >
        <a
          aria-label={t('app.topbar.downloadAria')}
          className="topbar-link-btn"
          href={GITHUB_RELEASES_URL}
          rel="noopener noreferrer"
          target="_blank"
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
