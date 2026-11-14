/* ability.yueqian · 跃迁 —— 二阶虚空折跃赋予全队「移形」主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   我方全体 / 吟唱 3000ms / 移形 Buff 5s（2×闪避与命中，赋能斩玉变踏云） / 冷却 24s / 灵力 50 / 符箓
 * 分镜设计（全粒子流体空间折跃，零硬椭圆与菱形折线）：
 *   1. 全队身形先后幻化闪动，原地凝出起点星渊，attractTo 引力内缩大量青紫 wisp、shard 与微缩 flare；
 *   2. 落点裂开纯径向渐变虚空星云，伴随渐变曲边光弧绽放、空间震荡高斯涟漪、wisp 星云雾与「移形」浮字；
 *   3. 友方之间飞掠一道紫电虚空折叠粒子带（8 段错位漂移的青紫 wisp、shard 与微缩 flare 组成折叠通道）。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  function drawFeatheredCurveArc(c2d, x0, y0, cx, cy, x1, y1, thickness, rgb, alpha) {
    if (alpha <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.3, 'rgba(' + rgb + ',' + (alpha * 0.65).toFixed(3) + ')');
    grad.addColorStop(0.55, 'rgba(' + rgb + ',' + alpha.toFixed(3) + ')');
    grad.addColorStop(0.8, 'rgba(' + rgb + ',' + (alpha * 0.48).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(x0, y0);
    c2d.quadraticCurveTo(cx + nx * thickness, cy + ny * thickness, x1, y1);
    c2d.quadraticCurveTo(cx - nx * thickness * 0.7, cy - ny * thickness * 0.7, x0, y0);
    c2d.fill();
  }

  function drawTaperedCrossRay(c2d, len, halfW, rgb, alpha) {
    if (alpha <= 0.01 || len <= 1) return;
    var grad = c2d.createLinearGradient(-len, 0, len, 0);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.28, 'rgba(' + rgb + ',' + (alpha * 0.45).toFixed(3) + ')');
    grad.addColorStop(0.5, 'rgba(255,255,255,' + (alpha * 0.92).toFixed(3) + ')');
    grad.addColorStop(0.72, 'rgba(' + rgb + ',' + (alpha * 0.45).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(-len, 0);
    c2d.quadraticCurveTo(0, -halfW, len, 0);
    c2d.quadraticCurveTo(0, halfW, -len, 0);
    c2d.fill();
  }

  function emitFoldCorridor(VFX, p1, p2, arcOffset, baseDelay, s) {
    var dx = p2.x - p1.x;
    var dy = p2.y - p1.y;
    var dist = Math.hypot(dx, dy) || 1;
    var nx = -dy / dist;
    var ny = dx / dist;
    var cx = (p1.x + p2.x) * 0.5 + nx * arcOffset;
    var cy = (p1.y + p2.y) * 0.5 + ny * arcOffset;

    // 柔细主引光弧
    VFX.streamer({
      delay: baseDelay,
      x1: p1.x,
      y1: p1.y,
      x2: p2.x,
      y2: p2.y,
      arc: arcOffset,
      wobble: 10 * s,
      width: 6.5 * s,
      tailLength: 0.46,
      duration: 330,
      color: '#a78bfa',
      coreColor: '#f8fafc',
      sparkColors: ['#67e8f9', '#e2e8f0', '#ffffff'],
    });

    // 8 段按 2D 法线错位漂移的青紫 wisp、shard 与微缩 flare 粒子带
    var steps = 8;
    for (var stIdx = 0; stIdx < steps; stIdx++) {
      (function (idx) {
        var u = idx / (steps - 1);
        var inv = 1 - u;
        var px = inv * inv * p1.x + 2 * inv * u * cx + u * u * p2.x;
        var py = inv * inv * p1.y + 2 * inv * u * cy + u * u * p2.y;
        // 切线方向并叠加错位法向偏移，形成宽幅折叠粒子带
        var tx = 2 * inv * (cx - p1.x) + 2 * u * (p2.x - cx);
        var ty = 2 * inv * (cy - p1.y) + 2 * u * (p2.y - cy);
        var tl = Math.hypot(tx, ty) || 1;
        var ux = tx / tl;
        var uy = ty / tl;
        var sideJitter = (idx % 2 === 0 ? 1 : -1) * VFX.rand(6, 16) * s;

        VFX.after(baseDelay + idx * 30, function () {
          VFX.burst({
            x: px + nx * sideJitter,
            y: py + ny * sideJitter,
            radius: 10 * s,
            count: 3,
            shape: 'wisp',
            colors: ['#a78bfa', '#67e8f9', '#818cf8', '#c4b5fd'],
            size: [3.2 * s, 6.6 * s],
            speed: [12 * s, 36 * s],
            vx: ux * 115 * s,
            vy: uy * 115 * s,
            turbulence: 115 * s,
            drag: 0.94,
            alpha: 0.72,
            life: [260, 460],
          });

          VFX.burst({
            x: px - nx * sideJitter * 0.6,
            y: py - ny * sideJitter * 0.6,
            radius: 12 * s,
            count: 2,
            shape: 'shard',
            colors: ['#e2e8f0', '#67e8f9', '#a78bfa'],
            size: [2.2 * s, 4.4 * s],
            speed: [18 * s, 52 * s],
            vx: ux * 92 * s,
            vy: uy * 92 * s,
            spin: [-9, 9],
            drag: 0.93,
            life: [240, 420],
            trail: 3,
          });

          VFX.burst({
            x: px,
            y: py,
            radius: 10 * s,
            count: 2,
            shape: 'flare',
            colors: ['#ffffff', '#c4b5fd', '#67e8f9'],
            size: [1.8 * s, 3.6 * s],
            speed: [16 * s, 48 * s],
            drag: 0.92,
            life: [190, 340],
          });
        });
      })(stIdx);
    }
  }

  VFX.register('ability.yueqian', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.allyEls && ctx.allyEls.length) {
      targets = ctx.allyEls.slice();
    }
    if (!targets.length && casterEl) {
      targets = [casterEl];
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var allySide = isEnemyCaster ? 'enemy' : 'ally';
    var s = VFX.scale();
    var casterPt = VFX.clampStage(casterEl ? VFX.center(casterEl) : VFX.sideCenter(allySide), 42 * s, 38 * s);
    var casterFoot = VFX.clampStage(casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(allySide), 42 * s, 26);

    // 轻微压暗凸显紫青虚空星云裂隙，起手星渊坍缩紧贴 casterPt
    VFX.cinematicDim(0.25, 660, '#070414');
    VFX.motion(casterEl, 'cast', { duration: 300 });

    VFX.magicCircle({
      x: casterFoot.x,
      y: casterFoot.y,
      radius: 60,
      tilt: 0.44,
      layer: 'ground',
      style: 'illusion',
      color: '#a78bfa',
      secondaryColor: '#67e8f9',
      rotationSpeed: 2.6,
      lineWidth: 1.5,
      alpha: 0.72,
      duration: 900,
    });

    VFX.burst({
      x: casterPt.x,
      y: casterPt.y,
      radius: 68 * s,
      ring: true,
      count: 14,
      shape: 'wisp',
      colors: ['#7c3aed', '#a78bfa', '#67e8f9', '#e2e8f0'],
      size: [3.8 * s, 7.6 * s],
      speed: [14 * s, 38 * s],
      attractTo: casterPt,
      attractStrength: 1380 * s,
      vortex: 320 * s,
      vortexCenter: casterPt,
      drag: 0.9,
      life: [185, 265],
    });

    VFX.burst({
      x: casterPt.x,
      y: casterPt.y,
      radius: 64 * s,
      ring: true,
      count: 14,
      shape: 'shard',
      colors: ['#a78bfa', '#67e8f9', '#e2e8f0', '#ffffff'],
      size: [2.4 * s, 4.6 * s],
      speed: [14 * s, 38 * s],
      attractTo: casterPt,
      attractStrength: 1620 * s,
      drag: 0.9,
      spin: [-10, 10],
      life: [180, 260],
      trail: 4,
    });

    VFX.burst({
      x: casterPt.x,
      y: casterPt.y,
      radius: 56 * s,
      ring: true,
      count: 6,
      shape: 'flare',
      colors: ['#ffffff', '#c4b5fd', '#67e8f9'],
      size: [1.8 * s, 3.4 * s],
      speed: [12 * s, 30 * s],
      attractTo: casterPt,
      attractStrength: 1500 * s,
      drag: 0.9,
      life: [170, 240],
    });

    var unitCenters = [];
    var list = targets.length ? targets : [null];
    var corridorIdx = 0;

    for (var i = 0; i < list.length; i++) {
      (function (tEl, idx) {
        var rawPt = tEl ? VFX.center(tEl) : VFX.sideCenter(allySide);
        var rawFoot = tEl ? VFX.foot(tEl) : VFX.sideFootCenter(allySide);
        var tPt = VFX.clampStage(rawPt, 42 * s, 38 * s);
        var tFoot = VFX.clampStage(rawFoot, 42 * s, 26);
        unitCenters.push(tPt);

        var isCasterUnit = (tEl && tEl === casterEl) || Math.hypot(tPt.x - casterPt.x, tPt.y - casterPt.y) < 16 * s;
        var startDelay = isCasterUnit ? 0 : (55 + idx * 65);

        // 阶段一：身形幻化闪动 + 各友方星渊 attractTo 空间粒子内缩（施法者已在 t=0 坍缩不重复叠加）
        VFX.after(startDelay, function () {
          if (tEl) {
            VFX.motion(tEl, 'dodge', { duration: 320, distance: 26 });
          }

          if (!isCasterUnit) {
            VFX.magicCircle({
              x: tFoot.x,
              y: tFoot.y,
              radius: 60,
              tilt: 0.44,
              layer: 'ground',
              style: 'illusion',
              color: '#a78bfa',
              secondaryColor: '#67e8f9',
              rotationSpeed: 2.6,
              lineWidth: 1.5,
              alpha: 0.72,
              duration: 900,
            });

            VFX.burst({
              x: tPt.x,
              y: tPt.y,
              radius: 78 * s,
              ring: true,
              count: 12,
              shape: 'wisp',
              colors: ['#7c3aed', '#a78bfa', '#67e8f9', '#e2e8f0'],
              size: [3.8 * s, 7.6 * s],
              speed: [14 * s, 38 * s],
              attractTo: tPt,
              attractStrength: 1380 * s,
              vortex: 320 * s,
              vortexCenter: tPt,
              drag: 0.9,
              life: [185, 265],
            });

            VFX.burst({
              x: tPt.x,
              y: tPt.y,
              radius: 72 * s,
              ring: true,
              count: 12,
              shape: 'shard',
              colors: ['#a78bfa', '#67e8f9', '#e2e8f0', '#ffffff'],
              size: [2.4 * s, 4.6 * s],
              speed: [14 * s, 38 * s],
              attractTo: tPt,
              attractStrength: 1620 * s,
              drag: 0.9,
              spin: [-10, 10],
              life: [180, 260],
              trail: 4,
            });
          }
        });

        // 虚空粒子带按 2D 法线折跃连接 casterPt -> 每位友方中心
        if (!isCasterUnit) {
          var cArc = (corridorIdx % 2 === 0 ? -46 : 46) * s;
          emitFoldCorridor(VFX, casterPt, tPt, cArc, 95 + corridorIdx * 55, s);
          corridorIdx++;
        }

        // 阶段二：落点柔和星云绽放 + 渐变曲边折跃光弧（按 s 缩放并经 clampStage 保护边缘）
        var portalDelay = startDelay + 170;
        VFX.custom({
          delay: portalDelay,
          layer: 'sky',
          blend: 'lighter',
          duration: 640,
          draw: function (c2d, p) {
            var openP = p < 0.24 ? 1 - Math.pow(1 - p / 0.24, 3) : p > 0.74 ? Math.max(0, 1 - (p - 0.74) / 0.26) : 1;
            var fade = p < 0.14 ? p / 0.14 : p > 0.74 ? Math.max(0, (1 - p) / 0.26) : 1;
            var flashBurst = p < 0.32 ? Math.sin((p / 0.32) * Math.PI) : 0;
            var rad = 64 * s * (0.45 + 0.55 * openP);

            c2d.save();
            c2d.translate(tPt.x, tPt.y);

            // 深邃虚空星云核心底晕（纯径向渐变，边缘严格透明归零）
            c2d.save();
            c2d.globalCompositeOperation = 'source-over';
            c2d.scale(0.64, 1.15);
            var voidG = c2d.createRadialGradient(0, 0, 1, 0, 0, rad);
            voidG.addColorStop(0, 'rgba(196, 181, 253,' + (0.42 * fade).toFixed(3) + ')');
            voidG.addColorStop(0.38, 'rgba(76, 29, 149,' + (0.58 * fade).toFixed(3) + ')');
            voidG.addColorStop(0.75, 'rgba(15, 10, 42,' + (0.32 * fade).toFixed(3) + ')');
            voidG.addColorStop(1, 'rgba(7, 4, 20, 0)');
            c2d.fillStyle = voidG;
            c2d.beginPath();
            c2d.arc(0, 0, rad, 0, TWO_PI);
            c2d.fill();
            c2d.restore();

            // 柔羽化星云绽放光晕
            var nebulaG = c2d.createRadialGradient(0, 0, 1, 0, 0, rad * 0.92);
            nebulaG.addColorStop(0, 'rgba(255, 255, 255,' + (0.55 * fade * (0.6 + 0.4 * flashBurst)).toFixed(3) + ')');
            nebulaG.addColorStop(0.28, 'rgba(103, 232, 249,' + (0.44 * fade).toFixed(3) + ')');
            nebulaG.addColorStop(0.62, 'rgba(167, 139, 250,' + (0.22 * fade).toFixed(3) + ')');
            nebulaG.addColorStop(1, 'rgba(167, 139, 250, 0)');
            c2d.fillStyle = nebulaG;
            c2d.beginPath();
            c2d.arc(0, 0, rad * 0.92, 0, TWO_PI);
            c2d.fill();

            // 两条错位交织的渐变空间弯弧（取代硬边椭圆钢笔线框）
            var arcR = rad * 0.88;
            var tiltRot = p * 1.6;
            c2d.save();
            c2d.rotate(tiltRot);
            drawFeatheredCurveArc(c2d, -arcR * 0.55, -arcR * 0.72, -arcR * 0.98, 0, -arcR * 0.45, arcR * 0.76, 5.5 * s * openP, '167, 139, 250', 0.78 * fade);
            drawFeatheredCurveArc(c2d, arcR * 0.55, arcR * 0.72, arcR * 0.98, 0, arcR * 0.45, -arcR * 0.76, 5.0 * s * openP, '103, 232, 249', 0.76 * fade);
            c2d.restore();

            // 中心渐变星芒裂辉（quadraticCurveTo + 两端归零渐变）
            c2d.save();
            c2d.rotate(Math.PI * 0.5);
            drawTaperedCrossRay(c2d, rad * (1.05 + flashBurst * 0.35), (4.2 + flashBurst * 3.8) * s * openP, '196, 181, 253', 0.92 * fade);
            c2d.restore();
            if (flashBurst > 0.04) {
              drawTaperedCrossRay(c2d, (38 + flashBurst * 44) * s * openP, 3.6 * s * flashBurst, '103, 232, 249', 0.88 * flashBurst);
            }

            c2d.restore();
          },
        });

        // 裂隙绽放瞬间：柔和星云气缕 + 震荡涟漪 + 碎晶微星 + 「移形」浮字
        VFX.after(portalDelay + 45, function () {
          VFX.burst({
            x: tPt.x,
            y: tPt.y,
            radius: 14 * s,
            count: 16,
            shape: 'wisp',
            colors: ['#a78bfa', '#67e8f9', '#c4b5fd', '#e0e7ff'],
            size: [4.5 * s, 9.2 * s],
            speed: [45 * s, 130 * s],
            vortex: 240 * s,
            vortexCenter: tPt,
            turbulence: 110 * s,
            drag: 0.93,
            life: [360, 640],
          });

          VFX.burst({
            x: tPt.x,
            y: tPt.y,
            radius: 10 * s,
            count: 14,
            shape: 'shard',
            colors: ['#a78bfa', '#67e8f9', '#e2e8f0', '#ffffff'],
            size: [2.4 * s, 4.8 * s],
            speed: [95 * s, 220 * s],
            drag: 0.91,
            spin: [-10, 10],
            life: [300, 520],
            trail: 4,
          });

          VFX.burst({
            x: tPt.x,
            y: tPt.y,
            radius: 12 * s,
            count: 8,
            shape: 'flare',
            colors: ['#ffffff', '#c4b5fd', '#67e8f9'],
            size: [2.4 * s, 4.6 * s],
            speed: [55 * s, 150 * s],
            drag: 0.91,
            life: [240, 420],
          });

          VFX.shockwave({
            x: tPt.x,
            y: tPt.y,
            fromRadius: 10,
            toRadius: 78,
            tilt: 0.92,
            layer: 'sky',
            color: '#a78bfa',
            coreColor: '#e2e8f0',
            lineWidth: 3.4,
            duration: 360,
          });

          if (tEl) {
            VFX.hitFlash(tEl, '#a78bfa', 260);
          }
          VFX.floatText(tEl || tPt, '移形', '#c4b5fd', { scale: 1.14 });
        });
      })(list[i], i);
    }

    // 单人自跃迁时补足一段斜掠 casterPt 的折叠通道
    if (corridorIdx === 0 && unitCenters.length >= 1) {
      VFX.after(110, function () {
        var p0 = unitCenters[0];
        var fp1 = VFX.clampStage({ x: p0.x - 66 * s, y: p0.y - 26 * s }, 18, 18);
        var fp2 = VFX.clampStage({ x: p0.x + 66 * s, y: p0.y + 18 * s }, 18, 18);
        emitFoldCorridor(VFX, fp1, fp2, -50 * s, 0, s);
      });
    }

    return 1400;
  });
});
