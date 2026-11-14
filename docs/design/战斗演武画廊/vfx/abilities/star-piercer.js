/**
 * ability.star-piercer · 贯星（金系二阶巅峰单体突刺 + 击杀连环 · 深度流体粒子化重构版）
 * 演出阶段：
 *   0ms   重磅暗紫夜幕 + 剑阵底晕 + 金紫星流向剑尖聚拢（attractTo）
 *   220ms 螺旋 wisp + 高速 spark 流 + 双股螺旋流光丝（streamer）构成破空流光矢（零生硬直线射线）
 *   310ms 命中瞬间：星核极暗紫金蓄能点急剧内吸坍缩（attractTo）
 *   410ms 轰然破星炸裂：多向 flare 破星光芒 + shard 裂片 + 紫金星云 wisp 冲击 + 重震屏
 *   640ms 若 ctx.kill 或存在其他活敌，自爆裂点折射 1~2 道次级螺旋流光矢连刺余敌
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
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
   * 获取高羽化辉光精灵（边缘 alpha 严格为 0）
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

  function collectChainFoes(casterEl, primaryTarget, targets, ctx) {
    var isEnemyCaster = casterEl && casterEl.classList && casterEl.classList.contains('is-enemy');
    var pool = [];
    var raw = isEnemyCaster ? ctx.allyEls : ctx.enemyEls;
    if (raw && raw.length) {
      for (var i = 0; i < raw.length; i++) pool.push(raw[i]);
    }
    for (var j = 1; j < targets.length; j++) {
      pool.push(targets[j]);
    }
    var out = [];
    for (var k = 0; k < pool.length; k++) {
      var el = pool[k];
      if (!el || el === primaryTarget || el === casterEl) continue;
      if (el.classList && (el.classList.contains('is-defeated') || el.classList.contains('is-dead'))) continue;
      if (out.indexOf(el) === -1) out.push(el);
    }
    return out;
  }

  /**
   * 发射由螺旋 wisp + 高速 spark 流 + 双股波动流光丝构成的流体贯星矢（彻底替代生硬直线射线）
   */
  function emitSpiralLightArrow(fromPt, toPt, opts) {
    opts = opts || {};
    var dv = VFX.dir(fromPt, toPt);
    var nx = -dv.dy;
    var ny = dv.dx;
    var steps = opts.steps || 12;
    var travelMs = opts.travelMs || 105;
    var s = VFX.scale();
    var arrowScale = s * (opts.scaleMul || 1);
    var distRef = opts.axisDist !== undefined ? opts.axisDist : dv.dist;
    var distFactor = Math.min(1, distRef / 260);
    var spiralAmp = (opts.spiralAmp || 15) * distFactor * arrowScale;
    var primaryColors = opts.colors || ['#f5c451', '#fef08a', '#c084fc', '#ffffff'];

    // 双股交缠柔弧流光丝：带正弦曲率与波动，彻底破除笔直硬射线感
    VFX.streamer({
      x1: fromPt.x,
      y1: fromPt.y,
      x2: toPt.x,
      y2: toPt.y,
      arc: 22 * distFactor * arrowScale,
      wobble: 7 * distFactor * arrowScale,
      width: 10 * arrowScale,
      tailLength: 0.48,
      color: '#f5c451',
      coreColor: '#ffffff',
      sparkColors: primaryColors,
      duration: travelMs + 160,
    });
    VFX.streamer({
      delay: 14,
      x1: fromPt.x,
      y1: fromPt.y,
      x2: toPt.x,
      y2: toPt.y,
      arc: -20 * distFactor * arrowScale,
      wobble: 6 * distFactor * arrowScale,
      width: 8 * arrowScale,
      tailLength: 0.44,
      color: '#a855f7',
      coreColor: '#fef08a',
      sparkColors: ['#c084fc', '#fef08a', '#ffffff'],
      duration: travelMs + 170,
    });

    // 沿路径分段喷涌螺旋 wisp 与高速顺向 spark 流，构建流动体积与缠绕星轨
    for (var st = 0; st <= steps; st++) {
      (function (idx) {
        var u = idx / steps;
        var at = Math.round(u * travelMs);
        var env = Math.sin(u * Math.PI);
        var phase = u * Math.PI * 3.8;
        var bx = VFX.lerp(fromPt.x, toPt.x, u);
        var by = VFX.lerp(fromPt.y, toPt.y, u);
        var off = Math.sin(phase) * spiralAmp * (0.35 + 0.65 * env);

        VFX.burst({
          delay: at,
          x: bx + nx * off,
          y: by + ny * off,
          radius: 5 * arrowScale,
          count: 3,
          shape: 'wisp',
          colors: primaryColors,
          size: [3.2 * arrowScale, 6.2 * arrowScale],
          baseAngle: dv.angle + Math.cos(phase) * 0.24,
          spread: 0.55,
          speed: [180 * arrowScale, 340 * arrowScale],
          turbulence: 72 * arrowScale,
          drag: 0.92,
          alpha: 0.78,
          life: [210, 380],
          trail: 3,
        });

        VFX.burst({
          delay: at + 6,
          x: bx - nx * off * 0.85,
          y: by - ny * off * 0.85,
          radius: 4 * arrowScale,
          count: 4,
          shape: 'spark',
          colors: ['#ffffff', '#fef08a', '#f5c451', '#c084fc'],
          size: [1.6 * arrowScale, 3.4 * arrowScale],
          baseAngle: dv.angle - Math.cos(phase) * 0.2,
          spread: 0.48,
          speed: [260 * arrowScale, 480 * arrowScale],
          drag: 0.93,
          life: [200, 360],
          trail: 5,
        });

        if (idx % 3 === 1) {
          VFX.burst({
            delay: at,
            x: bx,
            y: by,
            radius: 6 * arrowScale,
            count: 2,
            shape: 'orb',
            colors: ['#fef08a', '#c084fc'],
            size: [2.2 * arrowScale, 3.8 * arrowScale],
            speed: [18 * arrowScale, 62 * arrowScale],
            drag: 0.94,
            life: [260, 440],
          });
        }
      })(st);
    }
  }

  /**
   * 破星核心柔羽爆裂（纯边缘 alpha=0 径向渐变叠加 + 多向 flare 粒子，彻底删除硬尖菱形）
   */
  function emitStarBurst(x, y, maxRadius, duration, scaleMul) {
    var s = VFX.scale();
    var mul = scaleMul || 1;
    var sc = s * mul;
    var glowGold = getSoftGlow('#f5c451');
    var glowPurple = getSoftGlow('#7c3aed');

    // 柔和径向爆星余晖（零硬菱形、零硬描边）
    VFX.custom({
      duration: duration,
      draw: function (g, p) {
        var open = p < 0.16 ? p / 0.16 : 1 - (p - 0.16) * 0.25;
        var alpha = p < 0.1 ? p / 0.1 : Math.pow(1 - (p - 0.1) / 0.9, 1.45);
        if (alpha <= 0.01) return;

        var rad = maxRadius * sc * open;
        g.save();
        g.translate(x, y);

        var outerGrad = g.createRadialGradient(0, 0, 2, 0, 0, rad);
        outerGrad.addColorStop(0, 'rgba(255,255,255,' + (0.95 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(0.22, 'rgba(254,240,138,' + (0.72 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(0.54, 'rgba(168,85,247,' + (0.34 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(0.82, 'rgba(76,29,149,' + (0.12 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(1, 'rgba(76,29,149,0)');
        g.fillStyle = outerGrad;
        g.beginPath();
        g.arc(0, 0, rad, 0, TWO_PI);
        g.fill();

        if (glowPurple) {
          g.globalAlpha = alpha * 0.45;
          g.drawImage(glowPurple, -rad * 0.85, -rad * 0.85, rad * 1.7, rad * 1.7);
        }
        if (glowGold) {
          g.globalAlpha = alpha * 0.88;
          g.drawImage(glowGold, -rad * 0.52, -rad * 0.52, rad * 1.04, rad * 1.04);
        }
        g.restore();
      },
    });

    // 破星多向 flare 光芒（中心主星芒 + 放射四散次级星芒）
    VFX.burst({
      x: x,
      y: y,
      radius: 4 * sc,
      count: 2,
      shape: 'flare',
      colors: ['#ffffff', '#fef08a'],
      size: [10 * sc, 14.5 * sc],
      speed: [4 * sc, 18 * sc],
      spin: [-1.4, 1.4],
      alpha: 0.96,
      life: [280, 420],
    });
    VFX.burst({
      x: x,
      y: y,
      radius: 16 * sc,
      count: Math.max(5, Math.round(8 * mul)),
      shape: 'flare',
      colors: ['#fef08a', '#f5c451', '#c084fc', '#ffffff'],
      size: [4.0 * sc, 7.2 * sc],
      speed: [55 * sc, 175 * sc],
      spin: [-4.5, 4.5],
      drag: 0.91,
      life: [260, 460],
    });
  }

  /**
   * 命中后极暗紫金星核坍缩吸积阶段（吸引外部碎片向星核狂涌后再引爆破星）
   */
  function emitSingularityImplosion(pt, durationMs) {
    var s = VFX.scale();

    // 极暗紫金星核吸积盘（边缘 alpha=0 径向渐变叠涂，先微张再坍缩为亮白一点）
    VFX.custom({
      layer: 'sky',
      blend: 'source-over',
      duration: durationMs + 45,
      draw: function (g, p) {
        var shrink = p < 0.72 ? VFX.lerp(1.0, 0.36, p / 0.72) : Math.max(0.05, 0.36 * (1 - (p - 0.72) / 0.28));
        var alpha = p < 0.25 ? p / 0.25 : p > 0.85 ? (1 - p) / 0.15 : 1;
        var rad = 64 * s * shrink;

        g.save();
        g.translate(pt.x, pt.y);

        // 外层暗紫吸积晕
        var darkGrad = g.createRadialGradient(0, 0, 2 * s, 0, 0, rad * 1.45);
        darkGrad.addColorStop(0, 'rgba(9,6,20,' + (0.94 * alpha).toFixed(3) + ')');
        darkGrad.addColorStop(0.45, 'rgba(46,16,101,' + (0.76 * alpha).toFixed(3) + ')');
        darkGrad.addColorStop(0.78, 'rgba(124,58,237,' + (0.32 * alpha).toFixed(3) + ')');
        darkGrad.addColorStop(1, 'rgba(124,58,237,0)');
        g.fillStyle = darkGrad;
        g.beginPath();
        g.arc(0, 0, rad * 1.45, 0, TWO_PI);
        g.fill();

        // 紫金坍缩内环能量
        g.globalCompositeOperation = 'lighter';
        var goldGrad = g.createRadialGradient(0, 0, 0, 0, 0, rad * 0.72);
        goldGrad.addColorStop(0, 'rgba(255,255,255,' + (0.92 * alpha).toFixed(3) + ')');
        goldGrad.addColorStop(0.32, 'rgba(245,196,81,' + (0.64 * alpha).toFixed(3) + ')');
        goldGrad.addColorStop(0.68, 'rgba(168,85,247,' + (0.28 * alpha).toFixed(3) + ')');
        goldGrad.addColorStop(1, 'rgba(168,85,247,0)');
        g.fillStyle = goldGrad;
        g.beginPath();
        g.arc(0, 0, rad * 0.72, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });

    // 周围粒子向极暗紫金星核剧烈 attractTo 内吸蓄能
    VFX.burst({
      x: pt.x,
      y: pt.y,
      radius: 112 * s,
      ring: true,
      count: 26,
      shape: 'wisp',
      colors: ['#7c3aed', '#4c1d95', '#f5c451', '#fef08a'],
      size: [3.8 * s, 7.2 * s],
      speed: [18 * s, 48 * s],
      vortex: 260 * s,
      vortexCenter: pt,
      attractTo: pt,
      attractStrength: 1380 * s,
      drag: 0.93,
      alpha: 0.88,
      life: [durationMs - 10, durationMs + 35],
      trail: 4,
    });
    VFX.burst({
      x: pt.x,
      y: pt.y,
      radius: 96 * s,
      ring: true,
      count: 22,
      shape: 'spark',
      colors: ['#f5c451', '#c084fc', '#ffffff'],
      size: [2.0 * s, 4.0 * s],
      speed: [12 * s, 36 * s],
      attractTo: pt,
      attractStrength: 1450 * s,
      drag: 0.94,
      life: [durationMs - 15, durationMs + 25],
      trail: 5,
    });
  }

  VFX.register('ability.star-piercer', function (ctx) {
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length)
      ? ctx.targetEls
      : (ctx.targetEl ? [ctx.targetEl] : []);
    var targetEl = targets[0] || null;
    if (!targetEl) return;

    var s = VFX.scale();
    var axis = VFX.combatAxis(casterEl, targetEl);
    var t = VFX.clampStage(VFX.center(targetEl), 44 * s, 36 * s);

    var backPt = VFX.clampStage({
      x: axis.start.x - axis.ux * 22 * s,
      y: axis.start.y - axis.uy * 22 * s,
    }, 28 * s, 28 * s);
    var tipPt = VFX.clampStage(axis.palm(28 * s), 24 * s, 24 * s);

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：重磅暗紫夜幕 + 天枢剑阵 + 拉弦背步 + 星流向剑尖凝缩
    // -------------------------------------------------------------------------
    VFX.cinematicDim(0.46, 860, '#070410');
    if (casterEl) {
      VFX.motion(casterEl, 'lunge', { dx: -axis.ux, dy: -axis.uy, distance: 14, duration: 200 });
    }

    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 620,
      draw: function (g, p) {
        var fade = p < 0.2 ? p / 0.2 : Math.pow(1 - (p - 0.2) / 0.8, 1.3);
        var rad = 98 * s * (p < 0.22 ? VFX.lerp(0.4, 1.0, p / 0.22) : 1);
        g.save();
        g.translate(backPt.x, backPt.y);
        var grad = g.createRadialGradient(0, 0, 2 * s, 0, 0, rad);
        grad.addColorStop(0, 'rgba(254,240,138,' + (0.36 * fade).toFixed(3) + ')');
        grad.addColorStop(0.42, 'rgba(168,85,247,' + (0.24 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(76,29,149,0)');
        g.fillStyle = grad;
        g.beginPath();
        g.arc(0, 0, rad, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });

    VFX.magicCircle({
      x: backPt.x,
      y: backPt.y,
      radius: 80,
      tilt: 0.9,
      layer: 'sky',
      color: '#f5c451',
      secondaryColor: '#e9d5ff',
      style: 'sword',
      rotationSpeed: 2.5,
      duration: 620,
      alpha: 0.78,
      lineWidth: 2.0,
    });

    VFX.magicCircle({
      x: backPt.x,
      y: backPt.y,
      radius: 50,
      tilt: 0.9,
      layer: 'sky',
      color: '#a855f7',
      secondaryColor: '#fef08a',
      style: 'bagua',
      rotationSpeed: -3.2,
      duration: 580,
      alpha: 0.68,
      lineWidth: 1.5,
    });

    // 金紫星流向剑尖聚拢成一点
    VFX.burst({
      x: tipPt.x,
      y: tipPt.y,
      radius: 96 * s,
      ring: true,
      count: 24,
      shape: 'wisp',
      colors: ['#fef08a', '#f5c451', '#c084fc'],
      size: [3.2 * s, 6.0 * s],
      speed: [14 * s, 42 * s],
      vortex: 185 * s,
      vortexCenter: tipPt,
      attractTo: tipPt,
      attractStrength: 1020 * s,
      drag: 0.93,
      life: [200, 290],
      trail: 4,
    });
    VFX.burst({
      x: tipPt.x,
      y: tipPt.y,
      radius: 82 * s,
      count: 16,
      shape: 'orb',
      colors: ['#fef08a', '#ffffff', '#a855f7'],
      size: [2.0 * s, 4.0 * s],
      speed: [10 * s, 30 * s],
      attractTo: tipPt,
      attractStrength: 980 * s,
      life: [190, 280],
      trail: 4,
    });

    // -------------------------------------------------------------------------
    // 阶段二（220ms）：螺旋 wisp + 高速 spark 流构成破空流光矢
    // -------------------------------------------------------------------------
    VFX.after(220, function () {
      if (casterEl) {
        VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 34, duration: 220 });
      }

      // 剑尖起爆瞬光环与小星芒
      VFX.shockwave({
        x: tipPt.x,
        y: tipPt.y,
        fromRadius: 8,
        toRadius: 58,
        color: '#fef08a',
        coreColor: '#ffffff',
        lineWidth: 3.0,
        duration: 240,
        double: false,
      });
      VFX.burst({
        x: tipPt.x,
        y: tipPt.y,
        count: 2,
        shape: 'flare',
        colors: ['#ffffff', '#fef08a'],
        size: [4.2 * s, 6.4 * s],
        speed: [8 * s, 28 * s],
        life: [160, 240],
      });

      // 发射破空螺旋流光矢（直抵目标并贯透一小段惯性，钳制在舞台内防越界）
      var piercePt = VFX.clampStage({
        x: t.x + axis.ux * 26 * s,
        y: t.y + axis.uy * 26 * s,
      }, 28 * s, 28 * s);
      emitSpiralLightArrow(tipPt, piercePt, {
        steps: 13,
        travelMs: 95,
        scaleMul: 1.05,
        spiralAmp: 16,
        axisDist: axis.dist,
      });

      // -----------------------------------------------------------------------
      // 阶段三（220 + 90 = 310ms）：命中触发极暗紫金星核坍缩内吸 → 410ms 破星炸裂
      // -----------------------------------------------------------------------
      var primaryDmg = ctx.perTargetDamage != null
        ? Math.round(ctx.perTargetDamage)
        : (typeof ctx.damage === 'number' ? Math.round(ctx.damage) : null);

      VFX.after(90, function () {
        VFX.hitFlash(targetEl, '#c084fc', 160);
        emitSingularityImplosion(t, 115);

        VFX.after(110, function () {
          // 轰然破星爆发
          VFX.quake('lg', 320);
          VFX.impact(targetEl, { color: '#ffffff', distance: 20, duration: 280 });
          emitStarBurst(t.x, t.y, 108, 440, 1.08);

          if (primaryDmg !== null) {
            VFX.floatText(targetEl, '-' + primaryDmg, ctx.crit ? 'crit' : 'damage', {
              scale: ctx.crit ? 1.38 : 1.22,
            });
          }

          VFX.shockwave({
            x: t.x,
            y: t.y,
            fromRadius: 10,
            toRadius: 146,
            color: '#f5c451',
            coreColor: '#ffffff',
            lineWidth: 4.8,
            duration: 420,
            double: true,
          });
          VFX.shockwave({
            delay: 42,
            x: t.x,
            y: t.y,
            fromRadius: 14,
            toRadius: 112,
            color: '#a855f7',
            coreColor: '#fef08a',
            lineWidth: 3.0,
            duration: 360,
            double: false,
          });

          // 狂涌紫金星云气浪（wisp）
          VFX.burst({
            x: t.x,
            y: t.y,
            radius: 14 * s,
            count: 24,
            shape: 'wisp',
            colors: ['#f5c451', '#a855f7', '#fef08a', '#4c1d95'],
            size: [5.0 * s, 10.2 * s],
            speed: [110 * s, 260 * s],
            turbulence: 68 * s,
            drag: 0.91,
            alpha: 0.84,
            life: [340, 600],
          });

          // 高速放射星雨 spark + 大量破碎 shard 裂片
          VFX.burst({
            x: t.x,
            y: t.y,
            radius: 12 * s,
            count: 34,
            shape: 'spark',
            colors: ['#fef08a', '#f5c451', '#c084fc', '#ffffff'],
            size: [2.4 * s, 5.2 * s],
            baseAngle: axis.angle,
            spread: Math.PI * 1.65,
            speed: [160 * s, 380 * s],
            drag: 0.92,
            life: [280, 560],
            trail: 5,
          });
          VFX.burst({
            x: t.x,
            y: t.y,
            radius: 10 * s,
            count: 28,
            shape: 'shard',
            colors: ['#f5c451', '#fef08a', '#c084fc', '#ffffff'],
            size: [3.0 * s, 6.2 * s],
            speed: [140 * s, 340 * s],
            gravity: 115 * s,
            spin: [-11, 11],
            life: [380, 720],
            trail: 3,
          });

          // -------------------------------------------------------------------
          // 阶段四（+220ms ≈ 640ms）：击杀连环折射贯星（chainOnKillCount: 2）
          // -------------------------------------------------------------------
          var chainFoes = collectChainFoes(casterEl, targetEl, targets, ctx);
          var shouldChain = !!ctx.kill || chainFoes.length > 0;
          if (shouldChain) {
            VFX.after(220, function () {
              VFX.shockwave({
                x: t.x,
                y: t.y,
                fromRadius: 6,
                toRadius: 64,
                color: '#fef08a',
                coreColor: '#ffffff',
                lineWidth: 2.8,
                duration: 240,
                double: false,
              });

              if (chainFoes.length > 0) {
                var count = Math.min(2, chainFoes.length);
                for (var cIdx = 0; cIdx < count; cIdx++) {
                  (function (otherEl, seq) {
                    VFX.after(seq * 85, function () {
                      var ot = VFX.clampStage(VFX.center(otherEl), 36 * s, 30 * s);
                      var chainVec = VFX.dir(t, ot);
                      emitSpiralLightArrow(t, ot, {
                        steps: 10,
                        travelMs: 85,
                        scaleMul: 0.85,
                        spiralAmp: 12,
                        axisDist: chainVec.dist,
                      });

                      VFX.after(85, function () {
                        emitStarBurst(ot.x, ot.y, 76, 320, 0.78);
                        VFX.shockwave({
                          x: ot.x,
                          y: ot.y,
                          fromRadius: 10,
                          toRadius: 90,
                          color: '#f5c451',
                          coreColor: '#ffffff',
                          lineWidth: 3.4,
                          duration: 320,
                          double: true,
                        });
                        VFX.burst({
                          x: ot.x,
                          y: ot.y,
                          count: 18,
                          shape: 'shard',
                          colors: ['#f5c451', '#c084fc', '#fef08a', '#ffffff'],
                          size: [2.4 * s, 4.8 * s],
                          speed: [115 * s, 270 * s],
                          life: [280, 540],
                          trail: 4,
                        });
                        VFX.burst({
                          x: ot.x,
                          y: ot.y,
                          count: 14,
                          shape: 'wisp',
                          colors: ['#f5c451', '#c084fc', '#fef08a'],
                          size: [4.0 * s, 7.6 * s],
                          speed: [85 * s, 195 * s],
                          drag: 0.91,
                          life: [260, 480],
                        });
                        VFX.impact(otherEl, { color: '#fef08a', distance: 14, duration: 220 });
                        if (primaryDmg !== null) {
                          var chainDmg = Math.max(1, Math.round(primaryDmg * 0.72));
                          VFX.floatText(otherEl, '-' + chainDmg, ctx.crit ? 'crit' : 'damage', {
                            scale: ctx.crit ? 1.18 : 1.02,
                          });
                        }
                        VFX.quake('sm', 180);
                      });
                    });
                  })(chainFoes[cIdx], cIdx);
                }
              } else {
                // 若敌阵仅余该倒地目标且触发击杀，向敌阵后方折射两道螺旋流光矢
                var spreadAngles = [axis.angle - 0.48, axis.angle + 0.48];
                for (var pIdx = 0; pIdx < spreadAngles.length; pIdx++) {
                  (function (ang, idx) {
                    VFX.after(idx * 65, function () {
                      var endSpread = VFX.clampStage({
                        x: t.x + Math.cos(ang) * 145 * s,
                        y: t.y + Math.sin(ang) * 145 * s,
                      }, 24 * s, 24 * s);
                      emitSpiralLightArrow(t, endSpread, {
                        steps: 8,
                        travelMs: 85,
                        scaleMul: 0.75,
                        spiralAmp: 10,
                        axisDist: 145 * s,
                      });
                    });
                  })(spreadAngles[pIdx], pIdx);
                }
              }
            });
          }
        });
      });
    });
  });
});
