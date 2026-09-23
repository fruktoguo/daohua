/* =========================================================================
 * 真实功法目录镜像 —— 字段逐一摘自 config/game-data/abilities/*.json，
 * 演武沙盘的一切吟唱时长、灵力消耗、目标类型、效果类别均以此为准。
 * discipline: internal=内功位(0号槽) / spell=术法位(1~3号槽)
 * ========================================================================= */
(function () {
  'use strict';

  /** @type {Array<AbilityDef>} 与 config/game-data/abilities/*.json 逐条对应 */
  const ABILITIES = [
    // ---- physical_abilities.json（物理武技） ----
    {
      id: 'ability.heavy-slash', name: '重斩', discipline: 'spell', category: 'physical',
      element: 'none', tier: 1, castMilliseconds: 1000, cooldownMilliseconds: 18000,
      manaCost: 10, damageRatio: 1.8, flatDamage: 20, targetType: 'single',
      description: '蓄势挥出沉重一剑', icon: 'ability.heavy-slash',
    },
    {
      id: 'ability.chain-strike', name: '连环', discipline: 'spell', category: 'physical',
      element: 'none', tier: 1, castMilliseconds: 1000, cooldownMilliseconds: 16000,
      manaCost: 10, damageRatio: 1.0, flatDamage: 15, targetType: 'single',
      description: '剑势连绵，连环进击', icon: 'ability.chain-strike',
    },
    {
      id: 'ability.taunt', name: '嘲讽', discipline: 'internal', category: 'physical',
      element: 'none', tier: 1, castMilliseconds: 200, cooldownMilliseconds: 55000,
      manaCost: 20, threatGain: 500, threatRatio: 2.0, targetType: 'self',
      description: '运功怒喝，激将敌手', icon: 'ability.taunt',
    },
    {
      id: 'ability.heavy-clouds', name: '重云', discipline: 'spell', category: 'physical',
      element: 'none', tier: 2, castMilliseconds: 2000, cooldownMilliseconds: 24000,
      manaCost: 40, damageRatio: 1.2, flatDamage: 40, targetType: 'allEnemies',
      description: '掌势重云压顶，席卷敌阵', icon: 'ability.heavy-clouds',
    },
    {
      id: 'ability.star-piercer', name: '贯星', discipline: 'spell', category: 'physical',
      element: 'none', tier: 2, castMilliseconds: 2000, cooldownMilliseconds: 36000,
      manaCost: 30, damageRatio: 3.0, flatDamage: 50, targetType: 'single',
      description: '剑气如流星贯空', icon: 'ability.star-piercer',
    },

    // ---- spell_abilities.json（五行/符箓术法） ----
    {
      id: 'ability.palm-lightning', name: '掌心雷', discipline: 'spell', category: 'spell',
      element: 'wood', tier: 1, castMilliseconds: 2000, cooldownMilliseconds: 0,
      manaCost: 4, damageRatio: 0.6, flatDamage: 10, targetType: 'single',
      description: '从掌心发出一道雷光', icon: 'ability.palm-lightning',
    },
    {
      id: 'ability.blazing-blast', name: '轰炎', discipline: 'spell', category: 'spell',
      element: 'fire', tier: 1, castMilliseconds: 3000, cooldownMilliseconds: 24000,
      manaCost: 10, damageRatio: 1.0, flatDamage: 20, targetType: 'allEnemies',
      description: '轰出一片灼热烈焰', icon: 'ability.blazing-blast',
    },
    {
      id: 'ability.qingping-melody', name: '清平乐', discipline: 'internal', category: 'spell',
      element: 'wood', tier: 2, castMilliseconds: 500, cooldownMilliseconds: 30000,
      manaCost: 10, effectType: 'heal', healingRatio: 0.6, flatHealing: 20,
      targetType: 'lowestHitpointsAlly',
      description: '奏响清平雅乐', icon: 'ability.qingping-melody',
    },
    {
      id: 'ability.lightning-attraction', name: '雷光引', discipline: 'spell', category: 'spell',
      element: 'wood', tier: 2, castMilliseconds: 2000, cooldownMilliseconds: 28000,
      manaCost: 20, damageRatio: 0.5, flatDamage: 10, targetType: 'allEnemies',
      description: '引动九天雷光轰击敌阵', icon: 'ability.lightning-attraction',
    },
    {
      id: 'ability.pozhen-melody', name: '破阵子', discipline: 'internal', category: 'spell',
      element: 'none', tier: 2, castMilliseconds: 5000, cooldownMilliseconds: 60000,
      manaCost: 50, targetType: 'allAllies',
      buff: { name: '破阵子', durationSeconds: 30, damageAbsorptionRatio: 0.15 },
      description: '奏响破阵战歌', icon: 'ability.pozhen-melody',
    },
  ];

  /** 演武装配方案：内功位=破阵子，术法位=掌心雷/重斩/轰炎（与真实 4 槽一致） */
  const LOADOUT_IDS = [
    'ability.pozhen-melody',
    'ability.palm-lightning',
    'ability.heavy-slash',
    'ability.blazing-blast',
  ];

  /** 敌方单位的真实配表技能（镜像 config/game-data/monsters/*.json skills） */
  const ENEMY_SKILLS = {
    enemy1: [],                                  // 魔化野兔：仅普攻（tier1 minion 无 skills）
    enemy2: ['ability.heavy-slash'],             // 魔猿：重斩
    enemy3: ['ability.palm-lightning'],          // 星屑火鸦：掌心雷
  };

  function byId(id) {
    return ABILITIES.find(a => a.id === id) || null;
  }

  window.DaohuaCatalog = { ABILITIES, ENEMY_SKILLS, LOADOUT_IDS, byId };
})();
