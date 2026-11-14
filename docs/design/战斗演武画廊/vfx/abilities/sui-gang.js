/**
 * ability.sui-gang · 碎罡（刀修自增益三重剑意 · 深度流体粒子化重构版）
 * 演出阶段：
 *   0ms   脚底展开黑金柔光煞阵 + 聚煞尘烟
 *   120ms 三轴流光刀意环绕（纯柔羽光点轨与 slash 斩弧，彻底删除硬椭圆与直筒柱）+ 螺旋升腾罡风 wisp + 三系内吸归体
 *   480ms 碎罡震破瞬间：贴地撕裂尘烟 + 红铜金属崩碎 shard + 朝敌方四散的高密度 wisp/ember 裂甲火花 + 三重飘字
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var DEG_25 = (25 * Math.PI) / 180;
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
    grad.addColorStop(0.44, 'rgba(' + rgb + ',0.32)');
    grad.addColorStop(0.76, 'rgba(' + rgb + ',0.07)');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    gctx.fillStyle = grad;
    gctx.beginPath();
    gctx.arc(48, 48, 46, 0, TWO_PI);
    gctx.fill();
    localGlowCache[color] = c;
    return c;
  }

  VFX.register('ability.sui-gang', function (ctx) {
    var casterEl = ctx.casterEl || (ctx.targetEls && ctx.targetEls[0]) || ctx.targetEl || null;
    if (!casterEl) return;

    var s = VFX.scale();
    var isEnemyCaster = casterEl.classList && casterEl.classList.contains('is-enemy');
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    var axis = VFX.combatAxis(casterEl, foeSide);
    var c = VFX.clampStage(VFX.center(casterEl), 50 * s, 40 * s);
    var f = VFX.clampStage(VFX.foot(casterEl), 50 * s, 30 * s);
    var rawH = VFX.head(casterEl);
    var h = VFX.clampStage({ x: rawH.x, y: Math.max(54, rawH.y) }, 44 * s, 54);

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：脚底展开金黑护身柔阵 + 聚煞尘雾
    // -------------------------------------------------------------------------
    VFX.motion(casterEl, 'cast', { duration: 480 });

    // 沉稳黑金暗影底盘（纯边缘 alpha=0 径向渐变）
    VFX.custom({
      layer: 'ground',
      blend: 'source-over',
      duration: 1120,
      draw: function (g, p) {
        var fade = p < 0.15 ? p / 0.15 : p > 0.72 ? (1 - p) / 0.28 : 1;
        var r = 92 * s * (p < 0.18 ? VFX.lerp(0.5, 1, p / 0.18) : 1);
        g.save();
        g.translate(f.x, f.y + 2 * s);
        g.scale(1, 0.44);
        var grad = g.createRadialGradient(0, 0, r * 0.1, 0, 0, r);
        grad.addColorStop(0, 'rgba(12,10,9,' + (0.78 * fade).toFixed(3) + ')');
        grad.addColorStop(0.55, 'rgba(69,26,3,' + (0.42 * fade).toFixed(3) + ')');
        grad.addColorStop(0.82, 'rgba(180,83,9,' + (0.18 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(12,10,9,0)');
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
      radius: 76,
      tilt: 0.44,
      layer: 'ground',
      color: '#f5c451',
      secondaryColor: '#292524',
      style: 'sword',
      rotationSpeed: 1.4,
      duration: 1120,
      alpha: 0.76,
      lineWidth: 2.0,
    });

    VFX.magicCircle({
      delay: 40,
      x: f.x,
      y: f.y + 2 * s,
      radius: 52,
      tilt: 0.44,
      layer: 'ground',
      color: '#d97706',
      secondaryColor: '#f5c451',
      style: 'bagua',
      rotationSpeed: -1.8,
      duration: 1050,
      alpha: 0.68,
      lineWidth: 1.6,
    });

    // 聚煞初期黑红暗烟环脚底涡旋升腾
    VFX.burst({
      x: f.x,
      y: f.y,
      radius: 42 * s,
      ring: true,
      tiltY: 0.44,
      count: 14,
      shape: 'smoke',
      blend: 'source-over',
      colors: ['#1c1917', '#451a03', '#78350f'],
      size: [12 * s, 20 * s],
      speed: [22 * s, 54 * s],
      vortex: 145 * s,
      vortexCenter: c,
      gravity: -32 * s,
      alpha: 0.48,
      life: [460, 780],
    });

    // -------------------------------------------------------------------------
    // 阶段二（120ms）：三轴立体流光刀环 + 螺旋升腾流体罡气 + 三系内吸归体
    // -------------------------------------------------------------------------
    VFX.after(120, function () {
      // 替代直筒光柱：双股高旋度红铜/金/白 wisp 气缕与余烬螺旋升腾
      for (var colIdx = 0; colIdx < 2; colIdx++) {
        (function (spinDir, at) {
          VFX.burst({
            delay: at,
            x: f.x,
            y: f.y - 4 * s,
            radius: 30 * s,
            ring: true,
            tiltY: 0.44,
            count: 20,
            shape: 'wisp',
            colors: ['#f5c451', '#d97706', '#ef4444', '#e2e8f0'],
            size: [4.2 * s, 8.8 * s],
            angle: [-Math.PI * 0.68, -Math.PI * 0.32],
            speed: [70 * s, 155 * s],
            vortex: 245 * s * spinDir,
            vortexCenter: c,
            gravity: -120 * s,
            turbulence: 68 * s,
            drag: 0.96,
            alpha: 0.72,
            life: [460, 760],
            trail: 3,
          });
          VFX.burst({
            delay: at + 25,
            x: f.x,
            y: f.y - 6 * s,
            radius: 26 * s,
            ring: true,
            tiltY: 0.44,
            count: 14,
            shape: 'ember',
            colors: ['#fb923c', '#f5c451', '#fef08a'],
            size: [2.2 * s, 4.4 * s],
            angle: [-Math.PI * 0.68, -Math.PI * 0.32],
            speed: [65 * s, 145 * s],
            vortex: -215 * s * spinDir,
            vortexCenter: c,
            gravity: -130 * s,
            turbulence: 54 * s,
            drag: 0.96,
            life: [440, 720],
            trail: 4,
          });
        })(colIdx === 0 ? 1 : -1, colIdx * 90);
      }

      // 三向刀环羽化扫光
      VFX.slash({
        x: c.x,
        y: c.y,
        radius: 74,
        tilt: 0.34,
        rotation: DEG_25,
        startAngle: 0,
        endAngle: TWO_PI + 0.25,
        thickness: 15,
        color: '#f5c451',
        glowColor: '#f59e0b',
        coreColor: '#fef08a',
        sparks: false,
        duration: 420,
      });

      VFX.slash({
        delay: 45,
        x: c.x,
        y: c.y,
        radius: 78,
        tilt: 0.34,
        rotation: -DEG_25,
        startAngle: Math.PI,
        endAngle: -Math.PI - 0.25,
        thickness: 15,
        color: '#ef4444',
        glowColor: '#b45309',
        coreColor: '#fecaca',
        sparks: false,
        duration: 420,
      });

      VFX.slash({
        delay: 90,
        x: c.x,
        y: c.y + 4 * s,
        radius: 82,
        tilt: 0.32,
        rotation: 0,
        startAngle: -0.35,
        endAngle: TWO_PI - 0.05,
        thickness: 14,
        color: '#cbd5e1',
        glowColor: '#93c5fd',
        coreColor: '#ffffff',
        sparks: false,
        duration: 420,
      });

      // 三轴柔羽流光游走轨（rx/ry 随 s 缩放，零硬线椭圆）
      var rings = [
        { rot: DEG_25, rx: 72 * s, ry: 24 * s, col: '#f5c451', dir: 1 },
        { rot: -DEG_25, rx: 76 * s, ry: 25 * s, col: '#ef4444', dir: -1 },
        { rot: 0, rx: 80 * s, ry: 23 * s, col: '#e2e8f0', dir: 1.2 },
      ];
      VFX.custom({
        duration: 540,
        draw: function (g, p) {
          var open = p < 0.2 ? p / 0.2 : 1;
          var fade = p < 0.72 ? 1 : 1 - (p - 0.72) / 0.28;
          if (fade <= 0.01) return;

          for (var i = 0; i < rings.length; i++) {
            var rg = rings[i];
            var rx = rg.rx * (0.65 + 0.35 * open);
            var ry = rg.ry * (0.65 + 0.35 * open);
            var sp = getSoftGlow(rg.col);

            g.save();
            g.translate(c.x, c.y);
            g.rotate(rg.rot);

            // 每环两枚拖曳 6 节柔羽光尾的游走星核（零硬线椭圆）
            for (var orb = 0; orb < 2; orb++) {
              var headAng = p * 9.5 * rg.dir + orb * Math.PI + i * 1.1;
              for (var tIdx = 0; tIdx < 7; tIdx++) {
                var frac = 1 - tIdx / 7;
                var ang = headAng - tIdx * 0.14 * rg.dir;
                var ox = Math.cos(ang) * rx;
                var oy = Math.sin(ang) * ry;
                var rGlow = (tIdx === 0 ? 14 : 10 * frac) * s;
                if (sp) {
                  g.globalAlpha = fade * (tIdx === 0 ? 0.92 : 0.42 * frac * frac);
                  g.drawImage(sp, ox - rGlow, oy - rGlow, rGlow * 2, rGlow * 2);
                } else {
                  var og = g.createRadialGradient(ox, oy, 0, ox, oy, rGlow);
                  og.addColorStop(0, 'rgba(255,255,255,' + (0.8 * fade * frac).toFixed(3) + ')');
                  og.addColorStop(1, 'rgba(245,196,81,0)');
                  g.fillStyle = og;
                  g.beginPath();
                  g.arc(ox, oy, rGlow, 0, TWO_PI);
                  g.fill();
                }
              }
            }
            g.restore();
          }
        },
      });

      // 金 / 红铜赤煞 / 寒白三系护体气劲粒子向体表急聚归体
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 92 * s,
        ring: true,
        count: 14,
        shape: 'wisp',
        colors: ['#f5c451', '#fef08a'],
        size: [3.2 * s, 5.8 * s],
        speed: [10 * s, 30 * s],
        attractTo: c,
        attractStrength: 820 * s,
        life: [280, 380],
        trail: 4,
      });
      VFX.burst({
        delay: 45,
        x: c.x,
        y: c.y,
        radius: 96 * s,
        ring: true,
        count: 14,
        shape: 'ember',
        colors: ['#b45309', '#ef4444', '#fca5a5'],
        size: [2.6 * s, 4.8 * s],
        speed: [10 * s, 30 * s],
        attractTo: c,
        attractStrength: 850 * s,
        life: [280, 380],
        trail: 3,
      });
      VFX.burst({
        delay: 90,
        x: c.x,
        y: c.y,
        radius: 100 * s,
        ring: true,
        count: 14,
        shape: 'shard',
        colors: ['#e2e8f0', '#bae6fd', '#ffffff'],
        size: [2.6 * s, 4.8 * s],
        speed: [10 * s, 30 * s],
        attractTo: c,
        attractStrength: 880 * s,
        life: [280, 380],
        trail: 4,
      });
    });

    // -------------------------------------------------------------------------
    // 阶段三（480ms）：碎罡碎甲震破瞬间
    // 贴地撕裂尘烟 + 红铜金属崩碎 shard + 朝敌阵重心 axis.angle 喷涌的裂甲火花
    // -------------------------------------------------------------------------
    VFX.after(480, function () {
      VFX.quake('sm', 240);
      VFX.hitFlash(casterEl, '#fef08a', 240);

      // 脚底震裂地纹
      VFX.groundCrack({
        x: f.x,
        y: f.y,
        radius: 82,
        branches: 7,
        color: '#d97706',
        crackColor: '#0c0a09',
        lineWidth: 3.0,
        duration: 1350,
      });

      // 三重主题柔光冲击波
      VFX.shockwave({
        x: c.x,
        y: c.y,
        fromRadius: 12,
        toRadius: 115,
        color: '#f5c451',
        coreColor: '#fef08a',
        lineWidth: 4.0,
        duration: 390,
        double: false,
      });
      VFX.shockwave({
        delay: 35,
        x: c.x,
        y: c.y,
        fromRadius: 14,
        toRadius: 142,
        color: '#b45309',
        coreColor: '#fca5a5',
        lineWidth: 3.4,
        duration: 420,
        double: false,
      });
      VFX.shockwave({
        delay: 70,
        x: c.x,
        y: c.y,
        fromRadius: 16,
        toRadius: 168,
        color: '#cbd5e1',
        coreColor: '#ffffff',
        lineWidth: 3.0,
        duration: 450,
        double: false,
      });

      // 碎甲核心星芒瞬爆
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 10 * s,
        count: 5,
        shape: 'flare',
        colors: ['#ffffff', '#fef08a', '#fb923c'],
        size: [4.8 * s, 8.2 * s],
        speed: [24 * s, 78 * s],
        spin: [-4, 4],
        life: [240, 420],
      });

      // 贴地撕裂尘烟：沿地面向八方贴地狂涌的玄黑、灰褐与暗铜烟尘气浪
      VFX.burst({
        layer: 'ground',
        x: f.x,
        y: f.y,
        radius: 26 * s,
        ring: true,
        tiltY: 0.42,
        count: 18,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#1c1917', '#44403c', '#78350f', '#57534e'],
        size: [14 * s, 28 * s],
        speed: [75 * s, 185 * s],
        gravity: -16 * s,
        turbulence: 52 * s,
        drag: 0.9,
        alpha: 0.62,
        life: [460, 820],
      });
      VFX.burst({
        layer: 'ground',
        x: f.x,
        y: f.y,
        radius: 24 * s,
        ring: true,
        tiltY: 0.4,
        count: 20,
        shape: 'wisp',
        colors: ['#d97706', '#b45309', '#f5c451', '#a8a29e'],
        size: [5.0 * s, 10.2 * s],
        speed: [105 * s, 240 * s],
        gravity: -22 * s,
        turbulence: 46 * s,
        drag: 0.9,
        alpha: 0.74,
        life: [340, 620],
      });

      // 红铜金属崩碎 shard（带旋转与落地震溅）
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 16 * s,
        count: 26,
        shape: 'shard',
        colors: ['#b45309', '#d97706', '#f5c451', '#fb923c', '#e2e8f0'],
        size: [2.8 * s, 6.0 * s],
        speed: [130 * s, 310 * s],
        gravity: 250 * s,
        drag: 0.94,
        spin: [-12, 12],
        life: [360, 680],
        trail: 4,
      });

      // 震荡朝敌阵重心 axis.angle 扇面喷涌的高密度 wisp / ember 裂甲火花
      VFX.burst({
        x: c.x + axis.ux * 12 * s,
        y: c.y + axis.uy * 12 * s,
        radius: 14 * s,
        count: 24,
        shape: 'wisp',
        colors: ['#b45309', '#f59e0b', '#ef4444', '#fef08a'],
        size: [4.2 * s, 8.6 * s],
        baseAngle: axis.angle,
        spread: Math.PI * 0.95,
        speed: [185 * s, 420 * s],
        turbulence: 92 * s,
        drag: 0.92,
        alpha: 0.85,
        life: [340, 620],
        trail: 4,
      });
      VFX.burst({
        x: c.x + axis.ux * 14 * s,
        y: c.y + axis.uy * 14 * s,
        radius: 12 * s,
        count: 28,
        shape: 'ember',
        colors: ['#d97706', '#fb923c', '#f5c451', '#ffffff'],
        size: [2.4 * s, 5.2 * s],
        baseAngle: axis.angle,
        spread: Math.PI * 1.1,
        speed: [160 * s, 380 * s],
        gravity: 110 * s,
        turbulence: 80 * s,
        drag: 0.93,
        life: [360, 680],
        trail: 5,
      });
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 14 * s,
        count: 16,
        shape: 'spark',
        colors: ['#f5c451', '#fb923c', '#e2e8f0', '#ffffff'],
        size: [2.2 * s, 4.4 * s],
        speed: [125 * s, 290 * s],
        drag: 0.92,
        life: [280, 520],
        trail: 4,
      });

      // 三重剑意飘字错位依次升起（保持安全上边距）
      var txtRise = Math.round(46 * Math.max(0.76, s));
      VFX.floatText(VFX.clampStage({ x: h.x - 22 * s, y: h.y }, 38 * s, 46), '碎罡', '#f5c451', {
        scale: 1.08,
        rise: txtRise,
        duration: 920,
      });
      VFX.after(110, function () {
        VFX.floatText(VFX.clampStage({ x: h.x + 22 * s, y: h.y - 6 * s }, 38 * s, 50), '煞力', '#f87171', {
          scale: 1.08,
          rise: txtRise + 2,
          duration: 920,
        });
      });
      VFX.after(220, function () {
        VFX.floatText(VFX.clampStage({ x: h.x, y: h.y - 12 * s }, 38 * s, 54), '罡锋', '#e2e8f0', {
          scale: 1.14,
          rise: txtRise + 6,
          duration: 960,
        });
      });
    });
  });
});
