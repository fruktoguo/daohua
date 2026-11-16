/**
 * ability.pojun · 破军 —— 二阶金系刀修悍刃扫阵 + 移形与五层势不可挡（深度流体粒子化重构版）
 * 真源镜像：config/game-data/abilities/physical_abilities.json
 *   敌方全体 / 吟唱 1500ms / 1.2×AD+50 + 移形(5s) + 5层势不可挡 / 冷却 24s / 灵力 35 / 刀修·金
 * 演出阶段：
 *   0ms    暗金战幕 + 起跳下砸（slam）+ 足底赤金霸阵 + 掌缘金红煞气狂吸聚刃光团
 *   155ms  沿 axis 2D 投影计算跨阵扫掠参数：赤红熔金巨弧 + 残烟烈焰刀风 + 烈焰波涛 wave 席卷敌阵
 *   265ms  第二道白金逆扫破甲巨刃 + 敌阵中心熔金地裂 + 重度震屏 + 全敌独立裂甲迸射与伤害跳字
 *   340ms  施法者周身螺旋霸气流体柱升腾 + 5 道旋转羽化玄金护体罡盾曲弧（五层势不可挡）+ 青碧移形流风
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
    grad.addColorStop(0.45, 'rgba(' + rgb + ',0.35)');
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
   * 获取双向高斯柔雾羽流精灵（用于残烟刀风叠绘，边缘 alpha 严格归零）
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
      { x: 64, y: 64, r: 58, peak: 0.72 },
      { x: 52, y: 58, r: 40, peak: 0.28 },
      { x: 76, y: 70, r: 38, peak: 0.26 },
    ];
    for (var i = 0; i < lobes.length; i++) {
      var lb = lobes[i];
      var grad = gctx.createRadialGradient(lb.x, lb.y, 1, lb.x, lb.y, lb.r);
      grad.addColorStop(0, 'rgba(' + rgb + ',' + lb.peak + ')');
      grad.addColorStop(0.36, 'rgba(' + rgb + ',' + (lb.peak * 0.44).toFixed(3) + ')');
      grad.addColorStop(0.7, 'rgba(' + rgb + ',' + (lb.peak * 0.12).toFixed(3) + ')');
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
   * 绘制首尾渐变归零的羽化罡盾曲弧（零硬描边椭圆）
   */
  function drawFeatheredShieldArc(c2d, x0, y0, cx, cy, x1, y1, thickness, rgb, alpha) {
    if (alpha <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.26, 'rgba(' + rgb + ',' + (alpha * 0.62).toFixed(3) + ')');
    grad.addColorStop(0.54, 'rgba(' + rgb + ',' + alpha.toFixed(3) + ')');
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

  VFX.register('ability.pojun', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length)
      ? ctx.targetEls.slice()
      : (ctx.targetEl ? [ctx.targetEl] : []);
    var isEnemyCaster = !!(casterEl && casterEl.classList && (
      casterEl.classList.contains('is-enemy') ||
      casterEl.classList.contains('arena-actor--enemy')
    ));
    var selfSide = isEnemyCaster ? 'enemy' : 'ally';
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    if (!targets.length) {
      var pool = isEnemyCaster ? ctx.allyEls : ctx.enemyEls;
      if (pool && pool.length) targets = pool.slice();
    }

    var s = VFX.scale();
    var st = VFX.size();
    var group = targets.length ? targets : foeSide;
    var bounds = VFX.groupBounds(group);
    var axis = VFX.combatAxis(casterEl, group);
    var cSafe = VFX.clampStage(casterEl ? VFX.center(casterEl) : VFX.sideCenter(selfSide), 38 * s, 32 * s);
    var cFoot = VFX.clampStage(casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(selfSide), 38 * s, 22 * s);
    var bladePt = VFX.clampStage(axis.palm(22 * s), 28 * s, 24 * s);
    var formCenter = VFX.clampStage({ x: bounds.cx, y: bounds.cy }, 42 * s, 34 * s);
    var formFoot = VFX.clampStage({ x: bounds.footX, y: bounds.bottomFootY + 4 * s }, 48 * s, 22 * s);
    var isCrit = !!ctx.crit;

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：暗金战幕 + 破军重步下砸 + 足底赤金霸阵 + 掌缘金红煞气狂吸
    // -------------------------------------------------------------------------
    VFX.cinematicDim(0.42, 820, '#140902');
    if (casterEl) {
      VFX.motion(casterEl, 'slam', { dx: axis.ux, dy: axis.uy, duration: 360 });
    }

    // 施法者足底焦金暗光盘
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 1020,
      draw: function (c2d, p) {
        var fade = p < 0.16 ? p / 0.16 : p > 0.72 ? (1 - p) / 0.28 : 1;
        var rad = 98 * s * (p < 0.18 ? VFX.lerp(0.5, 1.0, p / 0.18) : 1);
        c2d.save();
        c2d.translate(cFoot.x, cFoot.y);
        c2d.scale(1, 0.44);
        var g = c2d.createRadialGradient(0, 0, 3 * s, 0, 0, rad);
        g.addColorStop(0, 'rgba(254,240,138,' + (0.38 * fade).toFixed(3) + ')');
        g.addColorStop(0.42, 'rgba(245,158,11,' + (0.28 * fade).toFixed(3) + ')');
        g.addColorStop(0.76, 'rgba(220,38,38,' + (0.14 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(127,29,29,0)');
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
      radius: 82,
      tilt: 0.44,
      layer: 'ground',
      style: 'sword',
      color: '#f59e0b',
      secondaryColor: '#dc2626',
      rotationSpeed: 2.0,
      duration: 1080,
      alpha: 0.82,
      lineWidth: 2.2,
    });

    VFX.magicCircle({
      delay: 30,
      x: cFoot.x,
      y: cFoot.y,
      radius: 54,
      tilt: 0.44,
      layer: 'ground',
      style: 'bagua',
      color: '#fef08a',
      secondaryColor: '#f59e0b',
      rotationSpeed: -2.6,
      duration: 1020,
      alpha: 0.7,
      lineWidth: 1.6,
    });

    // 掌缘聚刃震颤光团（外层金红热浪 + 内核白金聚锋）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 185,
      draw: function (c2d, p) {
        var rad = VFX.lerp(76, 30, p * p) * s;
        var jit = ((1 - p) * 2.8 + p * 3.2) * s;
        var jx = Math.sin(p * 44) * jit * 0.55;
        var jy = Math.cos(p * 39) * jit * 0.55;
        var alpha = p < 0.22 ? p / 0.22 : 1 - (p - 0.22) * 0.28;

        c2d.save();
        c2d.translate(bladePt.x + jx, bladePt.y + jy);

        var g1 = c2d.createRadialGradient(0, 0, rad * 0.1, 0, 0, rad);
        g1.addColorStop(0, 'rgba(255,255,255,' + (0.72 * alpha).toFixed(3) + ')');
        g1.addColorStop(0.28, 'rgba(245,158,11,' + (0.52 * alpha).toFixed(3) + ')');
        g1.addColorStop(0.64, 'rgba(220,38,38,' + (0.28 * alpha).toFixed(3) + ')');
        g1.addColorStop(1, 'rgba(220,38,38,0)');
        c2d.fillStyle = g1;
        c2d.beginPath();
        c2d.arc(0, 0, rad, 0, TWO_PI);
        c2d.fill();

        var rCore = rad * 0.44;
        var g2 = c2d.createRadialGradient(0, 0, 0, 0, 0, rCore);
        g2.addColorStop(0, 'rgba(255,255,255,' + (0.94 * alpha).toFixed(3) + ')');
        g2.addColorStop(0.52, 'rgba(254,240,138,' + (0.46 * alpha).toFixed(3) + ')');
        g2.addColorStop(1, 'rgba(245,158,11,0)');
        c2d.fillStyle = g2;
        c2d.beginPath();
        c2d.arc(0, 0, rCore, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    // 金红煞气高速 attractTo 聚刃狂吸
    VFX.burst({
      x: bladePt.x,
      y: bladePt.y,
      radius: 78 * s,
      ring: true,
      count: 18,
      shape: 'wisp',
      colors: ['#dc2626', '#f59e0b', '#fef08a'],
      size: [3.6 * s, 7.0 * s],
      speed: [14 * s, 38 * s],
      vortex: 245 * s,
      vortexCenter: bladePt,
      attractTo: bladePt,
      attractStrength: 960 * s,
      drag: 0.92,
      life: [145, 210],
      trail: 3,
    });
    VFX.burst({
      x: bladePt.x,
      y: bladePt.y,
      radius: 68 * s,
      ring: true,
      count: 16,
      shape: 'ember',
      colors: ['#fef08a', '#f59e0b', '#ef4444'],
      size: [2.2 * s, 4.2 * s],
      speed: [12 * s, 32 * s],
      attractTo: bladePt,
      attractStrength: 980 * s,
      drag: 0.92,
      life: [140, 200],
      trail: 4,
    });

    // -------------------------------------------------------------------------
    // 阶段二（155ms）：沿 axis 2D 投影计算跨阵扫掠参数，双弧巨刃 + 烈焰波涛扫阵
    // -------------------------------------------------------------------------
    var perpSpan = bounds.majorSpan;
    var alongHalf = 132 * s;
    if (targets.length > 0 && axis.dist > 1) {
      var minPerp = Infinity;
      var maxPerp = -Infinity;
      var minAlong = Infinity;
      var maxAlong = -Infinity;
      for (var ti = 0; ti < targets.length; ti++) {
        var tp = VFX.center(targets[ti]);
        var ddx = tp.x - bounds.cx;
        var ddy = tp.y - bounds.cy;
        var pProj = ddx * axis.nx + ddy * axis.ny;
        var aProj = ddx * axis.ux + ddy * axis.uy;
        if (pProj < minPerp) minPerp = pProj;
        if (pProj > maxPerp) maxPerp = pProj;
        if (aProj < minAlong) minAlong = aProj;
        if (aProj > maxAlong) maxAlong = aProj;
      }
      perpSpan = targets.length > 1 ? maxPerp - minPerp : 84 * s;
      alongHalf = Math.max(96 * s, (maxAlong - minAlong) * 0.5 + 88 * s);
    }

    var waveWidth = VFX.clamp(
      (axis.dist > 1 ? perpSpan : bounds.majorSpan) + 98 * s,
      118 * s,
      Math.min(st.w, st.h) * 0.86
    );
    var waveStart = VFX.clampStage({
      x: bounds.cx - axis.ux * alongHalf,
      y: bounds.cy - axis.uy * alongHalf,
    }, 20 * s, 20 * s);
    var waveEnd = VFX.clampStage({
      x: bounds.cx + axis.ux * alongHalf,
      y: bounds.cy + axis.uy * alongHalf,
    }, 20 * s, 20 * s);
    var arcRadius = VFX.clamp(
      Math.max(124, (bounds.majorSpan * 0.5 + 68 * s) / Math.max(0.58, s)),
      126,
      198
    );
    var arcPivot = VFX.clampStage({
      x: bounds.cx - axis.ux * 70 * s,
      y: bounds.cy - axis.uy * 70 * s,
    }, 40 * s, 30 * s);

    VFX.after(155, function () {
      // 从刀心奔袭敌阵的破军火锋轨迹
      if (axis.dist > 38) {
        VFX.streamer({
          x1: bladePt.x,
          y1: bladePt.y,
          x2: formCenter.x,
          y2: formCenter.y,
          arc: (axis.facingX >= 0 ? -1 : 1) * 20 * s,
          wobble: 4 * s,
          width: 13 * s,
          tailLength: 0.45,
          duration: 210,
          color: '#f59e0b',
          coreColor: '#fef08a',
          sparkColors: ['#fef08a', '#dc2626', '#ffffff'],
        });
      }

      // 第一道宽幅赤红熔金巨弧
      VFX.slash({
        x: arcPivot.x,
        y: arcPivot.y,
        radius: arcRadius,
        tilt: 0.5,
        rotation: axis.angle,
        thickness: 40,
        startAngle: -1.24,
        endAngle: 1.24,
        color: '#dc2626',
        glowColor: '#f59e0b',
        coreColor: '#fef08a',
        sparks: false,
        duration: 340,
      });
      VFX.slash({
        delay: 14,
        x: arcPivot.x,
        y: arcPivot.y,
        radius: arcRadius - 4,
        tilt: 0.5,
        rotation: axis.angle,
        thickness: 24,
        startAngle: -1.2,
        endAngle: 1.2,
        color: '#f59e0b',
        glowColor: '#dc2626',
        coreColor: '#ffffff',
        sparks: false,
        duration: 310,
      });

      // plume 残烟烈焰刀风沿弧切线连续叠绘
      var plumeHot = getPlume('#f59e0b');
      var plumeDark = getPlume('#78350f');
      VFX.custom({
        layer: 'sky',
        duration: 380,
        draw: function (c2d, p) {
          var headP = Math.min(1, p * 1.95);
          var tailP = Math.max(0, (p - 0.16) / 0.84);
          var fade = p < 0.22 ? p / 0.22 : Math.pow(1 - (p - 0.22) / 0.78, 1.35);
          if (fade <= 0.01) return;

          var segs = 14;
          var rScaled = arcRadius * s;
          c2d.save();
          c2d.translate(arcPivot.x, arcPivot.y);
          c2d.rotate(axis.angle);
          for (var i = 0; i <= segs; i++) {
            var u = i / segs;
            if (u < tailP * 0.72 || u > headP) continue;
            var ang = VFX.lerp(-1.24, 1.24, u);
            var env = Math.sin(u * Math.PI);
            var px = Math.cos(ang) * rScaled;
            var py = Math.sin(ang) * rScaled * 0.5;
            var tan = Math.atan2(Math.cos(ang) * 0.5, -Math.sin(ang));
            var sz = (24 + env * 20) * s * (1 + p * 0.42);

            c2d.save();
            c2d.translate(px, py);
            c2d.rotate(tan);
            if (plumeDark) {
              c2d.globalCompositeOperation = 'source-over';
              c2d.globalAlpha = fade * env * 0.3;
              c2d.drawImage(plumeDark, -sz * 1.35, -sz * 0.7, sz * 2.7, sz * 1.4);
            }
            if (plumeHot) {
              c2d.globalCompositeOperation = 'lighter';
              c2d.globalAlpha = fade * env * 0.44;
              c2d.drawImage(plumeHot, -sz * 1.2, -sz * 0.58, sz * 2.4, sz * 1.16);
            } else {
              var pg = c2d.createRadialGradient(0, 0, 1, 0, 0, sz);
              pg.addColorStop(0, 'rgba(245,158,11,' + (0.38 * fade * env).toFixed(3) + ')');
              pg.addColorStop(1, 'rgba(245,158,11,0)');
              c2d.fillStyle = pg;
              c2d.beginPath();
              c2d.arc(0, 0, sz, 0, TWO_PI);
              c2d.fill();
            }
            c2d.restore();
          }
          c2d.restore();
        },
      });

      // 沿弧线喷涌烈焰刀风 wisp
      for (var wk = 0; wk < 6; wk++) {
        var wu = 0.16 + wk * 0.14;
        var la = VFX.lerp(-1.15, 1.15, wu);
        var lx = Math.cos(la) * arcRadius * s;
        var ly = Math.sin(la) * arcRadius * s * 0.5;
        var wx = arcPivot.x + lx * axis.ux - ly * axis.uy;
        var wy = arcPivot.y + lx * axis.uy + ly * axis.ux;
        VFX.burst({
          delay: wk * 14,
          x: wx,
          y: wy,
          count: 4,
          shape: 'wisp',
          colors: ['#f59e0b', '#dc2626', '#fef08a'],
          size: [4.5 * s, 8.8 * s],
          baseAngle: axis.angle + la * 0.35,
          spread: 0.75,
          speed: [95 * s, 215 * s],
          drag: 0.91,
          alpha: 0.76,
          life: [240, 440],
        });
      }

      // 烈焰波涛 wave 席卷敌阵（必须传 x1, y1, x2, y2 且 rawScale: true）
      VFX.wave({
        x1: waveStart.x,
        y1: waveStart.y,
        x2: waveEnd.x,
        y2: waveEnd.y,
        width: waveWidth,
        thickness: 72 * s,
        rawScale: true,
        style: 'fire',
        color: '#f59e0b',
        secondaryColor: '#dc2626',
        foamColor: '#fef08a',
        crests: 3,
        duration: 420,
      });

      for (var fIdx = 0; fIdx < targets.length; fIdx++) {
        (function (tEl, idx) {
          VFX.after(40 + idx * 22, function () {
            if (tEl) VFX.hitFlash(tEl, '#fbbf24', 150);
          });
        })(targets[fIdx], fIdx);
      }
    });

    // -------------------------------------------------------------------------
    // 265ms：第二道白金逆扫破甲巨刃 + 敌阵中心熔金地裂 + 重震 + 全敌独立爆散
    // -------------------------------------------------------------------------
    VFX.after(265, function () {
      VFX.slash({
        x: arcPivot.x,
        y: arcPivot.y - 6 * s,
        radius: arcRadius + 10,
        tilt: 0.52,
        rotation: axis.angle,
        thickness: 38,
        startAngle: 1.28,
        endAngle: -1.28,
        color: '#fef08a',
        glowColor: '#f59e0b',
        coreColor: '#ffffff',
        sparks: false,
        duration: 340,
      });

      var crackRad = Math.max(96, Math.round((bounds.majorSpan * 0.42 + 46 * s) / Math.max(0.6, s)));
      VFX.groundCrack({
        x: formFoot.x,
        y: formFoot.y,
        radius: crackRad,
        branches: 10,
        color: '#f59e0b',
        crackColor: '#140802',
        lineWidth: 3.8,
        duration: 1650,
      });

      // 阵心贴地热浪烟尘与冲击波
      var sweepRad = Math.max(68 * s, bounds.majorSpan * 0.44);
      VFX.burst({
        layer: 'ground',
        x: formFoot.x,
        y: formFoot.y,
        radius: sweepRad,
        tiltY: 0.44,
        count: 18,
        shape: 'wisp',
        colors: ['#f59e0b', '#dc2626', '#fef08a'],
        size: [5.6 * s, 11.2 * s],
        speed: [95 * s, 215 * s],
        gravity: -28 * s,
        turbulence: 52 * s,
        drag: 0.9,
        alpha: 0.72,
        life: [340, 620],
      });
      VFX.burst({
        x: formFoot.x,
        y: formFoot.y,
        radius: sweepRad * 0.85,
        tiltY: 0.44,
        count: 14,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#292524', '#451a03', '#78350f'],
        size: [16 * s, 28 * s],
        speed: [35 * s, 90 * s],
        gravity: -24 * s,
        alpha: 0.48,
        life: [460, 820],
      });

      VFX.quake('lg', 320);

      var count = Math.max(1, targets.length);
      for (var m = 0; m < count; m++) {
        (function (tEl, idx) {
          VFX.after(idx * 28, function () {
            var tc = VFX.clampStage(tEl ? VFX.center(tEl) : formCenter, 36 * s, 28 * s);
            var dmg = Math.round(resolveTargetDamage(ctx, idx, count, 145));

            VFX.shockwave({
              x: tc.x,
              y: tc.y,
              fromRadius: 10,
              toRadius: 82,
              color: '#f59e0b',
              coreColor: '#ffffff',
              lineWidth: 3.2,
              duration: 320,
              double: true,
            });
            VFX.burst({
              x: tc.x,
              y: tc.y,
              radius: 14 * s,
              count: 14,
              shape: 'shard',
              colors: ['#ffffff', '#fef08a', '#f59e0b', '#dc2626'],
              size: [2.8 * s, 5.8 * s],
              speed: [110 * s, 265 * s],
              gravity: 145 * s,
              spin: [-11, 11],
              life: [300, 560],
              trail: 3,
            });
            VFX.burst({
              x: tc.x,
              y: tc.y,
              radius: 14 * s,
              count: 18,
              shape: 'spark',
              colors: ['#ffffff', '#fef08a', '#fb923c', '#f59e0b'],
              size: [2.0 * s, 4.4 * s],
              baseAngle: axis.angle,
              spread: Math.PI * 1.4,
              speed: [120 * s, 290 * s],
              drag: 0.92,
              life: [240, 460],
              trail: 5,
            });
            VFX.burst({
              x: tc.x,
              y: tc.y,
              radius: 10 * s,
              count: 4,
              shape: 'flare',
              colors: ['#ffffff', '#fef08a', '#f59e0b'],
              size: [3.8 * s, 6.8 * s],
              speed: [20 * s, 72 * s],
              spin: [-4, 4],
              life: [220, 380],
            });

            if (tEl) {
              VFX.impact(tEl, { color: '#f59e0b', duration: 240, distance: 16 });
            }
            VFX.floatText(tEl || tc, '-' + dmg, isCrit ? 'crit' : 'damage', {
              scale: isCrit ? 1.32 : 1.12,
            });
          });
        })(targets[m] || null, m);
      }
    });

    // -------------------------------------------------------------------------
    // 阶段三（340ms）：螺旋霸气流体柱 + 5 道羽化玄金护体罡盾曲弧 + 青碧移形流风
    // -------------------------------------------------------------------------
    VFX.after(340, function () {
      // 施法者脚下移形扩散双波
      VFX.shockwave({
        x: cFoot.x,
        y: cFoot.y,
        tilt: 0.44,
        layer: 'ground',
        fromRadius: 12,
        toRadius: 94,
        color: '#2dd4bf',
        coreColor: '#fef08a',
        lineWidth: 3.2,
        duration: 420,
        double: true,
      });

      // 3 波螺旋霸气流体柱（玄金赤炎 wisp/ember 交织青碧移形流风 wisp）
      for (var wv = 0; wv < 3; wv++) {
        (function (waveIdx, at) {
          var spinDir = waveIdx % 2 === 0 ? 1 : -1;
          VFX.burst({
            delay: at,
            x: cFoot.x,
            y: cFoot.y - 4 * s,
            radius: 32 * s,
            ring: true,
            tiltY: 0.44,
            count: 14,
            shape: 'wisp',
            colors: ['#f59e0b', '#fef08a', '#dc2626', '#fbbf24'],
            size: [4.2 * s, 8.6 * s],
            angle: [-Math.PI * 0.68, -Math.PI * 0.32],
            speed: [72 * s, 155 * s],
            vortex: 240 * s * spinDir,
            vortexCenter: cSafe,
            gravity: -115 * s,
            turbulence: 62 * s,
            drag: 0.96,
            alpha: 0.78,
            life: [440, 720],
            trail: 3,
          });
          VFX.burst({
            delay: at + 18,
            x: cFoot.x,
            y: cFoot.y - 6 * s,
            radius: 28 * s,
            ring: true,
            tiltY: 0.44,
            count: 10,
            shape: 'ember',
            colors: ['#fef08a', '#f59e0b', '#ef4444'],
            size: [2.0 * s, 4.0 * s],
            angle: [-Math.PI * 0.68, -Math.PI * 0.32],
            speed: [68 * s, 140 * s],
            vortex: -210 * s * spinDir,
            vortexCenter: cSafe,
            gravity: -125 * s,
            turbulence: 48 * s,
            drag: 0.96,
            life: [420, 680],
            trail: 4,
          });
          VFX.burst({
            delay: at + 30,
            x: cSafe.x,
            y: cSafe.y + 8 * s,
            radius: 34 * s,
            ring: true,
            tiltY: 0.46,
            count: 8,
            shape: 'wisp',
            colors: ['#2dd4bf', '#5eead4', '#e0f2fe'],
            size: [3.6 * s, 6.8 * s],
            speed: [24 * s, 62 * s],
            vortex: 260 * s * -spinDir,
            vortexCenter: cSafe,
            gravity: -65 * s,
            drag: 0.95,
            alpha: 0.7,
            life: [380, 620],
          });
        })(wv, wv * 115);
      }

      // 体表 5 道环绕旋转的羽化玄金护体罡盾渐变曲弧（对应 5 层「势不可挡」）
      var shieldLayers = [
        { oy: -18, rx: 46, ry: 18, tiltRot: -0.22, dir: 1.0, phase: 0.0, span: 1.85, rgb: '245,158,11', coreRgb: '254,240,138' },
        { oy: -9, rx: 54, ry: 21, tiltRot: 0.18, dir: -1.15, phase: 1.25, span: 1.95, rgb: '250,204,21', coreRgb: '255,255,255' },
        { oy: 0, rx: 60, ry: 22, tiltRot: 0.0, dir: 1.3, phase: 2.5, span: 2.05, rgb: '245,158,11', coreRgb: '254,240,138' },
        { oy: 9, rx: 53, ry: 20, tiltRot: -0.16, dir: -1.05, phase: 3.75, span: 1.9, rgb: '234,88,12', coreRgb: '254,240,138' },
        { oy: 18, rx: 45, ry: 17, tiltRot: 0.24, dir: 1.2, phase: 5.0, span: 1.8, rgb: '250,204,21', coreRgb: '255,255,255' },
      ];
      var shieldStarGlow = getSoftGlow('#fef08a');

      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 940,
        draw: function (c2d, p) {
          var open = p < 0.18 ? 1 - Math.pow(1 - p / 0.18, 3) : 1;
          var fade = p < 0.14 ? p / 0.14 : p > 0.72 ? Math.max(0, (1 - p) / 0.28) : 1;
          if (fade <= 0.01) return;

          c2d.save();
          c2d.translate(cSafe.x, cSafe.y);

          // 护体底辉（边缘 alpha 归零）
          var auraR = 66 * s * (0.72 + 0.28 * open);
          var auraG = c2d.createRadialGradient(0, 0, auraR * 0.2, 0, 0, auraR);
          auraG.addColorStop(0, 'rgba(254,240,138,' + (0.14 * fade).toFixed(3) + ')');
          auraG.addColorStop(0.55, 'rgba(245,158,11,' + (0.24 * fade).toFixed(3) + ')');
          auraG.addColorStop(0.84, 'rgba(45,212,191,' + (0.14 * fade).toFixed(3) + ')');
          auraG.addColorStop(1, 'rgba(245,158,11,0)');
          c2d.fillStyle = auraG;
          c2d.beginPath();
          c2d.arc(0, 0, auraR, 0, TWO_PI);
          c2d.fill();

          // 5 道旋转羽化玄金护体罡盾曲弧
          for (var i = 0; i < shieldLayers.length; i++) {
            var sl = shieldLayers[i];
            var rx = sl.rx * s * (0.68 + 0.32 * open);
            var ry = sl.ry * s * (0.68 + 0.32 * open);
            var aBase = sl.phase + p * 6.8 * sl.dir;
            var a0 = aBase - sl.span * 0.5;
            var a1 = aBase + sl.span * 0.5;
            var aMid = aBase;

            c2d.save();
            c2d.translate(0, sl.oy * s);
            c2d.rotate(sl.tiltRot);

            var x0 = Math.cos(a0) * rx;
            var y0 = Math.sin(a0) * ry;
            var cx = Math.cos(aMid) * rx * 1.18;
            var cy = Math.sin(aMid) * ry * 1.18;
            var x1 = Math.cos(a1) * rx;
            var y1 = Math.sin(a1) * ry;

            drawFeatheredShieldArc(c2d, x0, y0, cx, cy, x1, y1, 6.2 * s, sl.rgb, 0.54 * fade);
            drawFeatheredShieldArc(c2d, x0, y0, cx, cy, x1, y1, 2.6 * s, sl.coreRgb, 0.86 * fade);

            // 护盾中段游走星核光晕
            var sx = Math.cos(aMid) * rx;
            var sy = Math.sin(aMid) * ry;
            var sr = 10 * s;
            if (shieldStarGlow) {
              c2d.globalAlpha = fade * 0.72;
              c2d.drawImage(shieldStarGlow, sx - sr, sy - sr, sr * 2, sr * 2);
            }
            c2d.restore();
          }

          // 交织青碧「移形」柔弧流风
          for (var yx = 0; yx < 2; yx++) {
            var ya = p * 7.6 + yx * Math.PI;
            var yx0 = Math.cos(ya) * 58 * s;
            var yy0 = Math.sin(ya) * 24 * s;
            var ycx = Math.cos(ya + 0.75) * 68 * s;
            var ycy = Math.sin(ya + 0.75) * 28 * s;
            var yx1 = Math.cos(ya + 1.5) * 58 * s;
            var yy1 = Math.sin(ya + 1.5) * 24 * s;
            drawFeatheredShieldArc(c2d, yx0, yy0, ycx, ycy, yx1, yy1, 3.6 * s, '45,212,191', 0.48 * fade);
          }

          c2d.restore();
        },
      });

      VFX.floatText(casterEl || cSafe, '势不可挡 · 移形', '#facc15', { scale: 0.96, rise: 38 });
    });

    return 1460;
  });
});
