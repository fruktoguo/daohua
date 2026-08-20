import { memo } from 'react'

export type CharacterKind =
  | 'player'
  | 'qi-beast'
  | 'twilight-blood-cultivator'
  | 'twilight-corpse'
  | 'twilight-moon-demon'
  | 'twilight-valley-master'
  | 'default-enemy'
  | 'default-ally'

export function resolveCharacterKind(role: 'player' | 'enemy', id?: string | null, name?: string | null): CharacterKind {
  if (role === 'player') return 'player'
  const normalizedId = (id ?? '').toLowerCase()
  const normalizedName = (name ?? '').toLowerCase()

  if (normalizedId.includes('valley-master') || normalizedName.includes('谷主')) {
    return 'twilight-valley-master'
  }
  if (normalizedId.includes('moon-demon') || normalizedName.includes('啸月') || normalizedName.includes('妖修')) {
    return 'twilight-moon-demon'
  }
  if (normalizedId.includes('blood-cultivator') || normalizedName.includes('血影') || normalizedName.includes('邪修')) {
    return 'twilight-blood-cultivator'
  }
  if (normalizedId.includes('corpse') || normalizedName.includes('阴尸') || normalizedName.includes('尸')) {
    return 'twilight-corpse'
  }
  if (normalizedId.includes('qi-beast') || normalizedName.includes('聚气') || normalizedName.includes('灵兽') || normalizedName.includes('兽')) {
    return 'qi-beast'
  }

  return role === 'enemy' ? 'default-enemy' : 'default-ally'
}

interface VectorCharacterProps {
  kind: CharacterKind
  role?: 'player' | 'enemy'
  isDead?: boolean
  className?: string
}

export const VectorCharacter = memo(function VectorCharacter({
  kind,
  role = 'player',
  isDead = false,
  className = '',
}: VectorCharacterProps): React.JSX.Element {
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
      {/* 1. 太虚剑仙 (凌霄绝世剑修 - 清冷绝美、高马尾、飘逸剑袍、浮空冰晶神剑)            */}
      {/* ========================================================================= */}
      {kind === 'player' && (
        <g className="char-art char--player" strokeLinecap="round" strokeLinejoin="round">
          {/* 背后八卦剑阵光环 */}
          <circle cx="60" cy="70" r="42" stroke="#38bdf8" strokeWidth="1" strokeDasharray="6 4" opacity="0.5" />
          <circle cx="60" cy="70" r="38" stroke="#fbbf24" strokeWidth="0.8" opacity="0.3" />

          {/* 浮空凌霄飞剑 */}
          <g transform="translate(86, 28) rotate(18)">
            {/* 剑刃 */}
            <path d="M0,-42 L7,-8 L3,14 L-3,14 L-7,-8 Z" fill="#38bdf8" stroke="#090a0f" strokeWidth="1.8" />
            <path d="M0,-42 L7,-8 L3,14 L0,14 Z" fill="#0284c7" />
            <line x1="0" y1="-38" x2="0" y2="12" stroke="#ffffff" strokeWidth="1" />
            {/* 剑格与剑柄 */}
            <path d="M-10,14 L10,14 L7,19 L-7,19 Z" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.5" />
            <rect x="-2.5" y="19" width="5" height="14" rx="1" fill="#0f172a" stroke="#090a0f" strokeWidth="1.5" />
            <polygon points="0,33 4,37 0,41 -4,37" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.2" />
            {/* 赤红剑穗 */}
            <path d="M0,41 Q-3,50 -2,56 Q1,50 0,41 Z" fill="#ef4444" stroke="#090a0f" strokeWidth="1.2" />
          </g>

          {/* 飘逸后摆长袍 */}
          <path d="M38,112 C28,130 20,148 24,154 C34,154 48,150 52,118 Z" fill="#042f2e" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M68,112 C78,130 96,148 94,154 C82,154 70,150 66,118 Z" fill="#0f172a" stroke="#090a0f" strokeWidth="1.8" />

          {/* 修长劲装腿与云靴 */}
          <path d="M42,116 L38,152 L48,152 L50,116 Z" fill="#1e293b" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M70,116 L74,152 L64,152 L62,116 Z" fill="#1e293b" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M36,148 L49,148 L49,154 L34,154 Z" fill="#090a0f" />
          <path d="M76,148 L63,148 L63,154 L78,154 Z" fill="#090a0f" />

          {/* 青玄墨绿道袍主体 */}
          <path d="M36,66 C34,85 36,110 38,118 L82,118 C84,110 86,85 84,66 Z" fill="#0d9488" stroke="#090a0f" strokeWidth="2" />
          <path d="M44,66 L60,90 L52,118 L38,118 Z" fill="#042f2e" />
          <path d="M54,66 L60,90 L68,118 L82,118 Z" fill="#115e59" />

          {/* 交领白色内衬 */}
          <path d="M50,62 L60,78 L68,64 L62,58 L56,58 Z" fill="#f8fafc" stroke="#090a0f" strokeWidth="1.5" />

          {/* 黄金束腰与垂坠玉佩 */}
          <rect x="40" y="94" width="40" height="9" rx="1.5" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.6" />
          <path d="M58,103 L62,103 L64,126 L56,126 Z" fill="#ef4444" stroke="#090a0f" strokeWidth="1.4" />
          <circle cx="60" cy="112" r="3" fill="#38bdf8" stroke="#090a0f" strokeWidth="1.2" />

          {/* 左臂：御剑剑指 */}
          <path d="M36,68 L22,86 L30,92 L42,78 Z" fill="#0d9488" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M22,86 L14,94 L17,98 L28,91 Z" fill="#ffedd5" stroke="#090a0f" strokeWidth="1.5" />
          <line x1="14" y1="94" x2="8" y2="86" stroke="#090a0f" strokeWidth="2" />
          {/* 指尖剑意雷火 */}
          <circle cx="8" cy="86" r="3.5" fill="#38bdf8" />

          {/* 右臂：广袖垂云 */}
          <path d="M84,68 C96,82 102,96 94,106 C88,104 82,92 78,82 Z" fill="#115e59" stroke="#090a0f" strokeWidth="1.8" />

          {/* 帅气发型与高马尾 */}
          <path d="M60,22 C45,8 30,22 34,42 C38,40 45,34 48,38 Z" fill="#090a0f" />
          <path d="M60,20 C75,10 88,24 84,42 C80,40 75,34 72,38 Z" fill="#090a0f" />

          {/* 俊秀脸庞 */}
          <path d="M46,36 C45,52 52,64 60,64 C68,64 75,52 74,36 Z" fill="#ffedd5" stroke="#090a0f" strokeWidth="1.8" />

          {/* 飘逸刘海 */}
          <path d="M44,32 Q54,48 58,34 Q68,46 76,32 Q60,24 44,32 Z" fill="#0f172a" />

          {/* 剑眉与剑眸 */}
          <line x1="48" y1="44" x2="56" y2="42" stroke="#090a0f" strokeWidth="1.8" />
          <line x1="64" y1="42" x2="72" y2="44" stroke="#090a0f" strokeWidth="1.8" />
          <path d="M50,46 Q54,44 56,47 Z" fill="#0284c7" stroke="#090a0f" strokeWidth="1" />
          <path d="M64,47 Q66,44 70,46 Z" fill="#0284c7" stroke="#090a0f" strokeWidth="1" />

          {/* 黄金发冠与飞扬青丝带 */}
          <path d="M53,16 L67,16 L65,24 L55,24 Z" fill="#f59e0b" stroke="#090a0f" strokeWidth="1.6" />
          <path d="M52,22 Q36,18 38,28" stroke="#38bdf8" strokeWidth="2.2" fill="none" />
        </g>
      )}

      {/* ========================================================================= */}
      {/* 2. 聚气兽 (太古噬灵神尊·威武碧霄龙兽)                                        */}
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
