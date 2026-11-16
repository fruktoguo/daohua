/**
 * ability.zhuoxin · 灼心 —— 二阶火系零直伤心脉暗灼+海量燃烧附着（深度流体粒子化重构版）
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   单体敌 / 吟唱 2000ms / 0 直伤 + 50%AP+10 层「燃烧」 / 冷却 20s / 灵力 20 / 五行·火
 * 演出阶段：
 *   0ms    暗绯暗幕 + 施法浮咒 + 悬空 flame 绛红毒炎咒轮 + 暗红紫火 wisp/rune 向掌心 attractTo 凝成隐火火种
 *   160ms  双股反相交缠正弦螺旋隐火咒丝潜入目标心脉（双 streamer 配 wobble + 分段幽绯鬼火 wisp/orb/rune）
 *   360ms  目标胸口深紫红坍缩暗核（source-over）+ attractTo 内吸蓄压
 *   480ms  自肺腑内部透体绽开多层旋转羽化业火心莲与脉动热核（hitFlash 表现内灼，零大位移击飞）+
 *          5 波沿经脉盘旋升腾的赤金/深绯炎流、密集余烬、焚心烙印与「灼心·燃烧」浮字
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
    grad.addColorStop(0, 'rgba(255,255,255,0.96)');
    grad.addColorStop(0.18, 'rgba(' + rgb + ',0.78)');
    grad.addColorStop(0.46, 'rgba(' + rgb + ',0.32)');
    grad.addColorStop(0.78, 'rgba(' + rgb + ',0.07)');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    gctx.fillStyle = grad;
    gctx.beginPath();
    gctx.arc(48, 48, 46, 0, TWO_PI);
    gctx.fill();
    localGlowCache[color] = c;
    return c;
  }

  /**
   * 绘制首尾渐变归零的羽化炎丝弧（零硬描边）
   */
  function drawFeatheredFlameArc(c2d, x0, y0, cx, cy, x1, y1, thickness, rgb, alpha) {
    if (alpha <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.26, 'rgba(' + rgb + ',' + (alpha * 0.62).toFixed(3) + ')');
    grad.addColorStop(0.52, 'rgba(' + rgb + ',' + alpha.toFixed(3) + ')');
    grad.addColorStop(0.8, 'rgba(' + rgb + ',' + (alpha * 0.44).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(x0, y0);
    c2d.quadraticCurveTo(cx + nx * thickness, cy + ny * thickness, x1, y1);
    c2d.quadraticCurveTo(cx - nx * thickness * 0.72, cy - ny * thickness * 0.72, x0, y0);
    c2d.fill();
  }

  /**
   * 从肺腑内部透体绽开多层旋转羽化业火心莲与脉动热核（零硬瓣实边）
   */
  function emitHeartLotusBloom(pt, s) {
    var coreGlow = getSoftGlow('#f43f5e');
    var goldGlow = getSoftGlow('#fbbf24');

    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 780,
      draw: function (c2d, p) {
        var open = p < 0.22 ? 1 - Math.pow(1 - p / 0.22, 3) : 1;
        var fade = p < 0.14 ? p / 0.14 : p > 0.68 ? Math.max(0, (1 - p) / 0.32) : 1;
        if (fade <= 0.01) return;

        // 心脏脉动热核节律
        var beat = 0.8 + 0.2 * Math.sin(p * 26);
        var outerR = (28 + open * 36) * s * (0.94 + 0.08 * beat);
        var innerR = outerR * 0.64;

        c2d.save();
        c2d.translate(pt.x, pt.y);

        // 透体弥散暗绯金热核底晕
        var bgG = c2d.createRadialGradient(0, 0, 1, 0, 0, outerR * 1.25);
        bgG.addColorStop(0, 'rgba(254,240,138,' + (0.72 * fade * beat).toFixed(3) + ')');
        bgG.addColorStop(0.28, 'rgba(249,115,22,' + (0.48 * fade).toFixed(3) + ')');
        bgG.addColorStop(0.62, 'rgba(225,29,72,' + (0.28 * fade).toFixed(3) + ')');
        bgG.addColorStop(1, 'rgba(136,19,55,0)');
        c2d.fillStyle = bgG;
        c2d.beginPath();
        c2d.arc(0, 0, outerR * 1.25, 0, TWO_PI);
        c2d.fill();

        // 外层 8 瓣旋转羽化暗红业火莲瓣（每瓣均使用径向渐变归零椭圆光团）
        c2d.save();
        c2d.rotate(p * 1.15);
        for (var o = 0; o < 8; o++) {
          var oa = (o * TWO_PI) / 8;
          var ox = Math.cos(oa) * outerR * 0.54;
          var oy = Math.sin(oa) * outerR * 0.54;
          c2d.save();
          c2d.translate(ox, oy);
          c2d.rotate(oa);
          c2d.scale(1.58, 0.58);
          var opR = outerR * 0.44;
          var opG = c2d.createRadialGradient(0, 0, 0, 0, 0, opR);
          opG.addColorStop(0, 'rgba(251,113,133,' + (0.64 * fade).toFixed(3) + ')');
          opG.addColorStop(0.45, 'rgba(225,29,72,' + (0.34 * fade).toFixed(3) + ')');
          opG.addColorStop(1, 'rgba(136,19,55,0)');
          c2d.fillStyle = opG;
          c2d.beginPath();
          c2d.arc(0, 0, opR, 0, TWO_PI);
          c2d.fill();
          c2d.restore();
        }
        c2d.restore();

        // 内层 6 瓣逆旋炽金莲瓣
        c2d.save();
        c2d.rotate(-p * 1.85);
        for (var inn = 0; inn < 6; inn++) {
          var ia = (inn * TWO_PI) / 6;
          var ix = Math.cos(ia) * innerR * 0.48;
          var iy = Math.sin(ia) * innerR * 0.48;
          c2d.save();
          c2d.translate(ix, iy);
          c2d.rotate(ia);
          c2d.scale(1.48, 0.54);
          var ipR = innerR * 0.44;
          var ipG = c2d.createRadialGradient(0, 0, 0, 0, 0, ipR);
          ipG.addColorStop(0, 'rgba(255,255,255,' + (0.78 * fade * beat).toFixed(3) + ')');
          ipG.addColorStop(0.36, 'rgba(254,240,138,' + (0.52 * fade).toFixed(3) + ')');
          ipG.addColorStop(0.74, 'rgba(249,115,22,' + (0.22 * fade).toFixed(3) + ')');
          ipG.addColorStop(1, 'rgba(225,29,72,0)');
          c2d.fillStyle = ipG;
          c2d.beginPath();
          c2d.arc(0, 0, ipR, 0, TWO_PI);
          c2d.fill();
          c2d.restore();
        }
        c2d.restore();

        // 外缘旋转缠绕业火丝弧
        for (var fa = 0; fa < 3; fa++) {
          var a0 = p * 4.2 + (fa * TWO_PI) / 3;
          var a1 = a0 + 1.35;
          var aMid = (a0 + a1) * 0.5;
          drawFeatheredFlameArc(
            c2d,
            Math.cos(a0) * outerR * 0.86,
            Math.sin(a0) * outerR * 0.86,
            Math.cos(aMid) * outerR * 1.12,
            Math.sin(aMid) * outerR * 1.12,
            Math.cos(a1) * outerR * 0.86,
            Math.sin(a1) * outerR * 0.86,
            3.8 * s,
            fa % 2 === 0 ? '251,113,133' : '251,191,36',
            0.55 * fade
          );
        }

        // 中心脉动热核辉光叠加
        var cRad = outerR * 0.62 * beat;
        if (coreGlow) {
          c2d.globalAlpha = fade * 0.56;
          c2d.drawImage(coreGlow, -cRad, -cRad, cRad * 2, cRad * 2);
        }
        if (goldGlow) {
          c2d.globalAlpha = fade * 0.78 * beat;
          c2d.drawImage(goldGlow, -cRad * 0.65, -cRad * 0.65, cRad * 1.3, cRad * 1.3);
        }

        c2d.restore();
      },
    });
  }

  VFX.register('ability.zhuoxin', function (ctx) {
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
    var palmPt = VFX.clampStage(axis.palm(24 * s), 24 * s, 22 * s);
    var safePt = VFX.clampStage(targetEl ? VFX.center(targetEl) : VFX.sideCenter(foeSide), 36 * s, 32 * s);
    var safeFoot = VFX.clampStage(targetEl ? VFX.foot(targetEl) : VFX.sideFootCenter(foeSide), 34 * s, 22 * s);
    var distFactor = Math.min(1.15, Math.max(0.45, axis.dist / 240));

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：暗绯暗幕 + 悬空绛红毒炎咒轮 + 暗红紫火与咒印向掌心凝成隐火火种
    // -------------------------------------------------------------------------
    VFX.cinematicDim(0.34, 720, '#180306');
    if (casterEl) {
      VFX.motion(casterEl, 'cast', { duration: 400 });
    }

    // 身前悬空 flame 绛红毒炎咒轮与反向细血印
    VFX.magicCircle({
      x: palmPt.x,
      y: palmPt.y,
      radius: 46,
      tilt: 0.92,
      layer: 'sky',
      style: 'flame',
      color: '#e11d48',
      secondaryColor: '#a855f7',
      rotationSpeed: 2.3,
      duration: 540,
      alpha: 0.78,
      lineWidth: 1.8,
    });
    VFX.magicCircle({
      delay: 20,
      x: palmPt.x,
      y: palmPt.y,
      radius: 30,
      tilt: 0.92,
      layer: 'sky',
      style: 'blood',
      color: '#fb7185',
      secondaryColor: '#fde047',
      rotationSpeed: -3.1,
      duration: 500,
      alpha: 0.72,
      lineWidth: 1.4,
    });

    // 掌心隐火火种凝聚光核
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 210,
      draw: function (c2d, p) {
        var fade = p < 0.3 ? p / 0.3 : 1 - (p - 0.3) * 0.35;
        var rad = VFX.lerp(40, 18, p) * s;
        c2d.save();
        c2d.translate(palmPt.x, palmPt.y);
        var g = c2d.createRadialGradient(0, 0, 1, 0, 0, rad);
        g.addColorStop(0, 'rgba(254,240,138,' + (0.88 * fade).toFixed(3) + ')');
        g.addColorStop(0.34, 'rgba(225,29,72,' + (0.62 * fade).toFixed(3) + ')');
        g.addColorStop(0.72, 'rgba(147,51,234,' + (0.3 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(136,19,55,0)');
        c2d.fillStyle = g;
        c2d.beginPath();
        c2d.arc(0, 0, rad, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    // 暗红紫火 wisp 与 rune 咒印向掌心 attractTo 内吸
    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 58 * s,
      ring: true,
      count: 14,
      shape: 'wisp',
      colors: ['#9f1239', '#7e22ce', '#e11d48', '#f97316'],
      size: [3.6 * s, 6.8 * s],
      speed: [12 * s, 30 * s],
      vortex: 220 * s,
      vortexCenter: palmPt,
      attractTo: palmPt,
      attractStrength: 920 * s,
      drag: 0.92,
      life: [145, 205],
      trail: 3,
    });
    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 54 * s,
      ring: true,
      count: 10,
      shape: 'rune',
      colors: ['#fb7185', '#c084fc', '#fde047'],
      size: [3.2 * s, 5.4 * s],
      speed: [10 * s, 26 * s],
      attractTo: palmPt,
      attractStrength: 900 * s,
      life: [145, 200],
    });

    // -------------------------------------------------------------------------
    // 阶段二（160ms）：双股反相交缠正弦螺旋隐火咒丝潜入目标心脉
    // -------------------------------------------------------------------------
    VFX.after(160, function () {
      VFX.streamer({
        x1: palmPt.x,
        y1: palmPt.y,
        x2: safePt.x,
        y2: safePt.y,
        arc: 34 * distFactor * s,
        wobble: 12 * distFactor * s,
        width: 8 * s,
        tailLength: 0.5,
        duration: 225,
        color: '#e11d48',
        coreColor: '#fde047',
        sparkColors: ['#fb7185', '#f97316', '#c084fc'],
      });
      VFX.streamer({
        delay: 12,
        x1: palmPt.x,
        y1: palmPt.y,
        x2: safePt.x,
        y2: safePt.y,
        arc: -34 * distFactor * s,
        wobble: 12 * distFactor * s,
        width: 7 * s,
        tailLength: 0.48,
        duration: 225,
        color: '#9333ea',
        coreColor: '#fb7185',
        sparkColors: ['#c084fc', '#e11d48', '#fde047'],
      });

      // 沿正弦反相轨迹分段释放幽绯鬼火 wisp / orb / rune
      var pathSteps = 8;
      var amp = 18 * distFactor * s;
      for (var st = 0; st <= pathSteps; st++) {
        (function (idx) {
          var u = idx / pathSteps;
          var at = Math.round(u * 175);
          var env = Math.sin(u * Math.PI);
          var phase = u * Math.PI * 3.0;
          var bx = VFX.lerp(palmPt.x, safePt.x, u);
          var by = VFX.lerp(palmPt.y, safePt.y, u);
          var off = Math.sin(phase) * amp * (0.35 + 0.65 * env);

          VFX.burst({
            delay: at,
            x: bx + axis.nx * off,
            y: by + axis.ny * off,
            radius: 5 * s,
            count: 2,
            shape: 'wisp',
            colors: ['#be123c', '#9333ea', '#f97316'],
            size: [3.2 * s, 6.2 * s],
            baseAngle: axis.angle + Math.cos(phase) * 0.25,
            spread: 0.55,
            speed: [95 * s, 195 * s],
            turbulence: 72 * s,
            drag: 0.93,
            alpha: 0.72,
            life: [210, 380],
            trail: 3,
          });
          VFX.burst({
            delay: at + 8,
            x: bx - axis.nx * off * 0.9,
            y: by - axis.ny * off * 0.9,
            radius: 5 * s,
            count: 2,
            shape: idx % 2 === 0 ? 'orb' : 'rune',
            colors: ['#fb7185', '#c084fc', '#fde047'],
            size: [2.4 * s, 4.4 * s],
            baseAngle: axis.angle - Math.cos(phase) * 0.25,
            spread: 0.48,
            speed: [75 * s, 155 * s],
            drag: 0.94,
            life: [220, 400],
          });
        })(st);
      }
    });

    // -------------------------------------------------------------------------
    // 阶段三（360ms）：胸口深紫红坍缩暗核内吸蓄压 → 120ms 后透体业火心莲内灼爆发
    // -------------------------------------------------------------------------
    VFX.after(360, function () {
      if (targetEl) {
        VFX.hitFlash(targetEl, '#9f1239', 140);
      }

      // source-over 深紫红坍缩暗核（吸光压抑，随后爆燃透体心莲）
      VFX.custom({
        layer: 'sky',
        blend: 'source-over',
        duration: 145,
        draw: function (c2d, p) {
          var shrink = p < 0.65 ? VFX.lerp(1.0, 0.38, p / 0.65) : Math.max(0.08, 0.38 * (1 - (p - 0.65) / 0.35));
          var alpha = p < 0.22 ? p / 0.22 : p > 0.85 ? (1 - p) / 0.15 : 1;
          var rad = 54 * s * shrink;

          c2d.save();
          c2d.translate(safePt.x, safePt.y);

          var darkG = c2d.createRadialGradient(0, 0, 1, 0, 0, rad * 1.4);
          darkG.addColorStop(0, 'rgba(18,3,10,' + (0.94 * alpha).toFixed(3) + ')');
          darkG.addColorStop(0.42, 'rgba(136,19,55,' + (0.76 * alpha).toFixed(3) + ')');
          darkG.addColorStop(0.76, 'rgba(126,34,206,' + (0.34 * alpha).toFixed(3) + ')');
          darkG.addColorStop(1, 'rgba(126,34,206,0)');
          c2d.fillStyle = darkG;
          c2d.beginPath();
          c2d.arc(0, 0, rad * 1.4, 0, TWO_PI);
          c2d.fill();

          c2d.globalCompositeOperation = 'lighter';
          var coreG = c2d.createRadialGradient(0, 0, 0, 0, 0, rad * 0.65);
          coreG.addColorStop(0, 'rgba(254,240,138,' + (0.85 * alpha).toFixed(3) + ')');
          coreG.addColorStop(0.45, 'rgba(225,29,72,' + (0.55 * alpha).toFixed(3) + ')');
          coreG.addColorStop(1, 'rgba(225,29,72,0)');
          c2d.fillStyle = coreG;
          c2d.beginPath();
          c2d.arc(0, 0, rad * 0.65, 0, TWO_PI);
          c2d.fill();
          c2d.restore();
        },
      });

      // attractTo 内吸蓄压粒子
      VFX.burst({
        x: safePt.x,
        y: safePt.y,
        radius: 82 * s,
        ring: true,
        count: 18,
        shape: 'wisp',
        colors: ['#be123c', '#9333ea', '#f97316', '#fde047'],
        size: [3.6 * s, 6.8 * s],
        speed: [14 * s, 36 * s],
        vortex: 250 * s,
        vortexCenter: safePt,
        attractTo: safePt,
        attractStrength: 1180 * s,
        drag: 0.92,
        life: [105, 145],
        trail: 3,
      });
      VFX.burst({
        x: safePt.x,
        y: safePt.y,
        radius: 74 * s,
        ring: true,
        count: 10,
        shape: 'rune',
        colors: ['#fb7185', '#c084fc', '#fde047'],
        size: [3.4 * s, 5.6 * s],
        speed: [10 * s, 28 * s],
        attractTo: safePt,
        attractStrength: 1120 * s,
        life: [100, 140],
      });

      // 120ms 后（480ms）：自肺腑内部透体绽开业火心莲与脉动热核（用 hitFlash 表现内灼，不用大位移击飞）
      VFX.after(120, function () {
        if (targetEl) {
          VFX.hitFlash(targetEl, '#f43f5e', 300);
        }

        emitHeartLotusBloom(safePt, s);

        VFX.shockwave({
          x: safePt.x,
          y: safePt.y,
          fromRadius: 8,
          toRadius: 88,
          tilt: 0.88,
          layer: 'sky',
          color: '#f43f5e',
          coreColor: '#fde047',
          lineWidth: 3.6,
          duration: 380,
          double: true,
        });
        VFX.shockwave({
          delay: 45,
          x: safeFoot.x,
          y: safeFoot.y,
          fromRadius: 10,
          toRadius: 72,
          tilt: 0.44,
          layer: 'ground',
          color: '#e11d48',
          coreColor: '#fb923c',
          lineWidth: 2.6,
          duration: 360,
          double: false,
        });

        // 5 波沿经脉盘旋升腾的赤金/深绯 wisp 炎流、密集 ember 余烬与 rune 焚心烙印
        var flameWaves = 5;
        for (var fw = 0; fw < flameWaves; fw++) {
          (function (waveIdx) {
            VFX.after(waveIdx * 95, function () {
              var u = waveIdx / (flameWaves - 1);
              var baseAng = waveIdx * 1.5;
              var rr = VFX.lerp(18, 34, u) * s;

              for (var arm = 0; arm < 2; arm++) {
                var a = baseAng + arm * Math.PI;
                var sx = safeFoot.x + Math.cos(a) * rr;
                var sy = safeFoot.y - (8 + u * 38) * s + Math.sin(a) * rr * 0.36;
                var vx = -Math.sin(a) * 76 * s;
                var vy = Math.cos(a) * 24 * s - 78 * s;

                VFX.burst({
                  x: sx,
                  y: sy,
                  radius: 10 * s,
                  count: 2,
                  shape: 'wisp',
                  colors: ['#f97316', '#e11d48', '#fde047', '#fb7185'],
                  size: [4.8 * s, 9.2 * s],
                  endSize: [2.4 * s, 4.6 * s],
                  speed: [8 * s, 20 * s],
                  vx: vx,
                  vy: vy,
                  gravity: -72 * s,
                  turbulence: 88 * s,
                  drag: 0.95,
                  alpha: [0.56, 0.82],
                  life: [420, 720],
                });
              }

              VFX.burst({
                x: safePt.x,
                y: safePt.y + (8 - u * 18) * s,
                radius: 26 * s,
                tiltY: 0.55,
                count: 8,
                shape: 'ember',
                colors: ['#ffffff', '#fef08a', '#fb923c', '#f43f5e'],
                size: [2.2 * s, 4.6 * s],
                speed: [28 * s, 88 * s],
                angle: [-Math.PI * 0.82, -Math.PI * 0.18],
                vortex: 165 * s,
                vortexCenter: safePt,
                gravity: -85 * s,
                turbulence: 64 * s,
                life: [420, 740],
                trail: 3,
              });

              if (waveIdx % 2 === 0) {
                VFX.burst({
                  x: safePt.x,
                  y: safePt.y,
                  radius: 24 * s,
                  count: 4,
                  shape: 'rune',
                  colors: ['#fb7185', '#fde047', '#f97316'],
                  size: [4.2 * s, 6.8 * s],
                  speed: [36 * s, 95 * s],
                  gravity: -68 * s,
                  drag: 0.94,
                  life: [420, 700],
                });
              }
            });
          })(fw);
        }

        // 零直伤内灼火点微闪
        VFX.burst({
          x: safePt.x,
          y: safePt.y,
          radius: 14 * s,
          count: 5,
          shape: 'flare',
          colors: ['#ffffff', '#fef08a', '#fb7185'],
          size: [2.6 * s, 4.8 * s],
          speed: [22 * s, 72 * s],
          life: [220, 400],
        });

        VFX.floatText(targetEl || safePt, '灼心·燃烧', '#fb7185', { scale: 1.08, rise: 40 });
      });
    });

    return 1280;
  });
});
