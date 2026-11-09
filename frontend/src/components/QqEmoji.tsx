import { memo, useCallback, useEffect, useState } from 'react'
import { getQqEmojiUrls, QQ_EMOJI_MAP } from '../game/qqEmojis.ts'
import { chatStickerService } from '../game/chatStickerService.ts'

export interface QqEmojiProps {
  /** 经典表情名称，如 "微笑" 或 "呲牙" */
  name: string
  /** 尺寸（像素），聊天混排默认 20px，面板展示默认 28px */
  size?: number
  className?: string
  title?: string
}

/**
 * 经典 QQ 风格动态表情渲染组件 (QqEmoji)
 *
 * 容灾与加载流转机制：
 * 1. 本地离线检查：优先尝试从 IndexedDB (daohua_image_cache) 读取已解包的 Blob URL，秒开且零流量；
 * 2. 一级优先分发：尝试加载 Cloudflare R2 自建图床 (https://img.yuohira.com/emotes/s{qsid}.gif)；
 * 3. 二级自愈回退：若 CF 图床遇到网络异常或未命中，自动切换为本地游戏服务器资源 (/emotes/s{qsid}.gif)；
 * 4. 离线沉淀：网络拉取成功后，自动写入 IndexedDB，后续离线脱机可用；
 * 5. 极端兜底：全部资源不可用时，优雅展示 [表情名] 文本徽章。
 */
export const QqEmoji = memo(function QqEmoji({
  name,
  size = 20,
  className = '',
  title,
}: QqEmojiProps): React.JSX.Element {
  const meta = QQ_EMOJI_MAP.get(name)
  const qsid = meta?.qsid ?? ''
  const { cfUrl, localUrl } = qsid ? getQqEmojiUrls(qsid) : { cfUrl: '', localUrl: '' }

  // 状态机：'cached' | 'cf' | 'local' | 'failed'
  const [currentSource, setCurrentSource] = useState<'cached' | 'cf' | 'local' | 'failed'>('cf')
  const [displaySrc, setDisplaySrc] = useState<string>(cfUrl)

  useEffect(() => {
    if (!cfUrl || !localUrl) return
    let active = true

    // 1. 尝试从内存缓存同步获取
    const cached = chatStickerService.getCachedUrl(cfUrl) || chatStickerService.getCachedUrl(localUrl)
    if (cached) {
      setDisplaySrc(cached)
      setCurrentSource('cached')
      return
    }

    // 2. 异步查询 IndexedDB，优先尝试 CF 资源，命中后换成 ObjectURL
    chatStickerService.loadImageWithCache(cfUrl).then((resolved: string) => {
      if (!active) return
      if (resolved && resolved !== cfUrl) {
        // 成功提取或下载到了 Blob URL
        setDisplaySrc(resolved)
        setCurrentSource('cached')
      } else {
        setDisplaySrc(cfUrl)
        setCurrentSource('cf')
      }
    })

    return () => {
      active = false
    }
  }, [cfUrl, localUrl])

  // 处理图片加载失败（双源切换）
  const handleError = useCallback(() => {
    if (currentSource === 'cf') {
      // CF 失败，自动回退到游戏服务器本地静态源
      setCurrentSource('local')
      setDisplaySrc(localUrl)
      chatStickerService.loadImageWithCache(localUrl).catch(() => {})
    } else if (currentSource === 'local') {
      // 本地也失败，降级为文字
      setCurrentSource('failed')
    }
  }, [currentSource, localUrl])

  // 在所有 Hook 调用完成后再执行条件返回
  if (!meta || currentSource === 'failed') {
    return <span className="qq-emoji-fallback">[{name}]</span>
  }

  return (
    <span
      className={`qq-emoji-wrapper ${className}`.trim()}
      style={{ width: size, height: size }}
      title={title ?? `[${name}]`}
    >
      <img
        alt={`[${name}]`}
        className="qq-emoji-img"
        height={size}
        loading="lazy"
        onError={handleError}
        src={displaySrc || localUrl}
        style={{ width: size, height: size, objectFit: 'contain' }}
        width={size}
      />
    </span>
  )
})
