/* =========================================================================
 * 演武功法目录：config/game-data/abilities/*.json 的纯展示镜像。
 * 只提供名称/元数据/演示参数；数值权威源与正式战斗结算不在本文件。
 * discipline=internal 的十门为常驻内功，无吟唱/耗蓝/战斗施放语义。
 * ========================================================================= */
(function () {
  'use strict';

  function active(id, name, description, category, element, tier, castMilliseconds, cooldownMilliseconds, manaCost, targetType, extra) {
    return Object.assign({ id, abilityId: id, name, description, icon: id, discipline: 'spell', category, element, tier, castMilliseconds, cooldownMilliseconds, manaCost, targetType }, extra || {});
  }

  function internal(id, name, description, category, tier, extra) {
    return Object.assign({ id, abilityId: id, name, description, icon: id, discipline: 'internal', category, element: 'none', tier, passivePreview: true }, extra || {});
  }

  const ABILITIES = [
    active('ability.heavy-slash', '重斩', '重剑破空，一斩开山。', 'physical', 'metal', 1, 1000, 18000, 10, 'single', { damageRatio: 1.8, flatDamage: 20, style: 'sword' }),
    active('ability.chain-strike', '连环', '剑势连绵，环环相扣。', 'physical', 'fire', 1, 1000, 16000, 10, 'single', { damageRatio: 0.8, flatDamage: 10, hitCount: 3, style: 'sword' }),
    active('ability.taunt', '嘲讽', '怒喝惊四野，引敌向吾身。', 'physical', 'none', 1, 200, 55000, 20, 'self', { damageRatio: 0, threatGain: 500, buff: { name: '嘲讽', durationSeconds: 60 }, style: 'body' }),
    active('ability.heavy-clouds', '重云', '重云压城阙，掌落万敌崩。', 'physical', 'water', 2, 2000, 24000, 40, 'allEnemies', { damageRatio: 1.2, flatDamage: 40, style: 'sword' }),
    active('ability.star-piercer', '贯星', '一剑贯星斗，余势仍未休。', 'physical', 'metal', 2, 2000, 36000, 30, 'single', { damageRatio: 3, flatDamage: 50, chainOnKillCount: 2, style: 'sword' }),
    active('ability.sui-gang', '碎罡', '崩碎护体罡气，化坚甲为无边煞力。', 'physical', 'none', 1, 3000, 60000, 0, 'self', { damageRatio: 0, buffs: [{ name: '碎罡', durationSeconds: 20 }, { name: '煞力', durationSeconds: 20 }, { name: '罡锋', durationSeconds: 20 }], style: 'blade' }),
    active('ability.palm-lightning', '掌心雷', '袖中掌起一声雷。', 'spell', 'wood', 1, 2000, 0, 4, 'single', { damageRatio: 0.7, flatDamage: 10, style: 'fiveElements' }),
    active('ability.blazing-blast', '轰炎', '轰炎一怒焚千里。', 'spell', 'fire', 1, 3000, 24000, 10, 'allEnemies', { damageRatio: 1, flatDamage: 20, style: 'fiveElements' }),
    active('ability.qingping-melody', '清平乐', '清平一曲起，枯木又逢春。', 'spell', 'wood', 2, 500, 30000, 10, 'lowestHitpointsAlly', { damageRatio: 0, effectType: 'heal', healingRatio: 0.6, flatHealing: 20, style: 'fiveElements' }),
    active('ability.lightning-attraction', '雷光引', '九霄雷落，万钧天威。', 'spell', 'wood', 2, 500, 28000, 20, 'single', { damageRatio: 1.5, flatDamage: 30, style: 'fiveElements' }),
    active('ability.pozhen-melody', '破阵子', '一曲破阵壮军威。', 'spell', 'none', 2, 5000, 60000, 50, 'allAllies', { damageRatio: 0, buff: { name: '破阵子', durationSeconds: 10 }, style: 'talisman' }),
    active('ability.ganlu', '甘露', '天降甘露，润物无声。', 'spell', 'water', 1, 1500, 20000, 20, 'lowestHitpointsAlly', { damageRatio: 0, buff: { name: '甘露', durationSeconds: 10 }, style: 'fiveElements' }),
    active('ability.tide', '潮汐', '巨浪滔天，席卷敌阵。', 'spell', 'water', 1, 3000, 0, 10, 'allEnemies', { damageRatio: 0.5, flatDamage: 10, style: 'fiveElements' }),

    internal('ability.tuna-gong', '吐纳功', '一呼一吸，气行周身。', 'spell', 1),
    internal('ability.manniu-jin', '蛮牛劲', '力蛮如牛，不尚机巧。', 'physical', 1),
    internal('ability.tiebu-shan', '铁布衫', '筋骨如铁，刀枪不入。', 'physical', 1),
    internal('ability.feiyan-gong', '飞燕功', '身轻如燕，踏雪无痕。', 'physical', 1),
    internal('ability.qingxin-jue', '清心诀', '清心寡欲，灵台空明。', 'spell', 1),
    internal('ability.fenxue-gong', '焚血功', '燃血为薪，力可崩山。', 'physical', 2),
    internal('ability.xiaozhoutian-gong', '小周天功', '气行任督，周天不息。', 'spell', 2),
    internal('ability.cunshen-jue', '存神诀', '存神内视，万虑皆空。', 'spell', 2),
    internal('ability.lianjing-huaqi-jue', '炼精化气诀', '炼精化气，舍形求真。', 'spell', 2),
    internal('ability.xuanyuan-yiqi-gong', '玄元一气功', '玄元一气，六合归真。', 'spell', 3),
  ];

  const LOADOUT_IDS = ['ability.pozhen-melody', 'ability.palm-lightning', 'ability.heavy-slash', 'ability.blazing-blast'];
  const ENEMY_SKILLS = {
    enemy1: [],
    enemy2: ['ability.heavy-slash', 'ability.chain-strike', 'ability.sui-gang'],
    enemy3: ['ability.palm-lightning', 'ability.lightning-attraction'],
  };
  const byId = id => ABILITIES.find(ability => ability.id === id) || null;
  const activeAbilities = () => ABILITIES.filter(ability => ability.discipline !== 'internal');
  const internalAbilities = () => ABILITIES.filter(ability => ability.discipline === 'internal');
  window.DaohuaCatalog = { ABILITIES, ENEMY_SKILLS, LOADOUT_IDS, byId, activeAbilities, internalAbilities };
})();
