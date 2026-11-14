/* ability.qingping-melody · 清平乐 —— 二阶青木莲音治疗主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   气血最低友方 / 吟唱 500ms / 0.6×AP+20 治疗 / 冷却 30s / 灵力 10 / 五行·木
 * 分镜设计（全粒子柔羽化莲雾音韵，零硬花瓣与直筒光柱）：
 *   1. 施法者身畔浮现纯径向翠金晨露光晕与渐变青丝带，飘起零星翠玉灵雾与花瓣；
 *   2. 两条缠绕飞舞的青翠音韵丝带掠向目标，沿途播撒翠绿鹅黄飘零 petal、orb 甘露微光与 flare 晨露星点；
 *   3. 命中目标后，脚下绽开纯径向羽化玉莲光晕（零实心硬瓣），6 段舒缓螺旋升腾的 wisp 治疗灵雾
 *      裹挟 petal、flare 与 orb 绕体轻舞升空，温和治愈目标并飘出治疗跳字。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  function drawFeatheredSilkArc(c2d, x0, y0, cx, cy, x1, y1, thickness, rgb, alpha) {
    if (alpha <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.28, 'rgba(' + rgb + ',' + (alpha * 0.6).toFixed(3) + ')');
    grad.addColorStop(0.55, 'rgba(' + rgb + ',' + alpha.toFixed(3) + ')');
    grad.addColorStop(0.82, 'rgba(' + rgb + ',' + (alpha * 0.45).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(x0, y0);
    c2d.quadraticCurveTo(cx + nx * thickness, cy + ny * thickness, x1, y1);
    c2d.quadraticCurveTo(cx - nx * thickness * 0.72, cy - ny * thickness * 0.72, x0, y0);
    c2d.fill();
  }

  VFX.register('ability.qingping-melody', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && casterEl) {
      targets = [casterEl];
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var allySide = isEnemyCaster ? 'enemy' : 'ally';
    var targetEl = targets[0] || null;
    var s = VFX.scale();
    var rawCasterPt = casterEl ? VFX.center(casterEl) : VFX.sideCenter(allySide);
    var rawCasterFoot = casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(allySide);
    var rawTargetPt = targetEl ? VFX.center(targetEl) : rawCasterPt;
    var rawTargetFoot = targetEl ? VFX.foot(targetEl) : rawCasterFoot;

    var casterPt = VFX.clampStage(rawCasterPt, 40 * s, 32 * s);
    var casterFoot = VFX.clampStage(rawCasterFoot, 40 * s, 26 * s);
    var targetPt = VFX.clampStage(rawTargetPt, 40 * s, 32 * s);
    var targetFoot = VFX.clampStage(rawTargetFoot, 40 * s, 32 * s);
    var axis = VFX.combatAxis(casterEl, targetEl || targetPt);

    var healVal = Math.round(ctx.heal != null ? ctx.heal : 68);
    var isSelfHeal = Math.hypot(rawTargetPt.x - rawCasterPt.x, rawTargetPt.y - rawCasterPt.y) < 28 * s;

    // 阶段一：施法者身周展开青玉晨露柔韵与音律小阵（内置原语自动乘以 VFX.scale()）
    VFX.motion(casterEl, 'cast', { duration: 420 });

    VFX.magicCircle({
      x: casterFoot.x,
      y: casterFoot.y,
      radius: 52,
      tilt: 0.44,
      layer: 'ground',
      style: 'spirit',
      color: '#34d399',
      secondaryColor: '#fef08a',
      rotationSpeed: 0.85,
      lineWidth: 1.5,
      alpha: 0.72,
      duration: 760,
    });

    // 柔羽化青竹韵律光轮：纯径向玉光核心 + 渐变弧形丝带（按 s 缩放并保护边缘）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 640,
      draw: function (c2d, p) {
        var open = p < 0.22 ? 1 - Math.pow(1 - p / 0.22, 3) : 1;
        var fade = p < 0.2 ? p / 0.2 : 1 - (p - 0.2) / 0.8;
        var rad = (34 + open * 18) * (1 + p * 0.14) * s;
        var rot = p * 1.45;

        c2d.save();
        c2d.translate(casterPt.x, casterPt.y);

        var auraG = c2d.createRadialGradient(0, 0, 1, 0, 0, rad * 1.15);
        auraG.addColorStop(0, 'rgba(254, 240, 138,' + (0.34 * fade).toFixed(3) + ')');
        auraG.addColorStop(0.4, 'rgba(110, 231, 183,' + (0.24 * fade).toFixed(3) + ')');
        auraG.addColorStop(0.75, 'rgba(52, 211, 153,' + (0.1 * fade).toFixed(3) + ')');
        auraG.addColorStop(1, 'rgba(52, 211, 153, 0)');
        c2d.fillStyle = auraG;
        c2d.beginPath();
        c2d.arc(0, 0, rad * 1.15, 0, TWO_PI);
        c2d.fill();

        c2d.rotate(rot);
        for (var i = 0; i < 4; i++) {
          var a0 = (i * TWO_PI) / 4;
          var a1 = a0 + 1.25;
          var aMid = (a0 + a1) * 0.5;
          drawFeatheredSilkArc(
            c2d,
            Math.cos(a0) * rad * 0.78,
            Math.sin(a0) * rad * 0.78,
            Math.cos(aMid) * rad * 1.08,
            Math.sin(aMid) * rad * 1.08,
            Math.cos(a1) * rad * 0.78,
            Math.sin(a1) * rad * 0.78,
            4.2 * s,
            i % 2 === 0 ? '110, 231, 183' : '254, 240, 138',
            0.65 * fade
          );
        }
        c2d.restore();
      },
    });

    VFX.burst({
      x: casterPt.x,
      y: casterPt.y,
      radius: 26 * s,
      count: 10,
      shape: 'wisp',
      colors: ['#34d399', '#6ee7b7', '#fef08a'],
      size: [4.0 * s, 7.6 * s],
      speed: [22 * s, 58 * s],
      vortex: 140 * s,
      vortexCenter: casterPt,
      gravity: -45 * s,
      alpha: 0.65,
      life: [420, 700],
    });

    VFX.burst({
      x: casterPt.x,
      y: casterPt.y,
      radius: 24 * s,
      count: 6,
      shape: 'flare',
      colors: ['#ffffff', '#fef08a', '#6ee7b7'],
      size: [2.0 * s, 3.8 * s],
      speed: [18 * s, 52 * s],
      life: [280, 480],
    });

    // 阶段二：两条沿 axis 2D 法线 (axis.nx, axis.ny) 对称抬手飞向 targetPt 的青翠音韵丝带
    var s1Start = isSelfHeal
      ? VFX.clampStage({ x: casterPt.x - 42 * s, y: casterPt.y + 8 * s }, 24 * s, 24 * s)
      : VFX.clampStage({ x: casterPt.x + axis.nx * 14 * s, y: casterPt.y + axis.ny * 14 * s }, 24 * s, 24 * s);
    var s2Start = isSelfHeal
      ? VFX.clampStage({ x: casterPt.x + 42 * s, y: casterPt.y + 8 * s }, 24 * s, 24 * s)
      : VFX.clampStage({ x: casterPt.x - axis.nx * 14 * s, y: casterPt.y - axis.ny * 14 * s }, 24 * s, 24 * s);

    VFX.streamer({
      delay: 70,
      x1: s1Start.x,
      y1: s1Start.y,
      x2: targetPt.x,
      y2: targetPt.y,
      arc: (isSelfHeal ? -56 : -52) * s,
      wobble: 13 * s,
      width: 8.5 * s,
      tailLength: 0.46,
      duration: 450,
      color: '#34d399',
      coreColor: '#ecfdf5',
      sparkColors: ['#6ee7b7', '#fef08a', '#ffffff'],
    });

    VFX.streamer({
      delay: 95,
      x1: s2Start.x,
      y1: s2Start.y,
      x2: targetPt.x,
      y2: targetPt.y,
      arc: (isSelfHeal ? 56 : 52) * s,
      wobble: 13 * s,
      width: 7.5 * s,
      tailLength: 0.46,
      duration: 460,
      color: '#5be4a8',
      coreColor: '#fef08a',
      sparkColors: ['#34d399', '#a7f3d0', '#ffffff'],
      onArrive: function (hitPt) {
        // 阶段三：命中目标，纯径向羽化玉莲光晕 + 螺旋升腾治疗灵雾 + 飘零莲瓣与晨露星点
        VFX.magicCircle({
          x: targetFoot.x,
          y: targetFoot.y,
          radius: 56,
          tilt: 0.44,
          layer: 'ground',
          style: 'spirit',
          color: '#6ee7b7',
          secondaryColor: '#ffffff',
          rotationSpeed: -0.75,
          lineWidth: 1.5,
          alpha: 0.74,
          duration: 940,
        });

        // 地面纯径向渐变羽化 8 瓣柔光玉莲（按 s 缩放并用 clampStage 保边）
        VFX.custom({
          layer: 'ground',
          blend: 'lighter',
          duration: 940,
          draw: function (c2d, p) {
            var bloom = p < 0.26 ? 1 - Math.pow(1 - p / 0.26, 3) : 1;
            var fade = p < 0.2 ? p / 0.2 : p > 0.72 ? 1 - (p - 0.72) / 0.28 : 1;
            var R = 52 * s * (0.45 + 0.55 * bloom);

            c2d.save();
            c2d.translate(targetFoot.x, targetFoot.y);
            c2d.scale(1, 0.44);
            c2d.rotate(-p * 0.85);

            var baseG = c2d.createRadialGradient(0, 0, 2, 0, 0, R * 1.15);
            baseG.addColorStop(0, 'rgba(254, 240, 138,' + (0.38 * fade).toFixed(3) + ')');
            baseG.addColorStop(0.42, 'rgba(110, 231, 183,' + (0.28 * fade).toFixed(3) + ')');
            baseG.addColorStop(0.8, 'rgba(52, 211, 153,' + (0.1 * fade).toFixed(3) + ')');
            baseG.addColorStop(1, 'rgba(52, 211, 153, 0)');
            c2d.fillStyle = baseG;
            c2d.beginPath();
            c2d.arc(0, 0, R * 1.15, 0, TWO_PI);
            c2d.fill();

            // 八瓣柔雾光晕瓣影（每瓣皆为径向渐变归零光团）
            for (var k = 0; k < 8; k++) {
              var ka = (k * TWO_PI) / 8;
              var px = Math.cos(ka) * R * 0.64;
              var py = Math.sin(ka) * R * 0.64;
              c2d.save();
              c2d.translate(px, py);
              c2d.rotate(ka);
              c2d.scale(1.45, 0.68);
              var pGrad = c2d.createRadialGradient(0, 0, 0, 0, 0, R * 0.38);
              pGrad.addColorStop(0, k % 2 === 0 ? 'rgba(167, 243, 208,' + (0.48 * fade).toFixed(3) + ')' : 'rgba(254, 240, 138,' + (0.42 * fade).toFixed(3) + ')');
              pGrad.addColorStop(0.55, 'rgba(52, 211, 153,' + (0.18 * fade).toFixed(3) + ')');
              pGrad.addColorStop(1, 'rgba(52, 211, 153, 0)');
              c2d.fillStyle = pGrad;
              c2d.beginPath();
              c2d.arc(0, 0, R * 0.38, 0, TWO_PI);
              c2d.fill();
              c2d.restore();
            }
            c2d.restore();
          },
        });

        VFX.shockwave({
          x: targetFoot.x,
          y: targetFoot.y,
          fromRadius: 12,
          toRadius: 74,
          tilt: 0.44,
          layer: 'ground',
          color: '#6ee7b7',
          coreColor: '#ecfdf5',
          lineWidth: 2.8,
          duration: 480,
        });

        // 替代直筒光柱：6 段舒缓盘旋升腾的治疗 wisp 灵雾 + 飘零 petal + 晨露 flare + orb 微光
        var helixSteps = 6;
        for (var hs = 0; hs < helixSteps; hs++) {
          (function (step) {
            VFX.after(step * 78, function () {
              var u = step / (helixSteps - 1);
              for (var arm = 0; arm < 2; arm++) {
                var ang = step * 1.18 + arm * Math.PI;
                var rr = VFX.lerp(24, 42, u) * s;
                var sx = targetFoot.x + Math.cos(ang) * rr;
                var sy = targetFoot.y - (12 + u * 48) * s + Math.sin(ang) * rr * 0.35;
                var vx = -Math.sin(ang) * 72 * s;
                var vy = Math.cos(ang) * 26 * s - 68 * s;

                VFX.burst({
                  x: sx,
                  y: sy,
                  radius: 10 * s,
                  count: 2,
                  shape: 'wisp',
                  colors: ['#34d399', '#6ee7b7', '#a7f3d0', '#fef08a'],
                  size: [4.6 * s, 8.8 * s],
                  endSize: [2.2 * s, 4.5 * s],
                  speed: [6 * s, 18 * s],
                  vx: vx,
                  vy: vy,
                  gravity: -52 * s,
                  turbulence: 62 * s,
                  drag: 0.96,
                  alpha: [0.46, 0.7],
                  life: [520, 860],
                });

                VFX.burst({
                  x: sx,
                  y: sy,
                  radius: 12 * s,
                  count: 2,
                  shape: 'petal',
                  colors: ['#6ee7b7', '#a7f3d0', '#fef08a', '#fde68a'],
                  size: [2.4 * s, 4.4 * s],
                  speed: [10 * s, 26 * s],
                  vx: vx * 0.65,
                  vy: vy * 0.52,
                  gravity: -22 * s,
                  turbulence: 52 * s,
                  drag: 0.96,
                  alpha: [0.62, 0.88],
                  life: [640, 1080],
                });
              }

              // 晨露星点 flare 与甘露微光 orb
              VFX.burst({
                x: targetPt.x,
                y: targetPt.y + (10 - u * 26) * s,
                radius: 26 * s,
                count: 3,
                shape: 'flare',
                colors: ['#ffffff', '#fef08a', '#6ee7b7'],
                size: [2.0 * s, 3.8 * s],
                speed: [16 * s, 48 * s],
                gravity: -58 * s,
                drag: 0.94,
                life: [360, 620],
              });

              VFX.burst({
                x: targetPt.x,
                y: targetPt.y + (14 - u * 20) * s,
                radius: 28 * s,
                count: 3,
                shape: 'orb',
                colors: ['#fef08a', '#ecfdf5', '#6ee7b7'],
                size: [1.8 * s, 3.8 * s],
                speed: [16 * s, 52 * s],
                gravity: -64 * s,
                drag: 0.95,
                life: [460, 780],
                trail: 4,
              });
            });
          })(hs);
        }

        if (targetEl) {
          VFX.hitFlash(targetEl, '#6ee7b7', 260);
        }
        VFX.floatText(targetEl || hitPt, '+' + healVal, 'heal');
      },
    });

    // 丝带飞舞中途沿途按 2D 法线播撒翠绿鹅黄花瓣、轻雾与晨露微光
    var midSamples = [0.28, 0.48, 0.68, 0.84];
    for (var m = 0; m < midSamples.length; m++) {
      (function (frac, delayMs, idx) {
        VFX.after(delayMs, function () {
          var sideOff = (idx % 2 === 0 ? -16 : 16) * s;
          var mx = VFX.lerp(casterPt.x, targetPt.x, frac) + axis.nx * sideOff;
          var my = VFX.lerp(casterPt.y, targetPt.y, frac) + axis.ny * sideOff;
          VFX.burst({
            x: mx,
            y: my,
            radius: 22 * s,
            count: 5,
            shape: 'petal',
            colors: ['#34d399', '#6ee7b7', '#fef08a', '#fde68a'],
            size: [2.4 * s, 4.4 * s],
            speed: [14 * s, 42 * s],
            gravity: -24 * s,
            turbulence: 45 * s,
            drag: 0.95,
            life: [500, 840],
          });
          VFX.burst({
            x: mx,
            y: my,
            radius: 18 * s,
            count: 4,
            shape: 'wisp',
            colors: ['#6ee7b7', '#fef08a'],
            size: [3.4 * s, 6.2 * s],
            speed: [14 * s, 40 * s],
            gravity: -32 * s,
            alpha: 0.5,
            life: [380, 620],
          });
          VFX.burst({
            x: mx,
            y: my,
            radius: 18 * s,
            count: 4,
            shape: 'orb',
            colors: ['#fef08a', '#ffffff', '#a7f3d0'],
            size: [1.6 * s, 3.2 * s],
            speed: [14 * s, 40 * s],
            life: [380, 640],
          });
        });
      })(midSamples[m], 130 + m * 75, m);
    }

    return 1500;
  });
});
