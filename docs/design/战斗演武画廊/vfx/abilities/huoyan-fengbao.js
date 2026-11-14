/* ability.huoyan-fengbao · 火焰风暴 —— 二阶终极炎法 + 燃魂附着主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   敌方全体 / 吟唱 3000ms / 1.0×AP+50 + 5层「燃魂」 / 冷却 18s / 灵力 30 / 五行·火
 * 分镜设计（全粒子流体三维火龙卷）：
 *   1. cinematicDim 压暗天幕，敌阵足心展开暗红焦灼星环（纯径向渐变热池 + 焦土龟裂 + 地表环绕火星气缕）；
 *   2. 15 段螺旋发射高密度赤金/深绯大号 wisp 火焰舌、狂乱 ember 火星雨、暗橙黑滚滚火烟 smoke 与中心炽白 flare 热核爆散，
 *      搭配柔羽化 S 形热轴辉光与渐变火风丝带，构建由窄渐宽、真正三维卷曲升腾的烈焰龙卷；
 *   3. 每名受击敌人周身卷起旋炎气缕与柔光五芒「燃魂」炽印（纯渐变光晕与 rune/flare 爆散），伴随分段重震震撼全场。
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

  function drawFeatheredWindArc(c2d, x0, y0, cx, cy, x1, y1, thickness, colorRgb, alpha) {
    if (alpha <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + colorRgb + ',0)');
    grad.addColorStop(0.28, 'rgba(' + colorRgb + ',' + (alpha * 0.65).toFixed(3) + ')');
    grad.addColorStop(0.55, 'rgba(' + colorRgb + ',' + alpha.toFixed(3) + ')');
    grad.addColorStop(0.82, 'rgba(' + colorRgb + ',' + (alpha * 0.45).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + colorRgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(x0, y0);
    c2d.quadraticCurveTo(cx + nx * thickness, cy + ny * thickness, x1, y1);
    c2d.quadraticCurveTo(cx - nx * thickness * 0.72, cy - ny * thickness * 0.72, x0, y0);
    c2d.fill();
  }

  VFX.register('ability.huoyan-fengbao', function (ctx) {
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

    var padBaseX = Math.min(68 * s, st.w * 0.42);
    var minBaseY = Math.min(st.h * 0.42, Math.max(24, st.h - 18));
    var basePt = {
      x: VFX.clamp(bounds.cx, padBaseX, Math.max(padBaseX, st.w - padBaseX)),
      y: VFX.clamp(bounds.bottomFootY, minBaseY, Math.max(minBaseY, st.h - 18)),
    };
    var stormCenter = { x: bounds.cx, y: bounds.cy };

    var maxTornadoH = Math.max(48, basePt.y - 24);
    var tornadoHeight = VFX.clamp(basePt.y - 24, Math.min(120, maxTornadoH), Math.max(Math.min(120, maxTornadoH), 260 * s));
    var hScale = tornadoHeight / 265;

    var maxPoolCap = Math.max(52, Math.min(132, st.w * 0.26));
    var maxPoolR = VFX.clamp(Math.max(bounds.spanX * 0.48, 64 * s), 52, maxPoolCap);
    var poolRatio = maxPoolR / 132;

    // 阶段一：厚重暗幕 + 施法掌心赤金流炎引燃敌阵地心 + 焦灼星环
    VFX.cinematicDim(0.42, 880, '#0f0302');
    VFX.motion(casterEl, 'levitate', { duration: 540 });

    var palmPt = VFX.clampStage(axis.palm(22 * s), 20 * s, 20 * s);
    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 14 * s,
      count: 10,
      shape: 'wisp',
      colors: ['#fef08a', '#ff8c42', '#ff5722', '#dc2626'],
      size: [4.2 * s, 8.0 * s],
      speed: [35 * s, 95 * s],
      baseAngle: axis.angle,
      spread: 0.75,
      turbulence: 110 * s,
      drag: 0.93,
      life: [220, 380],
    });
    VFX.streamer({
      x1: palmPt.x,
      y1: palmPt.y,
      x2: basePt.x,
      y2: basePt.y - 12 * s,
      arc: -28 * s,
      wobble: 8 * s,
      width: 11 * s,
      tailLength: 0.45,
      duration: 210,
      color: '#ff5722',
      coreColor: '#fef08a',
      sparkColors: ['#ff8c42', '#fde047', '#ffffff'],
    });

    // 地面暗红焦灼星环：纯径向渐变热池 + 环形羽化余烬带 + 五枚柔光星锚（零硬边椭圆/实线）
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 1300,
      draw: function (c2d, p) {
        var open = p < 0.18 ? 1 - Math.pow(1 - p / 0.18, 3) : 1;
        var fade = p < 0.14 ? p / 0.14 : p > 0.74 ? Math.max(0, (1 - p) / 0.26) : 1;
        var R = maxPoolR * 1.06 * (0.55 + 0.45 * open);
        var pulse = 0.85 + 0.15 * Math.sin(p * 18);

        c2d.save();
        c2d.translate(basePt.x, basePt.y);
        c2d.scale(1, 0.44);

        // 地面熔红焦热底光池
        var pool = c2d.createRadialGradient(0, 0, R * 0.05, 0, 0, R * 1.12);
        pool.addColorStop(0, 'rgba(255, 110, 42,' + (0.42 * fade * pulse).toFixed(3) + ')');
        pool.addColorStop(0.45, 'rgba(220, 38, 38,' + (0.24 * fade).toFixed(3) + ')');
        pool.addColorStop(0.78, 'rgba(127, 29, 29,' + (0.12 * fade).toFixed(3) + ')');
        pool.addColorStop(1, 'rgba(69, 10, 10, 0)');
        c2d.fillStyle = pool;
        c2d.beginPath();
        c2d.arc(0, 0, R * 1.12, 0, TWO_PI);
        c2d.fill();

        // 环形羽化焦灼赤金光带（内外边缘严格归零）
        var ringGrad = c2d.createRadialGradient(0, 0, R * 0.56, 0, 0, R * 1.02);
        ringGrad.addColorStop(0, 'rgba(255, 87, 34, 0)');
        ringGrad.addColorStop(0.42, 'rgba(220, 38, 38,' + (0.26 * fade).toFixed(3) + ')');
        ringGrad.addColorStop(0.58, 'rgba(255, 140, 66,' + (0.44 * fade * pulse).toFixed(3) + ')');
        ringGrad.addColorStop(0.74, 'rgba(185, 28, 28,' + (0.18 * fade).toFixed(3) + ')');
        ringGrad.addColorStop(1, 'rgba(127, 29, 29, 0)');
        c2d.fillStyle = ringGrad;
        c2d.beginPath();
        c2d.arc(0, 0, R * 1.02, 0, TWO_PI);
        c2d.fill();

        // 五枚星环炽点与旋转羽化弧焰
        var rot = p * 2.1;
        var starNodeR = 24 * poolRatio;
        for (var i = 0; i < 5; i++) {
          var a0 = rot + (i * TWO_PI) / 5;
          var a1 = a0 + 0.95;
          var aMid = (a0 + a1) * 0.5;
          var rx = Math.cos(a0) * R * 0.84;
          var ry = Math.sin(a0) * R * 0.84;

          var starGrad = c2d.createRadialGradient(rx, ry, 0, rx, ry, starNodeR);
          starGrad.addColorStop(0, 'rgba(254, 240, 138,' + (0.72 * fade).toFixed(3) + ')');
          starGrad.addColorStop(0.4, 'rgba(255, 87, 34,' + (0.32 * fade).toFixed(3) + ')');
          starGrad.addColorStop(1, 'rgba(220, 38, 38, 0)');
          c2d.fillStyle = starGrad;
          c2d.beginPath();
          c2d.arc(rx, ry, starNodeR, 0, TWO_PI);
          c2d.fill();

          drawFeatheredWindArc(
            c2d,
            Math.cos(a0) * R * 0.84,
            Math.sin(a0) * R * 0.84,
            Math.cos(aMid) * R * 0.98,
            Math.sin(aMid) * R * 0.98,
            Math.cos(a1) * R * 0.84,
            Math.sin(a1) * R * 0.84,
            7 * poolRatio,
            '255, 115, 45',
            0.46 * fade
          );
        }

        c2d.restore();
      },
    });

    VFX.groundCrack({
      x: basePt.x,
      y: basePt.y,
      radius: maxPoolR * 0.96,
      rawScale: true,
      branches: 11,
      color: '#ff5722',
      crackColor: '#090302',
      lineWidth: 4.2 * s,
      duration: 1900,
    });

    // 地表星环外缘绕行的余烬气缕
    VFX.burst({
      layer: 'ground',
      x: basePt.x,
      y: basePt.y,
      radius: maxPoolR * 0.88,
      ring: true,
      tiltY: 0.44,
      count: 26,
      shape: 'wisp',
      colors: ['#dc2626', '#ff5722', '#fb923c', '#fde047'],
      size: [5.5 * s, 10.5 * s],
      speed: [28 * s, 72 * s],
      vortex: 320 * poolRatio,
      vortexCenter: basePt,
      drag: 0.95,
      alpha: 0.65,
      life: [460, 780],
    });

    // 阶段二：柔羽化热核心柱晕 + 分段螺旋发射三维火龙卷粒子系统（动态限高防切顶 + 限宽守半场）
    VFX.custom({
      delay: 40,
      layer: 'sky',
      blend: 'lighter',
      duration: 980,
      draw: function (c2d, p) {
        var open = p < 0.2 ? 0.25 + 0.75 * (1 - Math.pow(1 - p / 0.2, 3)) : 1;
        var fade = p < 0.14 ? p / 0.14 : p > 0.72 ? Math.max(0, (1 - p) / 0.28) : 1;
        var H = tornadoHeight * (0.45 + 0.55 * open);
        var spin = p * 14;

        c2d.save();
        c2d.translate(basePt.x, basePt.y);

        // 沿扭曲热轴堆叠 8 团纯径向羽化热核雾球（底部收束、上部渐宽、上下端透明归零）
        var orbs = 8;
        for (var i = 0; i < orbs; i++) {
          var u = (i + 0.5) / orbs;
          var env = Math.sin(u * Math.PI);
          var yy = -u * H;
          var sway = Math.sin(u * 5.8 - spin * 0.45) * (8 + u * 18) * poolRatio;
          var rx = VFX.lerp(maxPoolR * 0.27, maxPoolR * 0.97, Math.pow(u, 0.78)) * open;

          c2d.save();
          c2d.translate(sway, yy);
          c2d.scale(1, 0.72);
          var g = c2d.createRadialGradient(0, 0, 1, 0, 0, rx);
          g.addColorStop(0, 'rgba(254, 240, 138,' + (0.24 * fade * env).toFixed(3) + ')');
          g.addColorStop(0.36, 'rgba(255, 87, 34,' + (0.18 * fade * env).toFixed(3) + ')');
          g.addColorStop(0.72, 'rgba(185, 28, 28,' + (0.07 * fade * env).toFixed(3) + ')');
          g.addColorStop(1, 'rgba(127, 29, 29, 0)');
          c2d.fillStyle = g;
          c2d.beginPath();
          c2d.arc(0, 0, rx, 0, TWO_PI);
          c2d.fill();
          c2d.restore();
        }

        // 5 股渐变火风缠绕曲线丝带（两端 alpha = 0，表现卷曲上升气浪）
        for (var r = 0; r < 5; r++) {
          var flow = ((r / 5) + p * 1.45) % 1;
          var rEnv = Math.sin(flow * Math.PI);
          if (rEnv <= 0.08) continue;
          var yMid = -flow * H;
          var rSpan = VFX.lerp(maxPoolR * 0.24, maxPoolR * 1.02, Math.pow(flow, 0.76)) * open;
          var swayMid = Math.sin(flow * 5.8 - spin * 0.45) * (8 + flow * 18) * poolRatio;
          var angBase = spin * 0.9 + r * 1.35;
          var xA = swayMid + Math.cos(angBase) * rSpan;
          var yA = yMid + Math.sin(angBase) * rSpan * 0.26 + 12 * hScale;
          var xB = swayMid + Math.cos(angBase + 1.9) * rSpan * 1.06;
          var yB = yMid + Math.sin(angBase + 1.9) * rSpan * 0.28 - 22 * hScale;
          var cx = swayMid + Math.cos(angBase + 0.95) * rSpan * 1.28;
          var cy = yMid + Math.sin(angBase + 0.95) * rSpan * 0.34 - 4 * hScale;
          drawFeatheredWindArc(
            c2d,
            xA,
            yA,
            cx,
            cy,
            xB,
            yB,
            (7 + flow * 6) * s,
            r % 2 === 0 ? '255, 115, 42' : '253, 224, 71',
            0.42 * fade * rEnv
          );
        }

        c2d.restore();
      },
    });

    // 15 段螺旋火龙卷粒子喷射：自底向上越旋越宽的三维赤金涡流
    var tornadoSlices = 15;
    for (var sIdx = 0; sIdx < tornadoSlices; sIdx++) {
      (function (slice) {
        var sliceAt = 40 + slice * 52;
        var uTime = slice / (tornadoSlices - 1);

        VFX.after(sliceAt, function () {
          // 3 条交织螺旋火焰臂，每臂覆盖底/中/上 3 段高度以撑起整座火柱
          for (var arm = 0; arm < 3; arm++) {
            for (var band = 0; band < 3; band++) {
              var hFrac = VFX.clamp((band + Math.random() * 0.88) / 3.05, 0.02, 0.95);
              var funnelR = VFX.lerp(maxPoolR * 0.18, maxPoolR, Math.pow(hFrac, 0.74));
              var swayX = Math.sin(uTime * 6.5 - hFrac * 4.2) * (10 + hFrac * 18) * poolRatio;
              var theta = uTime * 11.5 + arm * (TWO_PI / 3) + hFrac * 5.2 + VFX.rand(-0.22, 0.22);

              var px = basePt.x + swayX + Math.cos(theta) * funnelR;
              var py = Math.max(24, basePt.y - hFrac * tornadoHeight + Math.sin(theta) * funnelR * 0.34);
              var swirlSpd = VFX.lerp(135, 285, hFrac) * poolRatio;
              var riseSpd = VFX.lerp(145, 235, hFrac) * hScale;
              var vx = -Math.sin(theta) * swirlSpd + Math.cos(theta) * 28 * poolRatio;
              var vy = Math.cos(theta) * swirlSpd * 0.34 - riseSpd;
              var isFront = Math.sin(theta) >= 0;

              // 1. 高密度大号 wisp 火焰舌（前亮赤金、后暗绯红以拉出立体纵深）
              VFX.burst({
                x: px,
                y: py,
                radius: 8 * s,
                count: 1,
                shape: 'wisp',
                colors: isFront ? ['#fef08a', '#ff8c42', '#ff5722'] : ['#dc2626', '#b91c1c', '#ea580c'],
                size: [(7.0 + hFrac * 3.5) * s, (12.5 + hFrac * 4.5) * s],
                endSize: [(12.0 + hFrac * 5.0) * s, (21.0 + hFrac * 6.0) * s],
                speed: [6 * s, 22 * s],
                vx: vx,
                vy: vy,
                gravity: -115 * hScale,
                turbulence: 195 * poolRatio,
                drag: 0.965,
                alpha: isFront ? [0.58, 0.84] : [0.42, 0.68],
                life: [380, 660],
              });

              // 2. 狂乱 ember 火星雨沿切线向四周卷飞
              if ((slice + band) % 2 === 0) {
                VFX.burst({
                  x: px,
                  y: py,
                  radius: 10 * s,
                  count: 2,
                  shape: 'ember',
                  colors: ['#ffffff', '#fef08a', '#ff7b47', '#ff5722'],
                  size: [2.2 * s, 4.8 * s],
                  speed: [24 * s, 72 * s],
                  vx: vx * 1.12,
                  vy: vy * 1.05 - 24 * hScale,
                  gravity: -95 * hScale,
                  turbulence: 235 * poolRatio,
                  drag: 0.955,
                  life: [420, 760],
                  trail: 4,
                });
              }
            }

            // 3. 外缘滚滚暗橙黑火烟 smoke
            if (slice % 2 === 0) {
              var shFrac = VFX.clamp(0.18 + Math.random() * 0.72, 0.15, 0.92);
              var sR = VFX.lerp(maxPoolR * 0.22, maxPoolR * 1.08, Math.pow(shFrac, 0.74));
              var sTheta = uTime * 9.5 + arm * (TWO_PI / 3) + shFrac * 4.4;
              var sSway = Math.sin(uTime * 6.5 - shFrac * 4.2) * (10 + shFrac * 18) * poolRatio;
              VFX.burst({
                x: basePt.x + sSway + Math.cos(sTheta) * sR,
                y: Math.max(24, basePt.y - shFrac * tornadoHeight + Math.sin(sTheta) * sR * 0.34),
                radius: 14 * s,
                count: 1,
                shape: 'smoke',
                colors: ['#1c0905', '#3b150c', '#5c1d0c'],
                size: [18 * s, 32 * s],
                speed: [10 * s, 32 * s],
                vx: -Math.sin(sTheta) * 95 * poolRatio,
                vy: -105 * hScale,
                gravity: -85 * hScale,
                turbulence: 120 * poolRatio,
                drag: 0.96,
                alpha: [0.32, 0.52],
                life: [520, 880],
              });
            }
          }

          // 4. 中心炽白 flare 热核爆散
          if (slice % 3 === 1) {
            var coreY = Math.max(28, basePt.y - VFX.rand(35 * hScale, tornadoHeight * 0.78));
            VFX.burst({
              x: basePt.x + VFX.rand(-18, 18) * poolRatio,
              y: coreY,
              radius: 16 * s,
              count: 4,
              shape: 'flare',
              colors: ['#ffffff', '#fef08a', '#ff8c42'],
              size: [3.2 * s, 6.2 * s],
              speed: [35 * s, 115 * s],
              gravity: -75 * hScale,
              drag: 0.92,
              life: [220, 420],
            });
          }
        });
      })(sIdx);
    }

    // 分段重震震撼全场
    VFX.after(120, function () {
      VFX.quake('md', 240);
    });
    VFX.after(350, function () {
      VFX.quake('lg', 380);
    });

    // 阶段三：每名敌方受击者身上的单点旋炎与柔光五芒「燃魂」炽印爆发
    var list = targets.length ? targets : [null];
    for (var i = 0; i < list.length; i++) {
      (function (tEl, idx) {
        var tPt = tEl ? VFX.center(tEl) : stormCenter;
        var tFoot = tEl ? VFX.foot(tEl) : basePt;
        var safePt = VFX.clampStage(tPt, 40 * s, 36 * s);
        var safeFoot = VFX.clampStage(tFoot, 34 * s, 20);
        var hitAt = 280 + idx * 110;
        var dmg = Math.round(resolveTargetDamage(ctx, idx, list.length, 132));

        VFX.after(hitAt, function () {
          // 替代受击直立光柱：单点升腾旋炎 wisp 与 ember 涡流
          VFX.burst({
            x: safeFoot.x,
            y: safeFoot.y - 10 * s,
            radius: 34 * s,
            tiltY: 0.45,
            count: 14,
            shape: 'wisp',
            colors: ['#dc2626', '#ff5722', '#fde047'],
            size: [5.5 * s, 10.5 * s],
            speed: [55 * s, 135 * s],
            angle: [-Math.PI * 0.78, -Math.PI * 0.22],
            vortex: 320 * s,
            vortexCenter: safePt,
            gravity: -165 * s,
            turbulence: 140 * s,
            drag: 0.96,
            life: [380, 640],
          });

          VFX.groundCrack({
            x: safeFoot.x,
            y: safeFoot.y,
            radius: 56,
            branches: 5,
            color: '#ff5722',
            crackColor: '#090302',
            lineWidth: 2.6,
            duration: 1300,
          });

          // 柔光五芒「燃魂」炽印绽放（按 s 缩放并保护内距，纯径向渐变光花 + 渐变弧焰）
          VFX.custom({
            layer: 'sky',
            blend: 'lighter',
            duration: 480,
            draw: function (c2d, p) {
              var exp = 1 - Math.pow(1 - p, 2.5);
              var fade = p < 0.2 ? p / 0.2 : 1 - (p - 0.2) / 0.8;
              var R = (22 + exp * 32) * s;
              var nodeR = 12 * s;
              c2d.save();
              c2d.translate(safePt.x, safePt.y);
              c2d.rotate(p * 1.9);

              var coreG = c2d.createRadialGradient(0, 0, 0, 0, 0, R * 0.95);
              coreG.addColorStop(0, 'rgba(254, 240, 138,' + (0.45 * fade).toFixed(3) + ')');
              coreG.addColorStop(0.5, 'rgba(255, 87, 34,' + (0.22 * fade).toFixed(3) + ')');
              coreG.addColorStop(1, 'rgba(220, 38, 38, 0)');
              c2d.fillStyle = coreG;
              c2d.beginPath();
              c2d.arc(0, 0, R * 0.95, 0, TWO_PI);
              c2d.fill();

              for (var v = 0; v < 5; v++) {
                var va = v * (TWO_PI / 5) - Math.PI * 0.5;
                var vb = va + (TWO_PI * 2) / 5;
                var vx = Math.cos(va) * R;
                var vy = Math.sin(va) * R;
                var vbx = Math.cos(vb) * R;
                var vby = Math.sin(vb) * R;

                drawFeatheredWindArc(
                  c2d,
                  vx,
                  vy,
                  (vx + vbx) * 0.2,
                  (vy + vby) * 0.2,
                  vbx,
                  vby,
                  3.8 * s,
                  '255, 115, 42',
                  0.68 * fade
                );

                var nodeG = c2d.createRadialGradient(vx, vy, 0, vx, vy, nodeR);
                nodeG.addColorStop(0, 'rgba(255, 255, 255,' + (0.92 * fade).toFixed(3) + ')');
                nodeG.addColorStop(0.35, 'rgba(254, 240, 138,' + (0.65 * fade).toFixed(3) + ')');
                nodeG.addColorStop(1, 'rgba(255, 87, 34, 0)');
                c2d.fillStyle = nodeG;
                c2d.beginPath();
                c2d.arc(vx, vy, nodeR, 0, TWO_PI);
                c2d.fill();
              }
              c2d.restore();
            },
          });

          VFX.burst({
            x: safePt.x,
            y: safePt.y,
            radius: 26 * s,
            count: 14,
            shape: 'rune',
            colors: ['#ff5722', '#fde047', '#dc2626', '#ffffff'],
            size: [5.0 * s, 8.2 * s],
            speed: [58 * s, 138 * s],
            gravity: -72 * s,
            drag: 0.94,
            life: [500, 840],
          });

          VFX.burst({
            x: safePt.x,
            y: safePt.y,
            radius: 18 * s,
            count: 8,
            shape: 'flare',
            colors: ['#ffffff', '#fef08a', '#ff7b47'],
            size: [2.8 * s, 5.2 * s],
            speed: [45 * s, 145 * s],
            drag: 0.91,
            life: [240, 440],
          });

          VFX.shockwave({
            x: safePt.x,
            y: safePt.y,
            fromRadius: 8,
            toRadius: 78,
            tilt: 0.9,
            layer: 'sky',
            color: '#ff5722',
            coreColor: '#fef08a',
            lineWidth: 4.0,
            duration: 360,
          });

          if (tEl) {
            VFX.impact(tEl, { color: '#ff5722', duration: 260, distance: 16 });
          }
          VFX.floatText(tEl || safePt, '-' + dmg, isCrit ? 'crit' : 'damage', {
            scale: isCrit ? 1.32 : 1.1,
          });
          VFX.after(110, function () {
            VFX.floatText(tEl || safePt, '燃魂 ×5', '#fb923c', { scale: 0.88, rise: 34 });
          });
        });
      })(list[i], i);
    }

    return 1850;
  });
});
