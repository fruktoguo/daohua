/**
 * 道化太初 · 演武画廊战斗状态机与响应式调度沙盒
 *
 * 职责：
 * 1. 维护 3v3 演武单位（血量、内力、Buff/阵法、移形状态、燃魂层数）。
 * 2. 管理技能蓄力读条、常驻读条法阵句柄与快慢读条倍速切换。
 * 3. 管理 11 个战斗消耗品（丹药持续回流、雷击符直伤、阵盘常驻光阵句柄及天雷/血煞被动触发）。
 * 4. 驱动「自动演武」多技能与消耗品组合轮播。
 */
(function (global) {
  'use strict';

  var ALLY_TEMPLATES = [
    {
      id: 'ally-0',
      side: 'ally',
      name: '凌虚子',
      role: '剑修·主将',
      avatar: 'player.webp',
      maxHp: 1200,
      maxMp: 600,
      physAtk: 145,
      spellAtk: 125,
    },
    {
      id: 'ally-1',
      side: 'ally',
      name: '青蘅',
      role: '灵医·辅助',
      avatar: 'avatar-wood-female.webp',
      maxHp: 940,
      maxMp: 540,
      physAtk: 60,
      spellAtk: 130,
    },
    {
      id: 'ally-2',
      side: 'ally',
      name: '玄岳',
      role: '体修·前卫',
      avatar: 'avatar-body-male.webp',
      maxHp: 1650,
      maxMp: 380,
      physAtk: 115,
      spellAtk: 55,
    },
  ];

  var ENEMY_TEMPLATES = [
    {
      id: 'enemy-0',
      side: 'enemy',
      name: '妖化暴兔',
      role: '前排精怪',
      avatar: 'demonized-rabbit.webp',
      maxHp: 2200,
      maxMp: 220,
      physAtk: 85,
      spellAtk: 60,
    },
    {
      id: 'enemy-1',
      side: 'enemy',
      name: '魔化巨猿',
      role: '狂暴重装',
      avatar: 'demon-ape.webp',
      maxHp: 3200,
      maxMp: 260,
      physAtk: 115,
      spellAtk: 70,
    },
    {
      id: 'enemy-2',
      side: 'enemy',
      name: '星炎魔鸦',
      role: '后排妖禽',
      avatar: 'star-flame-crow.webp',
      maxHp: 2400,
      maxMp: 360,
      physAtk: 75,
      spellAtk: 115,
    },
  ];

  var sandbox = {
    units: [],
    selectedCasterId: 'ally-0',
    selectedTargetId: 'enemy-1',
    fastCast: true,
    autoDemo: false,
    casting: null,
    activeFormations: {
      personal: null,
      team: null,
    },
    logs: [],
    listeners: new Set(),
    unitElResolver: null,
    tickTimer: 0,
    demoTimer: 0,
    demoStep: 0,
  };

  function cloneUnit(tpl) {
    return {
      id: tpl.id,
      side: tpl.side,
      name: tpl.name,
      role: tpl.role,
      avatar: tpl.avatar,
      hp: tpl.maxHp,
      maxHp: tpl.maxHp,
      mp: tpl.maxMp,
      maxMp: tpl.maxMp,
      physAtk: tpl.physAtk,
      spellAtk: tpl.spellAtk,
      buffs: Object.create(null),
    };
  }

  function emitChange(reason) {
    sandbox.listeners.forEach(function (fn) {
      try {
        fn(reason, sandbox);
      } catch (err) {
        console.error('[Sandbox listener error]', err);
      }
    });
  }

  function addLog(text, kind) {
    var now = new Date();
    var ts =
      String(now.getHours()).padStart(2, '0') +
      ':' +
      String(now.getMinutes()).padStart(2, '0') +
      ':' +
      String(now.getSeconds()).padStart(2, '0');
    sandbox.logs.unshift({ ts: ts, text: text, kind: kind || 'normal' });
    if (sandbox.logs.length > 80) sandbox.logs.length = 80;
    emitChange('log');
  }

  function getUnit(id) {
    for (var i = 0; i < sandbox.units.length; i++) {
      if (sandbox.units[i].id === id) return sandbox.units[i];
    }
    return null;
  }

  function getLivingUnits(side) {
    return sandbox.units.filter(function (u) {
      return u.side === side && u.hp > 0;
    });
  }

  function getEl(unitId) {
    if (typeof sandbox.unitElResolver === 'function') {
      return sandbox.unitElResolver(unitId);
    }
    return document.querySelector('.arena-unit[data-unit-id="' + unitId + '"]');
  }

  function getUnitEls(units) {
    return units
      .map(function (u) {
        return getEl(u.id);
      })
      .filter(Boolean);
  }

  function buildCtx(caster, targets, extra) {
    var allyUnits = getLivingUnits('ally');
    var enemyUnits = getLivingUnits('enemy');
    var targetList = Array.isArray(targets) ? targets : targets ? [targets] : [];
    var primary = targetList[0] || caster;

    var ctx = {
      caster: caster,
      casterEl: caster ? getEl(caster.id) : null,
      target: primary,
      targetEl: primary ? getEl(primary.id) : null,
      targets: targetList,
      targetEls: getUnitEls(targetList),
      allyEls: getUnitEls(allyUnits),
      enemyEls: getUnitEls(enemyUnits),
    };
    if (extra) {
      Object.keys(extra).forEach(function (k) {
        ctx[k] = extra[k];
      });
    }
    return ctx;
  }

  function ensureTeamAlive(side) {
    var living = getLivingUnits(side);
    if (living.length > 0) return;
    var sideUnits = sandbox.units.filter(function (u) {
      return u.side === side;
    });
    sideUnits.forEach(function (u) {
      u.hp = u.maxHp;
      u.mp = u.maxMp;
      u.buffs = Object.create(null);
      var el = getEl(u.id);
      if (el && global.DaohuaVFX) {
        global.DaohuaVFX.play('feedback.revive', { targetEl: el });
      }
    });
    addLog((side === 'enemy' ? '敌方阵营' : '我方道友') + '气血重聚，悉数复苏再战！', 'heal');
    emitChange('revive');
  }

  function cancelActiveCast() {
    if (!sandbox.casting) return;
    clearInterval(sandbox.casting.progressTimer);
    clearTimeout(sandbox.casting.doneTimer);
    if (sandbox.casting.ringHandle && typeof sandbox.casting.ringHandle.stop === 'function') {
      sandbox.casting.ringHandle.stop(160);
    }
    sandbox.casting = null;
    emitChange('cast');
  }

  function clearFormations() {
    if (sandbox.activeFormations.personal && sandbox.activeFormations.personal.handle) {
      sandbox.activeFormations.personal.handle.stop(250);
    }
    if (sandbox.activeFormations.team && sandbox.activeFormations.team.handle) {
      sandbox.activeFormations.team.handle.stop(250);
    }
    sandbox.activeFormations.personal = null;
    sandbox.activeFormations.team = null;
  }

  function resetArena() {
    cancelActiveCast();
    clearFormations();
    if (global.DaohuaVFX) {
      global.DaohuaVFX.clear();
    }
    sandbox.units = ALLY_TEMPLATES.map(cloneUnit).concat(ENEMY_TEMPLATES.map(cloneUnit));
    sandbox.selectedCasterId = 'ally-0';
    sandbox.selectedTargetId = 'enemy-1';
    sandbox.logs = [];
    addLog('演武场重置完成，凌虚子就位候命。', 'normal');
    emitChange('reset');
  }

  function applyDamage(attacker, target, rawDmg, opts) {
    if (!target || target.hp <= 0) return { damage: 0, killed: false, crit: false };
    opts = opts || {};
    var crit = opts.forceCrit !== undefined ? opts.forceCrit : Math.random() < 0.28;
    var dmg = Math.max(12, Math.round(rawDmg * (crit ? 1.55 : 1) * (0.94 + Math.random() * 0.12)));
    if (attacker && attacker.buffs['buff.pozhen']) {
      dmg += 40;
    }
    if (attacker && attacker.buffs['pill.heqi']) {
      dmg = Math.round(dmg * 1.15);
    }
    target.hp = Math.max(0, target.hp - dmg);
    var killed = target.hp <= 0;

    if (killed && global.DaohuaVFX) {
      global.DaohuaVFX.after(220, function () {
        var tel = getEl(target.id);
        if (tel) global.DaohuaVFX.play('feedback.defeat', { targetEl: tel });
        addLog(target.name + ' 力竭败退！', 'crit');
        emitChange('units');
        // 3.6 秒后自动补位重生，防止演武场缺兵
        global.DaohuaVFX.after(3400, function () {
          if (target.hp <= 0) {
            target.hp = target.maxHp;
            target.buffs = Object.create(null);
            var revEl = getEl(target.id);
            if (revEl) global.DaohuaVFX.play('feedback.revive', { targetEl: revEl });
            emitChange('units');
          }
        });
      });
    }

    return { damage: dmg, killed: killed, crit: crit };
  }

  function applyHeal(target, amount) {
    if (!target || target.hp <= 0) return 0;
    var gain = Math.min(target.maxHp - target.hp, Math.round(amount));
    target.hp += Math.max(0, gain);
    return Math.max(Math.round(amount), gain);
  }

  function grantBuff(unit, buffId, name, durationMs, extra) {
    if (!unit || unit.hp <= 0) return;
    unit.buffs[buffId] = {
      id: buffId,
      name: name,
      expiresAt: Date.now() + durationMs,
      durationMs: durationMs,
      stacks: (extra && extra.stacks) || (unit.buffs[buffId] ? unit.buffs[buffId].stacks || 1 : 1),
      hpPerSec: (extra && extra.hpPerSec) || 0,
      mpPerSec: (extra && extra.mpPerSec) || 0,
      kind: (extra && extra.kind) || 'buff',
    };
  }

  function triggerFormationProc(which, targetUnit, sourceDamage) {
    ensureTeamAlive('enemy');
    var caster = getUnit(sandbox.selectedCasterId) || getUnit('ally-0');
    var enemy =
      targetUnit && targetUnit.hp > 0
        ? targetUnit
        : getUnit(sandbox.selectedTargetId) || getLivingUnits('enemy')[0];
    if (!caster || !enemy) return;

    var VFX = global.DaohuaVFX;
    if (!VFX) return;

    if (which === 'tianlei') {
      if (!sandbox.activeFormations.personal || sandbox.activeFormations.personal.itemId !== 'item.tianlei-formation-disk') {
        useConsumable('item.tianlei-formation-disk');
      }
      var res = applyDamage(caster, enemy, Math.round(caster.spellAtk * 1.15 + 45), { forceCrit: false });
      var handleT = sandbox.activeFormations.personal && sandbox.activeFormations.personal.handle;
      if (handleT && typeof handleT.pulse === 'function') handleT.pulse('#7dd3fc', 1.65);

      VFX.play(
        'item.tianlei-formation-disk.proc',
        buildCtx(caster, [enemy], {
          formationHandle: handleT,
          damage: res.damage,
          element: 'wood',
        })
      );
      addLog('天雷阵共鸣反击【' + enemy.name + '】，造成 ' + res.damage + ' 点真雷伤害！', 'consumable');
    } else if (which === 'xuesha') {
      if (!sandbox.activeFormations.personal || sandbox.activeFormations.personal.itemId !== 'item.xuesha-formation-disk') {
        useConsumable('item.xuesha-formation-disk');
      }
      var dmg = sourceDamage || Math.round(caster.physAtk * 0.95 + 35);
      var resX = applyDamage(caster, enemy, dmg * 0.65, { forceCrit: false });
      var steal = Math.max(40, resX.damage);
      applyHeal(caster, steal);

      var handleX = sandbox.activeFormations.personal && sandbox.activeFormations.personal.handle;
      if (handleX && typeof handleX.pulse === 'function') handleX.pulse('#ef4444', 1.75);

      VFX.play(
        'item.xuesha-formation-disk.proc',
        buildCtx(caster, [enemy], {
          formationHandle: handleX,
          damage: resX.damage,
          lifesteal: steal,
        })
      );
      addLog('血煞阵汲血反哺：对【' + enemy.name + '】造成 ' + resX.damage + ' 血煞伤，并吸取 ' + steal + ' 气血！', 'consumable');
    }
    emitChange('units');
  }

  function resolveTargets(caster, skill, forceTayun) {
    var enemies = getLivingUnits('enemy');
    var allies = getLivingUnits('ally');
    var effectiveType = forceTayun ? 'allEnemies' : skill.targetType;

    if (effectiveType === 'self') return [caster];
    if (effectiveType === 'allAllies') return allies;
    if (effectiveType === 'allEnemies') return enemies;
    if (effectiveType === 'lowestHitpointsAlly') {
      var sorted = allies.slice().sort(function (a, b) {
        return a.hp / a.maxHp - b.hp / b.maxHp;
      });
      return [sorted[0] || caster];
    }
    var chosen = getUnit(sandbox.selectedTargetId);
    if (chosen && chosen.side === 'enemy' && chosen.hp > 0) return [chosen];
    return [enemies[0]].filter(Boolean);
  }

  function executeSkillPayload(caster, skill, opts) {
    opts = opts || {};
    var VFX = global.DaohuaVFX;
    if (!VFX) return;

    var hasYixing = !!caster.buffs['buff.yixing'];
    var useTayun = !!(opts.forceTayun || (skill.id === 'ability.zhanyu' && hasYixing));
    var effectId = useTayun ? 'ability.zhanyu.tayun' : skill.id;
    var targets = resolveTargets(caster, skill, useTayun);

    // 消耗内力（演武模式若内力不足则自动补满灵泉方便展示）
    if (skill.mpCost) {
      if (caster.mp < skill.mpCost) caster.mp = caster.maxMp;
      caster.mp = Math.max(0, caster.mp - skill.mpCost);
    }

    var totalDmg = 0;
    var primaryTarget = targets[0] || null;
    var anyKilled = false;

    if (skill.effectType === 'heal') {
      var healAmt = Math.round(caster.spellAtk * (skill.healRatio || 0.6) + (skill.flatHeal || 20));
      targets.forEach(function (t) {
        applyHeal(t, healAmt);
      });
      VFX.play(
        effectId,
        buildCtx(caster, targets, {
          skill: skill,
          element: skill.element,
          heal: healAmt,
          tier: skill.tier,
        })
      );
      addLog(caster.name + ' 施展【' + skill.name + '】，为 ' + (primaryTarget ? primaryTarget.name : '友方') + ' 回复 ' + healAmt + ' 生命。', 'heal');
    } else if (skill.effectType === 'buff') {
      if (skill.id === 'ability.yueqian') {
        targets.forEach(function (t) {
          grantBuff(t, 'buff.yixing', '移形', 5000, { kind: 'yixing' });
        });
      } else if (skill.id === 'ability.pozhen-melody') {
        targets.forEach(function (t) {
          grantBuff(t, 'buff.pozhen', '破阵子', 10000);
        });
      } else if (skill.id === 'ability.ganlu') {
        targets.forEach(function (t) {
          grantBuff(t, 'buff.ganlu', '甘露', 10000, { hpPerSec: 18 });
        });
      } else if (skill.id === 'ability.shangui-yao') {
        targets.forEach(function (t) {
          grantBuff(t, 'buff.ganlu', '甘露', 15000, { hpPerSec: 22 });
        });
      } else if (skill.id === 'ability.sui-gang') {
        grantBuff(caster, 'buff.sui-gang', '碎罡', 20000);
        grantBuff(caster, 'buff.sha-li', '煞力', 20000);
        grantBuff(caster, 'buff.gang-feng', '罡锋', 20000);
      } else if (skill.buff) {
        targets.forEach(function (t) {
          grantBuff(t, skill.buff.buffId, skill.buff.name, (skill.buff.durationSeconds || 15) * 1000);
        });
      }
      VFX.play(
        effectId,
        buildCtx(caster, targets, {
          skill: skill,
          element: skill.element,
          tier: skill.tier,
        })
      );
      addLog(
        caster.name +
          ' 施展【' +
          skill.name +
          '】，加持 ' +
          (skill.id === 'ability.sui-gang'
            ? '碎罡/煞力/罡锋 三重真意'
            : skill.id === 'ability.shangui-yao'
              ? '甘露（全队）'
              : skill.buff
                ? skill.buff.name
                : '状态') +
          '！',
        'normal'
      );
    } else {
      // 伤害或隐火挂印技能（如果是斩玉变招踏云或贯星演示击杀连星）
      var baseAtk = skill.kind === 'spell' ? caster.spellAtk : caster.physAtk;
      var dmgRatio = skill.dmgRatio !== undefined ? skill.dmgRatio : 1.0;
      var flatDmg = skill.flat !== undefined ? skill.flat : 20;
      var raw = baseAtk * dmgRatio + flatDmg;
      if (useTayun) raw *= 1.35;

      var hitTargets = skill.maxTargets && targets.length > skill.maxTargets ? targets.slice(0, skill.maxTargets) : targets;
      var critAny = false;
      hitTargets.forEach(function (t, idx) {
        if (raw > 0) {
          if (opts.forceKill && idx === 0) t.hp = Math.min(t.hp, 60);
          var res = applyDamage(caster, t, raw, { forceCrit: opts.forceCrit });
          totalDmg += res.damage;
          if (res.crit) critAny = true;
          if (res.killed) anyKilled = true;
        }
        if (skill.id === 'ability.tianlei-po') {
          grantBuff(t, 'buff.mabi', '麻痹', 5000, { kind: 'mabi' });
        }
        if (skill.ranhunBaseStacks) {
          var prevStacks = t.buffs['buff.ranhun'] ? t.buffs['buff.ranhun'].stacks : 0;
          grantBuff(t, 'buff.ranhun', '燃魂×' + Math.min(20, prevStacks + skill.ranhunBaseStacks), 10000, {
            stacks: Math.min(20, prevStacks + skill.ranhunBaseStacks),
            kind: 'ranhun',
          });
        }
      });

      // 如果贯星击杀且还有活敌，追加一次连环斩杀展示
      if (skill.id === 'ability.star-piercer' && anyKilled) {
        var leftover = getLivingUnits('enemy')[0];
        if (leftover) {
          applyDamage(caster, leftover, raw * 0.72);
        }
      }

      // 移形状态流转：斩玉消耗移形；瞬闪无移形时得移形、有移形时耗之（击杀重置冷却）；破军赋予移形+势不可挡
      if (skill.id === 'ability.zhanyu' && hasYixing && !opts.preserveYixing) {
        delete caster.buffs['buff.yixing'];
      } else if (skill.id === 'ability.shunshan') {
        if (!hasYixing) {
          grantBuff(caster, 'buff.yixing', '移形', 5000, { kind: 'yixing' });
        } else if (!opts.preserveYixing) {
          delete caster.buffs['buff.yixing'];
        }
      } else if (skill.id === 'ability.pojun') {
        grantBuff(caster, 'buff.yixing', '移形', 5000, { kind: 'yixing' });
        grantBuff(caster, 'buff.shibukedang', '势不可挡×5', 5000, { stacks: 5 });
      }

      VFX.play(
        effectId,
        buildCtx(caster, hitTargets, {
          skill: skill,
          element: skill.element,
          damage: totalDmg,
          perTargetDamage: Math.round(totalDmg / Math.max(1, hitTargets.length)),
          crit: critAny,
          morphed: useTayun,
          kill: anyKilled || !!opts.forceKill,
          tier: skill.tier,
        })
      );

      var shownName = useTayun ? '踏云（移形·斩玉）' : skill.name;
      if (raw <= 0) {
        addLog(
          caster.name +
            ' 施展【' +
            shownName +
            '】，隐火咒印透入 ' +
            hitTargets.length +
            ' 名敌人心脉并叠加【燃魂】。',
          'normal'
        );
      } else {
        addLog(
          caster.name +
            ' 施展【' +
            shownName +
            '】，命中 ' +
            hitTargets.length +
            ' 名敌人造成共 ' +
            totalDmg +
            ' 伤害' +
            (critAny ? '（暴击！）' : '。') +
            (skill.id === 'ability.shunshan' && hasYixing && anyKilled ? ' 触发击杀，瞬闪冷却重置！' : ''),
          critAny ? 'crit' : 'normal'
        );
      }

      // 联动阵盘被动触发：天雷阵或血煞阵
      var personal = sandbox.activeFormations.personal;
      if (personal && primaryTarget && primaryTarget.hp > 0) {
        if (personal.itemId === 'item.tianlei-formation-disk') {
          VFX.after(280, function () {
            triggerFormationProc('tianlei', primaryTarget, totalDmg);
          });
        } else if (personal.itemId === 'item.xuesha-formation-disk') {
          VFX.after(300, function () {
            triggerFormationProc('xuesha', primaryTarget, totalDmg);
          });
        }
      }
    }

    emitChange('units');
  }

  function castAbility(abilityId, opts) {
    opts = opts || {};
    var isForcedTayun = abilityId === 'ability.zhanyu.tayun';
    var realId = isForcedTayun ? 'ability.zhanyu' : abilityId;
    var skill = global.DaohuaCatalog.byId(realId);
    if (!skill || !skill.active) return;

    ensureTeamAlive('ally');
    if (skill.effectType === 'damage') ensureTeamAlive('enemy');

    var caster = getUnit(sandbox.selectedCasterId) || getLivingUnits('ally')[0];
    if (!caster || caster.hp <= 0) caster = getLivingUnits('ally')[0];
    if (!caster) return;

    cancelActiveCast();

    var rawCast = skill.castTimeMs || 500;
    var durMs = opts.instant ? 0 : Math.max(110, Math.round(rawCast * (sandbox.fastCast ? 0.18 : 1)));
    var VFX = global.DaohuaVFX;
    var casterEl = getEl(caster.id);

    if (durMs <= 0 || !VFX) {
      executeSkillPayload(caster, skill, Object.assign({ forceTayun: isForcedTayun }, opts));
      return;
    }

    var ringHandle = VFX.play('feedback.castRing', {
      casterEl: casterEl,
      element: skill.element,
      skill: skill,
      durationMs: durMs,
    });

    var startAt = performance.now();
    var displayName = isForcedTayun || (skill.id === 'ability.zhanyu' && caster.buffs['buff.yixing']) ? '踏云' : skill.name;
    sandbox.casting = {
      name: displayName,
      element: skill.element,
      casterId: caster.id,
      progress: 0,
      ringHandle: ringHandle,
      progressTimer: setInterval(function () {
        if (!sandbox.casting) return;
        sandbox.casting.progress = Math.min(1, (performance.now() - startAt) / durMs);
        emitChange('cast');
      }, 30),
      doneTimer: setTimeout(function () {
        if (sandbox.casting) {
          clearInterval(sandbox.casting.progressTimer);
          if (sandbox.casting.ringHandle && typeof sandbox.casting.ringHandle.stop === 'function') {
            sandbox.casting.ringHandle.stop(180);
          }
          sandbox.casting = null;
          emitChange('cast');
        }
        executeSkillPayload(caster, skill, Object.assign({ forceTayun: isForcedTayun }, opts));
      }, durMs),
    };
    emitChange('cast');
  }

  function castInternalArt(abilityId) {
    var skill = global.DaohuaCatalog.byId(abilityId);
    if (!skill || skill.kind !== 'internal') return;
    ensureTeamAlive('ally');
    var caster = getUnit(sandbox.selectedCasterId) || getLivingUnits('ally')[0];
    var VFX = global.DaohuaVFX;
    if (!caster || !VFX || !VFX.has(skill.id)) return;
    VFX.play(
      skill.id,
      buildCtx(caster, [caster], {
        skill: skill,
        element: skill.element,
        tier: skill.tier,
      })
    );
    addLog(caster.name + ' 运转心法【' + skill.name + '】，周天真意护体显形！', 'heal');
    emitChange('units');
  }

  function useConsumable(itemId, opts) {
    opts = opts || {};
    var item = global.DaohuaCatalog.byId(itemId);
    if (!item) return;

    ensureTeamAlive('ally');
    if (item.targetType === 'singleEnemy') ensureTeamAlive('enemy');

    var caster = getUnit(sandbox.selectedCasterId) || getLivingUnits('ally')[0];
    var VFX = global.DaohuaVFX;
    if (!caster || !VFX) return;

    if (item.consumableType === 'pill') {
      // 丹药疗效 + Buff 持续回流
      var isHp =
        itemId === 'item.zhixue-powder' ||
        itemId === 'item.shengji-balm' ||
        itemId === 'item.huangya-pill';
      var burstHeal = itemId === 'item.huangya-pill' ? 150 : itemId === 'item.shengji-balm' ? 72 : itemId === 'item.zhixue-powder' ? 36 : 0;
      var burstMp = itemId === 'item.heqi-pill' ? 100 : itemId === 'item.ningqi-powder' ? 48 : itemId === 'item.shunqi-pill' ? 24 : 0;

      if (burstHeal > 0) applyHeal(caster, burstHeal);
      if (burstMp > 0) caster.mp = Math.min(caster.maxMp, caster.mp + burstMp);

      var buffKey = itemId === 'item.heqi-pill' ? 'pill.heqi' : 'pill.' + item.id.split('.')[1];
      grantBuff(caster, buffKey, item.name, 30000, {
        hpPerSec: isHp ? Math.max(4, Math.round(burstHeal / 10)) : 0,
        mpPerSec: !isHp ? Math.max(3, Math.round(burstMp / 10)) : 0,
        kind: 'pill',
      });

      VFX.play(
        item.id,
        buildCtx(caster, [caster], {
          consumable: item,
          heal: burstHeal,
          mana: burstMp,
          tier: item.tier,
        })
      );
      addLog(caster.name + ' 服下消耗品【' + item.name + '】：' + item.effectSummary, 'consumable');
    } else if (item.consumableType === 'talisman') {
      var target = getUnit(sandbox.selectedTargetId) || getLivingUnits('enemy')[0];
      if (!target) return;
      var manaCost = Math.round(caster.maxMp * (item.manaCostRatio || 0.05));
      caster.mp = Math.max(0, caster.mp - manaCost);
      var total = Math.round(caster.spellAtk * (item.spellAttackRatio || 2.0) + caster.mp * (item.manaDamageRatio || 0.2));
      var res = applyDamage(caster, target, total, { forceCrit: opts.forceCrit });

      VFX.play(
        item.id,
        buildCtx(caster, [target], {
          consumable: item,
          damage: res.damage,
          crit: res.crit,
          element: 'wood',
          tier: item.tier,
        })
      );
      addLog(caster.name + ' 祭出【雷击符】直击 ' + target.name + '，轰出 ' + res.damage + ' 雷法+倾泻伤！', 'consumable');
    } else if (item.consumableType === 'formation') {
      var isTeam = !!item.teamWide;
      var slotKey = isTeam ? 'team' : 'personal';
      if (sandbox.activeFormations[slotKey]) {
        if (sandbox.activeFormations[slotKey].handle) {
          sandbox.activeFormations[slotKey].handle.stop(320);
        }
        var oldBuffId = sandbox.activeFormations[slotKey].buffId;
        sandbox.units.forEach(function (u) {
          delete u.buffs[oldBuffId];
        });
      }

      var targetsF = isTeam ? getLivingUnits('ally') : [caster];
      targetsF.forEach(function (u) {
        grantBuff(u, item.formationBuffId, item.formationName, 300000, {
          hpPerSec: item.id === 'item.juling-formation-disk' ? 12 : 0,
          mpPerSec: item.id === 'item.juling-formation-disk' ? 9 : 0,
          kind: 'formation',
        });
      });

      var handle = VFX.play(
        item.id,
        buildCtx(caster, targetsF, {
          consumable: item,
          tier: item.tier,
        })
      );

      sandbox.activeFormations[slotKey] = {
        buffId: item.formationBuffId,
        itemId: item.id,
        name: item.formationName,
        casterId: caster.id,
        handle: handle && typeof handle.stop === 'function' ? handle : null,
        expiresAt: Date.now() + 300000,
      };
      addLog(caster.name + ' 展开【' + item.formationName + '】(' + (isTeam ? '全队常驻' : '个人常驻') + ')！', 'consumable');
    }

    emitChange('units');
  }

  function triggerEnemyAttack() {
    ensureTeamAlive('enemy');
    ensureTeamAlive('ally');
    var attackers = getLivingUnits('enemy');
    var attacker = attackers[(Math.random() * attackers.length) | 0];
    var allies = getLivingUnits('ally');
    var tauntTarget = allies.find(function (u) {
      return !!u.buffs['buff.taunt'];
    });
    var victim = tauntTarget || allies[0];
    if (!attacker || !victim) return;

    var eskill = global.DaohuaCatalog.ENEMY_SKILLS[(Math.random() * global.DaohuaCatalog.ENEMY_SKILLS.length) | 0];
    var res = applyDamage(attacker, victim, attacker.physAtk * eskill.ratio);

    var VFX = global.DaohuaVFX;
    if (VFX) {
      VFX.play('feedback.attack', {
        side: 'enemy',
        attackerKey: attacker.id,
        casterEl: getEl(attacker.id),
        targetEl: getEl(victim.id),
        element: eskill.element,
        damage: res.damage,
        crit: res.crit,
        hit: true,
      });
      VFX.play('feedback.impact', {
        targetEl: getEl(victim.id),
        amount: res.damage,
        crit: res.crit,
        element: eskill.element,
        text: '-' + res.damage,
      });

      // 受击触发天雷阵反击
      var personal = sandbox.activeFormations.personal;
      if (personal && personal.itemId === 'item.tianlei-formation-disk' && victim.id === personal.casterId) {
        VFX.after(240, function () {
          triggerFormationProc('tianlei', attacker);
        });
      }
    }
    addLog(attacker.name + ' 施展【' + eskill.name + '】命中 ' + victim.name + ' (-' + res.damage + ')。', 'normal');
    emitChange('units');
  }

  function runShowcase(showcaseId) {
    var VFX = global.DaohuaVFX;
    if (!VFX) return;
    ensureTeamAlive('ally');
    ensureTeamAlive('enemy');
    var caster = getUnit(sandbox.selectedCasterId) || getLivingUnits('ally')[0];
    var target = getUnit(sandbox.selectedTargetId) || getLivingUnits('enemy')[0];

    if (showcaseId === 'showcase.tayun') {
      grantBuff(caster, 'buff.yixing', '移形', 5000, { kind: 'yixing' });
      addLog('演示连携：【移形】触发，斩玉切换为群体【踏云】！', 'consumable');
      castAbility('ability.zhanyu.tayun', { preserveYixing: true });
    } else if (showcaseId === 'showcase.star-kill') {
      addLog('演示机制：【贯星】击杀低血敌兵后连环贯刺余敌！', 'crit');
      castAbility('ability.star-piercer', { forceKill: true, forceCrit: true });
    } else if (showcaseId === 'showcase.crit') {
      if (!target) return;
      var res = applyDamage(caster, target, 420, { forceCrit: true });
      VFX.play('feedback.crit', {
        targetEl: getEl(target.id),
        amount: res.damage,
        element: 'metal',
      });
      addLog('演示通用暴击爆裂：命中 ' + target.name + ' 暴击 ' + res.damage + '！', 'crit');
      emitChange('units');
    } else if (showcaseId === 'showcase.defeat-revive') {
      if (!target) return;
      var tel = getEl(target.id);
      VFX.play('feedback.defeat', { targetEl: tel });
      addLog('演示败退与重聚特效：' + target.name + ' 败退 -> 1.1s后重生。', 'normal');
      VFX.after(1100, function () {
        VFX.play('feedback.revive', { targetEl: tel });
      });
    } else if (showcaseId === 'showcase.tianlei-proc') {
      triggerFormationProc('tianlei');
    } else if (showcaseId === 'showcase.xuesha-proc') {
      triggerFormationProc('xuesha');
    } else if (showcaseId === 'showcase.enemy-attack') {
      triggerEnemyAttack();
    } else if (showcaseId === 'showcase.xuyi') {
      var livingEnemies = getLivingUnits('enemy');
      var primary = target && target.side === 'enemy' && target.hp > 0 ? target : livingEnemies[0];
      if (!primary) return;
      var splashTargets = [primary].concat(
        livingEnemies.filter(function (e) { return e.id !== primary.id; })
      );
      var primaryDmg = 540;
      var splashDmg = 310;
      splashTargets.forEach(function (enemy, idx) {
        applyDamage(caster, enemy, idx === 0 ? primaryDmg : splashDmg, { forceCrit: idx === 0 });
      });
      VFX.play(
        'trait.xuyi',
        buildCtx(caster, splashTargets, {
          damage: primaryDmg,
          perTargetDamage: splashDmg,
          crit: true,
          element: 'none',
        })
      );
      addLog(
        caster.name +
          ' 触发特质【蓄意爆发】：轰穿 ' +
          primary.name +
          ' 真伤裂星' +
          (splashTargets.length > 1 ? ' 并溅射其余目标！' : '！'),
        'crit'
      );
      emitChange('units');
    } else if (showcaseId === 'showcase.ranhun') {
      var burnTargets = getLivingUnits('enemy').filter(function (e) { return !!e.buffs['buff.ranhun']; });
      var burnList = burnTargets.length > 0 ? burnTargets : target ? [target] : getLivingUnits('enemy').slice(0, 1);
      if (burnList.length === 0) return;
      var tickDmg = 185;
      burnList.forEach(function (enemy) {
        applyDamage(caster, enemy, tickDmg, { forceCrit: false });
      });
      VFX.play(
        'trait.ranhun',
        buildCtx(caster, burnList, {
          damage: tickDmg,
          perTargetDamage: tickDmg,
          element: 'fire',
        })
      );
      addLog(
        '特质【燃烧】：业火每秒灼烧 ' +
          burnList.map(function (e) { return e.name; }).join('、') +
          '（-' +
          tickDmg +
          '）。',
        'crit'
      );
      emitChange('units');
    } else if (showcaseId === 'showcase.basic-combo') {
      if (!target) return;
      var comboNames = ['断岳斜劈', '惊鸿逆撩', '穿云疾刺', '残月横扫', '双绝交错'];
      comboNames.forEach(function (cname, idx) {
        VFX.after(idx * 330, function () {
          ensureTeamAlive('ally');
          ensureTeamAlive('enemy');
          var liveTarget = target.hp > 0 ? target : getLivingUnits('enemy')[0];
          if (!liveTarget) return;
          var resC = applyDamage(caster, liveTarget, caster.physAtk * (1 + idx * 0.08));
          VFX.play('feedback.attack', {
            side: 'ally',
            attackerKey: caster.id,
            attackVariant: idx,
            casterEl: getEl(caster.id),
            targetEl: getEl(liveTarget.id),
            element: 'none',
            damage: resC.damage,
            crit: resC.crit,
            hit: true,
          });
          addLog(
            caster.name +
              ' 使出第 ' +
              (idx + 1) +
              ' 式普攻【' +
              cname +
              '】命中 ' +
              liveTarget.name +
              ' (-' +
              resC.damage +
              ')' +
              (resC.crit ? '（暴击！）' : '。'),
            resC.crit ? 'crit' : 'normal'
          );
          emitChange('units');
        });
      });
    }
  }

  function startSimulationLoop() {
    if (sandbox.tickTimer) clearInterval(sandbox.tickTimer);
    var regenTickCount = 0;
    sandbox.tickTimer = setInterval(function () {
      var now = Date.now();
      var dirty = false;
      regenTickCount++;
      var doRegenVfx = regenTickCount % 5 === 0; // 每秒一次持续回血/回蓝星尘

      sandbox.units.forEach(function (u) {
        if (u.hp <= 0) return;
        Object.keys(u.buffs).forEach(function (bk) {
          var b = u.buffs[bk];
          if (b.expiresAt <= now) {
            delete u.buffs[bk];
            dirty = true;
            return;
          }
          if (doRegenVfx && (b.hpPerSec > 0 || b.mpPerSec > 0)) {
            if (b.hpPerSec > 0 && u.hp < u.maxHp) {
              applyHeal(u, b.hpPerSec);
              if (global.DaohuaVFX) {
                global.DaohuaVFX.play('feedback.regen', {
                  targetEl: getEl(u.id),
                  kind: 'hp',
                  amount: b.hpPerSec,
                });
              }
              dirty = true;
            }
            if (b.mpPerSec > 0 && u.mp < u.maxMp) {
              u.mp = Math.min(u.maxMp, u.mp + b.mpPerSec);
              if (global.DaohuaVFX) {
                global.DaohuaVFX.play('feedback.regen', {
                  targetEl: getEl(u.id),
                  kind: 'mp',
                  amount: b.mpPerSec,
                });
              }
              dirty = true;
            }
          }
        });
      });

      ['personal', 'team'].forEach(function (k) {
        var f = sandbox.activeFormations[k];
        if (f && f.expiresAt <= now) {
          if (f.handle) f.handle.stop(350);
          sandbox.activeFormations[k] = null;
          dirty = true;
        }
      });

      if (dirty) emitChange('units');
    }, 200);
  }

  var AUTO_DEMO_PLAYLIST = [
    { type: 'consumable', id: 'item.tianlei-formation-disk' },
    { type: 'skill', id: 'ability.heavy-slash' },
    { type: 'skill', id: 'ability.huoyan-fengbao' },
    { type: 'skill', id: 'ability.yueqian' },
    { type: 'skill', id: 'ability.zhanyu' },
    { type: 'consumable', id: 'item.leiji-talisman' },
    { type: 'skill', id: 'ability.tide' },
    { type: 'consumable', id: 'item.xuesha-formation-disk' },
    { type: 'skill', id: 'ability.star-piercer' },
    { type: 'consumable', id: 'item.huangya-pill' },
    { type: 'skill', id: 'ability.lightning-attraction' },
    { type: 'skill', id: 'ability.pozhen-melody' },
  ];

  function toggleAutoDemo(force) {
    sandbox.autoDemo = force !== undefined ? !!force : !sandbox.autoDemo;
    clearInterval(sandbox.demoTimer);
    if (sandbox.autoDemo) {
      var runStep = function () {
        var step = AUTO_DEMO_PLAYLIST[sandbox.demoStep % AUTO_DEMO_PLAYLIST.length];
        sandbox.demoStep++;
        if (step.type === 'consumable') useConsumable(step.id);
        else castAbility(step.id);
        if (sandbox.demoStep % 3 === 0 && global.DaohuaVFX) {
          global.DaohuaVFX.after(1050, triggerEnemyAttack);
        }
      };
      runStep();
      sandbox.demoTimer = setInterval(runStep, 2300);
    }
    emitChange('demo');
  }

  function setFastCast(v) {
    sandbox.fastCast = !!v;
    emitChange('speed');
  }

  resetArena();
  startSimulationLoop();

  global.DaohuaSandbox = {
    state: sandbox,
    reset: resetArena,
    castAbility: castAbility,
    castInternalArt: castInternalArt,
    useConsumable: useConsumable,
    triggerFormationProc: triggerFormationProc,
    triggerEnemyAttack: triggerEnemyAttack,
    runShowcase: runShowcase,
    toggleAutoDemo: toggleAutoDemo,
    setFastCast: setFastCast,
    getUnit: getUnit,
    getLivingUnits: getLivingUnits,
    subscribe: function (fn) {
      sandbox.listeners.add(fn);
      return function () {
        sandbox.listeners.delete(fn);
      };
    },
    setUnitElResolver: function (fn) {
      sandbox.unitElResolver = fn;
    },
  };
})(window);
