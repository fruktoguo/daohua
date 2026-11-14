/**
 * ability.heavy-slash · 重斩（金系一阶大刀重劈 · 深度流体粒子化重构版）
 * 演出阶段：
 *   0ms   暗幕压场 + 起跳下砸（slam）+ 柔羽震颤聚气光团 + attractTo 金红聚锋涡旋内吸
 *   155ms 三层叠透重剑弧斩 + plumeSprite 残烟刀风沿弧拖曳 + 顺弧劈风 wisp 气浪
 *   220ms 金色地裂 + 地面冲击气浪粒（贴地 smoke/wisp）+ 金橙铁屑 spark/shard 迸射 + 靶心 flare 爆发 + 中等震屏
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
   * 获取双向高斯柔雾羽流精灵（优先复用 VFX.getPlumeSprite，否则本地边缘 alpha=0 多瓣生成）
   */
  function getPlume(color) {
    if (typeof VFX.getPlumeSprite === 'function') return VFX.getPlumeSprite(color);
    if (localPlumeCache[color]) return localPlumeCache[color];
    if (typeof document === 'undefined') return null;

    var c = document.createElement('canvas');
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

  function orientAngle(localAngle, axis) {
    return axis.facingX >= 0 ? axis.angle + localAngle : axis.angle - localAngle;
  }

  VFX.register('ability.heavy-slash', function (ctx) {
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length)
      ? ctx.targetEls
      : (ctx.targetEl ? [ctx.targetEl] : []);
    var targetEl = targets[0] || null;
    if (!targetEl) return;

    var s = VFX.scale();
    var axis = VFX.combatAxis(casterEl, targetEl);
    var t = VFX.center(targetEl);
    var slashCenter = VFX.clampStage(t, 48 * s, 38 * s);
    var f = VFX.clampStage(VFX.foot(targetEl), 42 * s, 30 * s);
    var chargeCenter = VFX.clampStage(axis.palm(18 * s), 34 * s, 30 * s);
    var dashFrom = axis.palm(14 * s);

    // 阶段一（0ms）：暗幕压低背景 + 起跳下砸 + 柔羽震颤聚气光团（锚定在掌缘蓄势点）
    VFX.cinematicDim(0.28, 520);
    if (casterEl) {
      VFX.motion(casterEl, 'slam', { dx: axis.ux, dy: axis.uy, duration: 380 });
    }

    VFX.custom({
      duration: 185,
      draw: function (c, p) {
        var radius = VFX.lerp(72 * s, 30 * s, p * p);
        var jitter = ((1 - p) * 2.8 + p * 3.5) * s;
        var jx = Math.sin(p * 42) * jitter * 0.55;
        var jy = Math.cos(p * 37) * jitter * 0.55;
        var alpha = p < 0.22 ? p / 0.22 : 1 - (p - 0.22) * 0.3;

        c.save();
        c.translate(chargeCenter.x + jx, chargeCenter.y + jy);

        // 外层赤金震颤热浪晕
        var g1 = c.createRadialGradient(0, 0, radius * 0.12, 0, 0, radius);
        g1.addColorStop(0, 'rgba(255,255,255,' + (0.65 * alpha).toFixed(3) + ')');
        g1.addColorStop(0.28, 'rgba(245,196,81,' + (0.48 * alpha).toFixed(3) + ')');
        g1.addColorStop(0.62, 'rgba(239,68,68,' + (0.24 * alpha).toFixed(3) + ')');
        g1.addColorStop(1, 'rgba(239,68,68,0)');
        c.fillStyle = g1;
        c.beginPath();
        c.arc(0, 0, radius, 0, TWO_PI);
        c.fill();

        // 内核白金聚锋亮斑
        var rCore = radius * 0.42;
        var g2 = c.createRadialGradient(0, 0, 0, 0, 0, rCore);
        g2.addColorStop(0, 'rgba(255,255,255,' + (0.9 * alpha).toFixed(3) + ')');
        g2.addColorStop(0.55, 'rgba(254,240,138,' + (0.42 * alpha).toFixed(3) + ')');
        g2.addColorStop(1, 'rgba(245,158,11,0)');
        c.fillStyle = g2;
        c.beginPath();
        c.arc(0, 0, rCore, 0, TWO_PI);
        c.fill();
        c.restore();
      },
    });

    // 聚气涡旋：金红 wisp 与火星向蓄势刀心高速内吸
    VFX.burst({
      x: chargeCenter.x,
      y: chargeCenter.y,
      radius: 68 * s,
      ring: true,
      count: 14,
      shape: 'wisp',
      colors: ['#f59e0b', '#ef4444', '#fef08a'],
      size: [3.2 * s, 6.2 * s],
      speed: [14 * s, 34 * s],
      vortex: 210 * s,
      vortexCenter: chargeCenter,
      attractTo: chargeCenter,
      attractStrength: 820 * s,
      life: [150, 220],
      trail: 3,
    });
    VFX.burst({
      x: chargeCenter.x,
      y: chargeCenter.y,
      radius: 60 * s,
      ring: true,
      count: 16,
      shape: 'spark',
      colors: ['#f5c451', '#ef4444', '#fef08a'],
      size: [2.0 * s, 3.8 * s],
      speed: [12 * s, 34 * s],
      attractTo: chargeCenter,
      attractStrength: 840 * s,
      life: [140, 210],
      trail: 3,
    });

    // 阶段二（155ms）：掌缘疾风刀气轨迹掠至靶点 + 三层叠透重剑弧斩 + plumeSprite 残烟刀风
    VFX.after(155, function () {
      // 从施法者掌缘飞袭靶点的重刀疾风轨迹
      if (axis.dist > 36) {
        VFX.streamer({
          x1: dashFrom.x,
          y1: dashFrom.y,
          x2: slashCenter.x,
          y2: slashCenter.y,
          arc: (axis.facingX >= 0 ? -1 : 1) * 18 * s,
          wobble: 3 * s,
          width: 12 * s,
          tailLength: 0.44,
          color: '#f59e0b',
          coreColor: '#fef08a',
          sparkColors: ['#fef08a', '#f59e0b', '#ffffff'],
          duration: 210,
        });
        for (var tw = 0; tw < 3; tw++) {
          var wu = 0.25 + tw * 0.25;
          VFX.burst({
            delay: tw * 16,
            x: VFX.lerp(dashFrom.x, slashCenter.x, wu),
            y: VFX.lerp(dashFrom.y, slashCenter.y, wu),
            radius: 8 * s,
            count: 4,
            shape: 'wisp',
            colors: ['#f59e0b', '#fef08a', '#ef4444'],
            size: [3.6 * s, 6.8 * s],
            baseAngle: axis.angle,
            spread: 0.45,
            speed: [130 * s, 260 * s],
            drag: 0.92,
            alpha: 0.78,
            life: [190, 320],
          });
        }
      }

      var slashX = slashCenter.x - axis.ux * 14 * s;
      var slashY = slashCenter.y - axis.uy * 14 * s - 6 * s;
      var sa = orientAngle(-2.22, axis);
      var ea = orientAngle(0.76, axis);

      // 第一层：宽幅赤红炙热外刃尾焰
      VFX.slash({
        x: slashX - axis.ux * 4 * s,
        y: slashY - axis.uy * 4 * s - 2 * s,
        radius: 114,
        tilt: 0.94,
        thickness: 42,
        startAngle: sa,
        endAngle: ea,
        color: '#dc2626',
        glowColor: '#ef4444',
        coreColor: '#f59e0b',
        duration: 300,
      });

      // 第二层：中层熔金重锋主弧
      VFX.slash({
        delay: 12,
        x: slashX,
        y: slashY,
        radius: 106,
        tilt: 0.94,
        thickness: 30,
        startAngle: sa,
        endAngle: ea,
        color: '#f59e0b',
        glowColor: '#ef4444',
        coreColor: '#fef08a',
        duration: 270,
      });

      // 第三层：内层白金破甲锐锋
      VFX.slash({
        delay: 24,
        x: slashX + axis.ux * 4 * s,
        y: slashY + axis.uy * 4 * s + 2 * s,
        radius: 96,
        tilt: 0.94,
        thickness: 17,
        startAngle: sa,
        endAngle: ea,
        color: '#fef08a',
        glowColor: '#f5c451',
        coreColor: '#ffffff',
        duration: 235,
      });

      // plumeSprite 残烟刀风：沿重斩轨迹连续叠绘切线拉伸柔羽气浪
      var plumeHot = getPlume('#f59e0b');
      var plumeSmoke = getPlume('#78350f');
      VFX.custom({
        duration: 380,
        draw: function (c, p) {
          var headP = Math.min(1, p * 1.9);
          var tailP = Math.max(0, (p - 0.18) / 0.82);
          var fade = p < 0.25 ? p / 0.25 : Math.pow(1 - (p - 0.25) / 0.75, 1.35);
          if (fade <= 0.01) return;

          var segs = 14;
          c.save();
          c.translate(slashX, slashY);
          for (var i = 0; i <= segs; i++) {
            var u = i / segs;
            if (u < tailP * 0.75 || u > headP) continue;
            var ang = VFX.lerp(sa, ea, u);
            var env = Math.sin(u * Math.PI);
            var rr = (104 + Math.sin(u * 6 + p * 8) * 6) * s;
            var px = Math.cos(ang) * rr;
            var py = Math.sin(ang) * rr * 0.94;
            var tangent = ang + (ea >= sa ? Math.PI * 0.5 : -Math.PI * 0.5);
            var sz = (24 + env * 22) * s * (1 + p * 0.45);

            c.save();
            c.translate(px, py);
            c.rotate(tangent);
            if (plumeSmoke) {
              c.globalCompositeOperation = 'source-over';
              c.globalAlpha = fade * env * 0.32;
              c.drawImage(plumeSmoke, -sz * 1.3, -sz * 0.7, sz * 2.6, sz * 1.4);
            }
            if (plumeHot) {
              c.globalCompositeOperation = 'lighter';
              c.globalAlpha = fade * env * 0.44;
              c.drawImage(plumeHot, -sz * 1.15, -sz * 0.55, sz * 2.3, sz * 1.1);
            } else {
              var g = c.createRadialGradient(0, 0, 1, 0, 0, sz);
              g.addColorStop(0, 'rgba(245,158,11,' + (0.4 * fade * env).toFixed(3) + ')');
              g.addColorStop(1, 'rgba(245,158,11,0)');
              c.fillStyle = g;
              c.beginPath();
              c.arc(0, 0, sz, 0, TWO_PI);
              c.fill();
            }
            c.restore();
          }
          c.restore();
        },
      });

      // 顺刀弧泼洒出的厚重刀风气缕
      for (var k = 0; k < 5; k++) {
        var arcU = 0.2 + k * 0.16;
        var aPos = VFX.lerp(sa, ea, arcU);
        var tangentA = aPos + (ea >= sa ? Math.PI * 0.5 : -Math.PI * 0.5);
        VFX.burst({
          delay: k * 14,
          x: slashX + Math.cos(aPos) * 100 * s,
          y: slashY + Math.sin(aPos) * 94 * s,
          count: 5,
          shape: 'wisp',
          colors: ['#f59e0b', '#fb923c', '#fef08a'],
          size: [4.2 * s, 8.2 * s],
          baseAngle: tangentA,
          spread: 0.75,
          speed: [85 * s, 195 * s],
          drag: 0.91,
          alpha: 0.76,
          life: [240, 440],
        });
      }
    });

    // 阶段三（220ms）：命中落地炸裂 —— 金色地裂 + 地面冲击气浪粒 + 金橙铁屑 + 星芒 + 震屏
    VFX.after(220, function () {
      VFX.groundCrack({
        x: f.x,
        y: f.y,
        radius: 98,
        branches: 8,
        color: '#f5c451',
        crackColor: '#090b10',
        lineWidth: 3.8,
        duration: 1550,
      });

      // 地层透视双环柔光冲击波
      VFX.shockwave({
        x: f.x,
        y: f.y,
        tilt: 0.44,
        fromRadius: 14,
        toRadius: 132,
        color: '#f59e0b',
        coreColor: '#fef08a',
        lineWidth: 4.4,
        duration: 420,
        double: true,
      });

      // 靶心炸裂环
      VFX.shockwave({
        x: slashCenter.x,
        y: slashCenter.y,
        fromRadius: 10,
        toRadius: 94,
        color: '#ef4444',
        coreColor: '#ffffff',
        lineWidth: 3.6,
        duration: 320,
        double: true,
      });

      // 靶心瞬爆 flare 星芒
      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y,
        radius: 8 * s,
        count: 4,
        shape: 'flare',
        colors: ['#ffffff', '#fef08a', '#f59e0b'],
        size: [5.5 * s, 9.5 * s],
        speed: [18 * s, 65 * s],
        spin: [-3, 3],
        life: [220, 360],
      });

      // 地面冲击气浪粒：贴地向两侧/四周狂涌的炽金尘浪与重烟
      VFX.burst({
        layer: 'ground',
        x: f.x,
        y: f.y,
        radius: 26 * s,
        ring: true,
        tiltY: 0.36,
        count: 22,
        shape: 'wisp',
        colors: ['#f59e0b', '#fb923c', '#d6d3d1'],
        size: [5.2 * s, 10.5 * s],
        speed: [115 * s, 250 * s],
        gravity: -28 * s,
        turbulence: 42 * s,
        drag: 0.89,
        alpha: 0.72,
        life: [320, 580],
      });

      VFX.burst({
        x: f.x,
        y: f.y,
        radius: 24 * s,
        count: 14,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#44403c', '#57534e', '#78716c'],
        size: [14 * s, 26 * s],
        angle: [-Math.PI * 0.94, -Math.PI * 0.06],
        speed: [48 * s, 125 * s],
        gravity: -22 * s,
        drag: 0.92,
        alpha: 0.52,
        life: [460, 780],
      });

      // 金橙铁屑 shard 与高频迸射火星 spark
      VFX.burst({
        x: f.x,
        y: f.y - 6 * s,
        radius: 14 * s,
        count: 26,
        shape: 'shard',
        colors: ['#f5c451', '#fb923c', '#ef4444', '#fde68a', '#ffffff'],
        size: [2.8 * s, 6.2 * s],
        angle: [-Math.PI * 0.92, -Math.PI * 0.08],
        speed: [130 * s, 310 * s],
        gravity: 320 * s,
        spin: [-11, 11],
        life: [380, 700],
        trail: 3,
      });

      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y,
        radius: 12 * s,
        count: 34,
        shape: 'spark',
        colors: ['#f5c451', '#fb923c', '#fef08a', '#ffffff'],
        size: [2.4 * s, 5.2 * s],
        speed: [145 * s, 350 * s],
        baseAngle: axis.angle,
        spread: Math.PI * 1.4,
        gravity: 160 * s,
        drag: 0.92,
        life: [280, 540],
        trail: 5,
      });

      // 余韵升腾灼热余烬
      VFX.burst({
        delay: 40,
        x: f.x,
        y: f.y - 10 * s,
        radius: 36 * s,
        count: 14,
        shape: 'ember',
        colors: ['#f59e0b', '#fb923c', '#fef08a'],
        size: [2.2 * s, 4.4 * s],
        angle: [-Math.PI * 0.8, -Math.PI * 0.2],
        speed: [35 * s, 95 * s],
        gravity: -85 * s,
        turbulence: 58 * s,
        life: [460, 820],
        trail: 3,
      });

      VFX.quake('md', 260);
      VFX.impact(targetEl, { color: '#fef08a', distance: 18, duration: 260 });

      var dmg = ctx.perTargetDamage != null ? Math.round(ctx.perTargetDamage) : (typeof ctx.damage === 'number' ? Math.round(ctx.damage) : null);
      if (dmg !== null) {
        VFX.floatText(targetEl, '-' + dmg, ctx.crit ? 'crit' : 'damage', {
          scale: ctx.crit ? 1.32 : 1.15,
        });
      }
    });
  });
});
