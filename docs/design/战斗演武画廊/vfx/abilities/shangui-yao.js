/**
 * ability.shangui-yao · 山鬼谣 —— 二阶符箓全体友方甘露群疗主动技能（深度流体粒子化重构版）
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   己方全体 / 吟唱 3000ms / 施加 12%AP+5 层「甘露」（10s） / 冷却 35s / 灵力 40 / 符箓·水
 * 演出阶段：
 *   0ms        幽翠暗幕 + 轻盈浮空（levitate）+ 足底碧水符阵 + 半空高斯柔羽远山云岚与渐变流云丝带 + 灵兰花瓣与楚辞祝文
 *   180ms      向每位存活友方上空飞出双股交织碧水天青音律丝带（沿途播撒翠玉花瓣与晨露）
 *   380..1350ms 全体友方头顶齐凝柔雾灵云，分 4 波倾洒晶莹甘霖雨丝；脚下青蓝水光池、玉阵与多层水波荡漾，
 *              周身双螺旋碧翠回春灵雾、灵兰花瓣、晨露与星芒升腾并飘出「甘露」浮字
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var localPlumeCache = Object.create(null);

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
   * 获取双向高斯柔雾羽流精灵（用于远山云岚叠画，边缘 alpha 严格归零）
   */
  function getPlume(color) {
    if (typeof VFX.getPlumeSprite === 'function') return VFX.getPlumeSprite(color);
    if (localPlumeCache[color]) return localPlumeCache[color];
    if (typeof document === 'undefined') return null;

    var c = document.createElement('canvas');
    if (!c || !c.getContext) return null;
    c.width = 128;
    c.height = 128;
    var gctx = c.getContext('2d');
    var rgb = colorToRgb(color);
    var lobes = [
      { x: 64, y: 64, r: 58, peak: 0.66 },
      { x: 48, y: 58, r: 42, peak: 0.3 },
      { x: 80, y: 62, r: 40, peak: 0.28 },
      { x: 62, y: 74, r: 36, peak: 0.24 },
    ];
    for (var i = 0; i < lobes.length; i++) {
      var lb = lobes[i];
      var grad = gctx.createRadialGradient(lb.x, lb.y, 1, lb.x, lb.y, lb.r);
      grad.addColorStop(0, 'rgba(' + rgb + ',' + lb.peak + ')');
      grad.addColorStop(0.38, 'rgba(' + rgb + ',' + (lb.peak * 0.44).toFixed(3) + ')');
      grad.addColorStop(0.74, 'rgba(' + rgb + ',' + (lb.peak * 0.1).toFixed(3) + ')');
      grad.addColorStop(1, 'rgba(' + rgb + ',0)');
      gctx.fillStyle = grad;
      gctx.beginPath();
      gctx.arc(lb.x, lb.y, lb.r, 0, TWO_PI);
      gctx.fill();
    }
    localPlumeCache[color] = c;
    return c;
  }

  /**
   * 绘制首尾渐变归零的流云音律丝带（零硬边弧块）
   */
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

  /**
   * 在单名友方头顶与周身释放柔雾灵云、4 波甘霖雨丝与双螺旋碧翠回春灵雾
   */
  function emitAllySweetRain(tEl, safePt, safeFoot, topPt, s) {
    var rainHeight = Math.max(72 * s, safeFoot.y - topPt.y);

    // 1. 头顶柔雾灵云与轻柔雨幕薄纱（纯径向渐变边缘归零）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 920,
      draw: function (c2d, p) {
        var open = p < 0.2 ? 1 - Math.pow(1 - p / 0.2, 3) : 1;
        var fade = p < 0.16 ? p / 0.16 : p > 0.7 ? Math.max(0, (1 - p) / 0.3) : 1;
        var cloudR = 60 * s * (0.66 + 0.34 * open);

        c2d.save();
        c2d.translate(topPt.x, topPt.y);

        // 头顶碧青灵云霭
        c2d.save();
        c2d.scale(1.45, 0.45);
        var cg = c2d.createRadialGradient(0, 0, 2, 0, 0, cloudR);
        cg.addColorStop(0, 'rgba(224,242,254,' + (0.46 * fade).toFixed(3) + ')');
        cg.addColorStop(0.4, 'rgba(94,234,212,' + (0.3 * fade).toFixed(3) + ')');
        cg.addColorStop(0.75, 'rgba(56,189,248,' + (0.12 * fade).toFixed(3) + ')');
        cg.addColorStop(1, 'rgba(56,189,248,0)');
        c2d.fillStyle = cg;
        c2d.beginPath();
        c2d.arc(0, 0, cloudR, 0, TWO_PI);
        c2d.fill();
        c2d.restore();

        // 垂落轻柔甘露雨纱（上下左右均自然归零）
        c2d.save();
        c2d.translate(0, rainHeight * 0.45);
        c2d.scale(0.68, 1.24);
        var veilR = rainHeight * 0.56;
        var vg = c2d.createRadialGradient(0, 0, 2, 0, 0, veilR);
        vg.addColorStop(0, 'rgba(94,234,212,' + (0.18 * fade).toFixed(3) + ')');
        vg.addColorStop(0.55, 'rgba(56,189,248,' + (0.09 * fade).toFixed(3) + ')');
        vg.addColorStop(1, 'rgba(56,189,248,0)');
        c2d.fillStyle = vg;
        c2d.beginPath();
        c2d.arc(0, 0, veilR, 0, TWO_PI);
        c2d.fill();
        c2d.restore();

        c2d.restore();
      },
    });

    // 轻量飘雾灵云 smoke + 盘旋云气 wisp
    VFX.burst({
      x: topPt.x,
      y: topPt.y,
      radius: 38 * s,
      tiltY: 0.34,
      count: 8,
      shape: 'smoke',
      blend: 'lighter',
      colors: ['#ccfbf1', '#bae6fd', '#5eead4'],
      size: [14 * s, 24 * s],
      speed: [10 * s, 26 * s],
      alpha: [0.16, 0.28],
      drag: 0.96,
      life: [540, 860],
    });
    VFX.burst({
      x: topPt.x,
      y: topPt.y,
      radius: 34 * s,
      tiltY: 0.36,
      count: 8,
      shape: 'wisp',
      colors: ['#5eead4', '#e0f2fe', '#38bdf8'],
      size: [3.8 * s, 7.0 * s],
      speed: [14 * s, 36 * s],
      vortex: 140 * s,
      vortexCenter: topPt,
      alpha: 0.56,
      life: [420, 720],
    });

    // 2. 分 4 波倾洒晶莹 drop 漫天甘霖雨丝
    for (var w = 0; w < 4; w++) {
      (function (waveIdx) {
        var at = waveIdx * 125;
        VFX.burst({
          delay: at,
          x: topPt.x,
          y: topPt.y + 4 * s,
          radius: 34 * s,
          tiltY: 0.28,
          count: 13,
          shape: 'drop',
          colors: ['#2dd4bf', '#38bdf8', '#e0f2fe', '#ffffff'],
          size: [2.1 * s, 3.7 * s],
          speed: [88 * s, 152 * s],
          angle: [Math.PI * 0.45, Math.PI * 0.55],
          gravity: 170 * s,
          drag: 0.985,
          life: [380, 620],
          trail: 4,
        });
        VFX.burst({
          delay: at + 18,
          x: topPt.x,
          y: topPt.y + 8 * s,
          radius: 28 * s,
          tiltY: 0.25,
          count: 4,
          shape: 'wisp',
          colors: ['#bae6fd', '#5eead4'],
          size: [2.8 * s, 5.2 * s],
          speed: [72 * s, 126 * s],
          angle: [Math.PI * 0.46, Math.PI * 0.54],
          gravity: 105 * s,
          alpha: 0.42,
          life: [340, 540],
        });
      })(w);
    }

    // 3. 脚下青蓝水光池、water 玉阵与多层高斯水波 shockwave
    VFX.custom({
      delay: 110,
      layer: 'ground',
      blend: 'lighter',
      duration: 860,
      draw: function (c2d, p) {
        var fade = p < 0.2 ? p / 0.2 : p > 0.72 ? Math.max(0, (1 - p) / 0.28) : 1;
        var r = 64 * s * (0.68 + 0.32 * Math.min(1, p * 3));
        c2d.save();
        c2d.translate(safeFoot.x, safeFoot.y);
        c2d.scale(1, 0.44);
        var pool = c2d.createRadialGradient(0, 0, 2, 0, 0, r);
        pool.addColorStop(0, 'rgba(94,234,212,' + (0.38 * fade).toFixed(3) + ')');
        pool.addColorStop(0.42, 'rgba(56,189,248,' + (0.26 * fade).toFixed(3) + ')');
        pool.addColorStop(0.78, 'rgba(125,211,252,' + (0.09 * fade).toFixed(3) + ')');
        pool.addColorStop(1, 'rgba(56,189,248,0)');
        c2d.fillStyle = pool;
        c2d.beginPath();
        c2d.arc(0, 0, r, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    VFX.magicCircle({
      delay: 110,
      x: safeFoot.x,
      y: safeFoot.y,
      radius: 52,
      tilt: 0.44,
      layer: 'ground',
      style: 'water',
      color: '#2dd4bf',
      secondaryColor: '#e0f2fe',
      rotationSpeed: -1.1,
      duration: 860,
      alpha: 0.72,
      lineWidth: 1.5,
    });

    var rippleDelays = [120, 270, 440];
    for (var rd = 0; rd < rippleDelays.length; rd++) {
      (function (at, idx) {
        VFX.shockwave({
          delay: at,
          x: safeFoot.x + VFX.rand(-8, 8) * s,
          y: safeFoot.y + VFX.rand(-3, 3) * s,
          fromRadius: 10,
          toRadius: 54 + idx * 11,
          tilt: 0.44,
          layer: 'ground',
          color: idx % 2 === 0 ? '#2dd4bf' : '#38bdf8',
          coreColor: '#e0f2fe',
          lineWidth: 2.4,
          double: false,
          duration: 480,
        });
        VFX.burst({
          delay: at + 16,
          x: safeFoot.x,
          y: safeFoot.y,
          radius: 24 * s,
          tiltY: 0.42,
          count: 5,
          shape: 'drop',
          colors: ['#5eead4', '#bae6fd', '#ffffff'],
          size: [1.7 * s, 2.9 * s],
          speed: [32 * s, 74 * s],
          angle: [-Math.PI * 0.85, -Math.PI * 0.15],
          gravity: 185 * s,
          life: [220, 380],
        });
      })(rippleDelays[rd], rd);
    }

    // 4. 周身双螺旋碧翠回春 wisp 灵雾、灵兰 petal、晨露 orb 与星芒 flare
    var riseSteps = 5;
    for (var rs = 0; rs < riseSteps; rs++) {
      (function (step) {
        VFX.after(120 + step * 95, function () {
          var u = step / (riseSteps - 1);
          var ang = step * 1.48;
          var rr = VFX.lerp(20, 34, u) * s;

          for (var arm = 0; arm < 2; arm++) {
            var a = ang + arm * Math.PI;
            var sx = safeFoot.x + Math.cos(a) * rr;
            var sy = safeFoot.y - (8 + u * 36) * s + Math.sin(a) * rr * 0.36;
            var vx = -Math.sin(a) * 66 * s;
            var vy = Math.cos(a) * 22 * s - 64 * s;

            VFX.burst({
              x: sx,
              y: sy,
              radius: 10 * s,
              count: 2,
              shape: 'wisp',
              colors: ['#2dd4bf', '#5eead4', '#38bdf8', '#a7f3d0'],
              size: [4.0 * s, 7.8 * s],
              endSize: [2.0 * s, 4.0 * s],
              speed: [6 * s, 16 * s],
              vx: vx,
              vy: vy,
              gravity: -52 * s,
              turbulence: 56 * s,
              drag: 0.96,
              alpha: [0.46, 0.7],
              life: [460, 760],
            });
          }

          VFX.burst({
            x: safeFoot.x,
            y: safeFoot.y - (10 + u * 24) * s,
            radius: 28 * s,
            tiltY: 0.44,
            count: 3,
            shape: 'orb',
            colors: ['#6ee7b7', '#5eead4', '#bae6fd', '#ffffff'],
            size: [1.8 * s, 3.6 * s],
            speed: [18 * s, 50 * s],
            angle: [-Math.PI * 0.78, -Math.PI * 0.22],
            gravity: -62 * s,
            drag: 0.96,
            life: [480, 800],
            trail: 4,
          });

          if (step % 2 === 0) {
            VFX.burst({
              x: safePt.x,
              y: safePt.y + (6 - u * 16) * s,
              radius: 26 * s,
              count: 4,
              shape: 'petal',
              colors: ['#99f6e4', '#5eead4', '#a7f3d0', '#ffffff'],
              size: [2.6 * s, 4.6 * s],
              speed: [20 * s, 56 * s],
              gravity: -38 * s,
              turbulence: 48 * s,
              life: [480, 780],
            });
          }

          VFX.burst({
            x: safePt.x,
            y: safePt.y + (6 - u * 18) * s,
            radius: 24 * s,
            count: 3,
            shape: 'flare',
            colors: ['#ffffff', '#a7f3d0', '#bae6fd'],
            size: [2.0 * s, 3.8 * s],
            speed: [16 * s, 46 * s],
            gravity: -52 * s,
            drag: 0.94,
            life: [320, 560],
          });
        });
      })(rs);
    }

    if (tEl) {
      VFX.hitFlash(tEl, '#5eead4', 220);
    }
    VFX.floatText(tEl || safePt, '甘露', 'heal', { scale: 0.98, rise: 38 });
  }

  VFX.register('ability.shangui-yao', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var isEnemyCaster = ctx.side === 'enemy' || !!(casterEl && casterEl.classList && (casterEl.classList.contains('is-enemy') || casterEl.classList.contains('arena-actor--enemy')));
    var allySide = isEnemyCaster ? 'enemy' : 'ally';
    var pool = isEnemyCaster ? ctx.enemyEls : ctx.allyEls;
    var targets = (ctx.targetEls && ctx.targetEls.length > 1) ? ctx.targetEls.slice() : (pool && pool.length > 1 ? pool.slice() : (ctx.targetEls && ctx.targetEls.length ? ctx.targetEls.slice() : (ctx.targetEl ? [ctx.targetEl] : (casterEl ? [casterEl] : []))));

    var s = VFX.scale();
    var st = VFX.size();
    var cRaw = casterEl ? VFX.center(casterEl) : VFX.sideCenter(allySide);
    var cFootRaw = casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(allySide);
    var cPt = VFX.clampStage(cRaw, 34 * s, 30 * s);
    var cFoot = VFX.clampStage(cFootRaw, 38 * s, 22 * s);
    var topPadX = Math.min(40 * s, st.w * 0.4);

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：幽翠山岚暗幕 + 轻盈浮空 + 碧水符阵 + 远山云岚与渐变流云丝带
    // -------------------------------------------------------------------------
    VFX.cinematicDim(0.28, 860, '#021618');
    if (casterEl) {
      VFX.motion(casterEl, 'levitate', { duration: 520 });
    }

    // 足底碧水柔光阵与水纹玉阵
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 1120,
      draw: function (c2d, p) {
        var fade = p < 0.18 ? p / 0.18 : p > 0.72 ? Math.max(0, (1 - p) / 0.28) : 1;
        var rad = 92 * s * (p < 0.22 ? VFX.lerp(0.5, 1.0, p / 0.22) : 1);
        c2d.save();
        c2d.translate(cFoot.x, cFoot.y);
        c2d.scale(1, 0.44);
        var g = c2d.createRadialGradient(0, 0, 2 * s, 0, 0, rad);
        g.addColorStop(0, 'rgba(153,246,228,' + (0.34 * fade).toFixed(3) + ')');
        g.addColorStop(0.44, 'rgba(45,212,191,' + (0.24 * fade).toFixed(3) + ')');
        g.addColorStop(0.8, 'rgba(56,189,248,' + (0.09 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(15,118,110,0)');
        c2d.fillStyle = g;
        c2d.beginPath();
        c2d.arc(0, 0, rad, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    VFX.magicCircle({
      x: cFoot.x,
      y: cFoot.y,
      radius: 74,
      tilt: 0.44,
      layer: 'ground',
      style: 'water',
      color: '#2dd4bf',
      secondaryColor: '#38bdf8',
      rotationSpeed: 1.4,
      duration: 1120,
      alpha: 0.76,
      lineWidth: 2.0,
    });

    VFX.magicCircle({
      delay: 30,
      x: cFoot.x,
      y: cFoot.y,
      radius: 48,
      tilt: 0.44,
      layer: 'ground',
      style: 'spirit',
      color: '#5eead4',
      secondaryColor: '#fef08a',
      rotationSpeed: -1.8,
      duration: 1060,
      alpha: 0.68,
      lineWidth: 1.5,
    });

    // 半空高斯柔羽 plume 远山云岚与渐变流云丝带意象
    var mistDeep = getPlume('#0f766e');
    var mistJade = getPlume('#2dd4bf');
    var mistSky = getPlume('#bae6fd');
    var mistAnchor = VFX.clampStage({ x: cPt.x, y: Math.max(36, cPt.y - 28 * s) }, 56 * s, 32 * s);

    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 880,
      draw: function (c2d, p) {
        var open = p < 0.2 ? 1 - Math.pow(1 - p / 0.2, 3) : 1;
        var fade = p < 0.16 ? p / 0.16 : p > 0.7 ? Math.max(0, (1 - p) / 0.3) : 1;
        if (fade <= 0.01) return;

        c2d.save();
        c2d.translate(mistAnchor.x, mistAnchor.y);

        // 7 团高斯柔羽远山云岚叠画
        for (var m = 0; m < 7; m++) {
          var mu = (m / 6) - 0.5;
          var mx = mu * 96 * s + Math.sin(p * 3.8 + m) * 10 * s;
          var my = -Math.cos(mu * Math.PI) * 18 * s + Math.cos(p * 3.2 + m) * 4 * s;
          var rx = (34 + (1 - Math.abs(mu)) * 18) * s * (0.7 + 0.3 * open);
          var ry = rx * 0.54;
          var sp = m % 3 === 0 ? mistDeep : m % 3 === 1 ? mistJade : mistSky;
          if (sp) {
            c2d.globalAlpha = fade * (m % 3 === 2 ? 0.28 : 0.34);
            c2d.drawImage(sp, mx - rx, my - ry, rx * 2, ry * 2);
          } else {
            var mg = c2d.createRadialGradient(mx, my, 1, mx, my, rx);
            mg.addColorStop(0, 'rgba(94,234,212,' + (0.28 * fade).toFixed(3) + ')');
            mg.addColorStop(1, 'rgba(45,212,191,0)');
            c2d.fillStyle = mg;
            c2d.beginPath();
            c2d.arc(mx, my, rx, 0, TWO_PI);
            c2d.fill();
          }
        }

        // 3 条渐变流云音律丝带环拂山岚
        var ribbons = [
          { yOff: -6, span: 68, amp: -24, thick: 5.4, rgb: '94,234,212', spd: 1.0 },
          { yOff: 10, span: 78, amp: 26, thick: 4.8, rgb: '56,189,248', spd: -0.9 },
          { yOff: -18, span: 56, amp: -18, thick: 3.6, rgb: '254,240,138', spd: 1.2 },
        ];
        for (var r = 0; r < ribbons.length; r++) {
          var rb = ribbons[r];
          var sway = Math.sin(p * 6.5 * rb.spd + r) * 10 * s;
          drawFeatheredSilkArc(
            c2d,
            -rb.span * s * open,
            rb.yOff * s + sway * 0.3,
            sway,
            (rb.yOff + rb.amp) * s,
            rb.span * s * open,
            rb.yOff * s - sway * 0.3,
            rb.thick * s,
            rb.rgb,
            0.52 * fade
          );
        }

        c2d.restore();
      },
    });

    // 灵兰 petal 与 rune 楚辞祝文伴随轻盈灵雾飘扬
    VFX.burst({
      x: cPt.x,
      y: cPt.y,
      radius: 30 * s,
      count: 16,
      shape: 'petal',
      colors: ['#99f6e4', '#5eead4', '#bbf7d0', '#ffffff'],
      size: [3.0 * s, 5.4 * s],
      speed: [28 * s, 82 * s],
      vortex: 145 * s,
      vortexCenter: cPt,
      gravity: -46 * s,
      life: [520, 860],
    });
    VFX.burst({
      x: cPt.x,
      y: cPt.y - 6 * s,
      radius: 34 * s,
      count: 10,
      shape: 'rune',
      colors: ['#2dd4bf', '#fde047', '#bae6fd'],
      size: [3.6 * s, 6.0 * s],
      speed: [22 * s, 58 * s],
      vortex: -135 * s,
      vortexCenter: cPt,
      gravity: -54 * s,
      life: [540, 880],
    });
    VFX.burst({
      x: cPt.x,
      y: cPt.y,
      radius: 28 * s,
      count: 12,
      shape: 'wisp',
      colors: ['#2dd4bf', '#bae6fd', '#a7f3d0'],
      size: [4.2 * s, 7.6 * s],
      speed: [24 * s, 62 * s],
      gravity: -48 * s,
      alpha: 0.62,
      life: [420, 720],
    });

    // -------------------------------------------------------------------------
    // 阶段二（180ms）→ 阶段三（380ms~1350ms）：
    // 向每位存活友方上空飞出双股交织碧水天青音律丝带，齐凝柔雾灵云与 4 波甘霖雨丝
    // -------------------------------------------------------------------------
    var totalT = 1480;
    var count = Math.max(1, targets.length);
    for (var i = 0; i < count; i++) {
      (function (idx) {
        var el = targets[idx] || null;
        var pt = el ? VFX.center(el) : VFX.sideCenter(allySide);
        var hd = el ? VFX.head(el) : { x: pt.x, y: pt.y - 38 * s };
        var ft = el ? VFX.foot(el) : { x: pt.x, y: pt.y + 38 * s };
        var safePt = VFX.clampStage(pt, 32 * s, 28 * s);
        var safeFoot = VFX.clampStage(ft, 34 * s, 20 * s);
        var topPt = {
          x: VFX.clamp(safePt.x, topPadX, Math.max(topPadX, st.w - topPadX)),
          y: VFX.clamp(hd.y - 50 * s, 24, Math.max(28, safePt.y - 26)),
        };

        var isSelf = Math.hypot(safePt.x - cPt.x, safePt.y - cPt.y) < 28 * s;
        var sStart = VFX.clampStage(
          isSelf ? { x: cPt.x, y: cPt.y - 16 * s } : { x: cPt.x, y: cPt.y - 10 * s },
          24 * s,
          22 * s
        );

        var ribbonDelay = 180 + idx * 45;
        var rainDelay = 380 + idx * 45;
        var arcMag = (isSelf ? 44 : 52) * s;

        // 双股交织碧水天青音律丝带
        VFX.streamer({
          delay: ribbonDelay,
          x1: sStart.x,
          y1: sStart.y,
          x2: topPt.x,
          y2: topPt.y,
          arc: -arcMag,
          wobble: 10 * s,
          width: 7.5 * s,
          tailLength: 0.45,
          duration: 245,
          color: '#2dd4bf',
          coreColor: '#e0f2fe',
          sparkColors: ['#5eead4', '#bae6fd', '#ffffff'],
        });
        VFX.streamer({
          delay: ribbonDelay + 14,
          x1: sStart.x,
          y1: sStart.y,
          x2: topPt.x,
          y2: topPt.y,
          arc: arcMag * 0.85,
          wobble: 9 * s,
          width: 6.5 * s,
          tailLength: 0.44,
          duration: 245,
          color: '#38bdf8',
          coreColor: '#ccfbf1',
          sparkColors: ['#7dd3fc', '#99f6e4', '#ffffff'],
        });

        // 沿音律丝带路径播撒翠玉花瓣与晨露 orb
        for (var sp = 1; sp <= 3; sp++) {
          (function (stepIdx) {
            var u = stepIdx / 4;
            var px = VFX.lerp(sStart.x, topPt.x, u);
            var py = VFX.lerp(sStart.y, topPt.y, u) - Math.sin(u * Math.PI) * 24 * s;
            VFX.burst({
              delay: ribbonDelay + stepIdx * 42,
              x: px,
              y: py,
              radius: 12 * s,
              count: 3,
              shape: 'petal',
              colors: ['#99f6e4', '#5eead4', '#bbf7d0'],
              size: [2.6 * s, 4.4 * s],
              speed: [18 * s, 48 * s],
              gravity: -18 * s,
              life: [380, 620],
            });
            VFX.burst({
              delay: ribbonDelay + stepIdx * 42 + 12,
              x: px,
              y: py,
              radius: 10 * s,
              count: 3,
              shape: 'orb',
              colors: ['#e0f2fe', '#7dd3fc', '#6ee7b7'],
              size: [1.8 * s, 3.4 * s],
              speed: [14 * s, 40 * s],
              life: [360, 580],
              trail: 3,
            });
          })(sp);
        }

        // 阶段三（380ms~1350ms）：头顶凝云 + 4 波甘霖 + 脚下水池玉阵波纹 + 螺旋灵雾
        VFX.after(rainDelay, function () {
          emitAllySweetRain(el, safePt, safeFoot, topPt, s);
        });

        totalT = Math.max(totalT, rainDelay + 1040);
      })(i);
    }

    return totalT;
  });
});
