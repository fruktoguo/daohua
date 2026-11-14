/**
 * ability.heavy-clouds · 重云（水系二阶全敌群体剑诀 · 深度流体粒子化重构版）
 * 演出阶段：
 *   0ms       二阶暗幕 + 敌阵玄水剑阵 + 敌阵上空数十团柔云烟尘（smoke + wisp + plume 软图叠画）压境翻涌
 *   50..270ms 乌云腹部隐现琥珀沉雷电弧（lightning）与云闪辉光，倾泻高密度 spark 剑雨尘丝与流光水剑（streamer）
 *   310ms     第一道湛蓝巨大月牙剑气劈开云海 + 涛浪扫阵（wave）
 *   440ms     第二道冰白逆扫月牙巨刃终结 + 寒雾爆散 + 破云星芒 flare + 中等震屏 + 全敌受击
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
   * 获取高羽化多瓣柔云软图（边缘 alpha 严格归零，用于叠画压境云海）
   */
  function getCloudPlume(color) {
    if (typeof VFX.getPlumeSprite === 'function') return VFX.getPlumeSprite(color);
    if (localPlumeCache[color]) return localPlumeCache[color];
    if (typeof document === 'undefined') return null;

    var c = document.createElement('canvas');
    c.width = 128;
    c.height = 128;
    var gctx = c.getContext('2d');
    var rgb = colorToRgb(color);
    var lobes = [
      { x: 64, y: 64, r: 58, peak: 0.62 },
      { x: 48, y: 56, r: 42, peak: 0.34 },
      { x: 80, y: 58, r: 40, peak: 0.32 },
      { x: 62, y: 76, r: 38, peak: 0.28 },
    ];
    for (var i = 0; i < lobes.length; i++) {
      var lb = lobes[i];
      var grad = gctx.createRadialGradient(lb.x, lb.y, 2, lb.x, lb.y, lb.r);
      grad.addColorStop(0, 'rgba(' + rgb + ',' + lb.peak + ')');
      grad.addColorStop(0.42, 'rgba(' + rgb + ',' + (lb.peak * 0.45).toFixed(3) + ')');
      grad.addColorStop(0.76, 'rgba(' + rgb + ',' + (lb.peak * 0.1).toFixed(3) + ')');
      grad.addColorStop(1, 'rgba(' + rgb + ',0)');
      gctx.fillStyle = grad;
      gctx.beginPath();
      gctx.arc(lb.x, lb.y, lb.r, 0, TWO_PI);
      gctx.fill();
    }
    localPlumeCache[color] = c;
    return c;
  }

  function mirrorAngle(a, facing) {
    return facing >= 0 ? a : Math.PI - a;
  }

  VFX.register('ability.heavy-clouds', function (ctx) {
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length)
      ? ctx.targetEls
      : (ctx.targetEl ? [ctx.targetEl] : (ctx.enemyEls && ctx.enemyEls.length ? ctx.enemyEls : []));

    var isEnemyCaster = casterEl && casterEl.classList && casterEl.classList.contains('is-enemy');
    var targetSide = isEnemyCaster ? 'ally' : 'enemy';
    var targetGroup = targets.length ? targets : targetSide;
    var bounds = VFX.groupBounds(targetGroup);
    var axis = VFX.combatAxis(casterEl, targetGroup);
    var s = VFX.scale();
    var st = VFX.size();
    var groundAnchor = VFX.clampStage({ x: bounds.footX, y: bounds.footY + 4 * s }, 52 * s, 32 * s);
    var formCenter = VFX.clampStage({ x: bounds.cx, y: bounds.cy }, 46 * s, 36 * s);

    // 阶段一（0ms）：二阶暗幕 + 敌阵脚底玄水柔光阵
    VFX.cinematicDim(0.36, 760, '#040a16');
    if (casterEl) {
      VFX.motion(casterEl, 'cast', { duration: 420 });
    }

    // 阵底柔光冷水雾铺垫
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 960,
      draw: function (g, p) {
        var fade = p < 0.16 ? p / 0.16 : p > 0.72 ? (1 - p) / 0.28 : 1;
        var r = 152 * s * (p < 0.2 ? VFX.lerp(0.55, 1.0, p / 0.2) : 1);
        g.save();
        g.translate(groundAnchor.x, groundAnchor.y);
        g.scale(1, 0.42);
        var grad = g.createRadialGradient(0, 0, r * 0.08, 0, 0, r);
        grad.addColorStop(0, 'rgba(186,230,253,' + (0.24 * fade).toFixed(3) + ')');
        grad.addColorStop(0.5, 'rgba(56,189,248,' + (0.18 * fade).toFixed(3) + ')');
        grad.addColorStop(0.84, 'rgba(2,132,199,' + (0.07 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(2,132,199,0)');
        g.fillStyle = grad;
        g.beginPath();
        g.arc(0, 0, r, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });

    VFX.magicCircle({
      x: groundAnchor.x,
      y: groundAnchor.y,
      radius: 132,
      tilt: 0.42,
      layer: 'ground',
      color: '#38bdf8',
      secondaryColor: '#e0f2fe',
      style: 'water',
      rotationSpeed: 1.1,
      duration: 940,
      alpha: 0.74,
      lineWidth: 2.0,
    });

    VFX.magicCircle({
      x: groundAnchor.x,
      y: groundAnchor.y,
      radius: 86,
      tilt: 0.42,
      layer: 'ground',
      color: '#0284c7',
      secondaryColor: '#ffffff',
      style: 'sword',
      rotationSpeed: -1.5,
      duration: 900,
      alpha: 0.68,
      lineWidth: 1.6,
    });

    // 自适应横/竖/斜屏的敌阵乌云天幕中心与横向跨度
    var cloudMidX = VFX.clamp(bounds.cx, 66 * s, st.w - 66 * s);
    var cloudTopY = bounds.skyY;
    var cloudSpan = Math.min(st.w * 0.48, Math.max(125 * s, bounds.spanX + 115 * s));
    var cloudMinX = cloudMidX - cloudSpan * 0.5;
    var cloudMaxX = cloudMidX + cloudSpan * 0.5;

    // -------------------------------------------------------------------------
    // 重云压境翻涌：plume 软图叠画动态云海 + 数十团 smoke & wisp 云气粒子翻滚
    // -------------------------------------------------------------------------
    var darkCloudPlume = getCloudPlume('#0f172a');
    var slateCloudPlume = getCloudPlume('#334155');
    var edgeCloudPlume = getCloudPlume('#38bdf8');
    var cloudNodes = [];
    for (var cn = 0; cn < 18; cn++) {
      var u = cn / 17;
      cloudNodes.push({
        ox: (u - 0.5) * cloudSpan * 1.04 + VFX.rand(-10, 10) * s,
        oy: (Math.sin(u * Math.PI * 2.4) * 14 + VFX.rand(-12, 10)) * s,
        rx: VFX.rand(42, 68) * s,
        ry: VFX.rand(24, 42) * s,
        phase: VFX.rand(0, TWO_PI),
        layer: cn % 3,
      });
    }

    VFX.custom({
      layer: 'sky',
      blend: 'source-over',
      duration: 860,
      draw: function (g, p) {
        var fadeIn = p < 0.18 ? p / 0.18 : 1;
        var fadeOut = p > 0.68 ? Math.pow(1 - (p - 0.68) / 0.32, 1.35) : 1;
        var alpha = fadeIn * fadeOut;
        if (alpha <= 0.01) return;

        g.save();
        g.translate(cloudMidX, cloudTopY);

        for (var idx = 0; idx < cloudNodes.length; idx++) {
          var nd = cloudNodes[idx];
          var driftX = nd.ox + Math.sin(p * 6 + nd.phase) * 14 * s + p * axis.facingX * 16 * s;
          var driftY = nd.oy + Math.cos(p * 5 + nd.phase) * 6 * s;
          var rx = nd.rx * (0.9 + 0.14 * Math.sin(p * 7 + nd.phase));
          var ry = nd.ry * (0.9 + 0.12 * Math.cos(p * 6 + nd.phase));

          var sp = nd.layer === 0 ? darkCloudPlume : nd.layer === 1 ? slateCloudPlume : edgeCloudPlume;
          if (sp) {
            g.globalCompositeOperation = nd.layer === 2 ? 'lighter' : 'source-over';
            g.globalAlpha = alpha * (nd.layer === 2 ? 0.24 : 0.62);
            g.drawImage(sp, driftX - rx, driftY - ry, rx * 2, ry * 2);
          } else {
            var cg = g.createRadialGradient(driftX, driftY, 2, driftX, driftY, rx);
            cg.addColorStop(0, 'rgba(30,41,59,' + (0.55 * alpha).toFixed(3) + ')');
            cg.addColorStop(1, 'rgba(30,41,59,0)');
            g.fillStyle = cg;
            g.beginPath();
            g.arc(driftX, driftY, rx, 0, TWO_PI);
            g.fill();
          }
        }

        // 云腹间歇闪动的琥珀沉雷闷光晕（边缘 alpha=0 径向渐变）
        var thunderPulse = Math.max(0, Math.sin(p * Math.PI * 6.5));
        if (thunderPulse > 0.2) {
          g.globalCompositeOperation = 'lighter';
          var tx = Math.sin(p * 19) * cloudSpan * 0.26;
          var tRad = 74 * s;
          var tg = g.createRadialGradient(tx, 6 * s, 2 * s, tx, 6 * s, tRad);
          tg.addColorStop(0, 'rgba(254,243,199,' + (0.48 * alpha * thunderPulse).toFixed(3) + ')');
          tg.addColorStop(0.4, 'rgba(245,158,11,' + (0.28 * alpha * thunderPulse).toFixed(3) + ')');
          tg.addColorStop(1, 'rgba(245,158,11,0)');
          g.fillStyle = tg;
          g.beginPath();
          g.arc(tx, 6 * s, tRad, 0, TWO_PI);
          g.fill();
        }
        g.restore();
      },
    });

    // 数十团柔云烟尘粒子翻涌压境（smoke + wisp）
    for (var cw = 0; cw < 3; cw++) {
      VFX.burst({
        delay: cw * 95,
        x: cloudMidX,
        y: cloudTopY + 4 * s,
        radius: cloudSpan * 0.44,
        tiltY: 0.24,
        count: 14,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#0f172a', '#1e293b', '#334155', '#475569'],
        size: [18 * s, 32 * s],
        baseAngle: axis.facingX > 0 ? 0.15 : Math.PI - 0.15,
        spread: Math.PI * 0.9,
        speed: [22 * s, 64 * s],
        turbulence: 48 * s,
        drag: 0.95,
        alpha: 0.64,
        life: [520, 860],
      });

      VFX.burst({
        delay: cw * 95 + 20,
        x: cloudMidX,
        y: cloudTopY + 8 * s,
        radius: cloudSpan * 0.42,
        tiltY: 0.22,
        count: 14,
        shape: 'wisp',
        colors: ['#475569', '#38bdf8', '#94a3b8', '#bae6fd'],
        size: [6.0 * s, 11.5 * s],
        baseAngle: axis.facingX > 0 ? 0.1 : Math.PI - 0.1,
        spread: Math.PI * 0.85,
        speed: [35 * s, 92 * s],
        turbulence: 68 * s,
        drag: 0.94,
        alpha: 0.56,
        life: [460, 780],
      });
    }

    // -------------------------------------------------------------------------
    // 阶段二（40..270ms）：云内琥珀沉雷电弧跳闪 + 追踪真实存活目标的剑雨倾泻
    // -------------------------------------------------------------------------
    var thunderDelays = [55, 145, 230];
    for (var td = 0; td < thunderDelays.length; td++) {
      (function (idx, at) {
        VFX.after(at, function () {
          var xA = VFX.lerp(cloudMinX + 18 * s, cloudMaxX - 18 * s, (idx + 0.3) / 3.2);
          var xB = VFX.clamp(
            xA + VFX.rand(38, 86) * s * (idx % 2 === 0 ? 1 : -1),
            cloudMinX + 8 * s,
            cloudMaxX - 8 * s
          );
          var yA = cloudTopY + VFX.rand(-12, 8) * s;
          var yB = cloudTopY + VFX.rand(6, 26) * s;

          // 琥珀沉雷在乌云内部窜动
          VFX.lightning({
            x1: xA,
            y1: yA,
            x2: xB,
            y2: yB,
            color: '#f59e0b',
            secondaryColor: '#fde68a',
            glowColor: '#b45309',
            haloScale: 1.35,
            coreColor: '#fef3c7',
            width: 3.4 * s,
            segments: 12,
            jagged: 0.28,
            branches: 2,
            flicker: 2,
            duration: 220,
          });

          VFX.burst({
            x: (xA + xB) * 0.5,
            y: (yA + yB) * 0.5,
            radius: 18 * s,
            count: 3,
            shape: 'flare',
            colors: ['#fbbf24', '#fef3c7', '#ffffff'],
            size: [3.2 * s, 5.4 * s],
            speed: [10 * s, 35 * s],
            life: [180, 300],
          });
        });
      })(td, thunderDelays[td]);
    }

    // 云腹倾泻剑雨：每一波追踪真实存活目标，确保竖屏纵列三敌同样精准淋剑雨
    var rainCount = Math.max(7, targets.length * 2 + 1);
    for (var r = 0; r < rainCount; r++) {
      (function (idx) {
        var at = 40 + idx * 32;
        var baseTargetPt;
        if (targets.length > 0) {
          baseTargetPt = VFX.center(targets[idx % targets.length]);
        } else {
          var frac = (idx + 0.5) / rainCount;
          baseTargetPt = {
            x: VFX.lerp(bounds.minX, bounds.maxX, frac),
            y: VFX.lerp(bounds.minY, bounds.maxY, (idx % 3) * 0.5),
          };
        }
        var rainPt = VFX.clampStage({
          x: baseTargetPt.x + VFX.rand(-16, 16) * s,
          y: baseTargetPt.y + VFX.rand(-8, 18) * s,
        }, 22 * s, 26 * s);

        var startX = VFX.clamp(
          rainPt.x - axis.ux * VFX.rand(24, 48) * s + VFX.rand(-18, 18) * s,
          cloudMinX + 8 * s,
          cloudMaxX - 8 * s
        );
        var startY = cloudTopY + VFX.rand(4, 24) * s;

        // 柔性流光剑雨丝（弧曲动态曳光）
        VFX.streamer({
          delay: at,
          x1: startX,
          y1: startY,
          x2: rainPt.x,
          y2: rainPt.y,
          arc: axis.facingX * VFX.rand(-14, 14) * s,
          wobble: 3 * s,
          width: VFX.rand(7, 10) * s,
          tailLength: 0.52,
          color: idx % 2 === 0 ? '#4fc3ff' : '#bae6fd',
          coreColor: '#ffffff',
          sparkColors: ['#bae6fd', '#fbbf24', '#ffffff'],
          duration: 185,
        });

        // 剑雨伴飞 wisp 寒风与密集 spark 尘雨
        VFX.burst({
          delay: at + 15,
          x: startX,
          y: startY,
          radius: 14 * s,
          count: 8,
          shape: 'spark',
          colors: ['#4fc3ff', '#bae6fd', '#fef3c7', '#ffffff'],
          size: [1.6 * s, 3.2 * s],
          baseAngle: Math.atan2(rainPt.y - startY, rainPt.x - startX),
          spread: 0.34,
          speed: [320 * s, 520 * s],
          drag: 0.97,
          life: [200, 340],
          trail: 4,
        });

        VFX.after(at + 105, function () {
          VFX.shockwave({
            x: rainPt.x,
            y: rainPt.y + 14 * s,
            tilt: 0.44,
            fromRadius: 6,
            toRadius: 44,
            color: '#4fc3ff',
            coreColor: '#e0f2fe',
            lineWidth: 2.0,
            duration: 240,
            double: false,
          });
          VFX.burst({
            x: rainPt.x,
            y: rainPt.y + 10 * s,
            count: 7,
            shape: 'drop',
            colors: ['#4fc3ff', '#bae6fd', '#ffffff'],
            size: [2.2 * s, 4.0 * s],
            angle: [-Math.PI * 0.88, -Math.PI * 0.12],
            speed: [75 * s, 165 * s],
            gravity: 220 * s,
            life: [240, 420],
          });
          VFX.burst({
            x: rainPt.x,
            y: rainPt.y + 8 * s,
            count: 5,
            shape: 'wisp',
            colors: ['#bae6fd', '#38bdf8'],
            size: [3.4 * s, 6.2 * s],
            angle: [-Math.PI * 0.85, -Math.PI * 0.15],
            speed: [45 * s, 115 * s],
            gravity: -28 * s,
            alpha: 0.65,
            life: [260, 440],
          });
        });
      })(r);
    }

    // 计算沿 axis 2D 推进的气浪跨度、起止点及破云巨弧参数
    var perpSpan = bounds.majorSpan;
    var alongHalf = 135 * s;
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
      perpSpan = targets.length > 1 ? maxPerp - minPerp : 80 * s;
      alongHalf = Math.max(95 * s, (maxAlong - minAlong) * 0.5 + 88 * s);
    }
    var waveWidth = VFX.clamp(
      (axis.dist > 1 ? perpSpan : bounds.majorSpan) + 95 * s,
      110 * s,
      Math.min(st.w, st.h) * 0.85
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
      Math.max(122, (bounds.majorSpan * 0.5 + 64 * s) / Math.max(0.58, s)),
      125,
      192
    );
    var arcPivot = VFX.clampStage({
      x: bounds.cx - axis.ux * 72 * s,
      y: bounds.cy - axis.uy * 72 * s,
    }, 40 * s, 30 * s);

    // -------------------------------------------------------------------------
    // 阶段三（300ms）：第一道湛蓝巨大月牙剑气沿 axis 横贯敌阵 + 流体巨浪扫阵
    // -------------------------------------------------------------------------
    VFX.after(300, function () {
      VFX.slash({
        x: arcPivot.x,
        y: arcPivot.y,
        radius: arcRadius,
        tilt: 0.48,
        rotation: axis.angle,
        thickness: 36,
        startAngle: -1.18,
        endAngle: 1.18,
        color: '#0ea5e9',
        glowColor: '#38bdf8',
        coreColor: '#e0f2fe',
        sparks: false,
        duration: 340,
      });

      VFX.wave({
        x1: waveStart.x,
        y1: waveStart.y,
        x2: waveEnd.x,
        y2: waveEnd.y,
        width: waveWidth,
        thickness: 68 * s,
        rawScale: true,
        color: '#38bdf8',
        secondaryColor: '#0284c7',
        foamColor: '#e0f2fe',
        crests: 2,
        duration: 420,
      });

      for (var k = 0; k < targets.length; k++) {
        (function (tEl, idx) {
          VFX.after(50 + idx * 25, function () {
            VFX.hitFlash(tEl, '#7dd3fc', 160);
          });
        })(targets[k], k);
      }
    });

    // -------------------------------------------------------------------------
    // 阶段四（430ms）：第二道冰白逆扫月牙巨斩 + 破云寒雾与水花 + 震屏 + 全敌重受击
    // -------------------------------------------------------------------------
    VFX.after(430, function () {
      VFX.slash({
        x: arcPivot.x,
        y: arcPivot.y - 8 * s,
        radius: arcRadius + 8,
        tilt: 0.52,
        rotation: axis.angle,
        thickness: 40,
        startAngle: 1.24,
        endAngle: -1.24,
        color: '#bae6fd',
        glowColor: '#0ea5e9',
        coreColor: '#ffffff',
        sparks: false,
        duration: 340,
      });

      // 寒雾翻涌与剑气余风（wisp + smoke）
      var burstRad = Math.max(70 * s, bounds.majorSpan * 0.45);
      VFX.burst({
        x: formCenter.x,
        y: formCenter.y + 8 * s,
        radius: burstRad,
        tiltY: 0.48,
        count: 18,
        shape: 'wisp',
        colors: ['#7dd3fc', '#bae6fd', '#38bdf8', '#e0f2fe'],
        size: [6.0 * s, 12.0 * s],
        speed: [60 * s, 155 * s],
        baseAngle: axis.angle,
        spread: Math.PI * 1.4,
        gravity: -28 * s,
        turbulence: 65 * s,
        alpha: 0.62,
        life: [460, 820],
      });

      VFX.burst({
        x: formCenter.x,
        y: formCenter.y + 8 * s,
        radius: burstRad,
        tiltY: 0.48,
        count: 14,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#475569', '#64748b', '#94a3b8'],
        size: [16 * s, 28 * s],
        speed: [32 * s, 82 * s],
        gravity: -22 * s,
        alpha: 0.34,
        life: [520, 900],
      });

      VFX.burst({
        x: formCenter.x,
        y: formCenter.y,
        radius: Math.max(65 * s, bounds.majorSpan * 0.4),
        tiltY: 0.5,
        count: 28,
        shape: 'drop',
        colors: ['#4fc3ff', '#e0f2fe', '#ffffff'],
        size: [2.4 * s, 4.6 * s],
        speed: [90 * s, 220 * s],
        gravity: 210 * s,
        life: [320, 560],
      });

      VFX.quake('md', 260);

      var dmgEach = ctx.perTargetDamage != null
        ? Math.round(ctx.perTargetDamage)
        : (typeof ctx.damage === 'number' ? Math.round(ctx.damage / Math.max(1, targets.length)) : null);

      for (var m = 0; m < targets.length; m++) {
        (function (tEl, idx) {
          VFX.after(45 + idx * 30, function () {
            var tc = VFX.clampStage(VFX.center(tEl), 36 * s, 28 * s);
            VFX.impact(tEl, { color: '#e0f2fe', distance: 15, duration: 240 });
            if (dmgEach !== null) {
              VFX.floatText(tEl, '-' + dmgEach, ctx.crit ? 'crit' : 'damage', {
                scale: ctx.crit ? 1.28 : 1.08,
              });
            }
            VFX.shockwave({
              x: tc.x,
              y: tc.y,
              fromRadius: 10,
              toRadius: 74,
              color: '#38bdf8',
              coreColor: '#ffffff',
              lineWidth: 2.8,
              duration: 300,
              double: false,
            });
            VFX.burst({
              x: tc.x,
              y: tc.y,
              radius: 10 * s,
              count: 3,
              shape: 'flare',
              colors: ['#ffffff', '#bae6fd', '#38bdf8'],
              size: [3.8 * s, 6.2 * s],
              speed: [18 * s, 60 * s],
              life: [210, 350],
            });
            VFX.burst({
              x: tc.x,
              y: tc.y,
              count: 14,
              shape: 'shard',
              colors: ['#bae6fd', '#38bdf8', '#ffffff'],
              size: [2.4 * s, 4.6 * s],
              speed: [95 * s, 220 * s],
              gravity: 130 * s,
              life: [280, 500],
              trail: 3,
            });
          });
        })(targets[m], m);
      }
    });
  });
});
