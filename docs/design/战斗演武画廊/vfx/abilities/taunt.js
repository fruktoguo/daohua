/**
 * ability.taunt · 嘲讽（体修定岳怒吼 · 深度流体粒子化重构版）
 * 演出阶段：
 *   0ms   重踏震地 + 脚底柔羽厚土玄金底晕与八卦阵 + 贴地重尘气浪
 *   60ms  体表环绕流动的半透明 wisp 金甲气盾 + 柔光护体穹罩（零硬六边形、零直筒柱）
 *   100ms..300ms 铁血玄金声浪涡旋粒子向外狂涌震荡
 *   240ms+ 敌方头顶红金 flare 嘲讽印记闪耀 + 红金 attractTo 嘲讽牵引丝粒飞归施法者
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var localPlumeCache = Object.create(null);

  /**
   * 十六进制颜色转 RGB 字符串
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
   * 获取柔羽气团精灵（边缘 alpha=0，避免任何硬线条轮廓）
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
      grad.addColorStop(0.38, 'rgba(' + rgb + ',' + (lb.peak * 0.42).toFixed(3) + ')');
      grad.addColorStop(0.72, 'rgba(' + rgb + ',' + (lb.peak * 0.1).toFixed(3) + ')');
      grad.addColorStop(1, 'rgba(' + rgb + ',0)');
      gctx.fillStyle = grad;
      gctx.beginPath();
      gctx.arc(lb.x, lb.y, lb.r, 0, TWO_PI);
      gctx.fill();
    }
    localPlumeCache[color] = c;
    return c;
  }

  function collectEnemies(casterEl, ctx) {
    var pool = [];
    if (ctx.enemyEls && ctx.enemyEls.length) {
      pool = ctx.enemyEls;
    } else if (ctx.targetEls && ctx.targetEls.length) {
      pool = ctx.targetEls;
    } else if (ctx.targetEl) {
      pool = [ctx.targetEl];
    }
    var out = [];
    for (var i = 0; i < pool.length; i++) {
      var el = pool[i];
      if (!el || el === casterEl) continue;
      if (el.classList && (el.classList.contains('is-defeated') || el.classList.contains('is-dead'))) continue;
      out.push(el);
    }
    return out;
  }

  VFX.register('ability.taunt', function (ctx) {
    var casterEl = ctx.casterEl || (ctx.targetEls && ctx.targetEls[0]) || ctx.targetEl || null;
    if (!casterEl) return;

    var s = VFX.scale();
    var isEnemyCaster = casterEl.classList && casterEl.classList.contains('is-enemy');
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    var axis = VFX.combatAxis(casterEl, foeSide);
    var c = VFX.clampStage(VFX.center(casterEl), 52 * s, 42 * s);
    var f = VFX.clampStage(VFX.foot(casterEl), 52 * s, 32 * s);
    var enemies = collectEnemies(casterEl, ctx);

    // 阶段一（0ms）：定岳重踏 + 脚底柔羽厚土底晕 + 八卦护阵 + 贴地冲击尘浪
    VFX.motion(casterEl, 'slam', { dir: 0, duration: 320 });
    VFX.quake('sm', 220);

    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 1120,
      draw: function (g, p) {
        var fade = p < 0.16 ? p / 0.16 : p > 0.72 ? (1 - p) / 0.28 : 1;
        var r = 98 * s * (p < 0.2 ? VFX.lerp(0.55, 1.0, p / 0.2) : 1);
        g.save();
        g.translate(f.x, f.y + 2 * s);
        g.scale(1, 0.44);
        var grad = g.createRadialGradient(0, 0, r * 0.08, 0, 0, r);
        grad.addColorStop(0, 'rgba(254,240,138,' + (0.34 * fade).toFixed(3) + ')');
        grad.addColorStop(0.45, 'rgba(216,166,87,' + (0.24 * fade).toFixed(3) + ')');
        grad.addColorStop(0.78, 'rgba(180,83,9,' + (0.09 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(180,83,9,0)');
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
      radius: 78,
      tilt: 0.44,
      layer: 'ground',
      color: '#d8a657',
      secondaryColor: '#fef08a',
      style: 'bagua',
      rotationSpeed: 1.0,
      duration: 1120,
      alpha: 0.76,
      lineWidth: 2.0,
    });

    VFX.shockwave({
      x: f.x,
      y: f.y + 2 * s,
      tilt: 0.44,
      fromRadius: 16,
      toRadius: 148,
      color: '#d8a657',
      coreColor: '#fef08a',
      lineWidth: 4.5,
      duration: 480,
      double: true,
    });

    VFX.burst({
      layer: 'ground',
      x: f.x,
      y: f.y,
      radius: 26 * s,
      ring: true,
      tiltY: 0.42,
      count: 16,
      shape: 'smoke',
      colors: ['#78350f', '#92400e', '#57534e'],
      size: [12 * s, 22 * s],
      speed: [55 * s, 130 * s],
      drag: 0.9,
      alpha: 0.44,
      life: [420, 740],
    });

    // 阶段二（60ms）：体表环绕流动的半透明 wisp 金甲气盾 + 柔羽气甲光罩（彻底取代硬六边形盾与直筒光柱）
    VFX.after(60, function () {
      var shieldPlume = getPlume('#d8a657');
      var corePlume = getPlume('#fef08a');

      // 护体半透明流动金甲光雾穹罩（仅用边缘 alpha=0 径向渐变与柔羽羽流环绕旋转）
      VFX.custom({
        duration: 940,
        draw: function (g, p) {
          var open = p < 0.18 ? p / 0.18 : 1;
          var fade = p < 0.68 ? 1 : Math.pow(1 - (p - 0.68) / 0.32, 1.3);
          if (fade <= 0.01) return;

          var rad = VFX.lerp(28 * s, 62 * s, open);
          g.save();
          g.translate(c.x, c.y);

          // 柔光核心气甲体积晕
          var domeGrad = g.createRadialGradient(0, 0, rad * 0.14, 0, 0, rad * 1.15);
          domeGrad.addColorStop(0, 'rgba(254,240,138,' + (0.24 * fade).toFixed(3) + ')');
          domeGrad.addColorStop(0.55, 'rgba(216,166,87,' + (0.2 * fade).toFixed(3) + ')');
          domeGrad.addColorStop(0.84, 'rgba(180,83,9,' + (0.08 * fade).toFixed(3) + ')');
          domeGrad.addColorStop(1, 'rgba(180,83,9,0)');
          g.fillStyle = domeGrad;
          g.beginPath();
          g.arc(0, 0, rad * 1.15, 0, TWO_PI);
          g.fill();

          // 环绕体表流动的 6 团半透明金甲气羽（取代硬六边形边线）
          if (shieldPlume) {
            for (var k = 0; k < 6; k++) {
              var orbitA = p * 5.2 + (k / 6) * TWO_PI;
              var ox = Math.cos(orbitA) * rad * 0.72;
              var oy = Math.sin(orbitA) * rad * 0.56 - Math.sin(p * 6 + k) * 6 * s;
              var wR = 26 * s * open;
              g.save();
              g.translate(ox, oy);
              g.rotate(orbitA + Math.PI * 0.5);
              g.globalAlpha = fade * 0.36;
              g.drawImage(shieldPlume, -wR * 1.35, -wR * 0.65, wR * 2.7, wR * 1.3);
              if (corePlume && k % 2 === 0) {
                g.globalAlpha = fade * 0.28;
                g.drawImage(corePlume, -wR * 0.9, -wR * 0.42, wR * 1.8, wR * 0.84);
              }
              g.restore();
            }
          }
          g.restore();
        },
      });

      // 体表正反双层螺旋流动的半透明 wisp 金甲气盾流
      for (var layer = 0; layer < 2; layer++) {
        (function (spinDir, delayAt) {
          VFX.burst({
            delay: delayAt,
            x: c.x,
            y: c.y + 6 * s,
            radius: 44 * s,
            ring: true,
            tiltY: 0.62,
            count: 24,
            shape: 'wisp',
            colors: ['#d8a657', '#f5c451', '#fef08a', '#b45309'],
            size: [4.8 * s, 9.6 * s],
            speed: [16 * s, 42 * s],
            vortex: 295 * s * spinDir,
            vortexCenter: { x: c.x, y: c.y - 6 * s },
            gravity: -48 * s,
            turbulence: 46 * s,
            drag: 0.96,
            alpha: 0.68,
            life: [680, 1020],
            trail: 3,
          });
        })(layer === 0 ? 1 : -1, layer * 80);
      }

      // 玄铜甲片碎屑与金辉灵珠环绕
      VFX.burst({
        x: c.x,
        y: c.y,
        radius: 46 * s,
        ring: true,
        tiltY: 0.56,
        count: 18,
        shape: 'orb',
        colors: ['#fef08a', '#f5c451', '#ffffff'],
        size: [2.0 * s, 3.8 * s],
        speed: [14 * s, 34 * s],
        vortex: 250 * s,
        vortexCenter: c,
        gravity: -36 * s,
        drag: 0.97,
        life: [580, 920],
        trail: 4,
      });

      // 护体气甲镇岳星印
      VFX.burst({
        x: c.x,
        y: c.y - 4 * s,
        radius: 10 * s,
        count: 2,
        shape: 'flare',
        colors: ['#fef08a', '#d8a657'],
        size: [5.0 * s, 7.6 * s],
        speed: [4 * s, 16 * s],
        life: [360, 520],
      });
    });

    // 阶段三（100ms..290ms）：铁血玄金声浪涡旋粒子严格沿 axis.angle 2D 扇面向敌阵重心推出
    var waveDelays = [100, 195, 290];
    for (var w = 0; w < waveDelays.length; w++) {
      (function (idx, at) {
        VFX.after(at, function () {
          // 全周柔光声波冲击波
          VFX.shockwave({
            x: c.x,
            y: c.y,
            tilt: 0.84,
            fromRadius: 20,
            toRadius: 235 + idx * 80,
            color: idx === 1 ? '#fef08a' : '#d8a657',
            coreColor: '#ffffff',
            lineWidth: 3.4 - idx * 0.4,
            duration: 460 + idx * 60,
            double: false,
          });

          // 朝向敌阵重心 2D 扇面爆发的铁血玄金声浪涡旋粒子群
          VFX.burst({
            x: c.x + axis.ux * 16 * s,
            y: c.y + axis.uy * 16 * s,
            radius: 16 * s,
            count: 20,
            shape: 'wisp',
            colors: ['#d8a657', '#f5c451', '#dc2626', '#fef08a'],
            size: [4.8 * s, 9.4 * s],
            baseAngle: axis.angle,
            spread: 1.35 + idx * 0.16,
            speed: [220 * s, 410 * s],
            vortex: (idx % 2 === 0 ? 1 : -1) * 160 * s,
            vortexCenter: c,
            turbulence: 85 * s,
            drag: 0.93,
            alpha: 0.78,
            life: [380, 620],
            trail: 3,
          });

          VFX.burst({
            x: c.x + axis.ux * 16 * s,
            y: c.y + axis.uy * 16 * s,
            radius: 14 * s,
            count: 14,
            shape: 'spark',
            colors: ['#fef08a', '#f5c451', '#f87171'],
            size: [2.0 * s, 4.0 * s],
            baseAngle: axis.angle,
            spread: 1.25,
            speed: [260 * s, 460 * s],
            drag: 0.92,
            life: [300, 500],
            trail: 4,
          });
        });
      })(w, waveDelays[w]);
    }

    // 阶段四（240ms+）：声波抵达敌阵，敌方头顶闪耀并向嘲讽者发射红金 attractTo 牵引丝粒
    for (var e = 0; e < enemies.length; e++) {
      (function (en, idx) {
        VFX.after(240 + idx * 55, function () {
          var ec = VFX.clampStage(VFX.center(en), 36 * s, 28 * s);
          var eh = VFX.head(en);
          var tauntHead = VFX.clampStage({ x: eh.x, y: eh.y - 6 * s }, 28 * s, 22 * s);

          VFX.hitFlash(en, '#f5c451', 160);
          VFX.shockwave({
            x: ec.x,
            y: ec.y,
            fromRadius: 48,
            toRadius: 10,
            color: '#dc2626',
            coreColor: '#fef08a',
            lineWidth: 2.4,
            duration: 240,
            double: false,
          });

          // 敌方头顶炸出红金嘲讽星芒印记
          VFX.burst({
            x: tauntHead.x,
            y: tauntHead.y,
            radius: 6 * s,
            count: 3,
            shape: 'flare',
            colors: ['#ef4444', '#f5c451', '#fef08a'],
            size: [3.4 * s, 5.4 * s],
            speed: [10 * s, 32 * s],
            life: [280, 440],
          });

          // 敌方头顶红金 attractTo 嘲讽牵引丝粒，携长拖尾高速射向嘲讽者核心
          VFX.burst({
            x: tauntHead.x,
            y: tauntHead.y,
            radius: 18 * s,
            count: 16,
            shape: 'spark',
            colors: ['#ef4444', '#f59e0b', '#fef08a'],
            size: [2.0 * s, 3.8 * s],
            speed: [35 * s, 85 * s],
            vortex: (idx % 2 === 0 ? 1 : -1) * 140 * s,
            vortexCenter: { x: (tauntHead.x + c.x) * 0.5, y: (tauntHead.y + c.y) * 0.5 },
            attractTo: c,
            attractStrength: 960 * s,
            drag: 0.94,
            life: [380, 620],
            trail: 7,
          });

          VFX.burst({
            delay: 45,
            x: tauntHead.x,
            y: tauntHead.y,
            radius: 14 * s,
            count: 10,
            shape: 'wisp',
            colors: ['#dc2626', '#d8a657', '#fef08a'],
            size: [3.2 * s, 5.8 * s],
            speed: [24 * s, 65 * s],
            attractTo: c,
            attractStrength: 860 * s,
            drag: 0.94,
            alpha: 0.82,
            life: [400, 650],
            trail: 4,
          });
        });
      })(enemies[e], e);
    }
  });
});
