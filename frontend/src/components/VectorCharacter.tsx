import { memo } from 'react'
import { DEFAULT_PLAYER_AVATAR_SRC } from '../game/avatarStore'

/**
 * 矢量/高清立绘人物/妖兽形象类型
 */
export type CharacterKind =
  | 'player'
  | 'player-female'
  | 'player-male'
  | 'qi-beast'
  | 'twilight-blood-cultivator'
  | 'twilight-corpse'
  | 'twilight-moon-demon'
  | 'twilight-valley-master'
  | 'default-enemy'
  | 'default-ally'

/**
 * 根据角色身份 (role)、实体标识符 (id) 与名称 (name) 智能解析匹配的矢量人物立绘形态
 */
export function resolveCharacterKind(role: 'player' | 'enemy', id?: string | null, name?: string | null): CharacterKind {
  const normalizedId = (id ?? '').toLowerCase()
  const normalizedName = (name ?? '').toLowerCase()

  if (role === 'player') {
    // 优先根据道号特征匹配修仙法相
    if (normalizedName.includes('宗师') || normalizedName.includes('老祖') || normalizedName.includes('天尊') || normalizedName.includes('皇') || normalizedName.includes('主') || normalizedName.includes('尊')) {
      return 'twilight-valley-master'
    }
    if (normalizedName.includes('魔') || normalizedName.includes('煞') || normalizedName.includes('邪') || normalizedName.includes('夜') || normalizedName.includes('刺')) {
      return 'twilight-blood-cultivator'
    }
    if (normalizedName.includes('妖') || normalizedName.includes('狼') || normalizedName.includes('兽') || normalizedName.includes('灵') || normalizedName.includes('狐')) {
      return 'twilight-moon-demon'
    }
    if (normalizedName.includes('仙') || normalizedName.includes('道') || normalizedName.includes('羽') || normalizedName.includes('玄') || normalizedName.includes('君')) {
      return 'default-ally'
    }
    // 哈希轮转丰富形象
    let hash = 0
    for (let i = 0; i < normalizedName.length; i++) {
      hash = ((hash << 5) - hash + normalizedName.charCodeAt(i)) | 0
    }
    const pool: CharacterKind[] = ['player', 'default-ally', 'twilight-valley-master', 'twilight-moon-demon']
    return pool[Math.abs(hash) % pool.length]
  }

  if (normalizedId.includes('valley-master') || normalizedName.includes('谷主') || normalizedId.includes('falling-star-beast') || normalizedName.includes('异兽')) {
    return 'twilight-valley-master'
  }
  if (normalizedId.includes('moon-demon') || normalizedName.includes('啸月') || normalizedId.includes('rift-wolf') || normalizedName.includes('狼') || normalizedName.includes('妖修')) {
    return 'twilight-moon-demon'
  }
  if (normalizedId.includes('blood-cultivator') || normalizedId.includes('plunder-cultivator') || normalizedId.includes('priest') || normalizedName.includes('邪修') || normalizedName.includes('劫修') || normalizedName.includes('祭司')) {
    return 'twilight-blood-cultivator'
  }
  if (normalizedId.includes('corpse') || normalizedName.includes('阴尸') || normalizedName.includes('尸')) {
    return 'twilight-corpse'
  }
  if (normalizedId.includes('rabbit') || normalizedId.includes('ape') || normalizedId.includes('bear') || normalizedId.includes('tiger') || normalizedId.includes('crow') || normalizedId.includes('spirit') || normalizedId.includes('qi-beast') || normalizedName.includes('兔') || normalizedName.includes('猿') || normalizedName.includes('熊') || normalizedName.includes('虎') || normalizedName.includes('精') || normalizedName.includes('兽')) {
    return 'qi-beast'
  }
  return role === 'enemy' ? 'default-enemy' : 'default-ally'
}

/** 矢量立绘组件 Props */
interface VectorCharacterProps {
  /** 形象类型 */
  kind: CharacterKind
  /** 参战身份 (player 己方 / enemy 敌方) */
  role?: 'player' | 'enemy'
  /** 是否已阵亡（死亡变灰暗倒下） */
  isDead?: boolean
  /** 外部样式类名 */
  className?: string
}

/**
 * 原生矢量修仙人物/妖兽立绘组件 (VectorCharacter)
 *
 * 采用纯 SVG 路径精细绘制包含剑阵光环、浮空飞剑、飘逸道袍、兽角鳞甲、血煞魔刃、幽冥法相与九霄魔皇冠冕等国风修仙视觉。
 */
export const VectorCharacter = memo(function VectorCharacter({
  kind,
  role = 'player',
  isDead = false,
  className = '',
}: VectorCharacterProps): React.JSX.Element {
  if (kind === 'player' || kind === 'player-female' || kind === 'player-male') {
    return (
      <img
        alt=""
        className={`vector-character vector-character--${kind} vector-character--${role}${isDead ? ' is-dead' : ''} ${className}`}
        draggable={false}
        src={DEFAULT_PLAYER_AVATAR_SRC}
      />
    )
  }

  return (
    <svg
      aria-hidden="true"
      className={`vector-character vector-character--${kind} vector-character--${role}${isDead ? ' is-dead' : ''} ${className}`}
      fill="none"
      preserveAspectRatio="xMidYMid meet"
      style={{ overflow: 'visible' }}
      viewBox="-10 -10 140 180"
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* 角色底部气场倒影 */}
      <ellipse cx="60" cy="155" rx="36" ry="7" fill="#090b10" opacity="0.45" />

      {/* ========================================================================= */}
      {/* 1. 聚气兽 (太古噬灵神尊·威武碧霄龙兽)                                        */}
      {/* ========================================================================= */}
      {kind === 'qi-beast' && (
        <g className="char-art char--beast" strokeLinecap="round" strokeLinejoin="round">
          {/* 龙尾 */}
          <path d="M84,118 Q120,96 112,126 Q92,136 84,118 Z" fill="#065f46" stroke="#090a0f" strokeWidth="2" />
          <path d="M112,126 Q126,104 122,86 Q108,102 112,126 Z" fill="#34d399" stroke="#090a0f" strokeWidth="1.5" />

          {/* 强健龙兽躯干 */}
          <path d="M34,80 C26,100 28,130 32,132 L88,132 C92,130 94,100 86,80 Z" fill="#047857" stroke="#090a0f" strokeWidth="2" />
          <path d="M40,84 C36,100 38,124 42,128 L78,128 C82,124 84,100 80,84 Z" fill="#065f46" />

          {/* 黄金龙鳞胸铠 */}
          <path d="M44,92 Q60,86 76,92 Q60,126 44,92 Z" fill="#fbbf24" stroke="#090a0f" strokeWidth="1.8" />
          <line x1="60" y1="88" x2="60" y2="124" stroke="#b45309" strokeWidth="1.5" />

          {/* 四足兽爪 */}
          <path d="M26,120 L22,154 L36,154 L38,120 Z" fill="#065f46" stroke="#090a0f" strokeWidth="1.8" />
          <polygon points="18,150 24,156 36,156 32,150" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.4" />
          <path d="M74,120 L76,154 L90,154 L86,120 Z" fill="#047857" stroke="#090a0f" strokeWidth="1.8" />
          <polygon points="72,150 78,156 90,156 86,150" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.4" />

          {/* 威武双龙角 */}
          <path d="M36,36 Q18,10 30,8 Q38,20 42,34 Z" fill="#fbbf24" stroke="#090a0f" strokeWidth="2" />
          <path d="M84,36 Q102,10 90,8 Q82,20 78,34 Z" fill="#d97706" stroke="#090a0f" strokeWidth="2" />

          {/* 霸气龙首与青绿鬃毛 */}
          <path d="M24,44 C18,65 30,86 60,86 C90,86 102,65 96,44 C84,36 36,36 24,44 Z" fill="#10b981" stroke="#090a0f" strokeWidth="2.2" />
          <path d="M32,46 C28,62 38,76 60,78 C82,76 92,62 88,46 Z" fill="#059669" />

          {/* 炽热金黄龙瞳与獠牙 */}
          <circle cx="40" cy="54" r="5" fill="#fbbf24" stroke="#090a0f" strokeWidth="1.2" />
          <circle cx="80" cy="54" r="5" fill="#fbbf24" stroke="#090a0f" strokeWidth="1.2" />
          <circle cx="40" cy="54" r="2.5" fill="#090a0f" />
          <circle cx="80" cy="54" r="2.5" fill="#090a0f" />
          <polygon points="38,72 44,72 41,79" fill="#ffffff" stroke="#090a0f" strokeWidth="1.2" />
          <polygon points="82,72 76,72 79,79" fill="#ffffff" stroke="#090a0f" strokeWidth="1.2" />

          {/* 环绕噬灵灵珠 */}
          <circle cx="16" cy="62" r="5.5" fill="#34d399" stroke="#090a0f" strokeWidth="1.5" />
          <circle cx="104" cy="74" r="6" fill="#6ee7b7" stroke="#090a0f" strokeWidth="1.5" />
        </g>
      )}

      {/* ========================================================================= */}
      {/* 3. 血影邪修 (血煞魔尊·冷酷阴鸷血刃刺客)                                     */}
      {/* ========================================================================= */}
      {kind === 'twilight-blood-cultivator' && (
        <g className="char-art char--cultivator" strokeLinecap="round" strokeLinejoin="round">
          {/* 背后血煞魔刃 */}
          <g transform="translate(88, 36) rotate(24)">
            <path d="M0,-44 Q16,-18 10,16 L-6,16 Q-12,-16 0,-44 Z" fill="#dc2626" stroke="#090a0f" strokeWidth="2" />
            <path d="M0,-44 Q16,-18 10,16 L0,16 Z" fill="#991b1b" />
            <rect x="-7" y="16" width="14" height="6" fill="#18181b" stroke="#090a0f" strokeWidth="1.5" />
            <rect x="-2.5" y="22" width="5" height="14" fill="#27272a" stroke="#090a0f" strokeWidth="1.5" />
            <circle cx="0" cy="38" r="4.5" fill="#991b1b" stroke="#090a0f" strokeWidth="1.5" />
          </g>

          {/* 破损猩红魔斗篷下摆 */}
          <path d="M34,108 C16,132 14,152 24,154 C36,154 44,136 42,108 Z" fill="#450a0a" stroke="#090a0f" strokeWidth="2" />
          <path d="M68,108 C86,132 98,152 90,154 C78,154 66,136 66,108 Z" fill="#7f1d1d" stroke="#090a0f" strokeWidth="2" />

          {/* 紧身战靴 */}
          <path d="M40,118 L36,154 L46,154 L48,118 Z" fill="#18181b" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M68,118 L72,154 L62,154 L60,118 Z" fill="#18181b" stroke="#090a0f" strokeWidth="1.8" />

          {/* 暗黑皮甲胸铠 */}
          <path d="M36,66 C32,84 36,108 40,114 L80,114 C84,108 88,84 84,66 Z" fill="#18181b" stroke="#090a0f" strokeWidth="2" />
          <path d="M44,66 L60,88 L76,66 Z" fill="#7f1d1d" stroke="#090a0f" strokeWidth="1.6" />
          <rect x="38" y="92" width="44" height="8" rx="1" fill="#dc2626" stroke="#090a0f" strokeWidth="1.6" />

          {/* 深渊刺客兜帽 */}
          <path d="M28,36 C24,14 50,4 60,4 C70,4 96,14 92,36 C86,68 76,78 60,78 C44,78 34,68 28,36 Z" fill="#18181b" stroke="#090a0f" strokeWidth="2.2" />
          <path d="M38,36 C34,22 52,14 60,14 C68,14 86,22 82,36 C78,60 70,68 60,68 C50,68 42,60 38,36 Z" fill="#090a0f" />

          {/* 猩红魔眼 */}
          <circle cx="48" cy="46" r="3.5" fill="#ef4444" />
          <circle cx="72" cy="46" r="3.5" fill="#ef4444" />
          <circle cx="48" cy="46" r="1.5" fill="#ffffff" />
          <circle cx="72" cy="46" r="1.5" fill="#ffffff" />

          {/* 鬼面獠牙铁面罩 */}
          <path d="M46,54 L60,50 L74,54 L68,66 L52,66 Z" fill="#27272a" stroke="#090a0f" strokeWidth="1.6" />
        </g>
      )}

      {/* ========================================================================= */}
      {/* 4. 暮影阴尸 (九幽冥铠·战煞远古尸尊)                                         */}
      {/* ========================================================================= */}
      {kind === 'twilight-corpse' && (
        <g className="char-art char--corpse" strokeLinecap="round" strokeLinejoin="round">
          {/* 幽蓝冥火光晕 */}
          <circle cx="60" cy="65" r="42" stroke="#06b6d4" strokeWidth="1.2" strokeDasharray="4 6" opacity="0.4" />

          {/* 战靴 */}
          <path d="M34,118 L28,154 L46,154 L48,118 Z" fill="#334155" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M72,118 L78,154 L60,154 L58,118 Z" fill="#1e293b" stroke="#090a0f" strokeWidth="1.8" />

          {/* 古战将重板甲 */}
          <path d="M30,62 C26,80 30,114 34,120 L86,120 C90,114 94,80 90,62 Z" fill="#1e293b" stroke="#090a0f" strokeWidth="2.2" />
          <path d="M42,66 L60,60 L78,66 L74,112 L46,112 Z" fill="#0f172a" stroke="#090a0f" strokeWidth="1.6" />
          <line x1="50" y1="72" x2="62" y2="92" stroke="#22d3ee" strokeWidth="1.8" />

          {/* 尖刺铁臂甲与幽蓝鬼爪 */}
          <path d="M26,64 L12,86 L18,104 L32,84 Z" fill="#334155" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M94,64 L108,86 L102,104 L88,84 Z" fill="#1e293b" stroke="#090a0f" strokeWidth="1.8" />
          <polygon points="12,100 6,112 18,114" fill="#22d3ee" stroke="#090a0f" strokeWidth="1.4" />
          <polygon points="108,100 114,112 102,114" fill="#22d3ee" stroke="#090a0f" strokeWidth="1.4" />

          {/* 鬼面战盔 */}
          <path d="M28,26 C26,10 50,4 60,4 C70,4 94,10 92,26 C86,58 76,64 60,64 C44,64 34,58 28,26 Z" fill="#334155" stroke="#090a0f" strokeWidth="2.2" />
          <polygon points="26,28 10,14 26,16" fill="#1e293b" stroke="#090a0f" strokeWidth="1.8" />
          <polygon points="94,28 110,14 94,16" fill="#0f172a" stroke="#090a0f" strokeWidth="1.8" />

          {/* 视孔幽蓝冥火 */}
          <ellipse cx="48" cy="42" rx="5" ry="3" fill="#22d3ee" />
          <ellipse cx="72" cy="42" rx="5" ry="3" fill="#22d3ee" />
        </g>
      )}

      {/* ========================================================================= */}
      {/* 5. 啸月妖修 (天狼妖尊·狂野双持月刃妖修)                                     */}
      {/* ========================================================================= */}
      {kind === 'twilight-moon-demon' && (
        <g className="char-art char--wolf" strokeLinecap="round" strokeLinejoin="round">
          {/* 双持月牙光刃 */}
          <g transform="translate(18, 92) rotate(-35)">
            <path d="M-4,-36 Q16,0 -4,36 Q4,0 -4,-36 Z" fill="#38bdf8" stroke="#090a0f" strokeWidth="1.8" />
            <polygon points="-4,-4 4,-4 0,16" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.4" />
          </g>
          <g transform="translate(102, 92) rotate(35)">
            <path d="M4,-36 Q-16,0 4,36 Q-4,0 4,-36 Z" fill="#38bdf8" stroke="#090a0f" strokeWidth="1.8" />
            <polygon points="-4,-4 4,-4 0,16" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.4" />
          </g>

          {/* 下肢与战靴 */}
          <path d="M42,120 L36,154 L48,154 L50,120 Z" fill="#2e1065" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M68,120 L74,154 L62,154 L60,120 Z" fill="#2e1065" stroke="#090a0f" strokeWidth="1.8" />

          {/* 紫黑战甲与毛皮披肩 */}
          <path d="M34,68 C30,86 36,112 40,118 L80,118 C84,112 90,86 86,68 Z" fill="#581c87" stroke="#090a0f" strokeWidth="2" />
          <path d="M28,64 Q60,78 92,64 Q78,56 60,58 Q42,56 28,64 Z" fill="#1e1b4b" stroke="#090a0f" strokeWidth="1.8" />

          {/* 战狼尖耳 */}
          <polygon points="30,30 18,6 40,22" fill="#581c87" stroke="#090a0f" strokeWidth="1.8" />
          <polygon points="90,30 102,6 80,22" fill="#581c87" stroke="#090a0f" strokeWidth="1.8" />
          <polygon points="26,14 32,24 24,26" fill="#f43f5e" />
          <polygon points="94,14 88,24 96,26" fill="#f43f5e" />

          {/* 狂野白发 */}
          <path d="M34,26 Q60,12 86,26 Q78,60 60,68 Q42,60 34,26 Z" fill="#f8fafc" stroke="#090a0f" strokeWidth="2" />

          {/* 俊美面庞与金瞳 */}
          <path d="M46,38 C45,54 52,64 60,64 C68,64 75,54 74,38 Z" fill="#ffedd5" stroke="#090a0f" strokeWidth="1.8" />
          <circle cx="53" cy="46" r="3" fill="#f59e0b" stroke="#090a0f" strokeWidth="1" />
          <circle cx="67" cy="46" r="3" fill="#f59e0b" stroke="#090a0f" strokeWidth="1" />
        </g>
      )}

      {/* ========================================================================= */}
      {/* 6. 暮影谷主 (通天魔君·九霄万劫魔皇 Boss)                                    */}
      {/* ========================================================================= */}
      {kind === 'twilight-valley-master' && (
        <g className="char-art char--boss" strokeLinecap="round" strokeLinejoin="round">
          {/* 背后 6 面环绕诛仙魔符 */}
          {[0, 60, 120, 180, 240, 300].map((deg, i) => (
            <g key={i} transform={`translate(60, 68) rotate(${deg}) translate(0, -48)`}>
              <polygon points="-7,-12 7,-12 9,12 -9,12" fill="#fbbf24" stroke="#090a0f" strokeWidth="1.5" />
              <line x1="0" y1="-8" x2="0" y2="8" stroke="#dc2626" strokeWidth="1.2" />
            </g>
          ))}

          {/* 帝袍下摆 */}
          <path d="M30,114 C10,140 10,154 20,154 L100,154 C110,154 110,140 90,114 Z" fill="#3b0764" stroke="#090a0f" strokeWidth="2.2" />
          <path d="M44,114 L50,154 L70,154 L76,114 Z" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.8" />

          {/* 紫金魔皇锦袍 */}
          <path d="M26,62 C22,80 28,114 32,118 L88,118 C92,114 98,80 94,62 Z" fill="#581c87" stroke="#090a0f" strokeWidth="2.2" />
          <path d="M38,62 L60,84 L82,62 L76,56 L44,56 Z" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.8" />

          {/* 龙首肩甲 */}
          <polygon points="18,60 30,46 34,70" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.8" />
          <polygon points="102,60 90,46 86,70" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.8" />

          {/* 掌托紫电神核 */}
          <g transform="translate(18, 76)">
            <circle cx="0" cy="0" r="7.5" fill="#c084fc" stroke="#090a0f" strokeWidth="1.6" />
            <polygon points="0,-10 3,-3 10,0 3,3 0,10 -3,3 -10,0 -3,-3" fill="#f8fafc" />
          </g>

          {/* 魔皇面容与白须 */}
          <path d="M44,34 C43,50 50,62 60,62 C70,62 77,50 76,34 Z" fill="#ffedd5" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M50,56 Q60,80 70,56 Z" fill="#f8fafc" stroke="#090a0f" strokeWidth="1.5" />
          <circle cx="53" cy="44" r="3" fill="#a855f7" stroke="#090a0f" strokeWidth="1" />
          <circle cx="67" cy="44" r="3" fill="#a855f7" stroke="#090a0f" strokeWidth="1" />

          {/* 九霄魔皇金冠 */}
          <path d="M44,22 L60,4 L76,22 L72,34 L48,34 Z" fill="#f59e0b" stroke="#090a0f" strokeWidth="2" />
          <circle cx="60" cy="22" r="3.5" fill="#a855f7" />
        </g>
      )}

      {/* ========================================================================= */}
      {/* 默认通用备用立绘                                                           */}
      {/* ========================================================================= */}
      {(kind === 'default-enemy' || kind === 'default-ally') && (
        <g className="char-art char--default" strokeLinecap="round" strokeLinejoin="round">
          <path d="M40,116 L34,152 L48,152 L50,116 Z" fill="#1e293b" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M70,116 L76,152 L62,152 L60,116 Z" fill="#1e293b" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M36,66 C32,84 36,112 40,118 L80,118 C84,112 88,84 84,66 Z" fill={role === 'enemy' ? '#831843' : '#0f766e'} stroke="#090a0f" strokeWidth="2" />
          <path d="M46,36 C45,52 52,62 60,62 C68,62 75,52 74,36 Z" fill="#ffedd5" stroke="#090a0f" strokeWidth="1.8" />
          <circle cx="53" cy="46" r="2.5" fill="#0284c7" />
          <circle cx="67" cy="46" r="2.5" fill="#0284c7" />
        </g>
      )}
    </svg>
  )
})
