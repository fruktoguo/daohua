/**
 * ability.tianlei-po · 天雷破 —— 二阶木系神雷打击至多两名敌人并施加麻痹（深度流体粒子化重构版）
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   至多 2 名敌人 / 吟唱 2000ms / 1.1×AP+50 + 5s「麻痹」 / 冷却 18s / 灵力 20 / 五行·木
 * 演出阶段：
 *   0ms        苍翠暗幕 + 施法仰首蓄咒 + 指尖乙木雷印内吸聚电 + 青碧引雷链直冲敌阵高空 + 深翠雷云翻滚闷雷
 *   135ms      对至多 2 名目标错峰 90ms 劈落三重色温高密分形神雷链 + 电离 wisp/flare + 落点电浆光池与青木地裂
 *   280..820ms 受击目标周身缠绕青黄双色旋转收缩渐变电弧环 + 4 波跳跃微缩分形麻痹电弧 + 迟滞电星与「麻痹」浮字
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var localGlowCache = Object.create(null);
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
   * 获取高羽化辉光精灵（边缘 alpha 严格归零）
   */
  function getSoftGlow(color) {
    if (typeof VFX.getGlowSprite === 'function') return VFX.getGlowSprite(color);
    if (localGlowCache[color]) return localGlowCache[color];
    if (typeof document === 'undefined') return null;

    var c = document.createElement('canvas');
    if (!c || !c.getContext) return null;
    c.width = 96;
    c.height = 96;
    var gctx = c.getContext('2d');
    var rgb = colorToRgb(color);
    var grad = gctx.createRadialGradient(48, 48, 0, 48, 48, 45);
    grad.addColorStop(0, 'rgba(255,255,255,0.98)');
    grad.addColorStop(0.16, 'rgba(' + rgb + ',0.8)');
    grad.addColorStop(0.45, 'rgba(' + rgb + ',0.34)');
    grad.addColorStop(0.78, 'rgba(' + rgb + ',0.08)');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    gctx.fillStyle = grad;
    gctx.beginPath();
    gctx.arc(48, 48, 46, 0, TWO_PI);
    gctx.fill();
    localGlowCache[color] = c;
    return c;
  }

  /**
   * 获取多瓣柔羽云团精灵（边缘 alpha 严格归零）
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
      { x: 64, y: 64, r: 58, peak: 0.68 },
      { x: 48, y: 56, r: 42, peak: 0.32 },
      { x: 80, y: 60, r: 40, peak: 0.3 },
      { x: 62, y: 76, r: 36, peak: 0.26 },
    ];
    for (var i = 0; i < lobes.length; i++) {
      var lb = lobes[i];
      var grad = gctx.createRadialGradient(lb.x, lb.y, 1, lb.x, lb.y, lb.r);
      grad.addColorStop(0, 'rgba(' + rgb + ',' + lb.peak + ')');
      grad.addColorStop(0.38, 'rgba(' + rgb + ',' + (lb.peak * 0.45).toFixed(3) + ')');
      grad.addColorStop(0.74, 'rgba(' + rgb + ',' + (lb.peak * 0.11).toFixed(3) + ')');
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
   * 绘制首尾渐变归零的羽化麻痹电弧（零硬边弧线）
   */
  function drawFeatheredArc(c2d, x0, y0, cx, cy, x1, y1, thickness, rgb, alpha) {
    if (alpha <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.26, 'rgba(' + rgb + ',' + (alpha * 0.64).toFixed(3) + ')');
    grad.addColorStop(0.52, 'rgba(' + rgb + ',' + alpha.toFixed(3) + ')');
    grad.addColorStop(0.8, 'rgba(' + rgb + ',' + (alpha * 0.48).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(x0, y0);
    c2d.quadraticCurveTo(cx + nx * thickness, cy + ny * thickness, x1, y1);
    c2d.quadraticCurveTo(cx - nx * thickness * 0.72, cy - ny * thickness * 0.72, x0, y0);
    c2d.fill();
  }

  function resolveTargetDamage(ctx, idx, targetCount, fallback) {
    if (Array.isArray(ctx.damage)) return ctx.damage[idx] != null ? ctx.damage[idx] : fallback;
    if (ctx.perTargetDamage != null) return ctx.perTargetDamage;
    if (ctx.damage != null) {
      return targetCount > 1 ? Math.round(ctx.damage / targetCount) : ctx.damage;
    }
    return fallback;
  }

  /**
   * 为受击目标缠绕青黄双色旋转收缩电弧环 + 4 波跳跃微缩分形麻痹电弧与迟滞电星
   */
  function emitParalysisField(tEl, safePt, s) {
    var arcNodeGlow = getSoftGlow('#facc15');
    var jadeNodeGlow = getSoftGlow('#10b981');

    // 青黄双色交错旋转收缩渐变电弧环（540ms，跨越 280ms~820ms 窗口）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 540,
      draw: function (c2d, p) {
        var fade = p < 0.16 ? p / 0.16 : p > 0.7 ? Math.max(0, (1 - p) / 0.3) : 1;
        if (fade <= 0.01) return;

        // 略微向体表收紧后再产生麻痹痉挛脉动
        var contract = VFX.lerp(1.08, 0.84, Math.min(1, p * 1.15));
        var jitter = Math.sin(p * 38) * 1.8 * s;
        var rx = 44 * s * contract + jitter;
        var ry = 22 * s * contract;

        c2d.save();
        c2d.translate(safePt.x, safePt.y);

        // 中心麻痹电浆内晕
        var bgG = c2d.createRadialGradient(0, 0, 2, 0, 0, rx * 1.12);
        bgG.addColorStop(0, 'rgba(167,243,208,' + (0.28 * fade).toFixed(3) + ')');
        bgG.addColorStop(0.55, 'rgba(250,204,21,' + (0.2 * fade).toFixed(3) + ')');
        bgG.addColorStop(1, 'rgba(16,185,129,0)');
        c2d.fillStyle = bgG;
        c2d.beginPath();
        c2d.ellipse(0, 0, rx * 1.12, ry * 1.4, 0, 0, TWO_PI);
        c2d.fill();

        // 两条交叉倾斜的青黄渐变电弧环
        var rings = [
          { rot: 0.36, dir: 1, rgb: '16,185,129', core: '167,243,208', glow: jadeNodeGlow, oy: -4 * s },
          { rot: -0.38, dir: -1.15, rgb: '250,204,21', core: '254,240,138', glow: arcNodeGlow, oy: 4 * s },
        ];
        for (var r = 0; r < rings.length; r++) {
          var rg = rings[r];
          c2d.save();
          c2d.translate(0, rg.oy);
          c2d.rotate(rg.rot);
          for (var seg = 0; seg < 2; seg++) {
            var a0 = p * 7.8 * rg.dir + seg * Math.PI + r * 0.9;
            var a1 = a0 + 2.05;
            var aMid = (a0 + a1) * 0.5;
            var x0 = Math.cos(a0) * rx;
            var y0 = Math.sin(a0) * ry;
            var cx = Math.cos(aMid) * rx * 1.2;
            var cy = Math.sin(aMid) * ry * 1.2;
            var x1 = Math.cos(a1) * rx;
            var y1 = Math.sin(a1) * ry;
            drawFeatheredArc(c2d, x0, y0, cx, cy, x1, y1, 4.4 * s, rg.rgb, 0.58 * fade);
            drawFeatheredArc(c2d, x0, y0, cx, cy, x1, y1, 1.8 * s, rg.core, 0.88 * fade);

            var nx = Math.cos(aMid) * rx;
            var ny = Math.sin(aMid) * ry;
            if (rg.glow) {
              var nr = 8.5 * s;
              c2d.globalAlpha = fade * 0.72;
              c2d.drawImage(rg.glow, nx - nr, ny - nr, nr * 2, nr * 2);
            }
          }
          c2d.restore();
        }
        c2d.restore();
      },
    });

    // 4 波跳跃微缩分形麻痹电弧 + 迟滞电星
    for (var w = 0; w < 4; w++) {
      (function (waveIdx) {
        var at = waveIdx * 120;
        VFX.after(at, function () {
          var a1 = waveIdx * 1.65 + VFX.rand(-0.35, 0.35);
          var a2 = a1 + Math.PI * 0.85 + VFX.rand(-0.4, 0.4);
          var p1 = VFX.clampStage({
            x: safePt.x + Math.cos(a1) * 30 * s,
            y: safePt.y + Math.sin(a1) * 24 * s,
          }, 12, 12);
          var p2 = VFX.clampStage({
            x: safePt.x + Math.cos(a2) * 30 * s,
            y: safePt.y + Math.sin(a2) * 24 * s,
          }, 12, 12);
          var isGold = waveIdx % 2 === 1;

          VFX.lightning({
            x1: p1.x,
            y1: p1.y,
            x2: p2.x,
            y2: p2.y,
            width: 2.6 * s,
            jagged: 0.38,
            segments: 10,
            branches: 2,
            flicker: 4,
            color: isGold ? '#facc15' : '#10b981',
            secondaryColor: isGold ? '#fef08a' : '#a7f3d0',
            glowColor: isGold ? '#d97706' : '#059669',
            haloScale: 1.1,
            coreColor: '#ffffff',
            duration: 145,
          });

          VFX.burst({
            x: safePt.x,
            y: safePt.y,
            radius: 24 * s,
            count: 5,
            shape: 'spark',
            colors: ['#ffffff', '#a7f3d0', '#facc15', '#10b981'],
            size: [1.5 * s, 2.8 * s],
            speed: [22 * s, 68 * s],
            drag: 0.88,
            life: [180, 320],
            trail: 3,
          });
          VFX.burst({
            x: safePt.x,
            y: safePt.y,
            radius: 22 * s,
            count: 3,
            shape: 'flare',
            colors: ['#ffffff', '#fef08a', '#34d399'],
            size: [2.0 * s, 3.6 * s],
            speed: [14 * s, 45 * s],
            drag: 0.89,
            life: [190, 340],
          });
        });
      })(w);
    }

    VFX.floatText(
      tEl ? { x: safePt.x, y: safePt.y - 28 * s } : safePt,
      '麻痹',
      '#facc15',
      { scale: 0.94, rise: 36 }
    );
  }

  VFX.register('ability.tianlei-po', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length)
      ? ctx.targetEls.slice(0, 2)
      : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.enemyEls && ctx.enemyEls.length) {
      targets = ctx.enemyEls.slice(0, 2);
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && (
      casterEl.classList.contains('is-enemy') ||
      casterEl.classList.contains('arena-actor--enemy')
    ));
    var selfSide = isEnemyCaster ? 'enemy' : 'ally';
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    var s = VFX.scale();
    var st = VFX.size();
    var isCrit = !!ctx.crit;
    var targetCount = Math.max(1, targets.length);
    var group = targets.length ? targets : foeSide;
    var axis = VFX.combatAxis(casterEl, group);
    var bounds = VFX.groupBounds(group);

    var cFoot = VFX.clampStage(casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(selfSide), 36 * s, 22 * s);
    var palmPt = VFX.clampStage(axis.palm(22 * s), 22 * s, 22 * s);
    var cloudPadX = Math.min(72 * s, st.w * 0.38);
    var cloudPt = {
      x: VFX.clamp(bounds.cx, cloudPadX, Math.max(cloudPadX, st.w - cloudPadX)),
      y: VFX.clamp(bounds.topHeadY - 96 * s, 28, Math.max(34, bounds.cy - 48)),
    };
    var cloudSpan = Math.min(st.w * 0.44, Math.max(118 * s, bounds.spanX + 96 * s));

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：深翠压境暗幕 + 指尖乙木雷印聚电 + 引雷升空 + 翻滚深翠雷云
    // -------------------------------------------------------------------------
    VFX.cinematicDim(0.4, 760, '#03130e');
    if (casterEl) {
      VFX.motion(casterEl, 'cast', { duration: 380 });
    }

    VFX.magicCircle({
      x: cFoot.x,
      y: cFoot.y,
      radius: 62,
      tilt: 0.44,
      layer: 'ground',
      style: 'bagua',
      color: '#10b981',
      secondaryColor: '#facc15',
      rotationSpeed: 1.8,
      duration: 680,
      alpha: 0.72,
      lineWidth: 1.8,
    });

    // 指尖乙木雷印 attractTo 聚电
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 160,
      draw: function (c2d, p) {
        var fade = p < 0.3 ? p / 0.3 : 1 - (p - 0.3) * 0.4;
        var rad = VFX.lerp(42, 18, p) * s;
        c2d.save();
        c2d.translate(palmPt.x, palmPt.y);
        var g = c2d.createRadialGradient(0, 0, 1, 0, 0, rad);
        g.addColorStop(0, 'rgba(255,255,255,' + (0.92 * fade).toFixed(3) + ')');
        g.addColorStop(0.32, 'rgba(16,185,129,' + (0.64 * fade).toFixed(3) + ')');
        g.addColorStop(0.68, 'rgba(250,204,21,' + (0.28 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(16,185,129,0)');
        c2d.fillStyle = g;
        c2d.beginPath();
        c2d.arc(0, 0, rad, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 52 * s,
      ring: true,
      count: 14,
      shape: 'spark',
      colors: ['#ffffff', '#a7f3d0', '#10b981', '#facc15'],
      size: [1.8 * s, 3.4 * s],
      speed: [10 * s, 26 * s],
      attractTo: palmPt,
      attractStrength: 980 * s,
      drag: 0.91,
      life: [105, 165],
      trail: 3,
    });
    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 48 * s,
      ring: true,
      count: 10,
      shape: 'wisp',
      colors: ['#10b981', '#34d399', '#fef08a'],
      size: [2.8 * s, 5.4 * s],
      speed: [12 * s, 28 * s],
      vortex: 210 * s,
      vortexCenter: palmPt,
      attractTo: palmPt,
      attractStrength: 920 * s,
      life: [110, 170],
    });
    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 10 * s,
      count: 4,
      shape: 'flare',
      colors: ['#ffffff', '#6ee7b7', '#facc15'],
      size: [2.2 * s, 4.2 * s],
      speed: [16 * s, 48 * s],
      life: [150, 250],
    });

    // 青碧引雷 streamer 与 lightning 直冲敌阵高空 cloudPt
    VFX.streamer({
      x1: palmPt.x,
      y1: palmPt.y,
      x2: cloudPt.x,
      y2: cloudPt.y,
      arc: -24 * s,
      wobble: 7 * s,
      width: 7 * s,
      tailLength: 0.46,
      duration: 165,
      color: '#10b981',
      coreColor: '#fef08a',
      sparkColors: ['#6ee7b7', '#facc15', '#ffffff'],
    });
    VFX.lightning({
      delay: 16,
      x1: palmPt.x,
      y1: palmPt.y,
      x2: cloudPt.x,
      y2: cloudPt.y,
      width: 3.6 * s,
      jagged: 0.3,
      segments: 14,
      branches: 2,
      flicker: 4,
      color: '#34d399',
      secondaryColor: '#ecfdf5',
      glowColor: '#059669',
      haloScale: 1.2,
      coreColor: '#ffffff',
      duration: 155,
    });

    // 高空深翠雷云（plume 叠画翻滚云体 + 云腹青金雷眼辉光）
    var cloudDeepPlume = getPlume('#042f2e');
    var cloudJadePlume = getPlume('#064e3b');
    var cloudRimPlume = getPlume('#065f46');
    var cloudNodes = [];
    for (var cn = 0; cn < 14; cn++) {
      var cu = cn / 13;
      cloudNodes.push({
        ox: (cu - 0.5) * cloudSpan * 0.96 + VFX.rand(-8, 8) * s,
        oy: (Math.sin(cu * Math.PI * 2.2) * 10 + VFX.rand(-8, 8)) * s,
        rx: VFX.rand(36, 58) * s,
        ry: VFX.rand(20, 34) * s,
        phase: VFX.rand(0, TWO_PI),
        layer: cn % 3,
      });
    }

    VFX.custom({
      layer: 'sky',
      blend: 'source-over',
      duration: 840,
      draw: function (c2d, p) {
        var fadeIn = p < 0.14 ? p / 0.14 : 1;
        var fadeOut = p > 0.7 ? Math.max(0, (1 - p) / 0.3) : 1;
        var alpha = fadeIn * fadeOut;
        if (alpha <= 0.01) return;

        c2d.save();
        c2d.translate(cloudPt.x, cloudPt.y);

        for (var idx = 0; idx < cloudNodes.length; idx++) {
          var nd = cloudNodes[idx];
          var dx = nd.ox + Math.sin(p * 6.5 + nd.phase) * 10 * s;
          var dy = nd.oy + Math.cos(p * 5.2 + nd.phase) * 5 * s;
          var rx = nd.rx * (0.9 + 0.12 * Math.sin(p * 7 + nd.phase));
          var ry = nd.ry * (0.9 + 0.1 * Math.cos(p * 6 + nd.phase));
          var sp = nd.layer === 0 ? cloudDeepPlume : nd.layer === 1 ? cloudJadePlume : cloudRimPlume;
          if (sp) {
            c2d.globalCompositeOperation = nd.layer === 2 ? 'lighter' : 'source-over';
            c2d.globalAlpha = alpha * (nd.layer === 2 ? 0.28 : 0.64);
            c2d.drawImage(sp, dx - rx, dy - ry, rx * 2, ry * 2);
          } else {
            var cg = c2d.createRadialGradient(dx, dy, 2, dx, dy, rx);
            cg.addColorStop(0, 'rgba(6,78,59,' + (0.52 * alpha).toFixed(3) + ')');
            cg.addColorStop(1, 'rgba(4,47,46,0)');
            c2d.fillStyle = cg;
            c2d.beginPath();
            c2d.arc(dx, dy, rx, 0, TWO_PI);
            c2d.fill();
          }
        }

        // 云腹径向雷眼辉光（边缘 alpha 严格归零）
        var eyePulse = 0.45 + 0.55 * Math.abs(Math.sin(p * Math.PI * 6));
        var eyeR = 84 * s * (0.88 + 0.14 * eyePulse);
        c2d.globalCompositeOperation = 'lighter';
        c2d.save();
        c2d.scale(1.45, 0.5);
        var eg = c2d.createRadialGradient(0, 0, 2, 0, 0, eyeR);
        eg.addColorStop(0, 'rgba(254,240,138,' + (0.48 * alpha * eyePulse).toFixed(3) + ')');
        eg.addColorStop(0.32, 'rgba(16,185,129,' + (0.36 * alpha).toFixed(3) + ')');
        eg.addColorStop(0.7, 'rgba(6,95,70,' + (0.16 * alpha).toFixed(3) + ')');
        eg.addColorStop(1, 'rgba(4,47,46,0)');
        c2d.fillStyle = eg;
        c2d.beginPath();
        c2d.arc(0, 0, eyeR, 0, TWO_PI);
        c2d.fill();
        c2d.restore();

        c2d.restore();
      },
    });

    // 翻滚深翠雷云 smoke + 云间游走 wisp
    VFX.burst({
      x: cloudPt.x,
      y: cloudPt.y,
      radius: cloudSpan * 0.42,
      tiltY: 0.34,
      count: 20,
      shape: 'smoke',
      blend: 'source-over',
      colors: ['#042f2e', '#064e3b', '#065f46'],
      size: [20 * s, 36 * s],
      speed: [16 * s, 44 * s],
      vortex: 210 * s,
      vortexCenter: cloudPt,
      drag: 0.96,
      alpha: [0.56, 0.8],
      life: [500, 820],
    });
    VFX.burst({
      x: cloudPt.x,
      y: cloudPt.y,
      radius: cloudSpan * 0.36,
      tiltY: 0.34,
      count: 14,
      shape: 'wisp',
      colors: ['#10b981', '#34d399', '#facc15'],
      size: [5.0 * s, 9.2 * s],
      speed: [24 * s, 62 * s],
      vortex: 280 * s,
      vortexCenter: cloudPt,
      drag: 0.95,
      alpha: 0.6,
      life: [360, 600],
    });

    // 云腹青金闷雷闪烁
    for (var cm = 0; cm < 2; cm++) {
      (function (idx) {
        var cA = VFX.clampStage({
          x: cloudPt.x + (idx === 0 ? -46 : 42) * s,
          y: cloudPt.y + (idx === 0 ? -6 : 6) * s,
        }, 12, 12);
        var cB = VFX.clampStage({
          x: cloudPt.x + (idx === 0 ? 40 : -36) * s,
          y: cloudPt.y + (idx === 0 ? 8 : -10) * s,
        }, 12, 12);
        VFX.lightning({
          delay: 35 + idx * 45,
          x1: cA.x,
          y1: cA.y,
          x2: cB.x,
          y2: cB.y,
          width: 3.2 * s,
          jagged: 0.35,
          segments: 12,
          branches: 2,
          flicker: 4,
          color: idx === 0 ? '#10b981' : '#facc15',
          secondaryColor: idx === 0 ? '#a7f3d0' : '#fef08a',
          glowColor: idx === 0 ? '#047857' : '#b45309',
          haloScale: 1.2,
          coreColor: '#ffffff',
          duration: 145,
        });
      })(cm);
    }

    // -------------------------------------------------------------------------
    // 阶段二（135ms）：对至多 2 名目标错峰 90ms 劈落三重色温高密分形神雷链
    // -------------------------------------------------------------------------
    var totalT = 920;
    for (var i = 0; i < targetCount; i++) {
      (function (idx) {
        var el = targets[idx] || null;
        var pt = el ? VFX.center(el) : VFX.sideCenter(foeSide);
        var ft = el ? VFX.foot(el) : { x: pt.x, y: pt.y + 36 * s };
        var safePt = VFX.clampStage(pt, 34 * s, 28 * s);
        var safeFoot = VFX.clampStage(ft, 36 * s, 20 * s);
        var strikeDelay = 135 + idx * 90;
        var dmg = Math.round(resolveTargetDamage(ctx, idx, targetCount, 128));

        VFX.after(strikeDelay, function () {
          var skyOriginX = VFX.clamp(
            VFX.lerp(cloudPt.x, safePt.x, 0.32) + (idx === 0 ? -10 : 10) * s,
            24 * s,
            st.w - 24 * s
          );
          var skyOriginY = Math.max(16, cloudPt.y);
          var strikeY = safeFoot.y - 4 * s;

          // 云口吞吐光晕 + 靶心瞬态电离光晕
          VFX.custom({
            layer: 'sky',
            blend: 'lighter',
            duration: 320,
            draw: function (c2d, p) {
              var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.45);
              var r = 74 * s * (0.7 + 0.3 * Math.sin(p * Math.PI));
              c2d.save();
              c2d.translate(skyOriginX, skyOriginY + 6 * s);
              c2d.scale(1.4, 0.52);
              var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
              g.addColorStop(0, 'rgba(255,255,255,' + (0.78 * fade).toFixed(3) + ')');
              g.addColorStop(0.32, 'rgba(16,185,129,' + (0.48 * fade).toFixed(3) + ')');
              g.addColorStop(0.68, 'rgba(250,204,21,' + (0.24 * fade).toFixed(3) + ')');
              g.addColorStop(1, 'rgba(6,95,70,0)');
              c2d.fillStyle = g;
              c2d.beginPath();
              c2d.arc(0, 0, r, 0, TWO_PI);
              c2d.fill();
              c2d.restore();
            },
          });

          // 三重色温高密分形神雷链（翠绿主雷 + 琥珀金副雷 + 炽白雷芯）
          VFX.lightning({
            x1: skyOriginX,
            y1: skyOriginY,
            x2: safeFoot.x - 4 * s,
            y2: strikeY,
            width: 8.6 * s,
            jagged: 0.26,
            segments: 22,
            branches: 6,
            flicker: 6,
            color: '#10b981',
            secondaryColor: '#a7f3d0',
            glowColor: '#047857',
            haloScale: 1.32,
            coreColor: '#ffffff',
            duration: 350,
          });
          VFX.lightning({
            delay: 24,
            x1: skyOriginX + 12 * s,
            y1: skyOriginY,
            x2: safeFoot.x + 6 * s,
            y2: strikeY - 2 * s,
            width: 5.8 * s,
            jagged: 0.32,
            segments: 18,
            branches: 4,
            flicker: 5,
            color: '#facc15',
            secondaryColor: '#fef08a',
            glowColor: '#d97706',
            haloScale: 1.18,
            coreColor: '#ffffff',
            duration: 310,
          });
          VFX.lightning({
            delay: 50,
            x1: skyOriginX - 4 * s,
            y1: skyOriginY + 4 * s,
            x2: safeFoot.x,
            y2: strikeY,
            width: 4.2 * s,
            jagged: 0.28,
            segments: 16,
            branches: 3,
            flicker: 5,
            color: '#34d399',
            secondaryColor: '#ffffff',
            glowColor: '#10b981',
            haloScale: 1.1,
            coreColor: '#ffffff',
            duration: 270,
          });

          // 沿雷道分段溅开电离 wisp 与 flare
          for (var ch = 0; ch < 4; ch++) {
            var frac = (ch + 0.35) / 4.1;
            var cx = VFX.lerp(skyOriginX, safeFoot.x, frac) + VFX.rand(-10, 10) * s;
            var cy = VFX.lerp(skyOriginY, strikeY, frac);
            VFX.burst({
              delay: ch * 18,
              x: cx,
              y: cy,
              radius: 14 * s,
              count: 4,
              shape: 'wisp',
              colors: ['#10b981', '#a7f3d0', '#facc15'],
              size: [3.8 * s, 7.2 * s],
              speed: [35 * s, 105 * s],
              turbulence: 125 * s,
              drag: 0.93,
              alpha: 0.72,
              life: [200, 380],
            });
            VFX.burst({
              delay: ch * 18 + 8,
              x: cx,
              y: cy,
              radius: 12 * s,
              count: 2,
              shape: 'flare',
              colors: ['#ffffff', '#fef08a', '#34d399'],
              size: [2.2 * s, 4.0 * s],
              speed: [25 * s, 85 * s],
              life: [180, 320],
            });
          }

          // 落点 ground 径向电浆光池
          VFX.custom({
            layer: 'ground',
            blend: 'lighter',
            duration: 480,
            draw: function (c2d, p) {
              var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.45);
              var r = 98 * s * (0.65 + 0.35 * Math.min(1, p * 3.2));
              c2d.save();
              c2d.translate(safeFoot.x, safeFoot.y);
              c2d.scale(1, 0.44);
              var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
              g.addColorStop(0, 'rgba(255,255,255,' + (0.82 * fade).toFixed(3) + ')');
              g.addColorStop(0.24, 'rgba(167,243,208,' + (0.56 * fade).toFixed(3) + ')');
              g.addColorStop(0.56, 'rgba(250,204,21,' + (0.28 * fade).toFixed(3) + ')');
              g.addColorStop(0.82, 'rgba(16,185,129,' + (0.12 * fade).toFixed(3) + ')');
              g.addColorStop(1, 'rgba(4,47,46,0)');
              c2d.fillStyle = g;
              c2d.beginPath();
              c2d.arc(0, 0, r, 0, TWO_PI);
              c2d.fill();
              c2d.restore();
            },
          });

          // 双层 shockwave + 青木 groundCrack
          VFX.shockwave({
            x: safeFoot.x,
            y: safeFoot.y,
            fromRadius: 12,
            toRadius: 124,
            tilt: 0.44,
            layer: 'ground',
            color: '#10b981',
            coreColor: '#ffffff',
            lineWidth: 4.4,
            duration: 400,
            double: true,
          });
          VFX.shockwave({
            x: safePt.x,
            y: safePt.y,
            fromRadius: 10,
            toRadius: 88,
            tilt: 0.9,
            layer: 'sky',
            color: '#facc15',
            coreColor: '#fef08a',
            lineWidth: 3.4,
            duration: 340,
            double: false,
          });

          VFX.groundCrack({
            x: safeFoot.x,
            y: safeFoot.y,
            radius: 84,
            branches: 8,
            color: '#10b981',
            crackColor: '#022c22',
            lineWidth: 3.4,
            duration: 1450,
          });

          // 落点电浆 wisp 漩涡 + 辐射电火花 + 爆闪电星
          VFX.burst({
            x: safePt.x,
            y: safePt.y + 6 * s,
            radius: 22 * s,
            count: 20,
            shape: 'wisp',
            colors: ['#10b981', '#a7f3d0', '#facc15', '#ffffff'],
            size: [4.6 * s, 9.2 * s],
            speed: [55 * s, 155 * s],
            vortex: 380 * s,
            vortexCenter: safePt,
            gravity: -40 * s,
            turbulence: 120 * s,
            drag: 0.93,
            life: [300, 560],
          });
          VFX.burst({
            x: safePt.x,
            y: safePt.y + 4 * s,
            count: 32,
            shape: 'spark',
            colors: ['#ffffff', '#a7f3d0', '#facc15', '#10b981'],
            size: [2.0 * s, 4.4 * s],
            speed: [115 * s, 295 * s],
            gravity: 160 * s,
            drag: 0.93,
            life: [240, 480],
            trail: 5,
          });
          VFX.burst({
            x: safePt.x,
            y: safePt.y,
            radius: 14 * s,
            count: 10,
            shape: 'flare',
            colors: ['#ffffff', '#fef08a', '#34d399'],
            size: [3.0 * s, 6.2 * s],
            speed: [65 * s, 200 * s],
            drag: 0.91,
            life: [220, 420],
          });

          VFX.quake('lg', 280);
          if (el) {
            VFX.impact(el, { color: '#34d399', duration: 250, distance: 16 });
          }
          VFX.floatText(el || safePt, '-' + dmg, isCrit ? 'crit' : 'damage', {
            scale: isCrit ? 1.3 : 1.1,
          });

          // -------------------------------------------------------------------
          // 阶段三（280ms~820ms）：青黄旋转收缩电弧环 + 4 波跳跃微缩分形麻痹电弧 + 麻痹浮字
          // -------------------------------------------------------------------
          VFX.after(145, function () {
            emitParalysisField(el, safePt, s);
          });
        });

        totalT = Math.max(totalT, strikeDelay + 740);
      })(i);
    }

    return Math.max(1280, totalT);
  });
});
