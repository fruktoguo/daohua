/**
 * 《道化太初》Canvas 2D v2.3.0 · 一阶被动内功心法周天护体特效组（5 门）
 * 真源镜像：config/game-data/abilities/internal_abilities.json
 *
 * 收录：
 *   1. ability.tuna-gong   · 吐纳功（一阶）—— 素白青玉八卦微阵 + 清气归入丹田 + 双股素青周天升腾
 *   2. ability.manniu-jin  · 蛮牛劲（一阶）—— 踏地微震 + 琥珀赤铜气浪环 + 刚猛赤橙劲气与铜金气芒
 *   3. ability.tiebu-shan  · 铁布衫（一阶）—— 玄金厚土双波纹 + 六面羽化玄铜金钟护罡弧光 + 金铁星点
 *   4. ability.feiyan-gong · 飞燕功（一阶）—— 轻身提纵 + 冰青翠白旋风 + 双飞燕流风羽弧与飞絮螺旋
 *   5. ability.qingxin-jue · 清心诀（一阶）—— 眉心冰蓝玉莲柔光 + 静心水蓝涤荡涟漪 + 空明灵珠与灵纹
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
   * 绘制分段渐隐柔光弧段（支持任意角度与椭圆压扁比例）
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

      g.strokeStyle = 'rgba(' + rgb + ',' + (alpha * 0.76 * env).toFixed(3) + ')';
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

    var padX = 40 * s;
    var padY = 32 * s;
    var c = VFX.clampStage(rawC, padX, padY);
    var f = VFX.clampStage(rawF, padX, Math.max(24 * s, padY * 0.85));
    var h = VFX.clampStage({ x: rawH.x, y: Math.max(44, rawH.y) }, padX, 40);
    var dantian = VFX.clampStage({ x: c.x, y: VFX.lerp(c.y, f.y, 0.34) }, padX, padY);
    var chest = VFX.clampStage({ x: c.x, y: VFX.lerp(c.y, h.y, 0.22) }, padX, padY);

    return {
      targetEl: targetEl,
      isEnemy: isEnemy,
      s: s,
      c: c,
      f: f,
      h: h,
      dantian: dantian,
      chest: chest,
    };
  }

  // ===========================================================================
  // 1. ability.tuna-gong · 吐纳功（一阶）
  //    素白青玉八卦微阵 -> 清气 orb/wisp attractTo 归入丹田 -> 双股素青周天升腾 + 微光 flare
  // ===========================================================================
  VFX.register('ability.tuna-gong', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;
    var dantian = a.dantian;

    if (targetEl) VFX.motion(targetEl, 'cast', { duration: 420 });

    // 脚底素白青玉八卦微阵与柔润吐纳底晕
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 1020,
      draw: function (g, p) {
        var fade = p < 0.16 ? p / 0.16 : p > 0.72 ? (1 - p) / 0.28 : 1;
        if (fade <= 0.01) return;
        var breathe = 1 + Math.sin(p * Math.PI * 2) * 0.07;
        var r = 68 * s * breathe;
        g.save();
        g.translate(f.x, f.y + 2 * s);
        g.scale(1, 0.44);
        var grad = g.createRadialGradient(0, 0, r * 0.06, 0, 0, r);
        grad.addColorStop(0, 'rgba(240,253,250,' + (0.28 * fade).toFixed(3) + ')');
        grad.addColorStop(0.42, 'rgba(153,246,228,' + (0.2 * fade).toFixed(3) + ')');
        grad.addColorStop(0.78, 'rgba(94,234,212,' + (0.08 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(94,234,212,0)');
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
      radius: 58,
      tilt: 0.44,
      layer: 'ground',
      style: 'bagua',
      color: '#99f6e4',
      secondaryColor: '#f0fdfa',
      rotationSpeed: 0.72,
      lineWidth: 1.5,
      alpha: 0.66,
      duration: 1020,
    });

    // 吸气归元：天地清气从外圈柔和内吸归入丹田气海
    VFX.burst({
      x: dantian.x,
      y: dantian.y - 6 * s,
      radius: 84 * s,
      ring: true,
      tiltY: 0.72,
      count: 18,
      shape: 'orb',
      colors: ['#f0fdfa', '#99f6e4', '#5eead4', '#e0f2fe'],
      size: [1.8 * s, 3.4 * s],
      speed: [10 * s, 26 * s],
      vortex: 85 * s,
      vortexCenter: dantian,
      attractTo: dantian,
      attractStrength: 460 * s,
      drag: 0.93,
      alpha: 0.84,
      life: [360, 480],
      trail: 3,
    });

    VFX.burst({
      delay: 45,
      x: dantian.x,
      y: dantian.y - 8 * s,
      radius: 78 * s,
      ring: true,
      tiltY: 0.74,
      count: 14,
      shape: 'wisp',
      colors: ['#99f6e4', '#ccfbf1', '#f8fafc'],
      size: [3.2 * s, 5.6 * s],
      speed: [10 * s, 24 * s],
      vortex: -75 * s,
      vortexCenter: dantian,
      attractTo: dantian,
      attractStrength: 440 * s,
      drag: 0.93,
      alpha: 0.54,
      life: [360, 500],
      trail: 2,
    });

    // 气海归一 -> 双股素青周天气流沿身躯舒缓盘旋升腾
    VFX.after(300, function () {
      if (targetEl) VFX.hitFlash(targetEl, '#99f6e4', 190);

      VFX.shockwave({
        x: dantian.x,
        y: dantian.y,
        fromRadius: 8,
        toRadius: 52,
        color: '#99f6e4',
        coreColor: '#f0fdfa',
        lineWidth: 2.4,
        double: false,
        duration: 380,
      });

      // 双股极柔吐纳丝带弧沿身侧缓缓上浮
      VFX.custom({
        duration: 640,
        draw: function (g, p) {
          var fade = p < 0.2 ? p / 0.2 : p > 0.65 ? (1 - p) / 0.35 : 1;
          if (fade <= 0.01) return;
          var sp = getSoftGlow('#99f6e4');
          g.save();
          for (var arm = 0; arm < 2; arm++) {
            var dir = arm === 0 ? 1 : -1;
            var yBase = VFX.lerp(dantian.y, h.y + 4 * s, p * 0.88);
            var phase = p * 4.4 + arm * Math.PI;
            var rx = (28 + Math.sin(p * Math.PI) * 10) * s;
            var x0 = c.x + Math.cos(phase - 0.65) * rx * dir;
            var y0 = yBase + 16 * s;
            var cxPt = c.x + Math.cos(phase) * rx * 1.18 * dir;
            var cyPt = yBase;
            var x1 = c.x + Math.cos(phase + 0.65) * rx * dir;
            var y1 = yBase - 16 * s;
            drawFeatheredSilkArc(
              g,
              x0,
              y0,
              cxPt,
              cyPt,
              x1,
              y1,
              3.4 * s,
              arm === 0 ? '153,246,228' : '240,253,250',
              0.52 * fade
            );
            if (sp) {
              var gr = 9 * s;
              g.globalAlpha = 0.72 * fade;
              g.drawImage(sp, x1 - gr, y1 - gr, gr * 2, gr * 2);
            }
          }
          g.restore();
        },
      });

      // 5 拍舒缓双螺旋升腾素青 wisp 与微光 orb
      var steps = 5;
      for (var st = 0; st < steps; st++) {
        (function (idx) {
          VFX.after(idx * 85, function () {
            var u = idx / (steps - 1);
            var baseAng = idx * 1.15;
            var rr = VFX.lerp(20, 32, u) * s;
            for (var arm = 0; arm < 2; arm++) {
              var ang = baseAng + arm * Math.PI;
              var sx = c.x + Math.cos(ang) * rr;
              var sy = dantian.y - u * (dantian.y - h.y) * 0.85 + Math.sin(ang) * rr * 0.32;
              var vx = -Math.sin(ang) * 58 * s;
              var vy = Math.cos(ang) * 18 * s - 56 * s;

              VFX.burst({
                x: sx,
                y: sy,
                radius: 8 * s,
                count: 2,
                shape: 'wisp',
                colors: ['#99f6e4', '#5eead4', '#f0fdfa'],
                size: [3.6 * s, 6.8 * s],
                endSize: [1.6 * s, 3.2 * s],
                speed: [6 * s, 14 * s],
                vx: vx,
                vy: vy,
                gravity: -42 * s,
                turbulence: 44 * s,
                drag: 0.96,
                alpha: 0.56,
                life: [440, 680],
              });
            }

            if (idx % 2 === 1) {
              VFX.burst({
                x: c.x,
                y: VFX.lerp(c.y, h.y, u),
                radius: 26 * s,
                count: 2,
                shape: 'flare',
                colors: ['#f0fdfa', '#99f6e4', '#ffffff'],
                size: [1.8 * s, 3.2 * s],
                speed: [14 * s, 36 * s],
                gravity: -40 * s,
                drag: 0.95,
                life: [320, 520],
              });
            }
          });
        })(st);
      }
    });

    VFX.after(640, function () {
      VFX.floatText(targetEl || h, '吐纳功', '#99f6e4', { scale: 0.94, duration: 860 });
    });

    return 1080;
  });

  // ===========================================================================
  // 2. ability.manniu-jin · 蛮牛劲（一阶）
  //    slam 踏地微震 + 琥珀赤铜气浪环 shockwave + 刚猛赤橙 wisp + 铜金 flare/spark
  // ===========================================================================
  VFX.register('ability.manniu-jin', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;

    VFX.cinematicDim(0.12, 520, '#140802');
    if (targetEl) VFX.motion(targetEl, 'slam', { dir: 0, duration: 300 });

    // 蓄势时周身先聚一缕低沉赤铜劲烟
    VFX.burst({
      x: f.x,
      y: f.y,
      radius: 34 * s,
      ring: true,
      tiltY: 0.44,
      count: 10,
      shape: 'wisp',
      colors: ['#ea580c', '#d97706', '#f59e0b'],
      size: [3.6 * s, 6.8 * s],
      speed: [16 * s, 38 * s],
      attractTo: c,
      attractStrength: 380 * s,
      life: [220, 340],
    });

    // 140ms 踏地落地：琥珀赤铜气浪环炸开 + 刚猛赤橙劲气鼓荡 + 铜金气芒迸发
    VFX.after(140, function () {
      VFX.quake('sm', 190);
      if (targetEl) VFX.hitFlash(targetEl, '#fb923c', 210);

      // 贴地琥珀赤铜沉稳光池
      VFX.custom({
        layer: 'ground',
        blend: 'lighter',
        duration: 780,
        draw: function (g, p) {
          var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.3);
          if (fade <= 0.01) return;
          var r = 86 * s * (0.65 + 0.35 * Math.min(1, p * 3.5));
          g.save();
          g.translate(f.x, f.y + 2 * s);
          g.scale(1, 0.44);
          var grad = g.createRadialGradient(0, 0, r * 0.08, 0, 0, r);
          grad.addColorStop(0, 'rgba(254,240,138,' + (0.36 * fade).toFixed(3) + ')');
          grad.addColorStop(0.45, 'rgba(234,88,12,' + (0.28 * fade).toFixed(3) + ')');
          grad.addColorStop(0.8, 'rgba(180,83,9,' + (0.1 * fade).toFixed(3) + ')');
          grad.addColorStop(1, 'rgba(180,83,9,0)');
          g.fillStyle = grad;
          g.beginPath();
          g.arc(0, 0, r, 0, TWO_PI);
          g.fill();
          g.restore();
        },
      });

      // 琥珀赤铜双气浪环炸开
      VFX.shockwave({
        x: f.x,
        y: f.y + 2 * s,
        tilt: 0.44,
        layer: 'ground',
        fromRadius: 12,
        toRadius: 112,
        color: '#ea580c',
        coreColor: '#fbbf24',
        lineWidth: 3.8,
        duration: 420,
        double: true,
      });

      VFX.shockwave({
        delay: 75,
        x: f.x,
        y: f.y + 2 * s,
        tilt: 0.44,
        layer: 'ground',
        fromRadius: 16,
        toRadius: 134,
        color: '#d97706',
        coreColor: '#fef08a',
        lineWidth: 3.2,
        duration: 450,
        double: false,
      });

      // 贴地踏尘烟气
      VFX.burst({
        layer: 'ground',
        x: f.x,
        y: f.y + 2 * s,
        radius: 26 * s,
        ring: true,
        tiltY: 0.42,
        count: 12,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#78350f', '#9a3412', '#57534e'],
        size: [11 * s, 20 * s],
        speed: [55 * s, 130 * s],
        drag: 0.9,
        alpha: 0.46,
        life: [380, 660],
      });

      // 周身鼓荡刚猛赤橙劲气 wisp
      VFX.burst({
        x: c.x,
        y: c.y + 4 * s,
        radius: 26 * s,
        count: 24,
        shape: 'wisp',
        colors: ['#ea580c', '#f97316', '#f59e0b', '#fbbf24'],
        size: [4.6 * s, 9.2 * s],
        speed: [62 * s, 155 * s],
        vortex: 165 * s,
        vortexCenter: c,
        gravity: -85 * s,
        turbulence: 72 * s,
        drag: 0.94,
        alpha: 0.78,
        life: [420, 720],
        trail: 3,
      });

      // 气劲澎湃羽化护体冲击波（自躯干向外鼓荡）
      VFX.custom({
        duration: 540,
        draw: function (g, p) {
          var fade = p < 0.16 ? p / 0.16 : 1 - (p - 0.16) / 0.84;
          if (fade <= 0.01) return;
          var r = VFX.lerp(24, 72, 1 - Math.pow(1 - p, 3)) * s;
          g.save();
          g.translate(c.x, c.y);
          var grad = g.createRadialGradient(0, 0, r * 0.12, 0, 0, r);
          grad.addColorStop(0, 'rgba(254,240,138,' + (0.28 * fade).toFixed(3) + ')');
          grad.addColorStop(0.55, 'rgba(249,115,22,' + (0.2 * fade).toFixed(3) + ')');
          grad.addColorStop(1, 'rgba(234,88,12,0)');
          g.fillStyle = grad;
          g.beginPath();
          g.arc(0, 0, r, 0, TWO_PI);
          g.fill();
          g.restore();
        },
      });

      // 向外迸发的铜金气芒 flare 与刚劲 spark
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 14 * s,
        count: 10,
        shape: 'flare',
        colors: ['#fef08a', '#fbbf24', '#fb923c', '#ffffff'],
        size: [2.8 * s, 5.4 * s],
        speed: [85 * s, 195 * s],
        drag: 0.92,
        spin: [-4, 4],
        life: [300, 540],
      });

      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 18 * s,
        count: 20,
        shape: 'spark',
        colors: ['#fbbf24', '#f97316', '#fef08a', '#ffffff'],
        size: [2.0 * s, 4.0 * s],
        speed: [110 * s, 265 * s],
        gravity: 40 * s,
        drag: 0.92,
        life: [280, 520],
        trail: 4,
      });
    });

    VFX.after(520, function () {
      VFX.floatText(targetEl || h, '蛮牛劲', '#fb923c', { scale: 0.94, duration: 860 });
    });

    return 1040;
  });

  // ===========================================================================
  // 3. ability.tiebu-shan · 铁布衫（一阶）
  //    玄金厚土双波纹自足底升起 + 六面羽化玄铜金钟护罡渐变弧光 + 金铁 shard/flare
  // ===========================================================================
  VFX.register('ability.tiebu-shan', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;

    VFX.cinematicDim(0.13, 560, '#0c0804');
    if (targetEl) VFX.motion(targetEl, 'cast', { duration: 380 });

    // 足底玄金厚土护阵与波纹起势
    VFX.magicCircle({
      x: f.x,
      y: f.y + 2 * s,
      radius: 62,
      tilt: 0.44,
      layer: 'ground',
      style: 'bagua',
      color: '#d8a657',
      secondaryColor: '#92400e',
      rotationSpeed: 0.58,
      lineWidth: 1.7,
      alpha: 0.64,
      duration: 1050,
    });

    VFX.shockwave({
      x: f.x,
      y: f.y + 2 * s,
      tilt: 0.44,
      layer: 'ground',
      fromRadius: 14,
      toRadius: 96,
      color: '#b45309',
      coreColor: '#fde68a',
      lineWidth: 3.4,
      duration: 480,
      double: false,
    });

    VFX.shockwave({
      delay: 130,
      x: f.x,
      y: f.y + 2 * s,
      tilt: 0.44,
      layer: 'ground',
      fromRadius: 16,
      toRadius: 118,
      color: '#d8a657',
      coreColor: '#fef08a',
      lineWidth: 3.0,
      duration: 500,
      double: false,
    });

    // 玄金厚土双波纹自足底沿躯干稳步升腾（无硬描边圆环）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 620,
      draw: function (g, p) {
        g.save();
        for (var w = 0; w < 2; w++) {
          var wp = (p - w * 0.24) / 0.76;
          if (wp <= 0 || wp >= 1) continue;
          var wFade = wp < 0.2 ? wp / 0.2 : 1 - (wp - 0.2) / 0.8;
          var wy = VFX.lerp(f.y, h.y + 4 * s, wp);
          var wr = (44 + Math.sin(wp * Math.PI) * 14) * s;
          g.save();
          g.translate(c.x, wy);
          g.scale(1, 0.36);
          drawSoftArcBand(g, wr, wr, 0, TWO_PI, 3.6 * s, w === 0 ? '216,166,87' : '180,83,9', wFade * 0.75, 20);
          g.restore();
        }
        g.restore();
      },
    });

    // 140ms 体表凝现六面羽化玄铜金钟护罡渐变弧光与浑厚光晕
    VFX.after(140, function () {
      if (targetEl) VFX.hitFlash(targetEl, '#d8a657', 240);

      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 920,
        draw: function (g, p) {
          var open = p < 0.2 ? 1 - Math.pow(1 - p / 0.2, 3) : 1;
          var fade = p < 0.16 ? p / 0.16 : p > 0.72 ? Math.pow(1 - (p - 0.72) / 0.28, 1.25) : 1;
          if (fade <= 0.01) return;

          var rad = VFX.lerp(28, 58, open) * s;
          var breathe = 1 + Math.sin(p * Math.PI * 3) * 0.03;
          var R = rad * breathe;
          var bellCenterY = VFX.lerp(c.y, f.y, 0.08);
          var sp = getSoftGlow('#f5c451');

          g.save();
          g.translate(c.x, bellCenterY);

          // 浑厚玄铜金钟护体核心柔晕（边缘归零）
          var domeGrad = g.createRadialGradient(0, -4 * s, R * 0.1, 0, 0, R * 1.22);
          domeGrad.addColorStop(0, 'rgba(254,240,138,' + (0.28 * fade).toFixed(3) + ')');
          domeGrad.addColorStop(0.42, 'rgba(216,166,87,' + (0.24 * fade).toFixed(3) + ')');
          domeGrad.addColorStop(0.76, 'rgba(180,83,9,' + (0.12 * fade).toFixed(3) + ')');
          domeGrad.addColorStop(1, 'rgba(180,83,9,0)');
          g.fillStyle = domeGrad;
          g.beginPath();
          g.arc(0, 0, R * 1.22, 0, TWO_PI);
          g.fill();

          // 六面羽化玄铜金钟护罡渐变弧光（6 瓣围绕金钟拱卫，兼具立体钟顶与底裙弧光）
          var rotOff = p * 0.45;
          for (var k = 0; k < 6; k++) {
            var aMid = rotOff + (k * TWO_PI) / 6 - HALF_PI;
            var a0 = aMid - 0.42;
            var a1 = aMid + 0.42;
            var rx = R * 0.96;
            var ry = R * 1.08;
            var x0 = Math.cos(a0) * rx;
            var y0 = Math.sin(a0) * ry;
            var cxPt = Math.cos(aMid) * rx * 1.16;
            var cyPt = Math.sin(aMid) * ry * 1.16;
            var x1 = Math.cos(a1) * rx;
            var y1 = Math.sin(a1) * ry;

            // 外层玄铜光晕弧
            drawFeatheredSilkArc(g, x0, y0, cxPt, cyPt, x1, y1, 5.2 * s, '180,83,9', 0.46 * fade);
            // 内层鎏金高亮护罡弧
            drawFeatheredSilkArc(g, x0, y0, cxPt, cyPt, x1, y1, 2.4 * s, '254,240,138', 0.78 * fade);

            if (sp) {
              var nr = 7.5 * s;
              var nx = Math.cos(aMid) * rx;
              var ny = Math.sin(aMid) * ry;
              g.globalAlpha = 0.55 * fade;
              g.drawImage(sp, nx - nr, ny - nr, nr * 2, nr * 2);
            }
          }

          // 金钟肩部与裙摆柔光横弧
          drawFeatheredSilkArc(g, -R * 0.88, -R * 0.12, 0, -R * 0.52, R * 0.88, -R * 0.12, 3.6 * s, '245,196,81', 0.56 * fade);
          drawFeatheredSilkArc(g, -R * 0.95, R * 0.38, 0, R * 0.72, R * 0.95, R * 0.38, 4.0 * s, '216,166,87', 0.52 * fade);

          g.restore();
        },
      });

      // 沉稳玄铜 wisp 低速环护体表
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 44 * s,
        ring: true,
        tiltY: 0.7,
        count: 14,
        shape: 'wisp',
        colors: ['#d8a657', '#b45309', '#f5c451', '#fef08a'],
        size: [4.2 * s, 8.2 * s],
        speed: [10 * s, 26 * s],
        vortex: 115 * s,
        vortexCenter: c,
        gravity: -28 * s,
        drag: 0.96,
        alpha: 0.62,
        life: [580, 880],
        trail: 3,
      });

      // 金铁星点 shard 与沉稳 flare 护身
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 36 * s,
        ring: true,
        count: 14,
        shape: 'shard',
        colors: ['#d8a657', '#fbbf24', '#b45309', '#fef08a'],
        size: [2.2 * s, 4.4 * s],
        speed: [38 * s, 108 * s],
        gravity: 65 * s,
        drag: 0.94,
        spin: [-6, 6],
        life: [420, 720],
        trail: 3,
      });

      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 42 * s,
        ring: true,
        count: 8,
        shape: 'flare',
        colors: ['#fef08a', '#f5c451', '#ffffff'],
        size: [2.4 * s, 4.4 * s],
        speed: [16 * s, 48 * s],
        spin: [-2.5, 2.5],
        life: [400, 680],
      });
    });

    VFX.after(620, function () {
      VFX.floatText(targetEl || h, '铁布衫', '#d8a657', { scale: 0.94, duration: 880 });
    });

    return 1120;
  });

  // ===========================================================================
  // 4. ability.feiyan-gong · 飞燕功（一阶）
  //    levitate 轻身提纵 + 冰青翠白旋风托足 + 双飞燕流风羽弧 + petal 与高速螺旋 wisp
  // ===========================================================================
  VFX.register('ability.feiyan-gong', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;

    if (targetEl) VFX.motion(targetEl, 'levitate', { duration: 420 });

    // 足底冰青翠白轻灵旋风托足
    VFX.shockwave({
      x: f.x,
      y: f.y + 2 * s,
      tilt: 0.42,
      layer: 'ground',
      fromRadius: 10,
      toRadius: 84,
      color: '#5eead4',
      coreColor: '#ecfeff',
      lineWidth: 2.6,
      duration: 380,
      double: true,
    });

    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 840,
      draw: function (g, p) {
        var fade = p < 0.16 ? p / 0.16 : p > 0.66 ? (1 - p) / 0.34 : 1;
        if (fade <= 0.01) return;
        var r = 60 * s * (0.7 + 0.3 * Math.min(1, p * 3));
        g.save();
        g.translate(f.x, f.y + 2 * s);
        g.scale(1, 0.42);
        var grad = g.createRadialGradient(0, 0, 2, 0, 0, r);
        grad.addColorStop(0, 'rgba(236,254,255,' + (0.34 * fade).toFixed(3) + ')');
        grad.addColorStop(0.45, 'rgba(103,232,249,' + (0.24 * fade).toFixed(3) + ')');
        grad.addColorStop(0.8, 'rgba(110,231,183,' + (0.09 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(110,231,183,0)');
        g.fillStyle = grad;
        g.beginPath();
        g.arc(0, 0, r, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });

    // 足底螺旋托升高速风涡 wisp
    VFX.burst({
      x: f.x,
      y: f.y,
      radius: 28 * s,
      ring: true,
      tiltY: 0.42,
      count: 18,
      shape: 'wisp',
      colors: ['#6ee7b7', '#67e8f9', '#ecfeff', '#a7f3d0'],
      size: [3.6 * s, 7.2 * s],
      angle: [-Math.PI * 0.72, -Math.PI * 0.28],
      speed: [70 * s, 155 * s],
      vortex: 320 * s,
      vortexCenter: c,
      gravity: -115 * s,
      drag: 0.96,
      alpha: 0.74,
      life: [420, 680],
      trail: 3,
    });

    // 70ms 两道渐变飞燕流风羽弧绕身疾旋掠影 + 轻盈 petal 与高速螺旋 wisp
    VFX.after(70, function () {
      if (targetEl) VFX.hitFlash(targetEl, '#67e8f9', 180);

      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 740,
        draw: function (g, p) {
          var fade = p < 0.14 ? p / 0.14 : p > 0.7 ? (1 - p) / 0.3 : 1;
          if (fade <= 0.01) return;

          var spMain = getSoftGlow('#67e8f9');
          var spSec = getSoftGlow('#6ee7b7');

          for (var sw = 0; sw < 2; sw++) {
            var dir = sw === 0 ? 1 : -1;
            var tiltRot = sw === 0 ? -0.34 : 0.34;
            var rx = (54 + sw * 8) * s;
            var ry = (22 + sw * 4) * s;
            var headAng = p * 9.8 * dir + sw * Math.PI;
            var yShift = Math.sin(p * Math.PI) * -10 * s;

            g.save();
            g.translate(c.x, c.y + yShift);
            g.rotate(tiltRot);

            // 主流风羽弧与对称燕尾双剪羽梢（纯线性渐变飞羽，零硬线）
            var aTail = headAng - 1.55 * dir;
            var aMid = headAng - 0.75 * dir;
            var hx = Math.cos(headAng) * rx;
            var hy = Math.sin(headAng) * ry;
            var mx = Math.cos(aMid) * rx * 1.06;
            var my = Math.sin(aMid) * ry * 1.06;
            var tx = Math.cos(aTail) * rx * 0.92;
            var ty = Math.sin(aTail) * ry * 0.92;

            var rgb = sw === 0 ? '103,232,249' : '110,231,183';
            drawFeatheredSilkArc(g, tx, ty, mx, my, hx, hy, 4.4 * s, rgb, 0.76 * fade);

            // 燕尾分叉双羽梢
            var forkA1 = aTail - 0.25 * dir;
            var forkA2 = headAng - 1.1 * dir;
            drawFeatheredSilkArc(
              g,
              Math.cos(forkA1) * rx * 1.12,
              Math.sin(forkA1) * ry * 1.24,
              Math.cos(forkA2) * rx * 1.04,
              Math.sin(forkA2) * ry * 1.08,
              hx,
              hy,
              2.2 * s,
              '236,254,255',
              0.62 * fade
            );

            // 燕首柔羽光核与 4 节飞掠尾影
            var sp = sw === 0 ? spMain : spSec;
            if (sp) {
              for (var k = 0; k < 5; k++) {
                var frac = 1 - k / 5;
                var ang = headAng - k * 0.16 * dir;
                var px = Math.cos(ang) * rx;
                var py = Math.sin(ang) * ry;
                var rGlow = (k === 0 ? 11 : 7.5 * frac) * s;
                g.globalAlpha = fade * (k === 0 ? 0.92 : 0.36 * frac * frac);
                g.drawImage(sp, px - rGlow, py - rGlow, rGlow * 2, rGlow * 2);
              }
            }

            g.restore();
          }
        },
      });

      // 高速反相螺旋疾风 wisp
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 42 * s,
        ring: true,
        tiltY: 0.65,
        count: 16,
        shape: 'wisp',
        colors: ['#67e8f9', '#a7f3d0', '#ecfeff', '#ffffff'],
        size: [3.2 * s, 6.4 * s],
        speed: [35 * s, 85 * s],
        vortex: -340 * s,
        vortexCenter: c,
        gravity: -48 * s,
        drag: 0.95,
        alpha: 0.74,
        life: [400, 680],
        trail: 4,
      });

      // 散出轻盈翠白风瓣 petal
      VFX.burst({
        x: c.x,
        y: c.y - 6 * s,
        radius: 36 * s,
        count: 16,
        shape: 'petal',
        colors: ['#a7f3d0', '#cffafe', '#ecfeff', '#6ee7b7'],
        size: [2.2 * s, 4.0 * s],
        speed: [26 * s, 72 * s],
        vortex: 210 * s,
        vortexCenter: c,
        gravity: -32 * s,
        turbulence: 68 * s,
        drag: 0.96,
        alpha: 0.82,
        life: [480, 780],
      });

      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 34 * s,
        count: 6,
        shape: 'flare',
        colors: ['#ffffff', '#cffafe', '#6ee7b7'],
        size: [2.0 * s, 3.6 * s],
        speed: [24 * s, 64 * s],
        life: [280, 480],
      });
    });

    VFX.after(520, function () {
      VFX.floatText(targetEl || h, '飞燕功', '#67e8f9', { scale: 0.94, duration: 840 });
    });

    return 1000;
  });

  // ===========================================================================
  // 5. ability.qingxin-jue · 清心诀（一阶）
  //    顶门/眉心冰蓝澄澈玉莲清光 + 静心水蓝 shockwave 涟漪 + 空明 orb 与 rune 灵纹浮升
  // ===========================================================================
  VFX.register('ability.qingxin-jue', function (ctx) {
    var a = resolveInternalAnchors(ctx);
    var targetEl = a.targetEl;
    var s = a.s;
    var c = a.c;
    var f = a.f;
    var h = a.h;

    var browPt = VFX.clampStage({ x: h.x, y: VFX.lerp(h.y, c.y, 0.22) }, 40 * s, 38);

    VFX.cinematicDim(0.12, 540, '#020b1a');
    if (targetEl) VFX.motion(targetEl, 'cast', { duration: 420 });

    // 足底静心柔蓝底晕
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 1040,
      draw: function (g, p) {
        var fade = p < 0.16 ? p / 0.16 : p > 0.72 ? (1 - p) / 0.28 : 1;
        if (fade <= 0.01) return;
        var r = 64 * s * (0.7 + 0.3 * Math.min(1, p * 3));
        g.save();
        g.translate(f.x, f.y + 2 * s);
        g.scale(1, 0.44);
        var grad = g.createRadialGradient(0, 0, 2, 0, 0, r);
        grad.addColorStop(0, 'rgba(224,242,254,' + (0.32 * fade).toFixed(3) + ')');
        grad.addColorStop(0.45, 'rgba(96,165,250,' + (0.22 * fade).toFixed(3) + ')');
        grad.addColorStop(0.8, 'rgba(56,189,248,' + (0.08 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(56,189,248,0)');
        g.fillStyle = grad;
        g.beginPath();
        g.arc(0, 0, r, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });

    // 60ms 顶门/眉心绽放冰蓝澄澈玉莲清光（多瓣纯径向渐变柔晕，零硬花瓣）
    VFX.after(60, function () {
      if (targetEl) VFX.hitFlash(targetEl, '#7dd3fc', 220);

      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 940,
        draw: function (g, p) {
          var bloom = p < 0.24 ? 1 - Math.pow(1 - p / 0.24, 3) : 1;
          var fade = p < 0.16 ? p / 0.16 : p > 0.7 ? (1 - p) / 0.3 : 1;
          if (fade <= 0.01) return;

          var R = 42 * s * (0.4 + 0.6 * bloom);

          g.save();
          g.translate(browPt.x, browPt.y);

          // 灵台空明核心光轮
          var coreG = g.createRadialGradient(0, 0, 1, 0, 0, R * 1.18);
          coreG.addColorStop(0, 'rgba(255,255,255,' + (0.58 * fade).toFixed(3) + ')');
          coreG.addColorStop(0.32, 'rgba(186,230,253,' + (0.38 * fade).toFixed(3) + ')');
          coreG.addColorStop(0.68, 'rgba(96,165,250,' + (0.16 * fade).toFixed(3) + ')');
          coreG.addColorStop(1, 'rgba(56,189,248,0)');
          g.fillStyle = coreG;
          g.beginPath();
          g.arc(0, 0, R * 1.18, 0, TWO_PI);
          g.fill();

          // 外圈 8 瓣冰蓝玉莲柔晕 + 内圈 4 瓣素白莲芯柔晕
          g.save();
          g.rotate(p * 0.55);
          for (var k = 0; k < 8; k++) {
            var ka = (k * TWO_PI) / 8;
            var px = Math.cos(ka) * R * 0.54;
            var py = Math.sin(ka) * R * 0.54;
            g.save();
            g.translate(px, py);
            g.rotate(ka);
            g.scale(1.55, 0.56);
            var pGrad = g.createRadialGradient(0, 0, 0, 0, 0, R * 0.42);
            pGrad.addColorStop(0, k % 2 === 0 ? 'rgba(224,242,254,' + (0.54 * fade).toFixed(3) + ')' : 'rgba(147,197,253,' + (0.46 * fade).toFixed(3) + ')');
            pGrad.addColorStop(0.55, 'rgba(56,189,248,' + (0.2 * fade).toFixed(3) + ')');
            pGrad.addColorStop(1, 'rgba(56,189,248,0)');
            g.fillStyle = pGrad;
            g.beginPath();
            g.arc(0, 0, R * 0.42, 0, TWO_PI);
            g.fill();
            g.restore();
          }
          g.restore();

          g.save();
          g.rotate(-p * 0.4);
          for (var m = 0; m < 4; m++) {
            var ma = (m * TWO_PI) / 4 + Math.PI * 0.25;
            var mx = Math.cos(ma) * R * 0.34;
            var my = Math.sin(ma) * R * 0.34;
            g.save();
            g.translate(mx, my);
            g.rotate(ma);
            g.scale(1.4, 0.55);
            var mGrad = g.createRadialGradient(0, 0, 0, 0, 0, R * 0.32);
            mGrad.addColorStop(0, 'rgba(255,255,255,' + (0.62 * fade).toFixed(3) + ')');
            mGrad.addColorStop(0.52, 'rgba(186,230,253,' + (0.26 * fade).toFixed(3) + ')');
            mGrad.addColorStop(1, 'rgba(186,230,253,0)');
            g.fillStyle = mGrad;
            g.beginPath();
            g.arc(0, 0, R * 0.32, 0, TWO_PI);
            g.fill();
            g.restore();
          }
          g.restore();

          g.restore();
        },
      });

      // 静心水蓝三重涟漪向四周涤荡
      var rippleDelays = [0, 140, 290];
      var rippleColors = ['#60a5fa', '#38bdf8', '#93c5fd'];
      for (var rIdx = 0; rIdx < rippleDelays.length; rIdx++) {
        VFX.shockwave({
          delay: rippleDelays[rIdx],
          x: browPt.x,
          y: browPt.y,
          fromRadius: 8,
          toRadius: 92 + rIdx * 24,
          color: rippleColors[rIdx],
          coreColor: '#e0f2fe',
          lineWidth: 2.6,
          double: false,
          duration: 480 + rIdx * 40,
        });
      }

      // 空明 orb 与清心 rune 灵纹冉冉浮升
      for (var wave = 0; wave < 3; wave++) {
        (function (wIdx) {
          VFX.after(wIdx * 130, function () {
            VFX.burst({
              x: c.x,
              y: VFX.lerp(c.y, browPt.y, wIdx * 0.35),
              radius: 34 * s,
              tiltY: 0.52,
              count: 6,
              shape: 'orb',
              colors: ['#e0f2fe', '#93c5fd', '#7dd3fc', '#ffffff'],
              size: [1.8 * s, 3.8 * s],
              speed: [12 * s, 34 * s],
              angle: [-Math.PI * 0.72, -Math.PI * 0.28],
              gravity: -52 * s,
              turbulence: 28 * s,
              drag: 0.96,
              alpha: 0.86,
              life: [540, 860],
              trail: 3,
            });

            VFX.burst({
              x: c.x,
              y: c.y - 4 * s,
              radius: 38 * s,
              ring: true,
              tiltY: 0.62,
              count: 4,
              shape: 'rune',
              colors: ['#93c5fd', '#7dd3fc', '#e0f2fe'],
              size: [3.2 * s, 5.0 * s],
              speed: [10 * s, 28 * s],
              angle: [-Math.PI * 0.68, -Math.PI * 0.32],
              gravity: -44 * s,
              spin: [-1.8, 1.8],
              drag: 0.96,
              alpha: 0.88,
              life: [540, 840],
            });
          });
        })(wave);
      }

      VFX.burst({
        x: browPt.x,
        y: browPt.y,
        radius: 26 * s,
        count: 7,
        shape: 'flare',
        colors: ['#ffffff', '#e0f2fe', '#93c5fd'],
        size: [2.0 * s, 3.8 * s],
        speed: [14 * s, 44 * s],
        gravity: -38 * s,
        life: [360, 620],
      });
    });

    VFX.after(640, function () {
      VFX.floatText(targetEl || h, '清心诀', '#7dd3fc', { scale: 0.94, duration: 880 });
    });

    return 1140;
  });
});
