/**
 * 《道化太初》Canvas 2D v2.3.0 · 二/三阶被动内功心法周天护体特效组（5 门）
 * 真源镜像：config/game-data/abilities/internal_abilities.json
 *
 * 收录：
 *   6.  ability.fenxue-gong        · 焚血功（二阶）—— 猩红血煞 blood 阵 + 暗红鎏金血焰冲霄 + 燃血 ember + 双血环 + 红闪
 *   7.  ability.xiaozhoutian-gong  · 小周天功（二阶）—— 玄金八卦护阵 + 任督二脉金青灵流与尾迹光珠 + 周天星罡柔晕
 *   8.  ability.cunshen-jue        · 存神诀（二阶）—— 紫府顶门星青紫微识海光轮与神识法眼柔弧 + 多层神念水波 + 灵纹
 *   9.  ability.lianjing-huaqi-jue · 炼精化气诀（二阶）—— 下丹田暖赤金 -> 胸腹翠青 -> 顶门紫白三段流体螺旋演化
 *  10.  ability.xuanyuan-yiqi-gong · 玄元一气功（三阶）—— 鎏金 bagua+spirit 双阵 + 六色灵流六方归真 + 纯白鎏金护体华盖与瑞彩星尘
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var HALF_PI = Math.PI * 0.5;
  var localGlowCache = Object.create(null);

  /**
   * 十六进制颜色转 RGB 元组字符串
   */
  function colorToRgb(hex) {
    if (typeof hex !== 'string' || hex.charAt(0) !== '#') return '255,255,255';
    var h = hex.slice(1);
    if (h.length === 3) {
      h = h.charAt(0) + h.charAt(0) + h.charAt(1) + h.charAt(1) + h.charAt(2) + h.charAt(2);
    }
    if (h.length < 6) return '255,255,255';
    var r = parseInt(h.slice(0, 2), 16);
    var g = parseInt(h.slice(2, 4), 16);
    var b = parseInt(h.slice(4, 6), 16);
    return (isNaN(r) ? 255 : r) + ',' + (isNaN(g) ? 255 : g) + ',' + (isNaN(b) ? 255 : b);
  }

  /**
   * 获取柔羽化辉光精灵（边缘 alpha 严格归零）
   */
  function getSoftGlow(color) {
    if (typeof VFX.getGlowSprite === 'function') return VFX.getGlowSprite(color);
    if (localGlowCache[color]) return localGlowCache[color];
    if (typeof document === 'undefined') return null;

    var c = document.createElement('canvas');
    c.width = 96;
    c.height = 96;
    var gctx = c.getContext('2d');
    var rgb = colorToRgb(color);
    var grad = gctx.createRadialGradient(48, 48, 0, 48, 48, 45);
    grad.addColorStop(0, 'rgba(255,255,255,0.96)');
    grad.addColorStop(0.16, 'rgba(' + rgb + ',0.78)');
    grad.addColorStop(0.45, 'rgba(' + rgb + ',0.3)');
    grad.addColorStop(0.76, 'rgba(' + rgb + ',0.07)');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    gctx.fillStyle = grad;
    gctx.beginPath();
    gctx.arc(48, 48, 46, 0, TWO_PI);
    gctx.fill();
    localGlowCache[color] = c;
    return c;
  }

  /**
   * 绘制两端线性渐隐的羽化丝带曲弧（无硬描边）
   */
  function drawFeatheredSilkArc(g, x0, y0, cx, cy, x1, y1, thickness, rgb, alpha) {
    if (alpha <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = g.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.26, 'rgba(' + rgb + ',' + (alpha * 0.58).toFixed(3) + ')');
    grad.addColorStop(0.52, 'rgba(' + rgb + ',' + alpha.toFixed(3) + ')');
    grad.addColorStop(0.8, 'rgba(' + rgb + ',' + (alpha * 0.45).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    g.fillStyle = grad;
    g.beginPath();
    g.moveTo(x0, y0);
    g.quadraticCurveTo(cx + nx * thickness, cy + ny * thickness, x1, y1);
    g.quadraticCurveTo(cx - nx * thickness * 0.72, cy - ny * thickness * 0.72, x0, y0);
    g.fill();
  }

  /**
   * 绘制分段渐隐柔光弧段（支持任意角度与椭圆半径）
   */
  function drawSoftArcBand(g, rx, ry, a0, a1, width, rgb, alpha, segs) {
    if (alpha <= 0.01) return;
    var steps = segs || 16;
    g.lineCap = 'round';
    for (var i = 0; i < steps; i++) {
      var u0 = i / steps;
      var u1 = (i + 1) / steps;
      var env = Math.sin(((u0 + u1) * 0.5) * Math.PI);
      if (env <= 0.04) continue;
      var ang0 = VFX.lerp(a0, a1, u0);
      var ang1 = VFX.lerp(a0, a1, u1);
      g.beginPath();
      g.moveTo(Math.cos(ang0) * rx, Math.sin(ang0) * ry);
      g.lineTo(Math.cos(ang1) * rx, Math.sin(ang1) * ry);

      g.strokeStyle = 'rgba(' + rgb + ',' + (alpha * 0.24 * env).toFixed(3) + ')';
      g.lineWidth = width * 2.2 * env;
      g.stroke();

      g.strokeStyle = 'rgba(' + rgb + ',' + (alpha * 0.78 * env).toFixed(3) + ')';
      g.lineWidth = width * 0.9 * env;
      g.stroke();
    }
  }

  /**
   * 统一解析心法受持者与安全护边锚点（兼容任意阵营、横竖屏及空节点回退）
   */
  function resolveInternalAnchors(ctx) {
    ctx = ctx || {};
    var targetEl = ctx.casterEl || (ctx.targetEls && ctx.targetEls[0]) || ctx.targetEl || null;
    var s = VFX.scale();
    var isEnemy = targetEl
      ? !!(targetEl.classList && (targetEl.classList.contains('is-enemy') || targetEl.classList.contains('arena-actor--enemy')))
      : ctx.side === 'enemy';
    var side = isEnemy ? 'enemy' : 'ally';

    var rawC = targetEl ? VFX.center(targetEl) : VFX.sideCenter(side);
    var rawF = targetEl ? VFX.foot(targetEl) : VFX.sideFootCenter(side);
    var rawH = targetEl ? VFX.head(targetEl) : { x: rawC.x, y: rawC.y - 38 * s };

    var padX = 44 * s;
    var padY = 32 * s;
    var c = VFX.clampStage(rawC, padX, padY);
    var f = VFX.clampStage(rawF, padX, Math.max(24 * s, padY * 0.85));
    var h = VFX.clampStage({ x: rawH.x, y: Math.max(44, rawH.y) }, padX, 40);
    var dantian = VFX.clampStage({ x: c.x, y: VFX.lerp(c.y, f.y, 0.34) }, padX, padY);
    var chest = VFX.clampStage({ x: c.x, y: VFX.lerp(c.y, h.y, 0.22) }, padX, padY);
    var crown = VFX.clampStage({ x: h.x, y: Math.max(30, h.y - 12 * s) }, padX, 28);

    return {
      targetEl: targetEl,
      isEnemy: isEnemy,
      s: s,
      c: c,
      f: f,
      h: h,
      dantian: dantian,
      chest: chest,
      crown: crown,
    };
  }

  // ===========================================================================
  // 6. ability.fenxue-gong · 焚血功（二阶）
  //    足底血煞 blood 阵 + 狂暴暗红鎏金血焰 wisp 冲霄 + 赤黑 ember + 双震荡血环 + 红闪
  // ===========================================================================
  VFX.register('ability.fenxue-gong', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;

    VFX.cinematicDim(0.16, 620, '#140305');
    if (targetEl) VFX.motion(targetEl, 'slam', { dir: 0, duration: 340 });

    // 足底暗红血煞底晕与猩红 blood 阵裂开
    VFX.custom({
      layer: 'ground',
      blend: 'source-over',
      duration: 1100,
      draw: function (g, p) {
        var fade = p < 0.14 ? p / 0.14 : p > 0.7 ? (1 - p) / 0.3 : 1;
        if (fade <= 0.01) return;
        var r = 90 * s * (p < 0.18 ? VFX.lerp(0.5, 1, p / 0.18) : 1);
        g.save();
        g.translate(f.x, f.y + 2 * s);
        g.scale(1, 0.44);
        var grad = g.createRadialGradient(0, 0, r * 0.08, 0, 0, r);
        grad.addColorStop(0, 'rgba(12,5,7,' + (0.76 * fade).toFixed(3) + ')');
        grad.addColorStop(0.45, 'rgba(127,29,29,' + (0.48 * fade).toFixed(3) + ')');
        grad.addColorStop(0.78, 'rgba(220,38,38,' + (0.2 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(12,5,7,0)');
        g.fillStyle = grad;
        g.beginPath();
        g.arc(0, 0, r, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });

    VFX.magicCircle({
      x: f.x,
      y: f.y + 2 * s,
      radius: 74,
      tilt: 0.44,
      layer: 'ground',
      style: 'blood',
      color: '#dc2626',
      secondaryColor: '#f59e0b',
      rotationSpeed: -1.5,
      lineWidth: 2.0,
      alpha: 0.8,
      duration: 1100,
    });

    VFX.groundCrack({
      x: f.x,
      y: f.y + 2 * s,
      radius: 66,
      branches: 6,
      color: '#dc2626',
      crackColor: '#0c0507',
      lineWidth: 2.4,
      duration: 1080,
    });

    // 90ms 燃血献祭爆发：红闪 + 双震荡血环 + 狂暴血焰螺旋冲霄 + 赤黑燃血余烬
    VFX.after(90, function () {
      VFX.quake('sm', 220);
      if (targetEl) VFX.hitFlash(targetEl, '#ef4444', 200);

      // 双层震荡血环 shockwave
      VFX.shockwave({
        x: c.x,
        y: c.y,
        fromRadius: 12,
        toRadius: 118,
        color: '#ef4444',
        coreColor: '#fbbf24',
        lineWidth: 4.0,
        duration: 420,
        double: false,
      });

      VFX.shockwave({
        delay: 75,
        x: f.x,
        y: f.y + 2 * s,
        tilt: 0.44,
        layer: 'ground',
        fromRadius: 16,
        toRadius: 146,
        color: '#991b1b',
        coreColor: '#fca5a5',
        lineWidth: 3.8,
        duration: 460,
        double: false,
      });

      // 狂暴暗红鎏金血焰双股羽弧螺旋飙升
      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 760,
        draw: function (g, p) {
          var fade = p < 0.14 ? p / 0.14 : p > 0.68 ? (1 - p) / 0.32 : 1;
          if (fade <= 0.01) return;
          var sp = getSoftGlow('#ef4444');
          g.save();
          for (var arm = 0; arm < 2; arm++) {
            var dir = arm === 0 ? 1 : -1;
            var rx = (34 + (1 - p) * 10) * s;
            var yHead = VFX.lerp(f.y, Math.max(24, h.y - 24 * s), Math.min(1, p * 1.15));
            var yTail = VFX.lerp(f.y, c.y, p);
            var angHead = p * 7.8 * dir + arm * Math.PI;
            var angMid = angHead - 0.9 * dir;
            var angTail = angHead - 1.8 * dir;
            var x0 = c.x + Math.cos(angTail) * rx;
            var y0 = yTail;
            var cxPt = c.x + Math.cos(angMid) * rx * 1.18;
            var cyPt = (yTail + yHead) * 0.5;
            var x1 = c.x + Math.cos(angHead) * rx * 0.84;
            var y1 = yHead;

            drawFeatheredSilkArc(g, x0, y0, cxPt, cyPt, x1, y1, 5.2 * s, '220,38,38', 0.66 * fade);
            drawFeatheredSilkArc(g, x0, y0, cxPt, cyPt, x1, y1, 2.2 * s, '251,191,36', 0.62 * fade);
            if (sp) {
              var gr = 12 * s;
              g.globalAlpha = 0.72 * fade;
              g.drawImage(sp, x1 - gr, y1 - gr, gr * 2, gr * 2);
            }
          }
          g.restore();
        },
      });

      // 三段狂暴暗红鎏金血焰 wisp 螺旋冲霄而起
      for (var wave = 0; wave < 3; wave++) {
        (function (wIdx, spinDir) {
          VFX.burst({
            delay: wIdx * 95,
            x: f.x,
            y: f.y - 6 * s,
            radius: 30 * s,
            ring: true,
            tiltY: 0.44,
            count: 16,
            shape: 'wisp',
            colors: ['#991b1b', '#dc2626', '#ef4444', '#f59e0b', '#fef08a'],
            size: [4.6 * s, 9.4 * s],
            angle: [-Math.PI * 0.65, -Math.PI * 0.35],
            speed: [110 * s, 225 * s],
            vortex: 275 * s * spinDir,
            vortexCenter: c,
            gravity: -165 * s,
            turbulence: 86 * s,
            drag: 0.95,
            alpha: 0.82,
            life: [440, 760],
            trail: 4,
          });
        })(wave, wave % 2 === 0 ? 1 : -1);
      }

      // 飞舞赤黑燃血 ember + 血煞暗烟 + 烈血星芒
      VFX.burst({
        x: c.x,
        y: c.y + 8 * s,
        radius: 34 * s,
        count: 26,
        shape: 'ember',
        colors: ['#ef4444', '#f59e0b', '#991b1b', '#fef08a', '#1c1917'],
        size: [2.4 * s, 4.8 * s],
        angle: [-Math.PI * 0.85, -Math.PI * 0.15],
        speed: [75 * s, 210 * s],
        vortex: 185 * s,
        vortexCenter: c,
        gravity: -75 * s,
        turbulence: 74 * s,
        drag: 0.95,
        life: [480, 820],
        trail: 4,
      });

      VFX.burst({
        x: f.x,
        y: f.y,
        radius: 30 * s,
        ring: true,
        tiltY: 0.44,
        count: 12,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#1c1917', '#450a0a', '#7f1d1d'],
        size: [12 * s, 22 * s],
        speed: [40 * s, 110 * s],
        gravity: -45 * s,
        alpha: 0.48,
        life: [420, 740],
      });

      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 26 * s,
        count: 8,
        shape: 'flare',
        colors: ['#fef08a', '#f87171', '#ffffff'],
        size: [2.6 * s, 4.8 * s],
        speed: [45 * s, 130 * s],
        spin: [-4, 4],
        life: [320, 540],
      });
    });

    VFX.after(620, function () {
      VFX.floatText(targetEl || h, '焚血功', '#ef4444', { scale: 0.94, duration: 880 });
    });

    return 1160;
  });

  // ===========================================================================
  // 7. ability.xiaozhoutian-gong · 小周天功（二阶）
  //    玄金八卦 bagua 护阵 + 任督二脉金青灵流与尾迹光珠循环流转 + 周天星罡柔晕
  // ===========================================================================
  VFX.register('ability.xiaozhoutian-gong', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;
    var dantian = a.dantian;
    var crown = a.crown;

    VFX.cinematicDim(0.14, 620, '#040a10');
    if (targetEl) VFX.motion(targetEl, 'cast', { duration: 420 });

    // 玄金八卦护阵铺地与星罡底晕
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 1140,
      draw: function (g, p) {
        var fade = p < 0.16 ? p / 0.16 : p > 0.72 ? (1 - p) / 0.28 : 1;
        if (fade <= 0.01) return;
        var r = 82 * s * (0.7 + 0.3 * Math.min(1, p * 3));
        g.save();
        g.translate(f.x, f.y + 2 * s);
        g.scale(1, 0.44);
        var grad = g.createRadialGradient(0, 0, r * 0.08, 0, 0, r);
        grad.addColorStop(0, 'rgba(254,240,138,' + (0.34 * fade).toFixed(3) + ')');
        grad.addColorStop(0.45, 'rgba(94,234,212,' + (0.2 * fade).toFixed(3) + ')');
        grad.addColorStop(0.8, 'rgba(245,196,81,' + (0.09 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(245,196,81,0)');
        g.fillStyle = grad;
        g.beginPath();
        g.arc(0, 0, r, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });

    VFX.magicCircle({
      x: f.x,
      y: f.y + 2 * s,
      radius: 74,
      tilt: 0.44,
      layer: 'ground',
      style: 'bagua',
      color: '#f5c451',
      secondaryColor: '#5eead4',
      rotationSpeed: 0.92,
      lineWidth: 1.9,
      alpha: 0.78,
      duration: 1140,
    });

    // 70ms 任督二脉周天环轨流转 + 外罩温润不破的周天星罡柔晕
    VFX.after(70, function () {
      if (targetEl) VFX.hitFlash(targetEl, '#fde68a', 240);

      var midY = (dantian.y + crown.y) * 0.5;
      var rxOrbit = 28 * s;
      var ryOrbit = Math.max(32 * s, (dantian.y - crown.y) * 0.5);
      var spGold = getSoftGlow('#f5c451');
      var spCyan = getSoftGlow('#5eead4');

      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 1050,
        draw: function (g, p) {
          var open = p < 0.18 ? 1 - Math.pow(1 - p / 0.18, 3) : 1;
          var fade = p < 0.14 ? p / 0.14 : p > 0.74 ? Math.pow(1 - (p - 0.74) / 0.26, 1.25) : 1;
          if (fade <= 0.01) return;

          g.save();
          g.translate(c.x, midY);

          // 1. 外罩温润不破的周天星罡柔晕
          var auraR = Math.max(58 * s, ryOrbit * 1.28) * (0.78 + 0.22 * open);
          var auraGrad = g.createRadialGradient(0, 0, auraR * 0.12, 0, 0, auraR * 1.15);
          auraGrad.addColorStop(0, 'rgba(254,240,138,' + (0.2 * fade).toFixed(3) + ')');
          auraGrad.addColorStop(0.48, 'rgba(94,234,212,' + (0.16 * fade).toFixed(3) + ')');
          auraGrad.addColorStop(0.8, 'rgba(245,196,81,' + (0.09 * fade).toFixed(3) + ')');
          auraGrad.addColorStop(1, 'rgba(245,196,81,0)');
          g.fillStyle = auraGrad;
          g.beginPath();
          g.arc(0, 0, auraR * 1.15, 0, TWO_PI);
          g.fill();

          // 外圈缓缓旋转的四段周天星罡羽化护弧（零硬线整圆）
          g.save();
          g.rotate(p * 1.1);
          for (var gArc = 0; gArc < 4; gArc++) {
            var aBase = (gArc * TWO_PI) / 4;
            drawSoftArcBand(
              g,
              auraR * 0.88,
              auraR * 0.94,
              aBase,
              aBase + 1.05,
              2.6 * s,
              gArc % 2 === 0 ? '245,196,81' : '94,234,212',
              0.55 * fade,
              12
            );
          }
          g.restore();

          // 2. 沿身躯中轴作任督二脉周天环轨：前降青玉弧 + 后升玄金弧
          var rx = rxOrbit * open;
          var ry = ryOrbit * open;
          drawSoftArcBand(g, rx, ry, -HALF_PI, HALF_PI, 3.0 * s, '94,234,212', 0.68 * fade, 16);
          drawSoftArcBand(g, rx, ry, HALF_PI, Math.PI * 1.5, 3.0 * s, '245,196,81', 0.72 * fade, 16);

          // 3. 金青双色周天灵珠携 9 节渐隐尾迹光珠沿任督二脉循环流转 2.25 周
          var orbitBase = p * TWO_PI * 2.25 - HALF_PI;
          for (var orb = 0; orb < 2; orb++) {
            var headAng = orbitBase + orb * Math.PI;
            var sp = orb === 0 ? spGold : spCyan;
            for (var tIdx = 0; tIdx < 10; tIdx++) {
              var frac = 1 - tIdx / 10;
              var ang = headAng - tIdx * 0.13;
              var ox = Math.cos(ang) * rx;
              var oy = Math.sin(ang) * ry;
              var rGlow = (tIdx === 0 ? 11.5 : 8.2 * frac) * s;
              if (sp) {
                g.globalAlpha = fade * (tIdx === 0 ? 0.96 : 0.44 * frac * frac);
                g.drawImage(sp, ox - rGlow, oy - rGlow, rGlow * 2, rGlow * 2);
              } else {
                var og = g.createRadialGradient(ox, oy, 0, ox, oy, rGlow);
                og.addColorStop(0, 'rgba(255,255,255,' + (0.85 * fade * frac).toFixed(3) + ')');
                og.addColorStop(1, 'rgba(245,196,81,0)');
                g.fillStyle = og;
                g.beginPath();
                g.arc(ox, oy, rGlow, 0, TWO_PI);
                g.fill();
              }
            }
          }

          // 百会（上极）与丹田（下极）脉窍微辉
          var poles = [-ry, ry];
          for (var pl = 0; pl < poles.length; pl++) {
            var pGlow = pl === 0 ? spCyan : spGold;
            if (pGlow) {
              var pr = 12 * s * (0.85 + 0.2 * Math.sin(p * 10 + pl * Math.PI));
              g.globalAlpha = 0.56 * fade;
              g.drawImage(pGlow, -pr, poles[pl] - pr, pr * 2, pr * 2);
            }
          }

          g.restore();
        },
      });

      // 周天灵息粒子随环轨轻缓护体
      for (var step = 0; step < 3; step++) {
        (function (idx) {
          VFX.after(idx * 160, function () {
            VFX.burst({
              x: c.x,
              y: c.y,
              radius: 40 * s,
              ring: true,
              tiltY: 0.72,
              count: 8,
              shape: 'wisp',
              colors: ['#f5c451', '#5eead4', '#fef08a', '#99f6e4'],
              size: [3.8 * s, 7.2 * s],
              speed: [14 * s, 32 * s],
              vortex: 185 * s * (idx % 2 === 0 ? 1 : -1),
              vortexCenter: c,
              gravity: -34 * s,
              drag: 0.96,
              alpha: 0.62,
              life: [520, 820],
              trail: 3,
            });

            VFX.burst({
              x: c.x,
              y: midY,
              radius: 36 * s,
              count: 5,
              shape: 'orb',
              colors: ['#fef08a', '#99f6e4', '#ffffff'],
              size: [1.6 * s, 3.2 * s],
              speed: [16 * s, 42 * s],
              vortex: 160 * s,
              vortexCenter: c,
              drag: 0.95,
              life: [460, 740],
              trail: 4,
            });
          });
        })(step);
      }

      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 46 * s,
        ring: true,
        count: 6,
        shape: 'flare',
        colors: ['#fef08a', '#99f6e4', '#ffffff'],
        size: [2.2 * s, 3.8 * s],
        speed: [14 * s, 42 * s],
        life: [420, 680],
      });
    });

    VFX.after(680, function () {
      VFX.floatText(targetEl || h, '小周天功', '#f5c451', { scale: 0.94, duration: 900 });
    });

    return 1200;
  });

  // ===========================================================================
  // 8. ability.cunshen-jue · 存神诀（二阶）
  //    紫府顶门星青紫微识海光轮 + 渐变神识法眼柔弧 + 多层神念水波 + flare/rune 观想
  // ===========================================================================
  VFX.register('ability.cunshen-jue', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;

    var zifuPt = VFX.clampStage({ x: h.x, y: Math.max(36, h.y - 16 * s) }, 44 * s, 36);

    VFX.cinematicDim(0.15, 620, '#070417');
    if (targetEl) VFX.motion(targetEl, 'cast', { duration: 420 });

    // 脚底紫微星青灵阵轻启
    VFX.magicCircle({
      x: f.x,
      y: f.y + 2 * s,
      radius: 64,
      tilt: 0.44,
      layer: 'ground',
      style: 'spirit',
      color: '#a78bfa',
      secondaryColor: '#67e8f9',
      rotationSpeed: -0.78,
      lineWidth: 1.6,
      alpha: 0.64,
      duration: 1100,
    });

    // 80ms 紫府顶门升起星青紫微识海光轮与渐变神识法眼柔弧
    VFX.after(80, function () {
      if (targetEl) VFX.hitFlash(targetEl, '#c4b5fd', 240);

      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 980,
        draw: function (g, p) {
          var open = p < 0.24 ? 1 - Math.pow(1 - p / 0.24, 3) : 1;
          var fade = p < 0.14 ? p / 0.14 : p > 0.72 ? Math.pow(1 - (p - 0.72) / 0.28, 1.25) : 1;
          if (fade <= 0.01) return;

          var wheelR = 46 * s * (0.65 + 0.35 * open);
          var eyeOpen = p < 0.26 ? p / 0.26 : p > 0.8 ? (1 - p) / 0.2 : 1;
          var eyeW = 28 * s * open;
          var eyeH = 14 * s * Math.max(0.15, eyeOpen);
          var spPupil = getSoftGlow('#67e8f9');

          g.save();
          g.translate(zifuPt.x, zifuPt.y);

          // 识海纯径向星青紫微柔晕
          var seaG = g.createRadialGradient(0, 0, 2, 0, 0, wheelR * 1.25);
          seaG.addColorStop(0, 'rgba(224,231,255,' + (0.48 * fade).toFixed(3) + ')');
          seaG.addColorStop(0.38, 'rgba(103,232,249,' + (0.28 * fade).toFixed(3) + ')');
          seaG.addColorStop(0.72, 'rgba(167,139,250,' + (0.16 * fade).toFixed(3) + ')');
          seaG.addColorStop(1, 'rgba(167,139,250,0)');
          g.fillStyle = seaG;
          g.beginPath();
          g.arc(0, 0, wheelR * 1.25, 0, TWO_PI);
          g.fill();

          // 外内双层旋转星青紫微识海光轮弧段
          g.save();
          g.rotate(p * 1.25);
          for (var w1 = 0; w1 < 6; w1++) {
            var a1 = (w1 * TWO_PI) / 6;
            drawSoftArcBand(g, wheelR, wheelR, a1, a1 + 0.68, 2.2 * s, w1 % 2 === 0 ? '167,139,250' : '103,232,249', 0.72 * fade, 10);
          }
          g.restore();

          g.save();
          g.rotate(-p * 1.65);
          for (var w2 = 0; w2 < 4; w2++) {
            var a2 = (w2 * TWO_PI) / 4;
            drawSoftArcBand(g, wheelR * 0.72, wheelR * 0.72, a2, a2 + 0.95, 1.8 * s, '196,181,253', 0.62 * fade, 10);
          }
          g.restore();

          // 神识法眼柔弧（上下双渐变眼睑弧 + 澄明紫微星瞳）
          drawFeatheredSilkArc(g, -eyeW, 0, 0, -eyeH * 1.35, eyeW, 0, 3.2 * s, '103,232,249', 0.85 * fade);
          drawFeatheredSilkArc(g, -eyeW, 0, 0, eyeH * 1.35, eyeW, 0, 3.2 * s, '167,139,250', 0.85 * fade);

          // 法眼瞳仁灵光
          var pupilR = 8.5 * s * (0.85 + 0.15 * Math.sin(p * 12));
          var pG = g.createRadialGradient(0, 0, 0, 0, 0, pupilR * 1.4);
          pG.addColorStop(0, 'rgba(255,255,255,' + (0.95 * fade).toFixed(3) + ')');
          pG.addColorStop(0.38, 'rgba(103,232,249,' + (0.76 * fade).toFixed(3) + ')');
          pG.addColorStop(0.75, 'rgba(167,139,250,' + (0.28 * fade).toFixed(3) + ')');
          pG.addColorStop(1, 'rgba(167,139,250,0)');
          g.fillStyle = pG;
          g.beginPath();
          g.arc(0, 0, pupilR * 1.4, 0, TWO_PI);
          g.fill();

          if (spPupil) {
            var sr = 10 * s;
            g.globalAlpha = 0.78 * fade;
            g.drawImage(spPupil, -sr, -sr, sr * 2, sr * 2);
          }

          g.restore();
        },
      });

      // 多层澄明神念水波向四周扩散
      var waveDelays = [20, 160, 310];
      var waveColors = ['#67e8f9', '#a78bfa', '#c4b5fd'];
      for (var wd = 0; wd < waveDelays.length; wd++) {
        VFX.shockwave({
          delay: waveDelays[wd],
          x: zifuPt.x,
          y: zifuPt.y,
          fromRadius: 10,
          toRadius: 102 + wd * 28,
          color: waveColors[wd],
          coreColor: '#ffffff',
          lineWidth: 2.8,
          double: false,
          duration: 480 + wd * 40,
        });
      }

      // 星芒 flare 与玄奥 rune 环绕观想
      for (var rStep = 0; rStep < 2; rStep++) {
        (function (idx) {
          VFX.after(idx * 180, function () {
            VFX.burst({
              x: zifuPt.x,
              y: zifuPt.y + 12 * s,
              radius: 46 * s,
              ring: true,
              tiltY: 0.62,
              count: 7,
              shape: 'rune',
              colors: ['#c4b5fd', '#67e8f9', '#e0e7ff'],
              size: [3.4 * s, 5.4 * s],
              speed: [10 * s, 26 * s],
              vortex: 140 * s * (idx === 0 ? 1 : -1),
              vortexCenter: zifuPt,
              gravity: -28 * s,
              spin: [-2.4, 2.4],
              drag: 0.96,
              alpha: 0.9,
              life: [580, 880],
            });

            VFX.burst({
              x: zifuPt.x,
              y: zifuPt.y,
              radius: 42 * s,
              count: 6,
              shape: 'flare',
              colors: ['#ffffff', '#67e8f9', '#c4b5fd'],
              size: [2.2 * s, 4.2 * s],
              speed: [18 * s, 54 * s],
              spin: [-3.2, 3.2],
              life: [380, 640],
            });
          });
        })(rStep);
      }

      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 36 * s,
        count: 14,
        shape: 'wisp',
        colors: ['#a78bfa', '#67e8f9', '#e0e7ff'],
        size: [3.8 * s, 7.2 * s],
        speed: [14 * s, 38 * s],
        vortex: 150 * s,
        vortexCenter: c,
        gravity: -48 * s,
        alpha: 0.56,
        life: [480, 780],
        trail: 3,
      });
    });

    VFX.after(660, function () {
      VFX.floatText(targetEl || h, '存神诀', '#c4b5fd', { scale: 0.94, duration: 900 });
    });

    return 1180;
  });

  // ===========================================================================
  // 9. ability.lianjing-huaqi-jue · 炼精化气诀（二阶）
  //    下丹田暖赤金 -> 胸腹澄澈翠青 -> 顶门紫白清气 三段流体 wisp/orb 螺旋演化
  // ===========================================================================
  VFX.register('ability.lianjing-huaqi-jue', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;
    var dantian = a.dantian;
    var chest = a.chest;
    var crown = a.crown;

    VFX.cinematicDim(0.15, 640, '#0a0714');
    if (targetEl) VFX.motion(targetEl, 'cast', { duration: 440 });

    VFX.magicCircle({
      x: f.x,
      y: f.y + 2 * s,
      radius: 66,
      tilt: 0.44,
      layer: 'ground',
      style: 'spirit',
      color: '#f59e0b',
      secondaryColor: '#34d399',
      rotationSpeed: 1.05,
      lineWidth: 1.7,
      alpha: 0.68,
      duration: 1140,
    });

    // 连续色相演化螺旋光带（暖赤金 -> 翠青 -> 紫白）贯通三田
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 1060,
      draw: function (g, p) {
        var fade = p < 0.14 ? p / 0.14 : p > 0.74 ? (1 - p) / 0.26 : 1;
        if (fade <= 0.01) return;

        var stages = [
          { y: dantian.y, rgb: '249,115,22', hex: '#f97316' },
          { y: chest.y, rgb: '52,211,153', hex: '#34d399' },
          { y: crown.y, rgb: '196,181,253', hex: '#c4b5fd' },
        ];

        g.save();
        var activeLevel = Math.min(1, p * 1.35);
        for (var seg = 0; seg < 24; seg++) {
          var u0 = seg / 24;
          var u1 = (seg + 1) / 24;
          if (u0 > activeLevel) break;

          var uMid = (u0 + u1) * 0.5;
          var env = Math.sin((uMid / Math.max(0.05, activeLevel)) * Math.PI);
          if (env <= 0.04) continue;

          var y0 = VFX.lerp(dantian.y + 10 * s, crown.y, u0);
          var y1 = VFX.lerp(dantian.y + 10 * s, crown.y, u1);
          var rgb = uMid < 0.36 ? stages[0].rgb : uMid < 0.72 ? stages[1].rgb : stages[2].rgb;

          for (var arm = 0; arm < 2; arm++) {
            var phase = p * 7.5 + arm * Math.PI;
            var r0 = VFX.lerp(32, 16, u0) * s;
            var r1 = VFX.lerp(32, 16, u1) * s;
            var x0 = c.x + Math.sin(u0 * TWO_PI * 1.4 + phase) * r0;
            var x1 = c.x + Math.sin(u1 * TWO_PI * 1.4 + phase) * r1;

            g.beginPath();
            g.moveTo(x0, y0);
            g.lineTo(x1, y1);
            g.lineCap = 'round';
            g.strokeStyle = 'rgba(' + rgb + ',' + (0.28 * fade * env).toFixed(3) + ')';
            g.lineWidth = 6.2 * s * env;
            g.stroke();

            g.strokeStyle = 'rgba(' + rgb + ',' + (0.82 * fade * env).toFixed(3) + ')';
            g.lineWidth = 2.2 * s * env;
            g.stroke();
          }
        }

        // 三田节点逐次点亮柔晕
        for (var stIdx = 0; stIdx < stages.length; stIdx++) {
          var threshold = stIdx * 0.25;
          if (p <= threshold) continue;
          var sp = Math.min(1, (p - threshold) / 0.45);
          var sFade = sp < 0.25 ? sp / 0.25 : 1 - (sp - 0.25) / 0.75;
          var glowSp = getSoftGlow(stages[stIdx].hex);
          if (glowSp && sFade > 0.02) {
            var gr = (16 + stIdx * 2) * s;
            g.globalAlpha = 0.72 * fade * sFade;
            g.drawImage(glowSp, c.x - gr, stages[stIdx].y - gr, gr * 2, gr * 2);
          }
        }
        g.restore();
      },
    });

    // 第一段（0ms）：下丹田暖赤金精气微粒旋聚升腾
    VFX.shockwave({
      x: dantian.x,
      y: dantian.y,
      fromRadius: 8,
      toRadius: 54,
      color: '#f97316',
      coreColor: '#fef08a',
      lineWidth: 2.6,
      double: false,
      duration: 380,
    });

    VFX.burst({
      x: dantian.x,
      y: dantian.y,
      radius: 56 * s,
      ring: true,
      tiltY: 0.68,
      count: 18,
      shape: 'wisp',
      colors: ['#fb7185', '#f97316', '#fbbf24', '#fef08a'],
      size: [3.8 * s, 7.6 * s],
      speed: [18 * s, 44 * s],
      vortex: 230 * s,
      vortexCenter: dantian,
      attractTo: chest,
      attractStrength: 340 * s,
      gravity: -78 * s,
      drag: 0.95,
      alpha: 0.78,
      life: [420, 620],
      trail: 4,
    });

    VFX.burst({
      x: dantian.x,
      y: dantian.y,
      radius: 48 * s,
      count: 14,
      shape: 'orb',
      colors: ['#fbbf24', '#fb923c', '#fda4af', '#fef08a'],
      size: [1.8 * s, 3.6 * s],
      speed: [18 * s, 46 * s],
      vortex: 210 * s,
      vortexCenter: dantian,
      gravity: -82 * s,
      drag: 0.95,
      life: [420, 640],
      trail: 3,
    });

    // 第二段（240ms）：行至胸腹渐变为澄澈翠青流体螺旋演化
    VFX.after(240, function () {
      if (targetEl) VFX.hitFlash(targetEl, '#34d399', 200);

      VFX.shockwave({
        x: chest.x,
        y: chest.y,
        fromRadius: 10,
        toRadius: 68,
        color: '#34d399',
        coreColor: '#ecfdf5',
        lineWidth: 2.8,
        double: false,
        duration: 420,
      });

      VFX.burst({
        x: chest.x,
        y: chest.y,
        radius: 38 * s,
        ring: true,
        tiltY: 0.66,
        count: 18,
        shape: 'wisp',
        colors: ['#10b981', '#34d399', '#6ee7b7', '#5eead4'],
        size: [4.2 * s, 8.2 * s],
        speed: [22 * s, 54 * s],
        vortex: -255 * s,
        vortexCenter: chest,
        attractTo: crown,
        attractStrength: 360 * s,
        gravity: -110 * s,
        turbulence: 54 * s,
        drag: 0.95,
        alpha: 0.76,
        life: [420, 660],
        trail: 4,
      });

      VFX.burst({
        x: chest.x,
        y: chest.y,
        radius: 36 * s,
        count: 14,
        shape: 'orb',
        colors: ['#6ee7b7', '#a7f3d0', '#5eead4', '#ffffff'],
        size: [1.8 * s, 3.8 * s],
        speed: [20 * s, 50 * s],
        vortex: -225 * s,
        vortexCenter: chest,
        gravity: -100 * s,
        drag: 0.95,
        life: [440, 680],
        trail: 4,
      });
    });

    // 第三段（490ms）：再化紫白清气汇入顶门
    VFX.after(490, function () {
      if (targetEl) VFX.hitFlash(targetEl, '#c4b5fd', 220);

      VFX.shockwave({
        x: crown.x,
        y: crown.y,
        fromRadius: 8,
        toRadius: 82,
        color: '#a78bfa',
        coreColor: '#ffffff',
        lineWidth: 3.0,
        double: false,
        duration: 460,
      });

      VFX.burst({
        x: crown.x,
        y: crown.y + 18 * s,
        radius: 42 * s,
        ring: true,
        tiltY: 0.64,
        count: 16,
        shape: 'wisp',
        colors: ['#a78bfa', '#c4b5fd', '#e0e7ff', '#ffffff'],
        size: [4.0 * s, 7.8 * s],
        speed: [20 * s, 48 * s],
        vortex: 220 * s,
        vortexCenter: crown,
        attractTo: crown,
        attractStrength: 420 * s,
        gravity: -72 * s,
        drag: 0.95,
        alpha: 0.8,
        life: [420, 680],
        trail: 4,
      });

      VFX.burst({
        x: crown.x,
        y: crown.y,
        radius: 36 * s,
        count: 16,
        shape: 'orb',
        colors: ['#ddd6fe', '#ffffff', '#c4b5fd'],
        size: [1.8 * s, 3.8 * s],
        speed: [18 * s, 52 * s],
        vortex: 185 * s,
        vortexCenter: crown,
        gravity: -64 * s,
        drag: 0.96,
        life: [460, 740],
        trail: 4,
      });

      VFX.burst({
        x: crown.x,
        y: crown.y,
        radius: 26 * s,
        count: 7,
        shape: 'flare',
        colors: ['#ffffff', '#ddd6fe', '#6ee7b7'],
        size: [2.2 * s, 4.2 * s],
        speed: [16 * s, 48 * s],
        life: [340, 580],
      });
    });

    VFX.after(680, function () {
      VFX.floatText(targetEl || h, '炼精化气诀', '#6ee7b7', { scale: 0.94, duration: 900 });
    });

    return 1200;
  });

  // ===========================================================================
  // 10. ability.xuanyuan-yiqi-gong · 玄元一气功（三阶）
  //     鎏金 bagua + spirit 双重太素微阵 -> 六色灵流六方 attractTo 合一归真
  //     -> 纯白鎏金混元护体华盖（渐变光伞 + 瑞彩 flare）+ 漫天瑞彩星尘
  // ===========================================================================
  VFX.register('ability.xuanyuan-yiqi-gong', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;

    var canopyPt = VFX.clampStage({ x: h.x, y: Math.max(38, h.y - 22 * s) }, 56 * s, 38);

    VFX.cinematicDim(0.2, 760, '#050812');
    if (targetEl) VFX.motion(targetEl, 'levitate', { duration: 540 });

    // 鎏金太素柔和地面光池
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 1200,
      draw: function (g, p) {
        var fade = p < 0.14 ? p / 0.14 : p > 0.72 ? (1 - p) / 0.28 : 1;
        if (fade <= 0.01) return;
        var r = 102 * s * (p < 0.2 ? VFX.lerp(0.5, 1, p / 0.2) : 1);
        g.save();
        g.translate(f.x, f.y + 2 * s);
        g.scale(1, 0.44);
        var grad = g.createRadialGradient(0, 0, r * 0.06, 0, 0, r);
        grad.addColorStop(0, 'rgba(255,255,255,' + (0.36 * fade).toFixed(3) + ')');
        grad.addColorStop(0.35, 'rgba(254,240,138,' + (0.28 * fade).toFixed(3) + ')');
        grad.addColorStop(0.72, 'rgba(245,196,81,' + (0.14 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(245,196,81,0)');
        g.fillStyle = grad;
        g.beginPath();
        g.arc(0, 0, r, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });

    // 鎏金六合 bagua + spirit 双重太素微阵展开
    VFX.magicCircle({
      x: f.x,
      y: f.y + 2 * s,
      radius: 86,
      tilt: 0.44,
      layer: 'ground',
      style: 'bagua',
      color: '#f5c451',
      secondaryColor: '#fef9c3',
      rotationSpeed: 0.95,
      lineWidth: 2.0,
      alpha: 0.82,
      duration: 1200,
    });

    VFX.magicCircle({
      delay: 30,
      x: f.x,
      y: f.y + 2 * s,
      radius: 60,
      tilt: 0.44,
      layer: 'ground',
      style: 'spirit',
      color: '#ffffff',
      secondaryColor: '#f5c451',
      rotationSpeed: -1.45,
      lineWidth: 1.7,
      alpha: 0.78,
      duration: 1160,
    });

    // 60ms 金 / 翠 / 湛蓝 / 赤炎 / 紫微 / 素白 六色灵流自外围六方旋转 attractTo 合一归真
    var sixColors = [
      { hex: '#fbbf24', core: '#fef08a' }, // 金
      { hex: '#34d399', core: '#a7f3d0' }, // 翠
      { hex: '#38bdf8', core: '#e0f2fe' }, // 湛蓝
      { hex: '#f97316', core: '#fed7aa' }, // 赤炎
      { hex: '#a78bfa', core: '#ede9fe' }, // 紫微
      { hex: '#f8fafc', core: '#ffffff' }, // 素白
    ];

    for (var hexIdx = 0; hexIdx < sixColors.length; hexIdx++) {
      (function (idx, colObj) {
        var dirAng = (idx * TWO_PI) / 6 - HALF_PI;
        var originRaw = {
          x: c.x + Math.cos(dirAng) * 102 * s,
          y: c.y + Math.sin(dirAng) * 76 * s,
        };
        var originPt = VFX.clampStage(originRaw, 18 * s, 18 * s);

        VFX.streamer({
          delay: 55 + idx * 24,
          x1: originPt.x,
          y1: originPt.y,
          x2: c.x,
          y2: c.y,
          arc: 40 * s,
          wobble: 5 * s,
          width: 6.2 * s,
          tailLength: 0.46,
          duration: 390,
          color: colObj.hex,
          coreColor: colObj.core,
          sparkColors: [colObj.hex, colObj.core, '#ffffff'],
        });

        VFX.burst({
          delay: 55 + idx * 24,
          x: originPt.x,
          y: originPt.y,
          radius: 14 * s,
          count: 5,
          shape: 'wisp',
          colors: [colObj.hex, colObj.core],
          size: [3.6 * s, 6.4 * s],
          speed: [10 * s, 24 * s],
          vortex: 240 * s,
          vortexCenter: c,
          attractTo: c,
          attractStrength: 780 * s,
          drag: 0.94,
          alpha: 0.78,
          life: [360, 460],
          trail: 4,
        });

        VFX.burst({
          delay: 65 + idx * 24,
          x: originPt.x,
          y: originPt.y,
          radius: 12 * s,
          count: 5,
          shape: 'orb',
          colors: [colObj.core, colObj.hex, '#ffffff'],
          size: [1.8 * s, 3.4 * s],
          speed: [10 * s, 24 * s],
          vortex: 240 * s,
          vortexCenter: c,
          attractTo: c,
          attractStrength: 800 * s,
          drag: 0.94,
          life: [360, 460],
          trail: 3,
        });
      })(hexIdx, sixColors[hexIdx]);
    }

    // 460ms 六合归一：纯白鎏金混元护体华盖轰然撑开 + 瑞彩 flare + 漫天瑞彩星尘
    VFX.after(460, function () {
      VFX.quake('sm', 220);
      if (targetEl) VFX.hitFlash(targetEl, '#fef9c3', 280);

      // 混元归真双层圣光波
      VFX.shockwave({
        x: c.x,
        y: c.y,
        fromRadius: 12,
        toRadius: 138,
        color: '#fef08a',
        coreColor: '#ffffff',
        lineWidth: 4.2,
        double: false,
        duration: 460,
      });

      VFX.shockwave({
        delay: 55,
        x: c.x,
        y: c.y,
        fromRadius: 16,
        toRadius: 176,
        color: '#f5c451',
        coreColor: '#ffffff',
        lineWidth: 3.4,
        double: false,
        duration: 500,
      });

      // 纯白鎏金混元护体华盖（渐变光伞穹窿 + 垂流鎏金光纱 + 顶冠瑞珠）
      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 760,
        draw: function (g, p) {
          var open = p < 0.2 ? 1 - Math.pow(1 - p / 0.2, 3) : 1;
          var fade = p < 0.14 ? p / 0.14 : p > 0.72 ? Math.pow(1 - (p - 0.72) / 0.28, 1.25) : 1;
          if (fade <= 0.01) return;

          var spanW = 74 * s * (0.45 + 0.55 * open);
          var domeH = 34 * s * (0.5 + 0.5 * open);
          var veilH = Math.max(76 * s, f.y - canopyPt.y);
          var spWhite = getSoftGlow('#ffffff');
          var spGold = getSoftGlow('#f5c451');

          g.save();
          g.translate(canopyPt.x, canopyPt.y);

          // 1. 华盖垂落纯白鎏金浑圆光纱薄罩（周身护体笼罩，边缘归零）
          g.save();
          g.translate(0, veilH * 0.52);
          g.scale(0.84, 1.18);
          var veilR = veilH * 0.62;
          var veilG = g.createRadialGradient(0, -veilR * 0.15, veilR * 0.1, 0, 0, veilR);
          veilG.addColorStop(0, 'rgba(255,255,255,' + (0.28 * fade).toFixed(3) + ')');
          veilG.addColorStop(0.45, 'rgba(254,240,138,' + (0.2 * fade).toFixed(3) + ')');
          veilG.addColorStop(0.78, 'rgba(245,196,81,' + (0.09 * fade).toFixed(3) + ')');
          veilG.addColorStop(1, 'rgba(245,196,81,0)');
          g.fillStyle = veilG;
          g.beginPath();
          g.arc(0, 0, veilR, 0, TWO_PI);
          g.fill();
          g.restore();

          // 2. 华盖顶冠穹窿光伞辉晕（压扁椭圆径向渐变，如云盖悬顶）
          g.save();
          g.scale(1.45, 0.58);
          var capR = spanW * 0.82;
          var capG = g.createRadialGradient(0, 0, 2, 0, 0, capR);
          capG.addColorStop(0, 'rgba(255,255,255,' + (0.64 * fade).toFixed(3) + ')');
          capG.addColorStop(0.36, 'rgba(254,240,138,' + (0.42 * fade).toFixed(3) + ')');
          capG.addColorStop(0.72, 'rgba(245,196,81,' + (0.18 * fade).toFixed(3) + ')');
          capG.addColorStop(1, 'rgba(245,196,81,0)');
          g.fillStyle = capG;
          g.beginPath();
          g.arc(0, 0, capR, 0, TWO_PI);
          g.fill();
          g.restore();

          // 3. 三重羽化伞骨光弧与伞檐垂缨流苏
          drawFeatheredSilkArc(g, -spanW, 12 * s, 0, -domeH * 1.18, spanW, 12 * s, 5.2 * s, '245,196,81', 0.72 * fade);
          drawFeatheredSilkArc(g, -spanW * 0.86, 9 * s, 0, -domeH * 0.96, spanW * 0.86, 9 * s, 3.0 * s, '255,255,255', 0.86 * fade);
          drawFeatheredSilkArc(g, -spanW * 0.54, 6 * s, 0, -domeH * 0.72, spanW * 0.54, 6 * s, 2.2 * s, '254,240,138', 0.78 * fade);

          // 伞檐两侧轻垂而下的鎏金流苏渐变丝带
          for (var side = -1; side <= 1; side += 2) {
            drawFeatheredSilkArc(
              g,
              side * spanW * 0.82,
              10 * s,
              side * spanW * 0.88,
              veilH * 0.45,
              side * spanW * 0.68,
              veilH * 0.82,
              2.6 * s,
              '245,196,81',
              0.52 * fade
            );
          }

          // 华盖顶心纯白玄元宝珠光核与 5 枚檐角瑞光
          if (spWhite) {
            var topR = 16 * s * (0.9 + 0.1 * Math.sin(p * 12));
            g.globalAlpha = 0.9 * fade;
            g.drawImage(spWhite, -topR, -domeH * 0.55 - topR, topR * 2, topR * 2);
          }
          if (spGold) {
            for (var b = -2; b <= 2; b++) {
              var frac = b / 2;
              var bx = frac * spanW * 0.84;
              var by = 11 * s - (1 - frac * frac) * 5 * s;
              var br = 7.5 * s;
              g.globalAlpha = 0.72 * fade;
              g.drawImage(spGold, bx - br, by - br, br * 2, br * 2);
            }
          }

          g.restore();
        },
      });

      // 华盖与周身绽放瑞彩 flare
      VFX.burst({
        x: canopyPt.x,
        y: canopyPt.y + 6 * s,
        radius: 48 * s,
        tiltY: 0.42,
        count: 14,
        shape: 'flare',
        colors: ['#ffffff', '#fef08a', '#fbbf24', '#a7f3d0', '#bae6fd', '#ddd6fe'],
        size: [2.4 * s, 4.8 * s],
        speed: [22 * s, 74 * s],
        gravity: -28 * s,
        spin: [-3.5, 3.5],
        life: [420, 740],
      });

      // 漫天瑞彩星尘 orb（六合余彩与纯白鎏金缓缓飘散）
      VFX.burst({
        x: c.x,
        y: c.y - 10 * s,
        radius: 68 * s,
        count: 34,
        shape: 'orb',
        colors: ['#ffffff', '#fef08a', '#fbbf24', '#6ee7b7', '#7dd3fc', '#c4b5fd', '#fdba74'],
        size: [1.6 * s, 3.6 * s],
        speed: [18 * s, 64 * s],
        gravity: -24 * s,
        turbulence: 54 * s,
        drag: 0.96,
        alpha: 0.88,
        life: [540, 820],
        trail: 4,
      });

      // 混元护体纯白鎏金 wisp 与玄门灵纹 rune
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 44 * s,
        ring: true,
        tiltY: 0.68,
        count: 18,
        shape: 'wisp',
        colors: ['#ffffff', '#fef08a', '#f5c451', '#fef9c3'],
        size: [4.2 * s, 8.4 * s],
        speed: [16 * s, 42 * s],
        vortex: 175 * s,
        vortexCenter: c,
        gravity: -42 * s,
        drag: 0.96,
        alpha: 0.68,
        life: [500, 780],
        trail: 3,
      });

      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 46 * s,
        ring: true,
        tiltY: 0.62,
        count: 8,
        shape: 'rune',
        colors: ['#fef08a', '#ffffff', '#f5c451'],
        size: [3.4 * s, 5.2 * s],
        speed: [12 * s, 32 * s],
        gravity: -32 * s,
        spin: [-2, 2],
        alpha: 0.86,
        life: [520, 780],
      });
    });

    VFX.after(720, function () {
      VFX.floatText(targetEl || h, '玄元一气功', '#fef08a', { scale: 0.96, duration: 920 });
    });

    return 1250;
  });
});
