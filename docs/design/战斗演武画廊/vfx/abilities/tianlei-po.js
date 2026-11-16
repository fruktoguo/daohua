/* ability.tianlei-po · 天雷破 —— 二阶木系神雷打击至多两名敌人并施加麻痹
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   至多 2 名敌人 / 吟唱 2000ms / 1.1×AP+50 + 5s「麻痹」 / 冷却 18s / 灵力 20 / 五行·木
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

  VFX.register('ability.tianlei-po', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls.slice(0, 2) : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.enemyEls && ctx.enemyEls.length) {
      targets = ctx.enemyEls.slice(0, 2);
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    var s = VFX.scale();
    var st = VFX.size();
    var isCrit = !!ctx.crit;
    var targetCount = Math.max(1, targets.length);
    var group = targets.length ? targets : foeSide;
    var axis = VFX.combatAxis(casterEl, group);
    var bounds = VFX.groupBounds(group);

    // 阶段一：苍翠暗幕 + 施法指尖引乙木青雷汇向敌阵上空雷涡
    VFX.cinematicDim(0.38, 680, '#041410');
    VFX.motion(casterEl, 'cast', { duration: 360 });

    var cloudPadX = Math.min(64 * s, st.w * 0.4);
    var cloudPt = {
      x: VFX.clamp(bounds.cx, cloudPadX, Math.max(cloudPadX, st.w - cloudPadX)),
      y: VFX.clamp(bounds.topHeadY - 96 * s, 26, Math.max(30, bounds.cy - 48)),
    };
    var palmPt = VFX.clampStage(axis.palm(20 * s), 20 * s, 20 * s);

    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 12 * s,
      count: 8,
      shape: 'flare',
      colors: ['#ffffff', '#6ee7b7', '#34d399'],
      size: [2.0 * s, 4.0 * s],
      speed: [28 * s, 76 * s],
      life: [180, 300],
    });
    VFX.lightning({
      x1: palmPt.x,
      y1: palmPt.y,
      x2: cloudPt.x,
      y2: cloudPt.y,
      width: 3.2 * s,
      jagged: 0.3,
      segments: 14,
      branches: 2,
      flicker: 4,
      color: '#34d399',
      secondaryColor: '#ecfdf5',
      glowColor: '#059669',
      coreColor: '#ffffff',
      duration: 165,
    });
    VFX.burst({
      x: cloudPt.x,
      y: cloudPt.y,
      radius: 42 * s,
      count: 14,
      shape: 'smoke',
      colors: ['#064e3b', '#065f46', '#10b981'],
      size: [18 * s, 32 * s],
      speed: [18 * s, 46 * s],
      alpha: 0.52,
      life: [420, 660],
    });

    // 阶段二：云涡分叉双雷贯落命中目标并缠绕麻痹电弧
    var totalT = 760;
    for (var i = 0; i < targetCount; i++) {
      (function (idx) {
        var el = targets[idx] || null;
        var pt = el ? VFX.center(el) : VFX.sideCenter(foeSide);
        var ft = el ? VFX.foot(el) : { x: pt.x, y: pt.y + 36 * s };
        var safePt = VFX.clampStage(pt, 30 * s, 24 * s);
        var safeFoot = VFX.clampStage(ft, 30 * s, 18);
        var delay = 115 + idx * 85;
        var dmg = Math.round(resolveTargetDamage(ctx, idx, targetCount, 128));

        VFX.after(delay, function () {
          VFX.lightning({
            x1: cloudPt.x + (idx === 0 ? -10 : 10) * s,
            y1: cloudPt.y,
            x2: safePt.x,
            y2: safePt.y,
            width: 5.4 * s,
            jagged: 0.32,
            segments: 16,
            branches: 3,
            flicker: 5,
            color: '#10b981',
            secondaryColor: '#a7f3d0',
            glowColor: '#047857',
            coreColor: '#ffffff',
            duration: 210,
          });
          VFX.lightning({
            delay: 25,
            x1: cloudPt.x,
            y1: cloudPt.y + 4 * s,
            x2: safeFoot.x,
            y2: safeFoot.y,
            width: 3.4 * s,
            jagged: 0.36,
            segments: 14,
            branches: 2,
            flicker: 4,
            color: '#fef08a',
            secondaryColor: '#6ee7b7',
            glowColor: '#10b981',
            coreColor: '#ffffff',
            duration: 185,
          });

          VFX.shockwave({
            x: safeFoot.x,
            y: safeFoot.y,
            fromRadius: 12,
            toRadius: 112,
            tilt: 0.44,
            layer: 'ground',
            color: '#34d399',
            coreColor: '#ecfdf5',
            lineWidth: 4.0,
            duration: 400,
          });
          VFX.groundCrack({
            x: safeFoot.x,
            y: safeFoot.y,
            radius: 78,
            branches: 7,
            color: '#34d399',
            crackColor: '#042f2e',
            lineWidth: 3.0,
            duration: 1200,
          });
          VFX.burst({
            x: safePt.x,
            y: safePt.y,
            radius: 18 * s,
            count: 14,
            shape: 'spark',
            colors: ['#ffffff', '#a7f3d0', '#fde047', '#34d399'],
            size: [2.2 * s, 4.2 * s],
            speed: [55 * s, 145 * s],
            life: [220, 420],
          });

          // 麻痹电丝缠绕柔光环
          VFX.custom({
            layer: 'sky',
            blend: 'lighter',
            duration: 620,
            draw: function (c2d, p) {
              var fade = p < 0.16 ? p / 0.16 : Math.pow(1 - (p - 0.16) / 0.84, 1.35);
              var rx = 38 * s;
              var ry = 22 * s;
              c2d.save();
              c2d.translate(safePt.x, safePt.y);
              var grad = c2d.createRadialGradient(0, 0, 2, 0, 0, rx * 1.15);
              grad.addColorStop(0, 'rgba(167,243,208,' + (0.38 * fade).toFixed(3) + ')');
              grad.addColorStop(0.55, 'rgba(250,204,21,' + (0.22 * fade).toFixed(3) + ')');
              grad.addColorStop(1, 'rgba(16,185,129,0)');
              c2d.fillStyle = grad;
              c2d.beginPath();
              c2d.ellipse(0, 0, rx * 1.15, ry * 1.25, 0, 0, TWO_PI);
              c2d.fill();
              c2d.restore();
            },
          });

          VFX.quake(idx === 0 ? 'lg' : 'md', 260);
          if (el) {
            VFX.impact(el, { color: '#34d399', duration: 240, distance: 14 });
          }
          VFX.floatText(el || safePt, '-' + dmg, isCrit ? 'crit' : 'damage', {
            scale: isCrit ? 1.28 : 1.08,
          });
          VFX.floatText({ x: safePt.x, y: safePt.y - 24 * s }, '麻痹', 'debuff', {
            scale: 0.92,
          });
        });

        totalT = Math.max(totalT, delay + 720);
      })(i);
    }

    return totalT;
  });
});
