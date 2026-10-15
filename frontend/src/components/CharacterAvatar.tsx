import { memo, useEffect, useMemo, useState } from 'react'
import { getAvatarCandidateUrls } from '../game/avatarStore'
import type { CharacterVisual } from '../game/types'
import { resolveCharacterKind, VectorCharacter } from './VectorCharacter'

/** CharacterAvatar 立绘组件 Props */
export interface CharacterAvatarProps {
  /** 实体标识符 (如 'player', 'demonized-rabbit', 'enemy.chang-tiger' 等，不关心后缀自动适应) */
  actorId?: string | null
  /** 实体名称 */
  name?: string | null
  /** 参战/角色身份 (player 己方 / enemy 敌方) */
  role?: 'player' | 'enemy'
  /** 视觉定义配置 (来自协议或本地覆写) */
  visual?: CharacterVisual | null
  /** 降级单字徽章 */
  fallback?: string
  /** 是否需要水平左右翻转 (详情、卡片展示敌方时默认为 true) */
  isFlipped?: boolean
  /** 是否阵亡 (置灰与半透明) */
  isDead?: boolean
  /** 外部附加样式类名 */
  className?: string
  /** 行内样式 */
  style?: React.CSSProperties
}

/**
 * 通用角色与妖兽立绘形象组件 (CharacterAvatar)
 *
 * 核心特性：
 * 1. 【后缀无关与自动适应】：支持仅传入 ID，自动在 WebP / PNG / SVG 等格式间无缝逐级回退加载；
 * 2. 【左右方向自适应控制】：支持 `isFlipped` 属性，敌方在详情/卡片中展示时自动水平翻转对齐；
 * 3. 【三级降级链路】：高清透明立绘图片 (WebP/PNG/SVG) -> 纯 SVG 矢量修仙法相 (`VectorCharacter`) -> 单字徽标；
 * 4. 【战斗阵亡置灰】：支持 `isDead` 状态。
 */
export const CharacterAvatar = memo(function CharacterAvatar({
  actorId,
  name,
  role = 'player',
  visual,
  fallback,
  isFlipped = false,
  isDead = false,
  className = '',
  style,
}: CharacterAvatarProps): React.JSX.Element {
  // 构建候选图片 URL 队列
  const candidates = useMemo<string[]>(() => {
    const list: string[] = []
    // 若显式指定了 visual.source 或 poster，优先使用
    if (visual?.source && (visual.renderer === 'image' || !visual.renderer)) {
      list.push(visual.source)
    }
    if (visual?.poster) {
      list.push(visual.poster)
    }

    // 根据 actorId 自动检索 webp / png / svg (player 角色若未指定专属图片，优先降级至 VectorCharacter 多样化法相)
    const resolvedId = actorId && actorId !== 'player' ? actorId : (role === 'enemy' ? 'enemy' : null)
    if (resolvedId) {
      const autoUrls = getAvatarCandidateUrls(resolvedId)
      for (const u of autoUrls) {
        if (!list.includes(u)) {
          list.push(u)
        }
      }
    }

    return list
  }, [actorId, role, visual?.poster, visual?.renderer, visual?.source])

  // 当前尝试加载的候选索引
  const [candidateIndex, setCandidateIndex] = useState(0)

  // 候选列表变化（切换角色/敌人）时重置回退索引，避免从已耗尽的索引直接降级到错误立绘。
  useEffect(() => {
    setCandidateIndex(0)
  }, [candidates])

  const currentSrc = candidates[candidateIndex] ?? null
  const hasExhaustedCandidates = candidateIndex >= candidates.length

  const flipClass = isFlipped ? ' is-flipped' : ''
  const deadClass = isDead ? ' is-dead' : ''
  const combinedClass = `character-avatar character-avatar--${role}${flipClass}${deadClass} ${className}`.trim()

  // 1. 若仍有候选图片可用，尝试渲染 <img>
  if (currentSrc && !hasExhaustedCandidates) {
    return (
      <img
        alt={name ?? ''}
        className={combinedClass}
        draggable={false}
        onError={() => {
          setCandidateIndex(prev => prev + 1)
        }}
        src={currentSrc}
        style={style}
      />
    )
  }

  // 2. 若配置为 text 渲染器且有单字 fallback
  if (visual?.renderer === 'text' && (visual.source || fallback)) {
    return (
      <span className={`character-avatar-text${flipClass}${deadClass} ${className}`} style={style}>
        {visual.source || fallback}
      </span>
    )
  }

  // 3. 兜底降级至原生矢量 SVG (VectorCharacter)
  const kind = resolveCharacterKind(role, actorId ?? (visual?.source || null), name)
  return (
    <div className={`character-avatar-vector-wrap${flipClass}${deadClass} ${className}`} style={style}>
      <VectorCharacter isDead={isDead} kind={kind} role={role} />
    </div>
  )
})
