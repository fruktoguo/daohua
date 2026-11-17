import { memo, useState } from 'react'
import { getQqEmojiUrls, QQ_EMOJI_MAP } from '../game/qqEmojis.ts'
import { useChatImageGeneration, useChatImageSource } from './useChatImageLease'

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
export const QqEmoji = memo(function QqEmoji(props: QqEmojiProps): React.JSX.Element {
  const generation = useChatImageGeneration()
  const meta = QQ_EMOJI_MAP.get(props.name)
  if (!meta) return <span className="qq-emoji-fallback">[{props.name}]</span>

  return <QqEmojiImage key={`${generation}:${meta.qsid}`} {...props} generation={generation} qsid={meta.qsid} />
})

function QqEmojiImage({
  name,
  size = 20,
  className = '',
  title,
  generation,
  qsid,
}: QqEmojiProps & { generation: number; qsid: string }): React.JSX.Element {
  const { cfUrl, localUrl } = getQqEmojiUrls(qsid)
  const [currentSource, setCurrentSource] = useState<'cf' | 'local' | 'failed'>('cf')
  const imageUrl = currentSource === 'cf' ? cfUrl : currentSource === 'local' ? localUrl : null
  const displaySrc = useChatImageSource(imageUrl, generation)

  if (currentSource === 'failed') return <span className="qq-emoji-fallback">[{name}]</span>

  return (
    <span
      className={`qq-emoji-wrapper ${className}`.trim()}
      style={{ width: size, height: size }}
      title={title ?? `[${name}]`}
    >
      {displaySrc && <img
        alt={`[${name}]`}
        className="qq-emoji-img"
        height={size}
        loading="lazy"
        onError={() => setCurrentSource(currentSource === 'cf' ? 'local' : 'failed')}
        src={displaySrc}
        style={{ width: size, height: size, objectFit: 'contain' }}
        width={size}
      />}
    </span>
  )
}
