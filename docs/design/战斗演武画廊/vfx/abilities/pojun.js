/* ability.pojun · 破军 —— 悍刃横扫敌阵并凝起移形与势不可挡护体霸气
 * 真源镜像：config/game-data/abilities/physical_abilities.json
 *   敌方全体 / 吟唱 1500ms / 1.2×AD+50 + 移形(5s) + 5层势不可挡 / 冷却 24s / 灵力 35 / 刀修·金
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  function resolveTargetDamage(ctx, idx, targetCount, fallback) {
    if (Array.isArray(ctx.damage)) return ctx.damage[idx] != null ? ctx.damage[idx] : fallback;
    if (ctx.perTargetDamage != null) return ctx.perTargetDamage;
    if (ctx.damage != null) {
      return targetCount > 1 ? Math.round(ctx.damage / targetCount) : ctx.damage;
    }
    return fallback;
  }

  VFX.register('ability.pojun', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls.slice() : (ctx.targetEl ? [ctx.targetEl] : []);
    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    if (!targets.length) {
      var pool = isEnemyCaster ? ctx.allyEls : ctx.enemyEls;
      if (pool && pool.length) targets = pool.slice();
    }

    var s = VFX.scale();
    var group = targets.length ? targets : foeSide;
    var bounds = VFX.groupBounds(group);
    var axis = VFX.combatAxis(casterEl, group);
    var cRaw = casterEl ? VFX.center(casterEl) : VFX.sideCenter(isEnemyCaster ? 'enemy' : 'ally');
    var cSafe = VFX.clampStage(cRaw, 30 * s, 26 * s);
    var sfRaw = casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(isEnemyCaster ? 'enemy' : 'ally');
    var sf = VFX.clampStage(sfRaw, 32 * s, 18);
    var formCenter = VFX.clampStage({ x: bounds.cx, y: bounds.cy }, 38 * s, 32 * s);
    var formFoot = VFX.clampStage({ x: bounds.cx, y: bounds.bottomFootY }, 42 * s, 18);
    var isCrit = !!ctx.crit;

    VFX.cinematicDim(0.4, 760, '#120a02');
    VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, duration: 320 });

    // 阶段一：施法者足底裂出金琥珀霸气光阵并蓄起横扫气浪
    VFX.magicCircle({
      x: sf.x,
      y: sf.y,
      radius: 74,
      tilt: 0.44,
      layer: 'ground',
      color: '#f59e0b',
      secondaryColor: '#fef08a',
      style: 'sword',
      rotationSpeed: 2.0,
      duration: 840,
      alpha: 0.78,
      lineWidth: 2.2,
    });
    VFX.burst({
      x: cSafe.x,
      y: cSafe.y,
      radius: 20 * s,
      count: 14,
      shape: 'ember',
      colors: ['#fef08a', '#f59e0b', '#ef4444', '#ffffff'],
      size: [2.4 * s, 4.6 * s],
      speed: [45 * s, 125 * s],
      life: [240, 460],
    });

    // 阶段二：140ms 后赤金破军巨刃斜切席卷整个敌阵
    VFX.after(140, function () {
      var sweepAngle = axis.angle - 0.18;
      var slashLen = Math.max(165 * s, bounds.spanX * 1.15);
      VFX.slash({
        x: formCenter.x,
        y: formCenter.y,
        angle: sweepAngle,
        radius: slashLen * 0.56,
        thickness: 22 * s,
        color: '#f59e0b',
        coreColor: '#ffffff',
        secondaryColor: '#ef4444',
        duration: 300,
      });
      VFX.wave({
        x: formCenter.x,
        y: formCenter.y,
        angle: axis.angle,
        length: slashLen,
        width: 58 * s,
        color: '#f59e0b',
        coreColor: '#fef08a',
        duration: 340,
      });
      VFX.groundCrack({
        x: formFoot.x,
        y: formFoot.y,
        radius: 108,
        branches: 8,
        color: '#f59e0b',
        crackColor: '#1f1203',
        lineWidth: 3.4,
        duration: 1500,
      });
      VFX.quake('lg', 320);

      var count = Math.max(1, targets.length);
      for (var i = 0; i < count; i++) {
        var el = targets[i] || null;
        var pt = el ? VFX.center(el) : formCenter;
        var sp = VFX.clampStage(pt, 28 * s, 24 * s);
        var dmg = Math.round(resolveTargetDamage(ctx, i, count, 145));
        VFX.burst({
          x: sp.x,
          y: sp.y,
          radius: 18 * s,
          count: 10,
          shape: 'shard',
          colors: ['#ffffff', '#fef08a', '#f59e0b'],
          size: [2.8 * s, 5.2 * s],
          speed: [55 * s, 145 * s],
          life: [220, 400],
        });
        if (el) {
          VFX.impact(el, { color: '#f59e0b', duration: 240, distance: 16 });
        }
        VFX.floatText(el || sp, '-' + dmg, isCrit ? 'crit' : 'damage', {
          scale: isCrit ? 1.3 : 1.1,
        });
      }

      // 阶段三：施法者自身同步凝聚「移形」青芒与五重玄金「势不可挡」护体罩
      VFX.after(120, function () {
        VFX.custom({
          layer: 'sky',
          blend: 'lighter',
          duration: 820,
          draw: function (c2d, p) {
            var fade = p < 0.16 ? p / 0.16 : Math.pow(1 - (p - 0.16) / 0.84, 1.35);
            var r = (58 - p * 12) * s;
            c2d.save();
            c2d.translate(cSafe.x, cSafe.y);
            var g = c2d.createRadialGradient(0, 0, r * 0.35, 0, 0, r);
            g.addColorStop(0, 'rgba(254, 240, 138, 0)');
            g.addColorStop(0.58, 'rgba(245, 158, 11,' + (0.48 * fade).toFixed(3) + ')');
            g.addColorStop(0.84, 'rgba(45, 212, 191,' + (0.28 * fade).toFixed(3) + ')');
            g.addColorStop(1, 'rgba(245, 158, 11, 0)');
            c2d.fillStyle = g;
            c2d.beginPath();
            c2d.arc(0, 0, r, 0, TWO_PI);
            c2d.fill();
            c2d.restore();
          },
        });
        VFX.burst({
          x: cSafe.x,
          y: cSafe.y,
          radius: 26 * s,
          count: 10,
          shape: 'wisp',
          colors: ['#2dd4bf', '#fef08a', '#f59e0b'],
          size: [4.2 * s, 7.6 * s],
          speed: [22 * s, 60 * s],
          vortex: 140 * s,
          vortexCenter: cSafe,
          alpha: 0.68,
          life: [380, 660],
        });
        VFX.floatText(casterEl || cSafe, '势不可挡 · 移形', 'buff', { scale: 0.96 });
      });
    });

    return 1260;
  });
});
