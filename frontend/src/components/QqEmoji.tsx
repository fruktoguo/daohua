import React, { memo } from 'react'
import { QQ_EMOJI_MAP } from '../game/qqEmojis.ts'

export interface QqEmojiProps {
  /** 经典表情名称，如 "微笑" 或 "呲牙" */
  name: string
  /** 尺寸（像素），聊天混排默认 20px，面板展示默认 26px */
  size?: number
  className?: string
  title?: string
}

/** 标准黄色渐变脸盘背景 */
const YellowFaceBase = ({ blush = false }: { blush?: boolean }): React.JSX.Element => (
  <>
    <defs>
      <radialGradient id="qq-face-grad" cx="35%" cy="30%" r="70%">
        <stop offset="0%" stopColor="#FFE066" />
        <stop offset="70%" stopColor="#FFB300" />
        <stop offset="100%" stopColor="#F57C00" />
      </radialGradient>
      <radialGradient id="qq-blush-grad" cx="50%" cy="50%" r="50%">
        <stop offset="0%" stopColor="#FF4081" stopOpacity="0.45" />
        <stop offset="100%" stopColor="#FF4081" stopOpacity="0" />
      </radialGradient>
    </defs>
    {/* 主脸盘 */}
    <circle cx="18" cy="18" fill="url(#qq-face-grad)" r="16" stroke="#D97706" strokeWidth="0.8" />
    {/* 脸部微光高光 */}
    <ellipse cx="13" cy="9" fill="#FFF9C4" opacity="0.45" rx="6" ry="3" />
    {/* 两颊红晕 */}
    {blush && (
      <>
        <circle cx="7" cy="21" fill="url(#qq-blush-grad)" r="3.5" />
        <circle cx="29" cy="21" fill="url(#qq-blush-grad)" r="3.5" />
      </>
    )}
  </>
)

/** 针对每个表情 ID 的具体矢量渲染器 */
const EMOJI_RENDERERS: Record<string, () => React.JSX.Element> = {
  smile: () => (
    <>
      <YellowFaceBase blush />
      <path d="M9 13 Q12 9 15 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M21 13 Q24 9 27 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M11 20 Q18 28 25 20" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
    </>
  ),
  grin: () => (
    <>
      <YellowFaceBase />
      <path d="M9 12 Q12.5 8 16 12" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M20 12 Q23.5 8 27 12" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M9 18 Q18 19 27 18 Q25 29 18 29 Q11 29 9 18 Z" fill="#FFFFFF" stroke="#451A03" strokeWidth="1.8" />
      <path d="M10 23 L26 23" stroke="#451A03" strokeWidth="1.2" />
      <line opacity="0.6" stroke="#451A03" strokeWidth="1" x1="14" x2="14" y1="18.5" y2="28" />
      <line opacity="0.6" stroke="#451A03" strokeWidth="1" x1="18" x2="18" y1="19" y2="28.8" />
      <line opacity="0.6" stroke="#451A03" strokeWidth="1" x1="22" x2="22" y1="18.5" y2="28" />
    </>
  ),
  smirk: () => (
    <>
      <YellowFaceBase blush />
      <path d="M9 14 Q12.5 10 16 14" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M20 14 Q23.5 10 27 14" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M13 22 Q18 26 23 22" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <ellipse cx="18" cy="26" fill="#FFE082" rx="5" ry="3" stroke="#B45309" strokeWidth="1" />
    </>
  ),
  dazed: () => (
    <>
      <YellowFaceBase />
      <circle cx="12" cy="14" fill="#FFFFFF" r="3.2" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="12" cy="14" fill="#451A03" r="1.5" />
      <circle cx="24" cy="14" fill="#FFFFFF" r="3.2" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="24" cy="14" fill="#451A03" r="1.5" />
      <ellipse cx="18" cy="24" fill="#451A03" rx="2.2" ry="3" />
    </>
  ),
  proud: () => (
    <>
      <YellowFaceBase />
      <path d="M8 10 Q12 8 16 12" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="1.8" />
      <path d="M20 12 Q24 8 28 10" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="1.8" />
      <path d="M9 15 Q12 12 15 15" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M21 15 Q24 12 27 15" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M11 23 Q18 27 26 20" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
    </>
  ),
  tear: () => (
    <>
      <YellowFaceBase />
      <path d="M9 12 Q12 14 15 12" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M21 12 Q24 14 27 12" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="12" cy="16" fill="#451A03" r="2.2" />
      <circle cx="24" cy="16" fill="#451A03" r="2.2" />
      <path d="M11 18 C11 18 8 23 8 25 A3 3 0 0 0 14 25 C14 23 11 18 11 18 Z" fill="#00B0FF" stroke="#0288D1" strokeWidth="0.8" />
      <path d="M13 26 Q18 21 23 26" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
    </>
  ),
  shy: () => (
    <>
      <YellowFaceBase blush />
      <path d="M10 14 Q13 11 16 14" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M20 14 Q23 11 26 14" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M14 22 Q18 26 22 22" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
    </>
  ),
  silent: () => (
    <>
      <YellowFaceBase />
      <circle cx="12" cy="14" fill="#451A03" r="2.2" />
      <circle cx="24" cy="14" fill="#451A03" r="2.2" />
      <line stroke="#451A03" strokeLinecap="round" strokeWidth="2.4" x1="12" x2="24" y1="24" y2="24" />
    </>
  ),
  sob: () => (
    <>
      <YellowFaceBase />
      <path d="M9 13 L15 16 L9 19" fill="none" stroke="#451A03" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.2" />
      <path d="M27 13 L21 16 L27 19" fill="none" stroke="#451A03" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.2" />
      <rect fill="#29B6F6" height="15" opacity="0.85" rx="2.2" width="4.5" x="7" y="16" />
      <rect fill="#29B6F6" height="15" opacity="0.85" rx="2.2" width="4.5" x="24.5" y="16" />
      <ellipse cx="18" cy="25" fill="#881337" rx="5" ry="4.5" stroke="#451A03" strokeWidth="1.5" />
    </>
  ),
  awkward: () => (
    <>
      <YellowFaceBase />
      <path d="M9 11 Q12 13 15 11" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="1.8" />
      <path d="M21 11 Q24 13 27 11" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="1.8" />
      <circle cx="12" cy="15" fill="#451A03" r="2" />
      <circle cx="24" cy="15" fill="#451A03" r="2" />
      <path d="M12 24 Q18 20 24 24" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M7 6 C7 6 4 10 4 12 A2.5 2.5 0 0 0 9 12 C9 10 7 6 7 6 Z" fill="#00E5FF" stroke="#0091EA" strokeWidth="0.8" />
    </>
  ),
  angry: () => (
    <>
      <defs>
        <radialGradient cx="40%" cy="40%" id="qq-angry-grad" r="65%">
          <stop offset="0%" stopColor="#FFA726" />
          <stop offset="60%" stopColor="#EF5350" />
          <stop offset="100%" stopColor="#C62828" />
        </radialGradient>
      </defs>
      <circle cx="18" cy="18" fill="url(#qq-angry-grad)" r="16" stroke="#B71C1C" strokeWidth="0.8" />
      <path d="M8 11 L15 15" stroke="#260C0C" strokeLinecap="round" strokeWidth="2.5" />
      <path d="M28 11 L21 15" stroke="#260C0C" strokeLinecap="round" strokeWidth="2.5" />
      <circle cx="12" cy="17" fill="#FFFFFF" r="2" />
      <circle cx="12" cy="17" fill="#000000" r="1.2" />
      <circle cx="24" cy="17" fill="#FFFFFF" r="2" />
      <circle cx="24" cy="17" fill="#000000" r="1.2" />
      <rect fill="#FFFFFF" height="6" rx="2" stroke="#451A03" strokeWidth="1.5" width="14" x="11" y="22" />
      <line stroke="#451A03" strokeWidth="1" x1="18" x2="18" y1="22" y2="28" />
    </>
  ),
  tongue: () => (
    <>
      <YellowFaceBase blush />
      <path d="M8 15 Q12 11 15 15" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <circle cx="23" cy="14" fill="#FFFFFF" r="3" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="23" cy="14" fill="#451A03" r="1.6" />
      <path d="M12 21 Q18 22 24 21 Q21 28 18 28 Q15 28 12 21 Z" fill="#7F1D1D" stroke="#451A03" strokeWidth="1.5" />
      <path d="M16 23 C16 27 20 27 20 23 Z" fill="#F43F5E" />
    </>
  ),
  cool: () => (
    <>
      <YellowFaceBase />
      <path d="M6 14 Q12 11 17 14 L17 18 Q12 21 6 18 Z" fill="#1F2937" stroke="#111827" strokeWidth="1" />
      <path d="M19 14 Q24 11 30 14 L30 18 Q24 21 19 18 Z" fill="#1F2937" stroke="#111827" strokeWidth="1" />
      <line stroke="#111827" strokeWidth="1.5" x1="17" x2="19" y1="14" y2="14" />
      <line stroke="#9CA3AF" strokeWidth="1.2" x1="8" x2="11" y1="14" y2="17" />
      <line stroke="#9CA3AF" strokeWidth="1.2" x1="21" x2="24" y1="14" y2="17" />
      <path d="M12 24 Q18 28 24 23" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
    </>
  ),
  sweat: () => (
    <>
      <YellowFaceBase />
      <path d="M9 13 Q12 15 15 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M21 13 Q24 15 27 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="12" cy="16" fill="#451A03" r="1.8" />
      <circle cx="24" cy="16" fill="#451A03" r="1.8" />
      <path d="M12 24 Q18 20 24 24" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M26 6 C26 6 23 10 23 12 A2.5 2.5 0 0 0 28 12 C28 10 26 6 26 6 Z" fill="#38BDF8" stroke="#0284C7" strokeWidth="0.8" />
    </>
  ),
  crazy: () => (
    <>
      <YellowFaceBase />
      <path d="M8 12 L15 14" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M28 12 L21 14" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <circle cx="12" cy="15" fill="#FFFFFF" r="3" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="12" cy="15" fill="#451A03" r="1.2" />
      <circle cx="24" cy="15" fill="#FFFFFF" r="3" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="24" cy="15" fill="#451A03" r="1.2" />
      <ellipse cx="18" cy="24" fill="#881337" rx="6" ry="5" stroke="#451A03" strokeWidth="1.5" />
    </>
  ),
  'roll-eyes': () => (
    <>
      <YellowFaceBase />
      <circle cx="12" cy="15" fill="#FFFFFF" r="3.6" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="12" cy="12.5" fill="#451A03" r="1.6" />
      <circle cx="24" cy="15" fill="#FFFFFF" r="3.6" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="24" cy="12.5" fill="#451A03" r="1.6" />
      <path d="M12 25 Q18 21 24 25" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
    </>
  ),
  giggle: () => (
    <>
      <YellowFaceBase blush />
      <path d="M8 14 Q12 9 16 14" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M20 14 Q24 9 28 14" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M11 20 Q18 20 25 20 Q23 27 18 27 Q13 27 11 20 Z" fill="#FFFFFF" stroke="#451A03" strokeWidth="1.5" />
    </>
  ),
  question: () => (
    <>
      <YellowFaceBase />
      <path d="M8 10 Q12 8 16 11" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M20 13 Q24 15 28 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="12" cy="15" fill="#FFFFFF" r="3" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="12" cy="15" fill="#451A03" r="1.5" />
      <circle cx="24" cy="16" fill="#451A03" r="2.2" />
      <path d="M13 24 Q18 22 23 25" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <text fill="#EA580C" fontFamily="sans-serif" fontSize="11" fontWeight="bold" x="24" y="10">?</text>
    </>
  ),
  hammer: () => (
    <>
      <YellowFaceBase />
      <path d="M8 13 L14 17" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M14 13 L8 17" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M22 13 L28 17" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M28 13 L22 17" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M13 24 Q18 20 23 24" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <rect fill="#B45309" height="12" rx="1.5" stroke="#78350F" strokeWidth="0.8" transform="rotate(-35 6 8)" width="7" x="2" y="2" />
    </>
  ),
  wave: () => (
    <>
      <YellowFaceBase blush />
      <path d="M8 13 Q11 10 14 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M19 13 Q22 10 25 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M11 21 Q16 26 22 21" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <g transform="translate(24, 8) rotate(-15)">
        <path d="M2 8 C0 4 3 2 5 4 C6 2 8 2 9 4 C10 3 12 4 12 6 C12 10 8 13 4 11 Z" fill="#FBBF24" stroke="#D97706" strokeWidth="0.8" />
      </g>
    </>
  ),
  wipe: () => (
    <>
      <YellowFaceBase />
      <path d="M9 13 Q12 11 15 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M21 13 Q24 11 27 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="12" cy="15.5" fill="#451A03" r="1.8" />
      <circle cx="24" cy="15.5" fill="#451A03" r="1.8" />
      <path d="M13 23 Q18 26 23 23" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <rect fill="#E2E8F0" height="6" rx="2" stroke="#94A3B8" strokeWidth="0.8" transform="rotate(20 24 8)" width="8" x="20" y="5" />
    </>
  ),
  evil: () => (
    <>
      <YellowFaceBase blush />
      <path d="M8 10 Q12 7 15 11" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M21 12 Q24 15 28 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="12" cy="14" fill="#FFFFFF" r="3" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="14" cy="14" fill="#451A03" r="1.5" />
      <circle cx="24" cy="14" fill="#FFFFFF" r="3" stroke="#451A03" strokeWidth="1.2" />
      <circle cx="26" cy="14" fill="#451A03" r="1.5" />
      <path d="M10 22 Q17 28 26 19" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.4" />
    </>
  ),
  plead: () => (
    <>
      <YellowFaceBase blush />
      <path d="M8 11 Q12 13 15 11" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M21 11 Q24 13 28 11" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="12" cy="15" fill="#1E293B" r="4.5" stroke="#451A03" strokeWidth="0.8" />
      <circle cx="10.5" cy="13.5" fill="#FFFFFF" r="1.8" />
      <circle cx="13.5" cy="16.5" fill="#FFFFFF" r="0.9" />
      <circle cx="24" cy="15" fill="#1E293B" r="4.5" stroke="#451A03" strokeWidth="0.8" />
      <circle cx="22.5" cy="13.5" fill="#FFFFFF" r="1.8" />
      <circle cx="25.5" cy="16.5" fill="#FFFFFF" r="0.9" />
      <path d="M14 25 Q18 22 22 25" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
    </>
  ),
  funny: () => (
    <>
      <YellowFaceBase blush />
      <path d="M8 10 Q12 7 16 9" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M20 9 Q24 7 28 10" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.2" />
      <path d="M9 13 Q13 11 16 14" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="15" cy="13" fill="#451A03" r="1.2" />
      <path d="M20 14 Q23 11 27 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="26" cy="12.5" fill="#451A03" r="1.2" />
      <path d="M10 20 Q18 29 26 20" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2.4" />
    </>
  ),
  salute: () => (
    <>
      <YellowFaceBase />
      <path d="M9 13 Q12 10 15 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M21 13 Q24 10 27 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <circle cx="12" cy="15" fill="#451A03" r="1.8" />
      <circle cx="24" cy="15" fill="#451A03" r="1.8" />
      <path d="M13 22 Q18 25 23 22" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <g transform="translate(11, 23)">
        <circle cx="4" cy="5" fill="#F59E0B" r="3.2" stroke="#B45309" strokeWidth="0.8" />
        <rect fill="#FBBF24" height="6" rx="2" stroke="#B45309" strokeWidth="0.8" width="7" x="6" y="2" />
      </g>
    </>
  ),
  'thumbs-up': () => (
    <>
      <YellowFaceBase blush />
      <path d="M8 13 Q11 10 14 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M18 13 Q21 10 24 13" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <path d="M10 21 Q16 26 22 21" fill="none" stroke="#451A03" strokeLinecap="round" strokeWidth="2" />
      <g transform="translate(23, 14)">
        <path d="M2 6 L2 12 A2 2 0 0 0 6 12 L6 6 Z" fill="#F59E0B" stroke="#B45309" strokeWidth="0.8" />
        <path d="M5 6 L5 2 C5 0 8 0 8 3 L8 6 L10 6 A2 2 0 0 1 12 8 L10 12 L6 12" fill="#FBBF24" stroke="#B45309" strokeWidth="0.8" />
      </g>
    </>
  ),
  heart: () => (
    <>
      <defs>
        <linearGradient id="qq-heart-grad" x1="0%" x2="100%" y1="0%" y2="100%">
          <stop offset="0%" stopColor="#FF4081" />
          <stop offset="100%" stopColor="#E11D48" />
        </linearGradient>
      </defs>
      <path
        d="M18 31 C18 31 4 22 4 12 A8 8 0 0 1 18 8 A8 8 0 0 1 32 12 C32 22 18 31 18 31 Z"
        fill="url(#qq-heart-grad)"
        stroke="#9F1239"
        strokeWidth="1"
      />
      <ellipse cx="11" cy="11" fill="#FFF" opacity="0.35" rx="4" ry="2" transform="rotate(-30 11 11)" />
    </>
  ),
  'broken-heart': () => (
    <>
      <path
        d="M18 31 C18 31 4 22 4 12 A8 8 0 0 1 18 8 A8 8 0 0 1 32 12 C32 22 18 31 18 31 Z"
        fill="#E11D48"
        stroke="#9F1239"
        strokeWidth="1"
      />
      <path d="M18 8 L15 15 L21 19 L16 26 L18 31" fill="none" stroke="#FFFFFF" strokeLinejoin="round" strokeWidth="2" />
    </>
  ),
}

export const QqEmoji = memo(function QqEmoji({
  name,
  size = 20,
  className = '',
  title,
}: QqEmojiProps): React.JSX.Element {
  const meta = QQ_EMOJI_MAP.get(name)

  if (!meta) {
    return <span className="qq-emoji-fallback">[{name}]</span>
  }

  const renderContent = EMOJI_RENDERERS[meta.id]
  if (!renderContent) {
    return <span className="qq-emoji-fallback">[{name}]</span>
  }

  return (
    <span
      className={`qq-emoji-wrapper ${className}`.trim()}
      style={{ width: size, height: size }}
      title={title ?? `[${name}]`}
    >
      <svg
        aria-label={`[${name}]`}
        className="qq-emoji-svg"
        height={size}
        role="img"
        viewBox="0 0 36 36"
        width={size}
      >
        {renderContent()}
      </svg>
    </span>
  )
})
