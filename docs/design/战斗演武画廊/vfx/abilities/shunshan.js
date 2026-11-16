/**
 * ability.shunshan · 瞬闪 —— 二阶金系剑修瞬身突刺 + 移形收招（深度流体粒子化重构版）
 * 真源镜像：config/game-data/abilities/physical_abilities.json
 *   单体敌 / 吟唱 500ms / 2.4×AD+100 / 赋予或消耗「移形」，击杀重置冷却 / 冷却 24s / 灵力 20 / 剑修·金
 * 演出阶段：
 *   0ms    暗幕压场 + 施法者疾掠突进（lunge）+ 足底青金移形剑阵 + 原地柔羽残影消散云团 + 剑尖白金寒星极速内吸
 *   90ms   贯透靶心的破空 slashLine + 对称双股青碧/鎏金幻影 streamer 夹击 + 沿剑路喷涌高速顺向 wisp 与 spark 剑风
 *   185ms  靶心极短暂金青星点内吸 → 正逆交叉斜切 slash + 渐变梭形十字破绽星芒 + 剑晶寒星迸射 + 双层冲击波 + 震屏伤字
 *   360ms  一缕青碧移形流光回掠施法者脚下，化作移形波纹与螺旋上升青羽灵风收招
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
   * 获取双向高斯柔雾羽流精灵（用于残影消散云团，边缘 alpha 严格归零）
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
      { x: 64, y: 64, r: 58, peak: 0.7 },
      { x: 50, y: 56, r: 40, peak: 0.3 },
      { x: 78, y: 70, r: 38, peak: 0.28 },
    ];
    for (var i = 0; i < lobes.length; i++) {
      var lb = lobes[i];
      var grad = gctx.createRadialGradient(lb.x, lb.y, 1, lb.x, lb.y, lb.r);
      grad.addColorStop(0, 'rgba(' + rgb + ',' + lb.peak + ')');
      grad.addColorStop(0.38, 'rgba(' + rgb + ',' + (lb.peak * 0.44).toFixed(3) + ')');
      grad.addColorStop(0.72, 'rgba(' + rgb + ',' + (lb.peak * 0.12).toFixed(3) + ')');
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
   * 按 2D 攻击轴方向偏转局部挥斩角（兼容横竖屏与敌我方向）
   */
  function orientAngle(localAngle, axis) {
    return axis.facingX >= 0 ? axis.angle + localAngle : axis.angle - localAngle;
  }

  /**
   * 绘制首尾渐变归零的梭形光芒臂（零硬尖几何块）
   */
  function drawTaperedCrossRay(c2d, len, halfW, rgb, alpha) {
    if (alpha <= 0.01 || len <= 1) return;
    var grad = c2d.createLinearGradient(-len, 0, len, 0);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.26, 'rgba(' + rgb + ',' + (alpha * 0.45).toFixed(3) + ')');
    grad.addColorStop(0.5, 'rgba(255,255,255,' + (alpha * 0.96).toFixed(3) + ')');
    grad.addColorStop(0.74, 'rgba(' + rgb + ',' + (alpha * 0.45).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(-len, 0);
    c2d.quadraticCurveTo(0, -halfW, len, 0);
    c2d.quadraticCurveTo(0, halfW, -len, 0);
    c2d.fill();
  }

  /**
   * 在原点绘制带旋转的渐隐十字破绽星芒爆裂（梭形双层叠加 + 柔光中心）
   */
  function emitCrossStarBurst(pt, baseAngle, maxLen, s) {
    var glowGold = getSoftGlow('#facc15');
    var glowCyan = getSoftGlow('#2dd4bf');

    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 360,
      draw: function (c2d, p) {
        var open = p < 0.16 ? 1 - Math.pow(1 - p / 0.16, 3) : 1;
        var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.45);
        if (fade <= 0.01) return;

        var armLen = maxLen * s * (0.35 + 0.65 * open) * (1 + p * 0.14);
        var radGlow = maxLen * 0.68 * s * (0.65 + 0.35 * open);
        var wOuter = (9.5 - p * 4.5) * s;
        var wInner = (4.2 - p * 2.0) * s;

        c2d.save();
        c2d.translate(pt.x, pt.y);

        // 中心青金复合柔光底晕（边缘严格归零）
        var bgGrad = c2d.createRadialGradient(0, 0, 1, 0, 0, radGlow * 1.15);
        bgGrad.addColorStop(0, 'rgba(255,255,255,' + (0.86 * fade).toFixed(3) + ')');
        bgGrad.addColorStop(0.28, 'rgba(254,240,138,' + (0.56 * fade).toFixed(3) + ')');
        bgGrad.addColorStop(0.62, 'rgba(45,212,191,' + (0.28 * fade).toFixed(3) + ')');
        bgGrad.addColorStop(1, 'rgba(45,212,191,0)');
        c2d.fillStyle = bgGrad;
        c2d.beginPath();
        c2d.arc(0, 0, radGlow * 1.15, 0, TWO_PI);
        c2d.fill();

        if (glowCyan) {
          c2d.globalAlpha = fade * 0.48;
          c2d.drawImage(glowCyan, -radGlow, -radGlow, radGlow * 2, radGlow * 2);
        }
        if (glowGold) {
          c2d.globalAlpha = fade * 0.78;
          c2d.drawImage(glowGold, -radGlow * 0.66, -radGlow * 0.66, radGlow * 1.32, radGlow * 1.32);
        }

        // 交叉十字破绽梭形光刃（主交叉与次交叉微微转角）
        var crossAngles = [baseAngle + 0.64, baseAngle - 0.64];
        for (var i = 0; i < crossAngles.length; i++) {
          c2d.save();
          c2d.rotate(crossAngles[i]);
          drawTaperedCrossRay(c2d, armLen, Math.max(1.2, wOuter), i === 0 ? '250,204,21' : '45,212,191', fade * 0.72);
          drawTaperedCrossRay(c2d, armLen * 0.88, Math.max(0.8, wInner), '255,255,255', fade * 0.94);
          c2d.restore();
        }

        c2d.restore();
      },
    });
  }

  VFX.register('ability.shunshan', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length)
      ? ctx.targetEls
      : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.enemyEls && ctx.enemyEls.length) {
      targets = [ctx.enemyEls[0]];
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && (
      casterEl.classList.contains('is-enemy') ||
      casterEl.classList.contains('arena-actor--enemy')
    ));
    var selfSide = isEnemyCaster ? 'enemy' : 'ally';
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    var targetEl = targets[0] || null;

    var s = VFX.scale();
    var axis = VFX.combatAxis(casterEl, targetEl || foeSide);
    var cRaw = casterEl ? VFX.center(casterEl) : VFX.sideCenter(selfSide);
    var cFootRaw = casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(selfSide);
    var tRaw = targetEl ? VFX.center(targetEl) : VFX.sideCenter(foeSide);
    var tFootRaw = targetEl ? VFX.foot(targetEl) : VFX.sideFootCenter(foeSide);

    var cCaster = VFX.clampStage(cRaw, 34 * s, 28 * s);
    var cFoot = VFX.clampStage(cFootRaw, 38 * s, 22 * s);
    var tSafe = VFX.clampStage(tRaw, 42 * s, 34 * s);
    var tFoot = VFX.clampStage(tFootRaw, 38 * s, 22 * s);
    var tipPt = VFX.clampStage(axis.palm(26 * s), 26 * s, 24 * s);

    var dmg = ctx.perTargetDamage != null
      ? Math.round(ctx.perTargetDamage)
      : (typeof ctx.damage === 'number' ? Math.round(ctx.damage) : 260);
    var isCrit = !!ctx.crit;
    var distFactor = Math.min(1.15, Math.max(0.45, axis.dist / 240));

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：暗幕压场 + 青金移形剑阵 + 原地柔羽残影消散云团 + 剑尖寒星极速内吸
    // -------------------------------------------------------------------------
    VFX.cinematicDim(0.34, 620, '#070b12');
    if (casterEl) {
      VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 36, duration: 200 });
    }

    // 施法者足底青金移形柔光晕与双层剑阵
    VFX.custom({
      layer: 'ground',
      blend: 'lighter',
      duration: 680,
      draw: function (c2d, p) {
        var fade = p < 0.16 ? p / 0.16 : Math.pow(1 - (p - 0.16) / 0.84, 1.35);
        var rad = 84 * s * (p < 0.2 ? VFX.lerp(0.45, 1.0, p / 0.2) : 1);
        c2d.save();
        c2d.translate(cFoot.x, cFoot.y);
        c2d.scale(1, 0.44);
        var g = c2d.createRadialGradient(0, 0, 2 * s, 0, 0, rad);
        g.addColorStop(0, 'rgba(254,240,138,' + (0.34 * fade).toFixed(3) + ')');
        g.addColorStop(0.42, 'rgba(45,212,191,' + (0.26 * fade).toFixed(3) + ')');
        g.addColorStop(0.78, 'rgba(250,204,21,' + (0.1 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(45,212,191,0)');
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
      radius: 68,
      tilt: 0.44,
      layer: 'ground',
      style: 'illusion',
      color: '#2dd4bf',
      secondaryColor: '#fef08a',
      rotationSpeed: 2.8,
      duration: 660,
      alpha: 0.78,
      lineWidth: 1.8,
    });

    VFX.magicCircle({
      x: cFoot.x,
      y: cFoot.y,
      radius: 44,
      tilt: 0.44,
      layer: 'ground',
      style: 'sword',
      color: '#facc15',
      secondaryColor: '#ffffff',
      rotationSpeed: -3.4,
      duration: 620,
      alpha: 0.7,
      lineWidth: 1.4,
    });

    // 原地柔羽残影消散云团：plume 旋散虚影 + wisp 气旋飘散
    var afterCyanPlume = getPlume('#2dd4bf');
    var afterGoldPlume = getPlume('#fef08a');
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 340,
      draw: function (c2d, p) {
        var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.45);
        if (fade <= 0.01) return;

        c2d.save();
        c2d.translate(cCaster.x, cCaster.y);
        for (var i = 0; i < 6; i++) {
          var ang = p * 4.2 + (i * TWO_PI) / 6;
          var distR = (10 + p * 38) * s;
          var px = Math.cos(ang) * distR * 0.9 - axis.ux * p * 18 * s;
          var py = Math.sin(ang) * distR * 0.75 - axis.uy * p * 18 * s;
          var sz = (26 + p * 14) * s;
          var sp = i % 2 === 0 ? afterCyanPlume : afterGoldPlume;
          if (sp) {
            c2d.save();
            c2d.translate(px, py);
            c2d.rotate(ang + 1.1);
            c2d.globalAlpha = fade * 0.36;
            c2d.drawImage(sp, -sz * 1.25, -sz * 0.75, sz * 2.5, sz * 1.5);
            c2d.restore();
          } else {
            var cg = c2d.createRadialGradient(px, py, 1, px, py, sz);
            cg.addColorStop(0, 'rgba(94,234,212,' + (0.34 * fade).toFixed(3) + ')');
            cg.addColorStop(1, 'rgba(45,212,191,0)');
            c2d.fillStyle = cg;
            c2d.beginPath();
            c2d.arc(px, py, sz, 0, TWO_PI);
            c2d.fill();
          }
        }
        c2d.restore();
      },
    });

    VFX.burst({
      x: cCaster.x,
      y: cCaster.y,
      radius: 26 * s,
      ring: true,
      count: 14,
      shape: 'wisp',
      colors: ['#2dd4bf', '#5eead4', '#fef08a', '#e0f2fe'],
      size: [4.2 * s, 8.2 * s],
      speed: [28 * s, 72 * s],
      vortex: 245 * s,
      vortexCenter: cCaster,
      drag: 0.93,
      alpha: 0.72,
      life: [220, 400],
    });

    // 剑尖白金寒星极速内吸蓄锐
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 120,
      draw: function (c2d, p) {
        var fade = p < 0.45 ? p / 0.45 : Math.max(0.1, 1 - (p - 0.45) * 1.4);
        var rad = VFX.lerp(42, 16, p) * s;
        c2d.save();
        c2d.translate(tipPt.x, tipPt.y);
        var g = c2d.createRadialGradient(0, 0, 0, 0, 0, rad);
        g.addColorStop(0, 'rgba(255,255,255,' + (0.95 * fade).toFixed(3) + ')');
        g.addColorStop(0.28, 'rgba(254,240,138,' + (0.68 * fade).toFixed(3) + ')');
        g.addColorStop(0.65, 'rgba(45,212,191,' + (0.28 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(45,212,191,0)');
        c2d.fillStyle = g;
        c2d.beginPath();
        c2d.arc(0, 0, rad, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    VFX.burst({
      x: tipPt.x,
      y: tipPt.y,
      radius: 56 * s,
      ring: true,
      count: 12,
      shape: 'wisp',
      colors: ['#ffffff', '#fef08a', '#2dd4bf'],
      size: [2.8 * s, 5.2 * s],
      speed: [10 * s, 28 * s],
      vortex: 210 * s,
      vortexCenter: tipPt,
      attractTo: tipPt,
      attractStrength: 1220 * s,
      drag: 0.91,
      life: [85, 135],
      trail: 3,
    });

    VFX.burst({
      x: tipPt.x,
      y: tipPt.y,
      radius: 50 * s,
      ring: true,
      count: 12,
      shape: 'spark',
      colors: ['#ffffff', '#fef08a', '#5eead4'],
      size: [1.8 * s, 3.4 * s],
      speed: [10 * s, 24 * s],
      attractTo: tipPt,
      attractStrength: 1320 * s,
      drag: 0.92,
      life: [80, 125],
      trail: 4,
    });

    // -------------------------------------------------------------------------
    // 阶段二（90ms）：主剑路 slashLine 贯透靶心 + 双股幻影夹击 + 分段高速剑风
    // -------------------------------------------------------------------------
    VFX.after(90, function () {
      var lineStart = VFX.clampStage({
        x: cCaster.x + axis.ux * 14 * s,
        y: cCaster.y + axis.uy * 14 * s,
      }, 22 * s, 22 * s);
      var lineEnd = VFX.clampStage({
        x: tSafe.x + axis.ux * 38 * s,
        y: tSafe.y + axis.uy * 38 * s,
      }, 22 * s, 22 * s);

      // 剑尖起爆瞬环与白金寒星
      VFX.shockwave({
        x: lineStart.x,
        y: lineStart.y,
        fromRadius: 6,
        toRadius: 46,
        color: '#fef08a',
        coreColor: '#ffffff',
        lineWidth: 2.6,
        duration: 185,
        double: false,
      });
      VFX.burst({
        x: lineStart.x,
        y: lineStart.y,
        count: 3,
        shape: 'flare',
        colors: ['#ffffff', '#fef08a', '#2dd4bf'],
        size: [3.6 * s, 5.8 * s],
        speed: [12 * s, 34 * s],
        life: [140, 220],
      });

      // 主剑路贯透靶心与背后安全钳位点
      VFX.slashLine({
        x1: lineStart.x,
        y1: lineStart.y,
        x2: lineEnd.x,
        y2: lineEnd.y,
        width: 15 * s,
        pierce: 0.22,
        ringCount: 3,
        color: '#facc15',
        coreColor: '#ffffff',
        duration: 240,
      });

      // 两侧对称双股青碧 / 鎏金幻影流光夹击
      VFX.streamer({
        x1: lineStart.x,
        y1: lineStart.y,
        x2: tSafe.x,
        y2: tSafe.y,
        arc: 36 * distFactor * s,
        wobble: 5 * s,
        width: 9.5 * s,
        tailLength: 0.48,
        duration: 210,
        color: '#2dd4bf',
        coreColor: '#ffffff',
        sparkColors: ['#5eead4', '#fef08a', '#ffffff'],
      });
      VFX.streamer({
        delay: 12,
        x1: lineStart.x,
        y1: lineStart.y,
        x2: tSafe.x,
        y2: tSafe.y,
        arc: -36 * distFactor * s,
        wobble: 5 * s,
        width: 9.5 * s,
        tailLength: 0.48,
        duration: 210,
        color: '#facc15',
        coreColor: '#fef08a',
        sparkColors: ['#fef08a', '#2dd4bf', '#ffffff'],
      });

      // 沿剑路分段喷涌高速顺向 wisp 与 spark 剑风
      var pathSteps = 7;
      for (var ps = 0; ps <= pathSteps; ps++) {
        (function (idx) {
          var u = idx / pathSteps;
          var at = Math.round(u * 78);
          var px = VFX.lerp(lineStart.x, lineEnd.x, u);
          var py = VFX.lerp(lineStart.y, lineEnd.y, u);
          var wingSign = idx % 2 === 0 ? 1 : -1;
          var wingOff = wingSign * Math.sin(u * Math.PI) * 14 * s;

          VFX.burst({
            delay: at,
            x: px + axis.nx * wingOff,
            y: py + axis.ny * wingOff,
            radius: 6 * s,
            count: 3,
            shape: 'wisp',
            colors: ['#2dd4bf', '#facc15', '#fef08a', '#ffffff'],
            size: [3.2 * s, 6.4 * s],
            baseAngle: axis.angle,
            spread: 0.44,
            speed: [185 * s, 340 * s],
            turbulence: 48 * s,
            drag: 0.92,
            alpha: 0.8,
            life: [190, 350],
            trail: 3,
          });

          VFX.burst({
            delay: at + 5,
            x: px - axis.nx * wingOff * 0.7,
            y: py - axis.ny * wingOff * 0.7,
            radius: 4 * s,
            count: 4,
            shape: 'spark',
            colors: ['#ffffff', '#fef08a', '#5eead4', '#facc15'],
            size: [1.6 * s, 3.4 * s],
            baseAngle: axis.angle,
            spread: 0.36,
            speed: [240 * s, 460 * s],
            drag: 0.93,
            life: [180, 330],
            trail: 5,
          });
        })(ps);
      }

      // -----------------------------------------------------------------------
      // 阶段三（185ms）：靶心短暂金青星点内吸 → 交叉斜切 + 梭形十字星芒爆裂 + 移形回掠
      // -----------------------------------------------------------------------
      VFX.after(95, function () {
        if (targetEl) {
          VFX.hitFlash(targetEl, '#5eead4', 130);
        }

        // 靶心极短暂内吸聚压光核
        VFX.custom({
          layer: 'sky',
          blend: 'lighter',
          duration: 85,
          draw: function (c2d, p) {
            var shrink = VFX.lerp(1.0, 0.24, p);
            var rad = 48 * s * shrink;
            var alpha = p < 0.3 ? p / 0.3 : 1;
            c2d.save();
            c2d.translate(tSafe.x, tSafe.y);
            var g = c2d.createRadialGradient(0, 0, 0, 0, 0, rad);
            g.addColorStop(0, 'rgba(255,255,255,' + (0.95 * alpha).toFixed(3) + ')');
            g.addColorStop(0.35, 'rgba(250,204,21,' + (0.65 * alpha).toFixed(3) + ')');
            g.addColorStop(0.72, 'rgba(45,212,191,' + (0.3 * alpha).toFixed(3) + ')');
            g.addColorStop(1, 'rgba(45,212,191,0)');
            c2d.fillStyle = g;
            c2d.beginPath();
            c2d.arc(0, 0, rad, 0, TWO_PI);
            c2d.fill();
            c2d.restore();
          },
        });

        VFX.burst({
          x: tSafe.x,
          y: tSafe.y,
          radius: 62 * s,
          ring: true,
          count: 14,
          shape: 'wisp',
          colors: ['#facc15', '#2dd4bf', '#fef08a'],
          size: [3.0 * s, 5.6 * s],
          speed: [10 * s, 26 * s],
          attractTo: tSafe,
          attractStrength: 1480 * s,
          drag: 0.9,
          life: [70, 105],
          trail: 3,
        });

        VFX.burst({
          x: tSafe.x,
          y: tSafe.y,
          radius: 56 * s,
          ring: true,
          count: 12,
          shape: 'spark',
          colors: ['#ffffff', '#fef08a', '#5eead4'],
          size: [1.8 * s, 3.2 * s],
          speed: [8 * s, 22 * s],
          attractTo: tSafe,
          attractStrength: 1520 * s,
          drag: 0.9,
          life: [65, 100],
          trail: 4,
        });

        // 75ms 极短暂内吸后立即瞬爆十字破绽双斩
        VFX.after(75, function () {
          var slashAnchorX = tSafe.x - axis.ux * 6 * s;
          var slashAnchorY = tSafe.y - axis.uy * 6 * s;

          // 交叉正逆斜切（slash 内部自动乘 VFX.scale()，传入基准半径与厚度）
          VFX.slash({
            x: slashAnchorX,
            y: slashAnchorY,
            radius: 92,
            tilt: 0.92,
            thickness: 24,
            startAngle: orientAngle(-1.62, axis),
            endAngle: orientAngle(1.06, axis),
            color: '#facc15',
            glowColor: '#2dd4bf',
            coreColor: '#ffffff',
            duration: 245,
          });
          VFX.slash({
            delay: 26,
            x: tSafe.x + axis.ux * 4 * s,
            y: tSafe.y + axis.uy * 4 * s,
            radius: 86,
            tilt: 0.92,
            thickness: 20,
            startAngle: orientAngle(1.54, axis),
            endAngle: orientAngle(-1.12, axis),
            color: '#2dd4bf',
            glowColor: '#facc15',
            coreColor: '#ffffff',
            duration: 235,
          });

          // 渐变梭形十字破绽星芒爆裂
          emitCrossStarBurst(tSafe, axis.angle, 94, s);

          // 金青 shard 剑晶与 flare 寒星迸射
          VFX.burst({
            x: tSafe.x,
            y: tSafe.y,
            radius: 10 * s,
            count: 24,
            shape: 'shard',
            colors: ['#facc15', '#fef08a', '#2dd4bf', '#ffffff'],
            size: [2.8 * s, 5.8 * s],
            speed: [135 * s, 325 * s],
            gravity: 110 * s,
            spin: [-12, 12],
            life: [320, 620],
            trail: 4,
          });
          VFX.burst({
            x: tSafe.x,
            y: tSafe.y,
            radius: 12 * s,
            count: 12,
            shape: 'flare',
            colors: ['#ffffff', '#fef08a', '#5eead4', '#facc15'],
            size: [3.2 * s, 6.8 * s],
            speed: [45 * s, 185 * s],
            spin: [-5, 5],
            drag: 0.91,
            life: [220, 410],
          });
          VFX.burst({
            x: tSafe.x,
            y: tSafe.y,
            radius: 12 * s,
            count: 18,
            shape: 'wisp',
            colors: ['#2dd4bf', '#facc15', '#fef08a'],
            size: [4.4 * s, 8.6 * s],
            speed: [95 * s, 235 * s],
            turbulence: 64 * s,
            drag: 0.91,
            alpha: 0.78,
            life: [280, 500],
          });

          // 双层冲击波
          VFX.shockwave({
            x: tSafe.x,
            y: tSafe.y,
            fromRadius: 10,
            toRadius: 126,
            color: '#facc15',
            coreColor: '#ffffff',
            lineWidth: 4.4,
            duration: 360,
            double: true,
          });
          VFX.shockwave({
            delay: 36,
            x: tFoot.x,
            y: tFoot.y,
            tilt: 0.44,
            layer: 'ground',
            fromRadius: 10,
            toRadius: 96,
            color: '#2dd4bf',
            coreColor: '#fef08a',
            lineWidth: 3.0,
            duration: 340,
            double: false,
          });

          VFX.quake('md', 250);
          if (targetEl) {
            VFX.impact(targetEl, { color: '#facc15', duration: 240, distance: 16 });
          }
          VFX.floatText(targetEl || tSafe, '-' + dmg, isCrit ? 'crit' : 'damage', {
            scale: isCrit ? 1.34 : 1.14,
          });

          // 收招回掠：一缕青碧移形流光自靶点掠回施法者脚下
          VFX.after(110, function () {
            VFX.streamer({
              x1: tSafe.x,
              y1: tSafe.y,
              x2: cFoot.x,
              y2: cFoot.y,
              arc: -34 * distFactor * s,
              wobble: 6 * s,
              width: 7.5 * s,
              tailLength: 0.46,
              duration: 240,
              color: '#2dd4bf',
              coreColor: '#fef08a',
              sparkColors: ['#5eead4', '#fef08a', '#ffffff'],
              onArrive: function () {
                VFX.shockwave({
                  x: cFoot.x,
                  y: cFoot.y,
                  tilt: 0.44,
                  layer: 'ground',
                  fromRadius: 10,
                  toRadius: 82,
                  color: '#2dd4bf',
                  coreColor: '#ffffff',
                  lineWidth: 3.0,
                  duration: 360,
                  double: true,
                });
                VFX.burst({
                  x: cFoot.x,
                  y: cFoot.y - 4 * s,
                  radius: 26 * s,
                  ring: true,
                  tiltY: 0.44,
                  count: 14,
                  shape: 'wisp',
                  colors: ['#2dd4bf', '#5eead4', '#fef08a'],
                  size: [3.8 * s, 7.2 * s],
                  angle: [-Math.PI * 0.7, -Math.PI * 0.3],
                  speed: [55 * s, 125 * s],
                  vortex: 200 * s,
                  vortexCenter: cCaster,
                  gravity: -95 * s,
                  drag: 0.95,
                  alpha: 0.72,
                  life: [360, 620],
                });
                VFX.burst({
                  x: cCaster.x,
                  y: cCaster.y,
                  radius: 20 * s,
                  count: 5,
                  shape: 'flare',
                  colors: ['#ffffff', '#5eead4', '#fef08a'],
                  size: [2.2 * s, 4.0 * s],
                  speed: [20 * s, 58 * s],
                  gravity: -65 * s,
                  life: [240, 420],
                });
                VFX.floatText(casterEl || cCaster, '移形', '#5eead4', { scale: 0.92, rise: 32 });
              },
            });
          });
        });
      });
    });

    return 1120;
  });
});
