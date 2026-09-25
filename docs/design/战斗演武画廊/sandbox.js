/* =========================================================================
 * 演武沙盘状态机 (DaohuaSandbox)
 *
 * 职责：
 * - 维护演示单位的 HP/MP/行动条/冷却/Buff 状态，结算走真实功法配表字段；
 * - 施法管线：扣灵力 → 吟唱(castMilliseconds) → 按 targetType 解析目标
 *   → 调起 vfx/abilities/<id>.js 演出 → ctx.impact/heal/buffApply 定时结算；
 * - 环境普攻底噪：行动条推进 + 普攻演出 + 受击反馈，模拟真实战斗节奏；
 * - 结算语义仅服务演示（±10% 浮动、12% 会心×1.6），不代表服务端权威数值。
 * ========================================================================= */
(function () {
  'use strict';
  const VFX = window.DaohuaVFX;
  const CATALOG = window.DaohuaCatalog;

  /* ---------------- 演示单位（HP/MP 为沙盘展示值，技能行为以配表为准） ---------------- */
  const ACTOR_DEFS = {
    // 我方三人：少侠（前排中先锋）+ 云裳（中排中）+ 岳罡（后排中），与敌方纵深对称 3v3
    player: { name: '少侠', side: 'ally', hp: 150, mp: 70, atk: 30, attackInterval: 3200 },
    ally2: { name: '云裳', side: 'ally', hp: 120, mp: 90, atk: 22, attackInterval: 3600 },
    ally3: { name: '岳罡', side: 'ally', hp: 200, mp: 50, atk: 26, attackInterval: 4000 },
    enemy1: { name: '魔化野兔', side: 'enemy', hp: 450, mp: 100, atk: 12, attackInterval: 4200 },
    enemy2: { name: '魔猿', side: 'enemy', hp: 950, mp: 250, atk: 24, attackInterval: 3800 },
    enemy3: { name: '星屑火鸦', side: 'enemy', hp: 850, mp: 350, atk: 18, attackInterval: 4600 },
  };

  const CRIT_CHANCE = 0.12;
  const CRIT_RATIO = 1.6;
  const TICK_MS = 100;

  const actors = {};
  const slotCells = new Map();   // abilityId -> 装配槽 cell 元素（冷却遮罩挂载点）
  let ambient = true;
  let autoDemoTimers = [];
  let demoSide = 'ally';

  /* ---------------- DOM 查询 ---------------- */
  function el(id) { return document.getElementById(id); }

  function actorEl(id) { return document.querySelector('.arena-actor[data-actor="' + id + '"]'); }

  function buildActors() {
    Object.keys(ACTOR_DEFS).forEach(id => {
      const def = ACTOR_DEFS[id];
      const dom = actorEl(id);
      actors[id] = {
        id,
        name: def.name,
        side: def.side,
        el: dom,
        hp: def.hp,
        maxHp: def.hp,
        mp: def.mp,
        maxMp: def.mp,
        atk: def.atk,
        attackInterval: def.attackInterval,
        alive: true,
        casting: false,
        actionRatio: 0,
        cooldowns: {},            // abilityId -> 剩余 ms（敌方用；玩家冷却同时画槽位遮罩）
        buffs: [],
      };
    });
  }

  function resourceEl(actor, kind) {
    return actor.el && actor.el.querySelector('.arena-resource--' + kind);
  }

  function actionBarEl(actor) {
    return actor.el && actor.el.querySelector('.arena-action-bar');
  }

  function actionFill(actor) {
    const bar = actionBarEl(actor);
    return bar ? bar.querySelector('.arena-action-track i b') : null;
  }

  function actionBadge(actor) {
    const bar = actionBarEl(actor);
    return bar ? bar.querySelector('.arena-action-badge') : null;
  }

  /* ---------------- 日志 ---------------- */
  function log(msg, cls) {
    const list = el('logList');
    if (!list) return;
    // 真实 GameLogPanel 同构：<li><time/> + <span.log-message/></li>
    const li = document.createElement('li');
    li.className = 'log-entry' + (cls ? ' log-entry--' + cls : '');
    const time = new Date();
    const t = document.createElement('time');
    t.textContent =
      String(time.getHours()).padStart(2, '0') + ':' +
      String(time.getMinutes()).padStart(2, '0') + ':' +
      String(time.getSeconds()).padStart(2, '0');
    const span = document.createElement('span');
    span.className = 'log-message';
    span.textContent = msg;
    li.append(t, span);
    list.prepend(li);
    while (list.children.length > 80) list.lastChild.remove();
  }

  /* ---------------- 血条 / 资源刷新 ---------------- */
  function setVitals(actor) {
    const hpRatio = Math.max(0, actor.hp / actor.maxHp);
    const mpRatio = Math.max(0, Math.min(1, actor.mp / actor.maxMp));
    const hp = resourceEl(actor, 'hp');
    const mp = resourceEl(actor, 'mp');
    if (hp) {
      hp.querySelector('.arena-resource-track i').style.transform = 'scaleX(' + hpRatio + ')';
      hp.querySelector('.arena-resource-badge').textContent = Math.ceil(actor.hp) + ' / ' + actor.maxHp;
    }
    if (mp) {
      mp.querySelector('.arena-resource-track i').style.transform = 'scaleX(' + mpRatio + ')';
      mp.querySelector('.arena-resource-badge').textContent = Math.ceil(actor.mp) + ' / ' + actor.maxMp;
    }
    syncQueueVitals();
  }

  /** 顶栏队列中心的双方气血聚合（真实界面同位置；我方聚合全体存活友方，与敌方口径对称） */
  function syncQueueVitals() {
    const allies = Object.values(actors).filter(a => a.side === 'ally');
    const enemies = Object.values(actors).filter(a => a.side === 'enemy');
    if (!allies.length) return;
    const aHp = allies.reduce((s, a) => s + Math.max(0, a.hp), 0);
    const aMax = allies.reduce((s, a) => s + a.maxHp, 0);
    const eHp = enemies.reduce((s, a) => s + Math.max(0, a.hp), 0);
    const eMax = enemies.reduce((s, a) => s + a.maxHp, 0);
    const pf = el('queuePlayerHpFill');
    const pv = el('queuePlayerHpValue');
    const ef = el('queueEnemyHpFill');
    const ev = el('queueEnemyHpValue');
    if (pf) pf.style.transform = 'scaleX(' + (aMax ? aHp / aMax : 0) + ')';
    if (pv) pv.textContent = Math.ceil(aHp) + ' / ' + aMax + '（' + (aMax ? Math.round(aHp / aMax * 100) : 0) + '%）';
    if (ef) ef.style.transform = 'scaleX(' + (eMax ? eHp / eMax : 0) + ')';
    if (ev) ev.textContent = Math.ceil(eHp) + ' / ' + eMax + '（' + (eMax ? Math.round(eHp / eMax * 100) : 0) + '%）';
  }

  /* ---------------- 行动条 ---------------- */
  function setActionBar(actor, ratio, opts) {
    const fill = actionFill(actor);
    const bar = actionBarEl(actor);
    if (!fill || !bar) return;
    if (opts && opts.transitionMs != null) {
      fill.style.transition = 'transform ' + opts.transitionMs + 'ms linear';
    } else {
      fill.style.transition = 'none';
    }
    fill.style.transform = 'scaleX(' + Math.max(0, Math.min(1, ratio)) + ')';
    const badge = actionBadge(actor);
    if (badge) {
      if (opts && opts.label) {
        badge.textContent = opts.label;
        badge.style.display = '';
      } else {
        badge.style.display = 'none';
      }
    }
    bar.classList.toggle('arena-action-bar--wait', !(opts && opts.active));
    bar.classList.toggle('arena-action-bar--cast', !!(opts && opts.casting));
  }

  /* ---------------- 目标解析（对齐真实 targetType 语义） ---------------- */
  function aliveOf(side) {
    return Object.values(actors).filter(a => a.side === side && a.alive);
  }

  function resolveTargets(caster, ability) {
    const foeSide = caster.side === 'ally' ? 'enemy' : 'ally';
    const allySide = caster.side;
    switch (ability.targetType) {
      case 'self':
        return [caster];
      case 'allEnemies':
        return aliveOf(foeSide);
      case 'allAllies':
        return aliveOf(allySide);
      case 'lowestHitpointsAlly': {
        const allies = aliveOf(allySide);
        if (!allies.length) return [];
        return [allies.reduce((a, b) => (a.hp / a.maxHp <= b.hp / b.maxHp ? a : b))];
      }
      case 'single':
      default: {
        const foes = aliveOf(foeSide);
        return foes.length ? [foes[0]] : [];
      }
    }
  }

  /* ---------------- 结算 ---------------- */
  function applyDamage(caster, target, spec, opts) {
    if (!target.alive) return;
    const ratio = spec.damageRatio || 0.85;
    const flat = spec.flatDamage || 6;
    const variance = 0.9 + Math.random() * 0.2;
    const crit = (opts && opts.crit) || Math.random() < CRIT_CHANCE;
    const raw = (caster.atk * ratio + flat) * variance * (crit ? CRIT_RATIO : 1);
    const dmg = Math.max(1, Math.round(raw));

    const hpBefore = target.hp / target.maxHp;
    target.hp = Math.max(0, target.hp - dmg);
    const hpAfter = target.hp / target.maxHp;

    setVitals(target);
    VFX.resourceSlice(target.el, 'hp', hpAfter, Math.max(0.01, hpBefore - hpAfter));
    VFX.floatText(target.el, '-' + dmg, { kind: 'hp', crit, stamp: crit ? '暴击' : null });
    VFX.impact(target.el, Math.min(1, dmg / target.maxHp * 3.2), opts && opts.impactVariant);
    log((spec.name || '普攻') + ' 命中 ' + target.name + '，造成 ' + dmg + ' 点伤害' + (crit ? '（会心）' : ''), 'damage');

    if (target.hp <= 0) kill(target);
  }

  function applyHeal(caster, target, spec) {
    if (!target.alive) return;
    const ratio = spec.healingRatio || 0.6;
    const flat = spec.flatHealing || 20;
    const amount = Math.round((caster.atk * ratio + flat) * (0.9 + Math.random() * 0.2));
    const hpBefore = target.hp / target.maxHp;
    target.hp = Math.min(target.maxHp, target.hp + amount);
    setVitals(target);
    VFX.resourceSlice(target.el, 'hp', hpBefore, Math.max(0.01, target.hp / target.maxHp - hpBefore));
    VFX.floatText(target.el, '+' + amount, { kind: 'hp', positive: true, stamp: '恢复' });
    log((spec.name || '治疗') + ' 为 ' + target.name + ' 恢复 ' + amount + ' 点气血', 'heal');
  }

  function applyBuffFx(target, buffDef) {
    const buff = buffDef || { name: '破阵子', durationSeconds: 30 };
    const list = target.el && target.el.querySelector('.arena-actor-buff-list');
    if (list) {
      const sq = document.createElement('span');
      sq.className = 'arena-buff-square';
      sq.title = buff.name + '（' + buff.durationSeconds + 's）';
      const cd = document.createElement('i');
      cd.className = 'arena-buff-cd-fill';
      cd.style.animationDuration = buff.durationSeconds + 's';
      sq.appendChild(cd);
      const txt = document.createElement('b');
      txt.textContent = buff.name.charAt(0);
      sq.appendChild(txt);
      list.appendChild(sq);
      target.buffs.push(sq);
      VFX.after(buff.durationSeconds * 1000, () => sq.remove());
    }
    // 全局 Buff 栏：玩家获得增益时顶栏同步出现 chip（与真实 BuffBar 同位）
    if (target.id === 'player') {
      const bar = el('buffBar');
      if (bar) {
        // 真实 BuffBar chip 同构：30×30 方格 + svg 图标 + buff-tip 悬停气泡
        const chip = document.createElement('span');
        chip.className = 'buff-chip';
        chip.setAttribute('tabindex', '0');
        chip.innerHTML = '<svg fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.7" viewBox="0 0 24 24"><use href="#icon-buff"></use></svg>' +
          '<span class="buff-tip"><b></b><em></em></span>';
        chip.querySelector('.buff-tip b').textContent = buff.name;
        chip.querySelector('.buff-tip em').textContent = buff.durationSeconds + 's';
        bar.appendChild(chip);
        bar.hidden = false;
        VFX.after(buff.durationSeconds * 1000, () => {
          chip.remove();
          if (!bar.children.length) bar.hidden = true;
        });
      }
    }
    VFX.floatText(target.el, buff.name, { kind: 'hp', positive: true, stamp: '增益' });
    log(target.name + ' 获得增益「' + buff.name + '」（' + buff.durationSeconds + ' 秒）', 'buff');
  }

  function kill(actor) {
    if (!actor.alive) return;
    actor.alive = false;
    actor.casting = false;
    VFX.defeat(actor.el);
    VFX.floatText(actor.el, '力竭', { kind: 'hp', stamp: '阵亡' });
    setActionBar(actor, 0);
    log(actor.name + ' 力竭倒下', 'defeat');
    if (actor.side === 'enemy' && !aliveOf('enemy').length) {
      log('敌阵全灭 —— 可在控制台「重置战场」重新演武', 'system');
    }
    if (actor.side === 'ally' && !aliveOf('ally').length) {
      log('我方全员败北 —— 点击「复活」或「重置战场」继续', 'system');
    } else if (actor.id === 'player') {
      log('少侠败北 —— 点击「复活」或「重置战场」继续', 'system');
    }
  }

  function reviveActor(actor) {
    actor.alive = true;
    actor.hp = Math.max(actor.hp, Math.round(actor.maxHp * 0.5));
    setVitals(actor);
    VFX.revive(actor.el);
    VFX.floatText(actor.el, '+' + Math.ceil(actor.hp), { kind: 'hp', positive: true, stamp: '复活' });
    log(actor.name + ' 重燃战意，重返战场', 'heal');
  }

  /* ---------------- 施法管线 ---------------- */
  function castAbility(casterId, abilityId, opts) {
    const caster = actors[casterId];
    const ability = CATALOG.byId(abilityId);
    if (!caster || !ability) return false;
    const manual = !!(opts && opts.manual);
    if (!caster.alive) { log(caster.name + ' 已倒下，无法出手', 'system'); return false; }
    if (caster.casting) { log(caster.name + ' 正在吟唱中', 'system'); return false; }
    // 手动演武（点击槽位/功法格/轮播）不进冷却也不耗灵力：冷却遮罩/灵力条仅用于展示真实 UI 行为
    const cd = caster.cooldowns[abilityId] || 0;
    if (!manual && cd > 0) { log('「' + ability.name + '」冷却中（' + (cd / 1000).toFixed(1) + 's）', 'system'); return false; }
    if (!manual) {
      if (caster.mp < ability.manaCost) {
        log(caster.name + ' 灵力不足，无法施展「' + ability.name + '」（需 ' + ability.manaCost + '）', 'system');
        return false;
      }
      // 扣灵力 + 灵力条跳字（负值表示消耗）
      caster.mp -= ability.manaCost;
      setVitals(caster);
      VFX.floatText(caster.el, '-' + ability.manaCost, { kind: 'mp' });
    }

    const castMs = ability.castMilliseconds;
    caster.casting = true;
    setActionBar(caster, 0, { active: true, casting: true });
    // 吟唱期：行动条按真实 castMilliseconds 线性推进 + 徽标显示功法名
    VFX.after(30, () => setActionBar(caster, 1, { transitionMs: Math.max(60, castMs - 30), active: true, casting: true, label: ability.name }));
    // 聚灵法阵门槛与变体（粒子色相/轨迹）在 feedback.castRing 内按 ability 统一判定
    VFX.play('feedback.castRing', { caster, targets: [], castMs, ability, impact: () => {}, heal: () => {}, threat: () => {}, buffApply: () => {}, kill, reviveActor, regen: () => {} });
    log(caster.name + ' 开始吟唱「' + ability.name + '」（' + (castMs / 1000).toFixed(1) + 's）', 'cast');

    VFX.after(castMs, () => {
      caster.casting = false;
      if (!caster.alive) { setActionBar(caster, 0); return; }
      const targets = resolveTargets(caster, ability);
      setActionBar(caster, 0, { active: false });
      if (!targets.length) {
        log('「' + ability.name + '」没有可命中的目标', 'system');
        return;
      }
      // 冷却开始（真实 cooldownMilliseconds；0CD 不记；手动演武不记冷却）
      if (ability.cooldownMilliseconds > 0 && !manual) {
        caster.cooldowns[abilityId] = ability.cooldownMilliseconds;
      }
      const ctx = buildCtx(caster, targets, ability);
      VFX.play(abilityId, ctx);
      log(caster.name + ' 施展「' + ability.name + '」' + describeTargets(ability, targets), 'cast');
    });
    return true;
  }

  function describeTargets(ability, targets) {
    const names = targets.map(t => t.name).join('、');
    switch (ability.targetType) {
      case 'self': return '（运功自身）';
      case 'allEnemies': return ' → 敌阵全体（' + names + '）';
      case 'allAllies': return ' → 我方全体（' + names + '）';
      default: return ' → ' + names;
    }
  }

  /** 技能脚本结算回调上下文：所有伤害/治疗/增益经此定时落地 */
  function buildCtx(caster, targets, ability) {
    return {
      caster,
      targets,
      castMs: ability.castMilliseconds || 0,
      ability,
      impact: (target, atMs, opts) => VFX.after(atMs || 0, () => applyDamage(caster, target, ability, opts)),
      heal: (target, atMs) => VFX.after(atMs || 0, () => applyHeal(caster, target, ability)),
      threat: (target, atMs) => VFX.after(atMs || 0, () => {
        VFX.floatText(target.el, '威胁 +' + (ability.threatGain || 0), { kind: 'hp', stamp: '嘲讽' });
        log(target.name + ' 运功怒喝，威胁值 +' + (ability.threatGain || 0), 'buff');
      }),
      buffApply: (target, atMs) => VFX.after(atMs || 0, () => applyBuffFx(target, ability.buff)),
      kill,
      reviveActor,
      regen: (actor) => {
        actor.mp = Math.min(actor.maxMp, actor.mp + actor.maxMp * 0.15);
        setVitals(actor);
        VFX.floatText(actor.el, '+' + Math.round(actor.maxMp * 0.15), { kind: 'mp', positive: true, stamp: '调息' });
      },
    };
  }

  /** 普攻：真实 attack 动作 + 命中结算（环境底噪与演示按钮共用） */
  function basicAttack(attackerId, targetId) {
    const attacker = actors[attackerId];
    if (!attacker || !attacker.alive || attacker.casting) return false;
    const foeSide = attacker.side === 'ally' ? 'enemy' : 'ally';
    const foes = aliveOf(foeSide);
    // 未指定目标时在存活敌阵中随机择一（3v3 混战演示，覆盖多目标受击/阵亡面）
    const target = targetId ? actors[targetId] : foes[Math.floor(Math.random() * foes.length)];
    if (!target || !target.alive) return false;
    const ctx = {
      caster: attacker,
      targets: [target],
      castMs: 0,
      impact: (t, atMs, opts) => VFX.after(atMs || 0, () => applyDamage(attacker, t, { name: '普攻', damageRatio: 0.85, flatDamage: 6 }, opts)),
      heal: () => {}, threat: () => {}, buffApply: () => {},
      kill, reviveActor, regen: () => {},
    };
    VFX.play('feedback.attack', ctx);
    return true;
  }

  /* ---------------- 通用反馈演示按钮 ---------------- */
  function playFeedback(kind, variant) {
    const demoCaster = demoSide === 'ally' ? actors.player : (aliveOf('enemy')[0] || actors.enemy1);
    const foes = demoSide === 'ally' ? aliveOf('enemy') : aliveOf('ally');
    const target = foes[0] || actors.player;
    const ctx = {
      caster: demoCaster,
      targets: [target],
      castMs: 900,
      variant,
      impact: (t, atMs, opts) => VFX.after(atMs || 0, () => {
        if (kind === 'crit') {
          applyDamage(demoCaster, t, { name: '会心一击', damageRatio: 1.4, flatDamage: 30 }, { ...opts, crit: true });
        } else {
          applyDamage(demoCaster, t, { name: '受击演示', damageRatio: 0.5, flatDamage: 8 }, opts);
        }
      }),
      heal: () => {}, threat: () => {},
      buffApply: () => {},
      kill, reviveActor,
      regen: (actor) => {
        actor.mp = Math.min(actor.maxMp, actor.mp + actor.maxMp * 0.15);
        setVitals(actor);
        VFX.floatText(actor.el, '+' + Math.round(actor.maxMp * 0.15), { kind: 'mp', positive: true, stamp: '调息' });
      },
    };
    switch (kind) {
      case 'attack': return VFX.play('feedback.attack', ctx);
      case 'impact': return VFX.play('feedback.impact', ctx);
      case 'crit':   return VFX.play('feedback.crit', ctx);
      case 'defeat': {
        const victim = foes[0] || target;
        ctx.targets = [victim];
        ctx.kill = kill;
        return VFX.play('feedback.defeat', ctx);
      }
      case 'regen':  ctx.caster = demoCaster; return VFX.play('feedback.regen', ctx);
      case 'revive': {
        // 复活全部阵亡单位；无阵亡则对当前目标演示复活特效
        const dead = Object.values(actors).filter(a => !a.alive);
        ctx.targets = dead.length ? dead : [target];
        return VFX.play('feedback.revive', ctx);
      }
    }
    return 0;
  }

  /* ---------------- 环境底噪：行动条推进 + 自动普攻 + 灵力回复 ---------------- */
  function tick() {
    Object.values(actors).forEach(actor => {
      // 冷却递减
      Object.keys(actor.cooldowns).forEach(id => {
        actor.cooldowns[id] = Math.max(0, actor.cooldowns[id] - TICK_MS);
        if (actor.cooldowns[id] <= 0) delete actor.cooldowns[id];
      });
      if (!actor.alive) return;
      // 灵力缓缓回复（沙盘演示速率）
      if (actor.mp < actor.maxMp) {
        actor.mp = Math.min(actor.maxMp, actor.mp + actor.maxMp * 0.004);
        const mp = resourceEl(actor, 'mp');
        if (mp) {
          mp.querySelector('.arena-resource-track i').style.transform = 'scaleX(' + (actor.mp / actor.maxMp) + ')';
          mp.querySelector('.arena-resource-badge').textContent = Math.ceil(actor.mp) + ' / ' + actor.maxMp;
        }
      }
      // 环境普攻：行动条推进，满则出手
      if (ambient && !actor.casting) {
        actor.actionRatio += TICK_MS / actor.attackInterval;
        if (actor.actionRatio >= 1) {
          actor.actionRatio = 0;
          basicAttack(actor.id);
        }
        if (!actor.casting) setActionBar(actor, actor.actionRatio, { active: true });
      }
    });
    // 玩家功法冷却遮罩（真实 combat-ability-cooldown 同构）
    updateCooldownOverlays();
  }

  function updateCooldownOverlays() {
    const player = actors.player;
    if (!player) return;
    slotCells.forEach((cell, abilityId) => {
      const ability = CATALOG.byId(abilityId);
      let overlay = cell.querySelector('.combat-ability-cooldown');
      const remaining = player.cooldowns[abilityId] || 0;
      if (remaining > 0 && ability) {
        if (!overlay) {
          overlay = document.createElement('span');
          overlay.className = 'combat-ability-cooldown';
          cell.appendChild(overlay);
        }
        overlay.style.setProperty('--cooldown-ratio', String(Math.min(1, remaining / ability.cooldownMilliseconds)));
        overlay.textContent = (remaining / 1000).toFixed(1) + 's';
        overlay.hidden = false;
      } else if (overlay) {
        overlay.remove();
      }
    });
  }

  /* ---------------- 全量功法轮播 ---------------- */
  function autoDemo() {
    stopAutoDemo();
    const casterId = demoSide === 'ally' ? 'player' : 'enemy2';
    const caster = actors[casterId];
    if (!caster) {
      log('演示出手方不可用，请先重置战场', 'system');
      return 0;
    }
    // 出手方已阵亡则先复位战场，保证轮播完整跑完十式
    if (!caster.alive) resetAll();
    caster.mp = caster.maxMp;
    setVitals(caster);
    let t = 600;
    CATALOG.ABILITIES.forEach(ability => {
      const timer = setTimeout(() => {
        // 轮播中途被环境普攻带走则复活续演，不中断演出节奏
        if (!caster.alive) reviveActor(caster);
        caster.mp = caster.maxMp; // 轮播时保持灵力充足，专注演出
        setVitals(caster);
        castAbility(casterId, ability.id, { manual: true });
      }, t);
      autoDemoTimers.push(timer);
      t += ability.castMilliseconds + 2400;
    });
    log('开始全量功法轮播（' + CATALOG.ABILITIES.length + ' 式 · ' + (demoSide === 'ally' ? '我方' : '敌方') + '出手）', 'system');
    return t;
  }

  function stopAutoDemo() {
    autoDemoTimers.forEach(t => clearTimeout(t));
    autoDemoTimers = [];
  }

  /* ---------------- 重置 ---------------- */
  function resetAll() {
    stopAutoDemo();
    VFX.resetAll();
    Object.values(actors).forEach(actor => {
      const def = ACTOR_DEFS[actor.id];
      actor.hp = def.hp;
      actor.maxHp = def.hp;
      actor.mp = def.mp;
      actor.maxMp = def.mp;
      actor.alive = true;
      actor.casting = false;
      actor.actionRatio = 0;
      actor.cooldowns = {};
      actor.buffs = [];
      if (actor.el) {
        actor.el.classList.remove('is-dead', 'is-hit', 'is-action-attack', 'is-action-cast');
        const avatar = actor.el.querySelector('.character-avatar');
        if (avatar) avatar.classList.remove('is-dead');
        const buffList = actor.el.querySelector('.arena-actor-buff-list');
        if (buffList) buffList.innerHTML = '';
      }
      setVitals(actor);
      setActionBar(actor, 0);
    });
    const bar = el('buffBar');
    if (bar) { bar.innerHTML = ''; bar.hidden = true; }
    log('战场已重置 —— 全员气血/灵力恢复，增益与冷却清空', 'system');
  }

  /* ---------------- 装配槽位绑定（gallery.js 渲染后注册） ---------------- */
  function bindSlot(abilityId, cellEl) {
    slotCells.set(abilityId, cellEl);
  }

  function setAmbient(on) {
    ambient = !!on;
    if (!ambient) {
      Object.values(actors).forEach(a => { if (!a.casting) setActionBar(a, 0); });
    }
    return ambient;
  }

  function setDemoSide(side) {
    demoSide = side === 'enemy' ? 'enemy' : 'ally';
    return demoSide;
  }

  buildActors();
  setInterval(tick, TICK_MS);

  window.DaohuaSandbox = {
    actors,
    ambient: () => ambient,
    basicAttack,
    bindSlot,
    castAbility,
    demoSide: () => demoSide,
    autoDemo,
    stopAutoDemo,
    playFeedback,
    resetAll,
    reviveActor,
    setAmbient,
    setDemoSide,
    log,
    resolveTargets,
  };
})();
