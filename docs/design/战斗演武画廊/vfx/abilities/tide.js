/* ability.tide · 潮汐 —— 五行玄水全敌三叠巨浪主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   敌方全体 / 吟唱 3000ms / 0.5×AP+30 / 无冷却 / 灵力 10 / 五行·水
 * 分镜设计（水墨惊涛流体粒子化，零硬边水墙）：
 *   1. 施法者身前凝出纯径向渐变苍澜海眼涡流，卷起墨蓝与青白 wisp 潮水气缕、水珠与轻海雾；
 *   2. 连续释放三叠渐进惊涛：以柔羽化 wave 助势，并在浪脊沿途 6 段密集喷发多层苍青墨蓝翻滚 wisp 浪舌
 *      （深墨蓝暗浪打底 + 亮青浪头卷曲翻涌）与白色碎浪泡沫 drop/orb/轻雾 smoke；
 *   3. 每叠浪头拍击各敌人时激起浪花飞溅、高斯水波、闪白受击、三段跳字与递增海啸震屏。
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

  VFX.register('ability.tide', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.enemyEls && ctx.enemyEls.length) {
      targets = ctx.enemyEls.slice();
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';

    var group = targets.length ? targets : foeSide;
    var axis = VFX.combatAxis(casterEl, group);
    var bounds = VFX.groupBounds(group);
    var s = VFX.scale();
    var st = VFX.size();
    var isCrit = !!ctx.crit;

    // 阶段一：施法者身前掀起苍澜水墨海眼旋涡（纯渐变光池 + 翻滚流体气缕）
    VFX.motion(casterEl, 'cast', { duration: 480 });
    var eyePt = VFX.clampStage(axis.palm(42 * s), 36 * s, 28 * s);

    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 820,
      draw: function (c2d, p) {
        var fade = p < 0.18 ? p / 0.18 : p > 0.7 ? Math.max(0, (1 - p) / 0.3) : 1;
        var r = 84 * s * (0.65 + 0.35 * Math.sin(p * Math.PI * 0.7));
        c2d.save();
        c2d.translate(eyePt.x, eyePt.y);
        c2d.scale(1, 0.56);
        var g = c2d.createRadialGradient(0, 0, 2, 0, 0, r);
        g.addColorStop(0, 'rgba(224, 242, 254,' + (0.42 * fade).toFixed(3) + ')');
        g.addColorStop(0.36, 'rgba(56, 189, 248,' + (0.3 * fade).toFixed(3) + ')');
        g.addColorStop(0.72, 'rgba(12, 74, 110,' + (0.2 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(8, 47, 73, 0)');
        c2d.fillStyle = g;
        c2d.beginPath();
        c2d.arc(0, 0, r, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    VFX.burst({
      x: eyePt.x,
      y: eyePt.y,
      radius: 48 * s,
      tiltY: 0.56,
      count: 18,
      shape: 'wisp',
      colors: ['#0c4a6e', '#0284c7', '#38bdf8', '#e0f2fe'],
      size: [5.5 * s, 10.5 * s],
      speed: [32 * s, 82 * s],
      vortex: axis.facingX * 380 * s,
      vortexCenter: eyePt,
      turbulence: 110 * s,
      drag: 0.95,
      life: [420, 720],
    });

    VFX.burst({
      x: eyePt.x,
      y: eyePt.y,
      radius: 46 * s,
      tiltY: 0.56,
      count: 18,
      shape: 'drop',
      colors: ['#38bdf8', '#7dd3fc', '#e0f2fe', '#ffffff'],
      size: [2.2 * s, 4.0 * s],
      speed: [32 * s, 84 * s],
      vortex: axis.facingX * 340 * s,
      vortexCenter: eyePt,
      drag: 0.96,
      life: [380, 660],
    });

    VFX.burst({
      x: eyePt.x,
      y: eyePt.y,
      radius: 36 * s,
      count: 8,
      shape: 'smoke',
      blend: 'lighter',
      colors: ['#bae6fd', '#38bdf8'],
      size: [16 * s, 26 * s],
      speed: [18 * s, 46 * s],
      alpha: [0.18, 0.3],
      life: [420, 680],
    });

    // 阶段二：三叠苍青墨蓝多层流体浪卷粒子席卷敌阵（全向自适应横排/竖列）
    var waveStart = VFX.clampStage(axis.palm(24 * s), 20 * s, 20 * s);
    var waveEnd = VFX.clampStage({
      x: bounds.cx + axis.ux * 72 * s,
      y: bounds.cy + axis.uy * 72 * s,
    }, 32, 32);
    var waveDx = waveEnd.x - waveStart.x;
    var waveDy = waveEnd.y - waveStart.y;
    var waveLenSq = waveDx * waveDx + waveDy * waveDy;

    // 计算存活敌人在攻击法线 (axis.nx, axis.ny) 上的投影跨度，兼容横排、竖列与斜向阵型
    var perpSpan = 0;
    if (targets.length > 1) {
      var minPerp = Infinity;
      var maxPerp = -Infinity;
      for (var pi = 0; pi < targets.length; pi++) {
        var ptC = VFX.center(targets[pi]);
        var proj = (ptC.x - bounds.cx) * axis.nx + (ptC.y - bounds.cy) * axis.ny;
        if (proj < minPerp) minPerp = proj;
        if (proj > maxPerp) maxPerp = proj;
      }
      if (maxPerp > minPerp) perpSpan = maxPerp - minPerp;
    }

    var maxWaveW = Math.max(110 * s, Math.min(st.w, st.h) * 0.88);
    var waves = [
      { delay: 140, dur: 540, thickness: 64, crests: 2, color: '#38bdf8', sec: '#0c4a6e', foam: '#e0f2fe', dmgShare: 0.3 },
      { delay: 380, dur: 580, thickness: 82, crests: 3, color: '#4fc3ff', sec: '#0369a1', foam: '#f0f9ff', dmgShare: 0.3 },
      { delay: 620, dur: 640, thickness: 102, crests: 3, color: '#38bdf8', sec: '#082f49', foam: '#ffffff', dmgShare: 0.4 },
    ];

    for (var wIdx = 0; wIdx < waves.length; wIdx++) {
      (function (cfg, idx) {
        var cfgWidth = VFX.clamp(perpSpan + (80 + idx * 28) * s, 110 * s, maxWaveW);

        // 柔羽化波浪衬底助势（基于阵地 span 计算宽度，传入 rawScale 防止双重缩窄）
        VFX.wave({
          delay: cfg.delay,
          duration: cfg.dur,
          x1: waveStart.x,
          y1: waveStart.y,
          x2: waveEnd.x,
          y2: waveEnd.y,
          width: cfgWidth,
          rawScale: true,
          thickness: cfg.thickness,
          crests: cfg.crests,
          style: 'water',
          color: cfg.color,
          secondaryColor: cfg.sec,
          foamColor: cfg.foam,
        });

        // 沿波浪推进路线 5 段密集生成苍青墨蓝翻滚 wisp 浪舌与白色碎浪泡沫
        var sampleProgs = [0.14, 0.31, 0.48, 0.66, 0.84];
        for (var sp = 0; sp < sampleProgs.length; sp++) {
          (function (p, stepIdx) {
            var ease = 1 - Math.pow(1 - p, 3);
            var wx = VFX.lerp(waveStart.x, waveEnd.x, ease);
            var wy = VFX.lerp(waveStart.y, waveEnd.y, ease);
            var at = cfg.delay + Math.round(cfg.dur * p);
            VFX.after(at, function () {
              // 3 处法向错落的浪头切片，形成高低起伏的波脊线
              for (var lane = -1; lane <= 1; lane++) {
                var lateralOff = lane * cfgWidth * 0.26 + VFX.rand(-12, 12) * s;
                var forwardOff = (lane === 0 ? 16 : -8) * s;
                var lanePt = {
                  x: wx + axis.nx * lateralOff,
                  y: wy + axis.ny * lateralOff,
                };
                var crestPt = {
                  x: lanePt.x + axis.ux * forwardOff,
                  y: lanePt.y + axis.uy * forwardOff,
                };

                // 深墨蓝水墨暗浪体（source-over 营造墨色厚重惊涛层次）
                var spdDark = (135 + idx * 32) * s;
                VFX.burst({
                  x: crestPt.x - axis.ux * 10 * s,
                  y: crestPt.y - axis.uy * 10 * s + 6 * s,
                  radius: 18 * s,
                  count: 2 + (idx === 2 ? 1 : 0),
                  shape: 'wisp',
                  blend: 'source-over',
                  colors: ['#082f49', '#0c4a6e', '#0369a1'],
                  size: [9.5 * s, 16.5 * s],
                  endSize: [14.0 * s, 22.0 * s],
                  speed: [18 * s, 45 * s],
                  vx: axis.ux * spdDark + axis.nx * lane * 14 * s,
                  vy: axis.uy * spdDark + axis.ny * lane * 14 * s - 42 * s,
                  gravity: 130 * s,
                  turbulence: 145 * s,
                  drag: 0.955,
                  alpha: [0.44, 0.66],
                  life: [360, 620],
                });

                // 苍青泛白高光浪舌（lighter 卷曲浪冠）
                var spdLight = (165 + idx * 36) * s;
                VFX.burst({
                  x: crestPt.x,
                  y: crestPt.y - 4 * s,
                  radius: 16 * s,
                  count: 2 + (idx > 0 ? 1 : 0),
                  shape: 'wisp',
                  colors: ['#0ea5e9', '#38bdf8', '#7dd3fc', '#e0f2fe'],
                  size: [7.0 * s, 13.0 * s],
                  endSize: [5.0 * s, 9.5 * s],
                  speed: [22 * s, 54 * s],
                  vx: axis.ux * spdLight + axis.nx * lane * 16 * s,
                  vy: axis.uy * spdLight + axis.ny * lane * 16 * s - 68 * s,
                  gravity: 155 * s,
                  turbulence: 165 * s,
                  drag: 0.95,
                  alpha: [0.56, 0.8],
                  life: [320, 560],
                });
              }

              // 白色碎浪泡沫 drop 与浮沫 orb、水雾 smoke
              var dropLat = VFX.rand(-cfgWidth * 0.28, cfgWidth * 0.28);
              VFX.burst({
                x: wx + axis.ux * 8 * s + axis.nx * dropLat,
                y: wy + axis.uy * 8 * s + axis.ny * dropLat,
                radius: 34 * s,
                count: 7 + idx * 2,
                shape: 'drop',
                colors: ['#ffffff', '#e0f2fe', '#7dd3fc', '#38bdf8'],
                size: [2.2 * s, 4.4 * s],
                speed: [85 * s, 210 * s],
                baseAngle: axis.angle - 0.35 * axis.facingX,
                spread: 1.4,
                gravity: 225 * s,
                drag: 0.94,
                life: [260, 480],
                trail: 3,
              });

              var orbLat = VFX.rand(-cfgWidth * 0.24, cfgWidth * 0.24);
              VFX.burst({
                x: wx + axis.nx * orbLat,
                y: wy + axis.ny * orbLat,
                radius: 30 * s,
                count: 5 + idx,
                shape: 'orb',
                colors: ['#ffffff', '#e0f2fe', '#bae6fd'],
                size: [2.2 * s, 4.6 * s],
                speed: [35 * s, 98 * s],
                gravity: -18 * s,
                drag: 0.94,
                life: [300, 540],
              });

              if (stepIdx % 2 === 0) {
                VFX.burst({
                  x: wx - axis.ux * 14 * s,
                  y: wy - axis.uy * 14 * s,
                  radius: cfgWidth * 0.26,
                  count: 4 + idx,
                  shape: 'smoke',
                  blend: 'lighter',
                  colors: ['#e0f2fe', '#bae6fd', '#7dd3fc'],
                  size: [16 * s, 28 * s],
                  speed: [22 * s, 56 * s],
                  alpha: [0.16, 0.26],
                  life: [400, 680],
                });
              }
            });
          })(sampleProgs[sp], sp);
        }

        // 每叠浪拍击敌阵前沿触发海啸震屏
        VFX.after(cfg.delay + Math.round(cfg.dur * 0.52), function () {
          VFX.quake(idx === 2 ? 'md' : 'sm', idx === 2 ? 220 : 150);
        });

        // 阶段三：浪头掠过各敌人位置时的水墨浪花炸裂、闪白与三段跳字（按 2D 主轴投影进度）
        var list = targets.length ? targets : [null];
        for (var ti = 0; ti < list.length; ti++) {
          (function (tEl, tIdx) {
            var tPt = tEl ? VFX.center(tEl) : { x: bounds.cx, y: bounds.cy };
            var uRaw = waveLenSq > 1
              ? ((tPt.x - waveStart.x) * waveDx + (tPt.y - waveStart.y) * waveDy) / waveLenSq
              : 0.65;
            var u = VFX.clamp(uRaw, 0.14, 0.92);
            var pHit = 1 - Math.pow(1 - u, 1 / 3);
            var hitAt = cfg.delay + Math.round(cfg.dur * pHit);

            var fullDmg = Math.round(resolveTargetDamage(ctx, tIdx, list.length, 60));
            var waveDmg = idx < 2
              ? Math.round(fullDmg * cfg.dmgShare)
              : Math.max(1, fullDmg - Math.round(fullDmg * 0.3) * 2);

            VFX.after(hitAt, function () {
              // 拍岸浪卷气缕回溅
              VFX.burst({
                x: tPt.x,
                y: tPt.y + 4 * s,
                radius: 20 * s,
                count: 8 + idx * 2,
                shape: 'wisp',
                colors: ['#0c4a6e', '#0ea5e9', '#38bdf8', '#e0f2fe'],
                size: [5.5 * s, 10.5 * s],
                speed: [65 * s, 155 * s],
                baseAngle: axis.angle - 0.55 * axis.facingX,
                spread: 1.7,
                gravity: 120 * s,
                turbulence: 130 * s,
                drag: 0.94,
                life: [300, 520],
              });

              VFX.burst({
                x: tPt.x,
                y: tPt.y,
                count: 14 + idx * 4,
                shape: 'drop',
                colors: ['#0284c7', '#38bdf8', '#e0f2fe', '#ffffff'],
                size: [2.4 * s, 4.6 * s],
                speed: [90 * s, 215 * s],
                baseAngle: axis.angle - 0.45 * axis.facingX,
                spread: 1.5,
                gravity: 230 * s,
                drag: 0.93,
                life: [260, 480],
                trail: 3,
              });

              VFX.burst({
                x: tPt.x,
                y: tPt.y,
                radius: 16 * s,
                count: 5 + idx,
                shape: 'flare',
                colors: ['#ffffff', '#e0f2fe', '#7dd3fc'],
                size: [2.0 * s, 3.8 * s],
                speed: [45 * s, 125 * s],
                drag: 0.92,
                life: [200, 380],
              });

              VFX.shockwave({
                x: tPt.x,
                y: tPt.y + 8 * s,
                fromRadius: 8,
                toRadius: 48 + idx * 10,
                tilt: 0.68,
                layer: 'sky',
                color: '#38bdf8',
                coreColor: '#e0f2fe',
                lineWidth: 2.8,
                duration: 260,
              });

              if (tEl) {
                VFX.impact(tEl, { color: '#38bdf8', duration: 180, distance: 7 + idx * 2 });
              }
              VFX.floatText(tEl || tPt, '-' + waveDmg, isCrit && idx === 2 ? 'crit' : 'damage');
            });
          })(list[ti], ti);
        }
      })(waves[wIdx], wIdx);
    }

    return 1500;
  });
});
