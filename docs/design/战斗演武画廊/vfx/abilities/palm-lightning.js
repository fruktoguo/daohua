/* ability.palm-lightning · 掌心雷 —— 五行木系雷法单体主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   单体敌 / 吟唱 2000ms / 0.7×AP+40 / 无冷却 / 灵力 4 / 五行·木（青木雷灵）
 * 分镜设计（粒子内吸聚雷 + 分段弧形掣电 + 持续麻痹电花）：
 *   1. 施法者掌心浮现纯径向紫青电球，attractTo 强力内吸青木/幽紫 wisp、spark 与 flare 电粒，跃动短电弧；
 *   2. 120ms 后释放柔羽化曲线掣电：弧形流光拖尾携手 3 段沿弧线接力奔袭的分形电弧与游离电浆 wisp 直贯敌身；
 *   3. 命中瞬间爆开三维分形电弧、电浆气缕、高斯雷环与四射电星，并持续在受击者周身跳动闪烁 5 波麻痹火花与微弧。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  function evalArcPoint(p0, p1, arcAmt, t) {
    var dx = p1.x - p0.x;
    var dy = p1.y - p0.y;
    var dist = Math.hypot(dx, dy) || 1;
    var nx = -dy / dist;
    var ny = dx / dist;
    var cx = (p0.x + p1.x) * 0.5 + nx * arcAmt;
    var cy = (p0.y + p1.y) * 0.5 + ny * arcAmt;
    var inv = 1 - t;
    return {
      x: inv * inv * p0.x + 2 * inv * t * cx + t * t * p1.x,
      y: inv * inv * p0.y + 2 * inv * t * cy + t * t * p1.y,
    };
  }

  VFX.register('ability.palm-lightning', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.enemyEls && ctx.enemyEls.length) {
      targets = [ctx.enemyEls[0]];
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    var targetEl = targets[0] || null;
    var s = VFX.scale();
    var axis = VFX.combatAxis(casterEl, targetEl || foeSide);
    var targetPt = VFX.clampStage(axis.end, 26 * s, 22 * s);
    var dirX = axis.facingX;
    var arcFactor = Math.min(1, axis.dist / 240) * s;

    // 阶段一：施法起势 + 掌心紫青电球内吸 attractTo
    VFX.motion(casterEl, 'cast', { duration: 360 });
    var palmPt = VFX.clampStage(axis.palm(24 * s), 24 * s, 20 * s);

    VFX.magicCircle({
      x: palmPt.x,
      y: palmPt.y,
      radius: 32,
      tilt: 1,
      layer: 'sky',
      style: 'thunder',
      color: '#5be4a8',
      secondaryColor: '#a78bfa',
      rotationSpeed: 3.8,
      lineWidth: 1.4,
      alpha: 0.72,
      duration: 300,
    });

    // 掌心纯径向紫青聚能雷球（白核 -> 翠青 -> 幽紫 -> 靛蓝大气晕）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 240,
      draw: function (c2d, p) {
        var pulse = 0.78 + 0.22 * Math.sin(p * 24);
        var fade = p < 0.22 ? p / 0.22 : p > 0.75 ? Math.max(0, (1 - p) / 0.25) : 1;
        var r = (34 + 22 * Math.sin(p * Math.PI * 0.85)) * pulse * s;
        c2d.save();
        c2d.translate(palmPt.x, palmPt.y);
        var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
        g.addColorStop(0, 'rgba(255, 255, 255,' + (0.94 * fade).toFixed(3) + ')');
        g.addColorStop(0.24, 'rgba(91, 228, 168,' + (0.64 * fade).toFixed(3) + ')');
        g.addColorStop(0.52, 'rgba(139, 92, 246,' + (0.38 * fade).toFixed(3) + ')');
        g.addColorStop(0.78, 'rgba(79, 70, 229,' + (0.16 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(79, 70, 229, 0)');
        c2d.fillStyle = g;
        c2d.beginPath();
        c2d.arc(0, 0, r, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    // 紫青电粒向掌心电球旋吸凝聚
    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 56 * s,
      ring: true,
      count: 10,
      shape: 'wisp',
      colors: ['#8b5cf6', '#5be4a8', '#67e8f9'],
      size: [3.2 * s, 5.8 * s],
      speed: [12 * s, 34 * s],
      attractTo: palmPt,
      attractStrength: 1220 * s,
      vortex: 240 * s,
      vortexCenter: palmPt,
      drag: 0.9,
      life: [140, 220],
    });

    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 58 * s,
      ring: true,
      count: 14,
      shape: 'spark',
      colors: ['#a78bfa', '#5be4a8', '#67e8f9', '#ffffff'],
      size: [1.6 * s, 3.0 * s],
      speed: [14 * s, 38 * s],
      attractTo: palmPt,
      attractStrength: 1360 * s,
      drag: 0.9,
      life: [130, 210],
      trail: 3,
    });

    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 44 * s,
      ring: true,
      count: 6,
      shape: 'flare',
      colors: ['#ffffff', '#a78bfa', '#5be4a8'],
      size: [1.6 * s, 3.0 * s],
      speed: [10 * s, 28 * s],
      attractTo: palmPt,
      attractStrength: 1180 * s,
      drag: 0.9,
      life: [130, 200],
    });

    // 掌心短弧跃动预兆
    var preL1 = VFX.clampStage({
      x: palmPt.x - axis.nx * 16 * s - axis.ux * 10 * s,
      y: palmPt.y - axis.ny * 16 * s - axis.uy * 10 * s,
    }, 10, 10);
    var preL2 = VFX.clampStage({
      x: palmPt.x + axis.nx * 16 * s + axis.ux * 10 * s,
      y: palmPt.y + axis.ny * 16 * s + axis.uy * 10 * s,
    }, 10, 10);
    VFX.lightning({
      x1: preL1.x,
      y1: preL1.y,
      x2: preL2.x,
      y2: preL2.y,
      width: 2.8 * s,
      jagged: 0.34,
      segments: 10,
      branches: 2,
      flicker: 3,
      color: '#a78bfa',
      secondaryColor: '#a7f3d0',
      glowColor: '#6366f1',
      haloScale: 1.15,
      coreColor: '#ffffff',
      duration: 210,
    });

    // 阶段二：柔羽化贯通长龙雷与交缠紫电副弧奔袭（替代断截短弧）
    var dmg = Math.round(ctx.damage != null ? ctx.damage : 68);
    var isCrit = !!ctx.crit;
    var arcAmt = dirX * -24 * arcFactor;

    VFX.after(115, function () {
      // 第一拍（0ms）：先行探出至弧线 0.62 处的翠青先导雷
      var leadPt = evalArcPoint(palmPt, targetPt, arcAmt, 0.62);
      VFX.lightning({
        delay: 0,
        x1: palmPt.x,
        y1: palmPt.y,
        x2: leadPt.x,
        y2: leadPt.y,
        width: 4.2 * s,
        jagged: 0.27,
        segments: 16,
        branches: 3,
        flicker: 3,
        color: '#5be4a8',
        secondaryColor: '#ccfbf1',
        glowColor: '#6366f1',
        haloScale: 1.18,
        coreColor: '#ffffff',
        duration: 135,
      });

      // 第二拍（42ms）：贯通掌心至目标全长的翠青紫晕主龙雷
      VFX.lightning({
        delay: 42,
        x1: palmPt.x,
        y1: palmPt.y,
        x2: targetPt.x,
        y2: targetPt.y,
        width: 5.2 * s,
        jagged: 0.25,
        segments: 20,
        branches: 5,
        flicker: 5,
        color: '#5be4a8',
        secondaryColor: '#ccfbf1',
        glowColor: '#6366f1',
        haloScale: 1.25,
        coreColor: '#ffffff',
        duration: 230,
      });

      // 交缠紫电长副弧（58ms）
      VFX.lightning({
        delay: 58,
        x1: palmPt.x,
        y1: palmPt.y,
        x2: targetPt.x,
        y2: targetPt.y,
        width: 3.5 * s,
        jagged: 0.3,
        segments: 16,
        branches: 3,
        flicker: 4,
        color: '#a78bfa',
        secondaryColor: '#67e8f9',
        glowColor: '#4f46e5',
        haloScale: 1.1,
        coreColor: '#ffffff',
        duration: 210,
      });

      // 沿弧线 0.35 与 0.7 处绽开游离电浆 wisp 与 flare 星点
      var arcSpawns = [
        { t: 0.35, delay: 24, col: '#5be4a8' },
        { t: 0.7, delay: 62, col: '#a78bfa' },
      ];
      for (var sp = 0; sp < arcSpawns.length; sp++) {
        var spPt = evalArcPoint(palmPt, targetPt, arcAmt, arcSpawns[sp].t);
        VFX.burst({
          delay: arcSpawns[sp].delay,
          x: spPt.x,
          y: spPt.y,
          radius: 14 * s,
          count: 5,
          shape: 'wisp',
          colors: [arcSpawns[sp].col, '#67e8f9', '#ffffff'],
          size: [3.6 * s, 6.6 * s],
          speed: [32 * s, 92 * s],
          turbulence: 120 * s,
          alpha: 0.68,
          life: [190, 340],
        });
        VFX.burst({
          delay: arcSpawns[sp].delay + 12,
          x: spPt.x,
          y: spPt.y,
          radius: 10 * s,
          count: 3,
          shape: 'flare',
          colors: ['#ffffff', arcSpawns[sp].col],
          size: [2.0 * s, 3.8 * s],
          speed: [20 * s, 62 * s],
          life: [160, 280],
        });
      }

      VFX.streamer({
        x1: palmPt.x,
        y1: palmPt.y,
        x2: targetPt.x,
        y2: targetPt.y,
        arc: arcAmt,
        wobble: 8 * arcFactor,
        width: 10 * s,
        tailLength: 0.46,
        duration: 240,
        color: '#5be4a8',
        coreColor: '#ffffff',
        sparkColors: ['#5be4a8', '#a78bfa', '#67e8f9', '#ffffff'],
        onArrive: function (hitPt) {
          var safeHit = VFX.clampStage(hitPt, 30 * s, 26 * s);

          // 阶段三：命中爆开分形电弧 + 电浆气缕 + 受击者周身持续跳动麻痹火花（防右界裁切）
          var arcs = [
            { dx1: -34 * dirX, dy1: -44, dx2: 28 * dirX, dy2: 26, w: 5.0, col: '#5be4a8', sec: '#ccfbf1', glow: '#6366f1', delay: 0 },
            { dx1: 26 * dirX, dy1: -28, dx2: -30 * dirX, dy2: 18, w: 4.0, col: '#a78bfa', sec: '#e9d5ff', glow: '#4f46e5', delay: 28 },
            { dx1: -10 * dirX, dy1: -62, dx2: 16 * dirX, dy2: 38, w: 3.6, col: '#67e8f9', sec: '#e0f2fe', glow: '#6366f1', delay: 56 },
          ];
          for (var i = 0; i < arcs.length; i++) {
            var a = arcs[i];
            var p1 = VFX.clampStage({ x: safeHit.x + a.dx1 * s, y: safeHit.y + a.dy1 * s }, 10, 10);
            var p2 = VFX.clampStage({ x: safeHit.x + a.dx2 * s, y: safeHit.y + a.dy2 * s }, 10, 10);
            VFX.lightning({
              x1: p1.x,
              y1: p1.y,
              x2: p2.x,
              y2: p2.y,
              width: a.w * s,
              jagged: 0.28,
              segments: 12,
              branches: 3,
              flicker: 4,
              color: a.col,
              secondaryColor: a.sec,
              glowColor: a.glow,
              haloScale: 1.2,
              coreColor: '#ffffff',
              duration: 290,
              delay: a.delay,
            });
          }

          // 命中瞬间柔羽化雷爆大气光晕（白核 -> 翠雷 -> 紫冕）
          VFX.custom({
            layer: 'sky',
            blend: 'lighter',
            duration: 320,
            draw: function (c2d, p) {
              var fade = 1 - Math.pow(p, 1.35);
              var r = 74 * s * (0.58 + 0.42 * p);
              c2d.save();
              c2d.translate(safeHit.x, safeHit.y);
              var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
              g.addColorStop(0, 'rgba(255, 255, 255,' + (0.82 * fade).toFixed(3) + ')');
              g.addColorStop(0.28, 'rgba(91, 228, 168,' + (0.52 * fade).toFixed(3) + ')');
              g.addColorStop(0.58, 'rgba(139, 92, 246,' + (0.28 * fade).toFixed(3) + ')');
              g.addColorStop(0.84, 'rgba(99, 102, 241,' + (0.1 * fade).toFixed(3) + ')');
              g.addColorStop(1, 'rgba(99, 102, 241, 0)');
              c2d.fillStyle = g;
              c2d.beginPath();
              c2d.arc(0, 0, r, 0, TWO_PI);
              c2d.fill();
              c2d.restore();
            },
          });

          VFX.magicCircle({
            x: safeHit.x,
            y: safeHit.y,
            radius: 42,
            tilt: 0.92,
            layer: 'sky',
            style: 'thunder',
            color: '#5be4a8',
            secondaryColor: '#a78bfa',
            rotationSpeed: -4.2,
            lineWidth: 1.5,
            alpha: 0.76,
            duration: 320,
          });

          VFX.shockwave({
            x: safeHit.x,
            y: safeHit.y,
            fromRadius: 8,
            toRadius: 72,
            tilt: 0.92,
            layer: 'sky',
            color: '#5be4a8',
            coreColor: '#ffffff',
            lineWidth: 3.6,
            duration: 300,
          });

          VFX.burst({
            x: safeHit.x,
            y: safeHit.y,
            radius: 16 * s,
            count: 14,
            shape: 'wisp',
            colors: ['#5be4a8', '#a78bfa', '#67e8f9'],
            size: [4.2 * s, 8.2 * s],
            speed: [45 * s, 135 * s],
            vortex: 260 * s,
            vortexCenter: safeHit,
            drag: 0.93,
            life: [240, 440],
          });

          VFX.burst({
            x: safeHit.x,
            y: safeHit.y,
            count: 22,
            shape: 'spark',
            colors: ['#5be4a8', '#a7f3d0', '#a78bfa', '#67e8f9', '#ffffff'],
            size: [2 * s, 4.2 * s],
            speed: [75 * s, 215 * s],
            drag: 0.92,
            life: [190, 380],
            trail: 4,
          });

          VFX.burst({
            x: safeHit.x,
            y: safeHit.y,
            radius: 12 * s,
            count: 8,
            shape: 'flare',
            colors: ['#ffffff', '#67e8f9', '#a78bfa'],
            size: [2.4 * s, 4.6 * s],
            speed: [55 * s, 155 * s],
            drag: 0.91,
            life: [190, 360],
          });

          // 麻痹火花在受击者周身持续跳动闪烁（5 波脉冲微弧 + 闪烁电花）
          var paraDelays = [130, 260, 400, 550, 700];
          for (var pd = 0; pd < paraDelays.length; pd++) {
            (function (at, idx) {
              VFX.after(at, function () {
                var a1 = Math.random() * TWO_PI;
                var a2 = a1 + VFX.rand(1.4, 2.8);
                var rArc = VFX.rand(18, 30) * s;
                var pa1 = VFX.clampStage({ x: safeHit.x + Math.cos(a1) * rArc, y: safeHit.y + Math.sin(a1) * rArc }, 8, 8);
                var pa2 = VFX.clampStage({ x: safeHit.x + Math.cos(a2) * rArc, y: safeHit.y + Math.sin(a2) * rArc }, 8, 8);
                VFX.lightning({
                  x1: pa1.x,
                  y1: pa1.y,
                  x2: pa2.x,
                  y2: pa2.y,
                  width: 2.3 * s,
                  jagged: 0.36,
                  segments: 10,
                  branches: 1,
                  flicker: 2,
                  color: idx % 2 === 0 ? '#5be4a8' : '#a78bfa',
                  secondaryColor: idx % 2 === 0 ? '#ccfbf1' : '#e9d5ff',
                  glowColor: idx % 2 === 0 ? '#6366f1' : '#7c3aed',
                  haloScale: 1.15,
                  coreColor: '#ffffff',
                  duration: 135,
                });
                VFX.burst({
                  x: safeHit.x,
                  y: safeHit.y,
                  radius: 26 * s,
                  count: 5,
                  shape: 'spark',
                  colors: ['#5be4a8', '#a78bfa', '#ffffff'],
                  size: [1.5 * s, 3.0 * s],
                  speed: [35 * s, 95 * s],
                  turbulence: 190 * s,
                  drag: 0.91,
                  life: [150, 280],
                  trail: 3,
                });
                VFX.burst({
                  x: safeHit.x,
                  y: safeHit.y,
                  radius: 24 * s,
                  count: 3,
                  shape: 'flare',
                  colors: ['#ffffff', '#67e8f9', '#a78bfa'],
                  size: [1.8 * s, 3.4 * s],
                  speed: [18 * s, 60 * s],
                  life: [140, 250],
                });
              });
            })(paraDelays[pd], pd);
          }

          VFX.quake('sm', 160);
          if (targetEl) {
            VFX.impact(targetEl, { color: '#5be4a8', duration: 200, distance: 10 });
          }
          VFX.floatText(targetEl || safeHit, '-' + dmg, isCrit ? 'crit' : 'damage');
        },
      });
    });

    return 1150;
  });
});
