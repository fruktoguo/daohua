/**
 * 战斗特质反应专属特效：
 *   - trait.xuyi   · 蓄意爆发（积伤满溢反击：主目标纯白鎏金真伤坍缩裂星 + 其余目标赤金物理溅射冲击）
 *   - trait.ranhun · 燃烧（每秒持续火伤 DoT：轻快干脆无暗幕不震屏，心口暗绯赤金内晕 + 盘旋噬魂火苗与余烬）
 *
 * 对应服务端规则真源：src/Ecs/Combat/CombatTraitReactions.cs
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var HALF_PI = Math.PI * 0.5;

  /**
   * 判定指定 DOM 节点是否归属敌方阵营
   */
  function isEnemyNode(el) {
    if (!el || !el.classList) return false;
    return el.classList.contains('is-enemy') || el.classList.contains('arena-actor--enemy');
  }

  /**
   * 判定节点是否处于阵亡状态
   */
  function isDeadNode(el) {
    if (!el || !el.classList) return false;
    return el.classList.contains('is-defeated') || el.classList.contains('is-dead');
  }

  /**
   * 收集不重复且排除施法者自身的目标列表（用于蓄意爆发防自误伤）
   */
  function collectBurstTargets(ctx, casterEl, foeSide) {
    var raw = null;
    if (ctx.targetEls && ctx.targetEls.length) {
      raw = ctx.targetEls;
    } else if (ctx.targetEl) {
      raw = [ctx.targetEl];
    } else {
      raw = foeSide === 'ally' ? ctx.allyEls : ctx.enemyEls;
    }

    var out = [];
    if (!raw || !raw.length) return out;

    for (var i = 0; i < raw.length; i++) {
      var el = raw[i];
      if (!el || el === casterEl) continue;
      if (out.indexOf(el) === -1) out.push(el);
    }

    // 兜底池若仅含部分存活目标，优先保留未阵亡目标，全灭尾帧则保留全部
    if (!ctx.targetEls && !ctx.targetEl && out.length > 1) {
      var alive = [];
      for (var j = 0; j < out.length; j++) {
        if (!isDeadNode(out[j])) alive.push(out[j]);
      }
      if (alive.length) return alive;
    }
    return out;
  }

  /**
   * 解析目标伤害数值（兼容数组、均分伤害、单值伤害及无数值演示模式）
   */
  function resolveTargetDamage(ctx, idx, targetCount) {
    if (Array.isArray(ctx.damage)) {
      return typeof ctx.damage[idx] === 'number' ? ctx.damage[idx] : null;
    }
    if (typeof ctx.perTargetDamage === 'number') {
      return ctx.perTargetDamage;
    }
    if (typeof ctx.damage === 'number') {
      return targetCount > 1 ? Math.round(ctx.damage / targetCount) : ctx.damage;
    }
    return null;
  }

  /**
   * 绘制两端透明度严格归零的渐变羽毛弧（用于灼烧弧晕与真伤极光十字芒）
   */
  function drawFeatheredFlameArc(c2d, x0, y0, cx, cy, x1, y1, thickness, colorRgb, alpha) {
    var a = VFX.clamp(alpha, 0, 1);
    if (a <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + colorRgb + ',0)');
    grad.addColorStop(0.26, 'rgba(' + colorRgb + ',' + (a * 0.62).toFixed(3) + ')');
    grad.addColorStop(0.54, 'rgba(' + colorRgb + ',' + a.toFixed(3) + ')');
    grad.addColorStop(0.82, 'rgba(' + colorRgb + ',' + (a * 0.42).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + colorRgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(x0, y0);
    c2d.quadraticCurveTo(cx + nx * thickness, cy + ny * thickness, x1, y1);
    c2d.quadraticCurveTo(cx - nx * thickness * 0.72, cy - ny * thickness * 0.72, x0, y0);
    c2d.fill();
  }

  /**
   * 绘制梭形渐变极光臂（纯二次贝塞尔与线性渐变，边缘 alpha 严格归零）
   */
  function drawAuroraCrossArm(c2d, armLen, halfWidth, rgbOuter, rgbCore, alpha) {
    var a = VFX.clamp(alpha, 0, 1);
    if (a <= 0.01 || armLen <= 1) return;

    var outerGrad = c2d.createLinearGradient(-armLen, 0, armLen, 0);
    outerGrad.addColorStop(0, 'rgba(' + rgbOuter + ',0)');
    outerGrad.addColorStop(0.24, 'rgba(' + rgbOuter + ',' + (a * 0.55).toFixed(3) + ')');
    outerGrad.addColorStop(0.5, 'rgba(' + rgbCore + ',' + (a * 0.96).toFixed(3) + ')');
    outerGrad.addColorStop(0.76, 'rgba(' + rgbOuter + ',' + (a * 0.55).toFixed(3) + ')');
    outerGrad.addColorStop(1, 'rgba(' + rgbOuter + ',0)');

    c2d.fillStyle = outerGrad;
    c2d.beginPath();
    c2d.moveTo(-armLen, 0);
    c2d.quadraticCurveTo(0, -halfWidth, armLen, 0);
    c2d.quadraticCurveTo(0, halfWidth, -armLen, 0);
    c2d.fill();

    // 中心高能炽白芯芒
    var coreLen = armLen * 0.74;
    var coreW = Math.max(0.8, halfWidth * 0.42);
    var coreGrad = c2d.createLinearGradient(-coreLen, 0, coreLen, 0);
    coreGrad.addColorStop(0, 'rgba(255,255,255,0)');
    coreGrad.addColorStop(0.32, 'rgba(255,255,255,' + (a * 0.65).toFixed(3) + ')');
    coreGrad.addColorStop(0.5, 'rgba(255,255,255,' + a.toFixed(3) + ')');
    coreGrad.addColorStop(0.68, 'rgba(255,255,255,' + (a * 0.65).toFixed(3) + ')');
    coreGrad.addColorStop(1, 'rgba(255,255,255,0)');

    c2d.fillStyle = coreGrad;
    c2d.beginPath();
    c2d.moveTo(-coreLen, 0);
    c2d.quadraticCurveTo(0, -coreW, coreLen, 0);
    c2d.quadraticCurveTo(0, coreW, -coreLen, 0);
    c2d.fill();
  }

  /**
   * 发射纯白鎏金真伤螺旋破空流（双股互缠 streamer + 沿路螺旋喷涌高速 wisp/spark）
   */
  function emitTrueBurstSpiralBeam(fromPt, toPt, travelMs, s) {
    var dv = VFX.dir(fromPt, toPt);
    var nx = -dv.dy;
    var ny = dv.dx;
    var distFactor = Math.min(1, dv.dist / 240);
    var spiralAmp = 15 * distFactor * s;

    // 双股互缠鎏金真伤流光丝
    VFX.streamer({
      x1: fromPt.x,
      y1: fromPt.y,
      x2: toPt.x,
      y2: toPt.y,
      arc: 22 * distFactor * s,
      wobble: 7 * distFactor * s,
      width: 10.5 * s,
      tailLength: 0.48,
      color: '#fef08a',
      coreColor: '#ffffff',
      sparkColors: ['#ffffff', '#fef08a', '#f5c451'],
      duration: travelMs + 70,
    });
    VFX.streamer({
      delay: 10,
      x1: fromPt.x,
      y1: fromPt.y,
      x2: toPt.x,
      y2: toPt.y,
      arc: -20 * distFactor * s,
      wobble: 6.5 * distFactor * s,
      width: 8.5 * s,
      tailLength: 0.44,
      color: '#f5c451',
      coreColor: '#ffffff',
      sparkColors: ['#fef08a', '#f5c451', '#ffffff'],
      duration: travelMs + 75,
    });

    // 沿路径分段喷涌高速螺旋 wisp 与 spark
    var steps = 8;
    for (var idx = 0; idx <= steps; idx++) {
      (function (stepIdx) {
        var u = stepIdx / steps;
        var at = Math.round(u * (travelMs - 10));
        var env = Math.sin(u * Math.PI);
        var phase = u * Math.PI * 3.4;
        var bx = VFX.lerp(fromPt.x, toPt.x, u);
        var by = VFX.lerp(fromPt.y, toPt.y, u);
        var off = Math.sin(phase) * spiralAmp * (0.3 + 0.7 * env);

        var wispPt = VFX.clampStage({ x: bx + nx * off, y: by + ny * off }, 16 * s, 16 * s);
        var sparkPt = VFX.clampStage({ x: bx - nx * off * 0.85, y: by - ny * off * 0.85 }, 16 * s, 16 * s);

        VFX.burst({
          delay: at,
          x: wispPt.x,
          y: wispPt.y,
          radius: 4.5 * s,
          count: 3,
          shape: 'wisp',
          colors: ['#ffffff', '#fef08a', '#f5c451'],
          size: [3.2 * s, 6.2 * s],
          baseAngle: dv.angle + Math.cos(phase) * 0.25,
          spread: 0.52,
          speed: [175 * s, 320 * s],
          turbulence: 65 * s,
          drag: 0.92,
          alpha: 0.82,
          life: [190, 340],
          trail: 3,
        });

        VFX.burst({
          delay: at + 5,
          x: sparkPt.x,
          y: sparkPt.y,
          radius: 4 * s,
          count: 4,
          shape: 'spark',
          colors: ['#ffffff', '#fef08a', '#f59e0b'],
          size: [1.8 * s, 3.6 * s],
          baseAngle: dv.angle - Math.cos(phase) * 0.22,
          spread: 0.45,
          speed: [240 * s, 440 * s],
          drag: 0.93,
          life: [180, 320],
          trail: 4,
        });
      })(idx);
    }
  }

  /**
   * 绘制主目标纯白鎏金真伤坍缩裂星（多层径向柔光 + 交叉渐变极光 + 耀星粒子）
   */
  function emitTrueDamageNova(pt, s) {
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 420,
      draw: function (c2d, p) {
        // 先 0..0.14 瞬间坍缩凝聚，再迅速裂星绽放后柔和消散
        var collapse = p < 0.14 ? VFX.lerp(1.08, 0.48, p / 0.14) : VFX.lerp(0.55, 1.18, Math.min(1, (p - 0.14) / 0.34));
        var alpha = p < 0.08 ? p / 0.08 : Math.pow(1 - (p - 0.08) / 0.92, 1.45);
        if (alpha <= 0.01) return;

        var glowR = Math.max(4, 88 * s * collapse);
        var armLen = Math.max(4, 96 * s * collapse);
        var subArmLen = Math.max(4, 58 * s * collapse);

        c2d.save();
        c2d.translate(pt.x, pt.y);

        // 1. 多层径向柔光核心（边缘严格归零）
        var outerGrad = c2d.createRadialGradient(0, 0, 1, 0, 0, glowR);
        outerGrad.addColorStop(0, 'rgba(255,255,255,' + (0.96 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(0.22, 'rgba(254,240,138,' + (0.72 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(0.52, 'rgba(245,196,81,' + (0.34 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(0.8, 'rgba(245,158,11,' + (0.11 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(1, 'rgba(245,158,11,0)');
        c2d.fillStyle = outerGrad;
        c2d.beginPath();
        c2d.arc(0, 0, glowR, 0, TWO_PI);
        c2d.fill();

        // 2. 主正交十字极光芒
        c2d.save();
        c2d.rotate(p * 0.25);
        drawAuroraCrossArm(c2d, armLen, 7.5 * s * (1 - p * 0.35), '245,196,81', '254,240,138', alpha * 0.95);
        c2d.rotate(HALF_PI);
        drawAuroraCrossArm(c2d, armLen, 7.5 * s * (1 - p * 0.35), '245,196,81', '254,240,138', alpha * 0.95);
        c2d.restore();

        // 3. 斜交 45° 次级碎星极光芒
        c2d.save();
        c2d.rotate(Math.PI * 0.25 - p * 0.45);
        drawAuroraCrossArm(c2d, subArmLen, 4.6 * s * (1 - p * 0.3), '245,158,11', '255,255,255', alpha * 0.72);
        c2d.rotate(HALF_PI);
        drawAuroraCrossArm(c2d, subArmLen, 4.6 * s * (1 - p * 0.3), '245,158,11', '255,255,255', alpha * 0.72);
        c2d.restore();

        c2d.restore();
      },
    });

    // 中心主 flare 耀星 + 四散碎星 flare
    VFX.burst({
      x: pt.x,
      y: pt.y,
      radius: 4 * s,
      count: 3,
      shape: 'flare',
      colors: ['#ffffff', '#fef08a'],
      size: [9.5 * s, 14.5 * s],
      speed: [6 * s, 24 * s],
      spin: [-1.8, 1.8],
      alpha: 0.96,
      life: [260, 400],
    });
    VFX.burst({
      x: pt.x,
      y: pt.y,
      radius: 14 * s,
      count: 8,
      shape: 'flare',
      colors: ['#ffffff', '#fef08a', '#f5c451'],
      size: [3.6 * s, 6.4 * s],
      speed: [65 * s, 185 * s],
      spin: [-4.5, 4.5],
      drag: 0.91,
      life: [240, 420],
    });
  }

  // ===========================================================================
  // 1. trait.xuyi · 蓄意爆发
  // ===========================================================================
  VFX.register('trait.xuyi', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var isEnemyCaster = ctx.side === 'enemy' || isEnemyNode(casterEl);
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';

    var targets = collectBurstTargets(ctx, casterEl, foeSide);
    var primaryEl = targets[0] || null;
    var splashTargets = targets.slice(1);
    var targetCount = Math.max(1, targets.length);

    var s = VFX.scale();
    var axis = VFX.combatAxis(casterEl, primaryEl || foeSide);
    var casterPt = VFX.clampStage(casterEl ? VFX.center(casterEl) : axis.start, 28 * s, 26 * s);
    var palmPt = VFX.clampStage(axis.palm(22 * s), 22 * s, 22 * s);
    var primaryPt = VFX.clampStage(primaryEl ? VFX.center(primaryEl) : VFX.sideCenter(foeSide), 38 * s, 32 * s);
    var primaryFoot = VFX.clampStage(
      primaryEl ? VFX.foot(primaryEl) : { x: primaryPt.x, y: primaryPt.y + 38 * s },
      36 * s,
      18
    );

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：琥珀暗幕 + 施法者胸口怒意核心蓄爆 + 碎甲十字锐芒 + 突进闪白
    // -------------------------------------------------------------------------
    VFX.cinematicDim(0.26, 560, '#120c04');
    if (casterEl) {
      VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 30, duration: 220 });
      VFX.hitFlash(casterEl, '#fef08a', 200);
    }

    // 胸口积蓄怒意核心柔光 + 碎甲极光十字
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 280,
      draw: function (c2d, p) {
        var open = p < 0.25 ? p / 0.25 : 1 - (p - 0.25) * 0.2;
        var alpha = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.4);
        if (alpha <= 0.01) return;
        var rad = Math.max(4, 48 * s * open);
        var crossLen = Math.max(4, 54 * s * open);

        c2d.save();
        c2d.translate(casterPt.x, casterPt.y);

        var g = c2d.createRadialGradient(0, 0, 1, 0, 0, rad);
        g.addColorStop(0, 'rgba(255,255,255,' + (0.94 * alpha).toFixed(3) + ')');
        g.addColorStop(0.28, 'rgba(254,240,138,' + (0.68 * alpha).toFixed(3) + ')');
        g.addColorStop(0.62, 'rgba(245,196,81,' + (0.28 * alpha).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(245,158,11,0)');
        c2d.fillStyle = g;
        c2d.beginPath();
        c2d.arc(0, 0, rad, 0, TWO_PI);
        c2d.fill();

        drawAuroraCrossArm(c2d, crossLen, 4.4 * s, '245,196,81', '255,255,255', alpha * 0.85);
        c2d.rotate(HALF_PI);
        drawAuroraCrossArm(c2d, crossLen, 4.4 * s, '245,196,81', '255,255,255', alpha * 0.85);
        c2d.restore();
      },
    });

    VFX.shockwave({
      x: casterPt.x,
      y: casterPt.y,
      fromRadius: 8,
      toRadius: 68,
      color: '#fef08a',
      coreColor: '#ffffff',
      lineWidth: 3.4,
      duration: 240,
      double: true,
    });
    VFX.burst({
      x: casterPt.x,
      y: casterPt.y,
      radius: 6 * s,
      count: 4,
      shape: 'flare',
      colors: ['#ffffff', '#fef08a', '#f5c451'],
      size: [6.2 * s, 9.8 * s],
      speed: [18 * s, 68 * s],
      spin: [-3.2, 3.2],
      life: [180, 290],
    });
    VFX.burst({
      x: casterPt.x,
      y: casterPt.y,
      radius: 14 * s,
      count: 10,
      shape: 'wisp',
      colors: ['#fef08a', '#f5c451', '#f59e0b'],
      size: [3.6 * s, 6.4 * s],
      baseAngle: axis.angle,
      spread: 1.5,
      speed: [55 * s, 145 * s],
      drag: 0.91,
      alpha: 0.78,
      life: [180, 320],
    });

    // -------------------------------------------------------------------------
    // 阶段二（95ms）：纯白鎏金真伤螺旋破空流直扑主目标 → 175ms 轰然坍缩裂星炸裂
    // -------------------------------------------------------------------------
    VFX.after(95, function () {
      emitTrueBurstSpiralBeam(palmPt, primaryPt, 80, s);

      // 175ms（95 + 80）抵达主目标触发真伤坍缩裂星
      VFX.after(80, function () {
        emitTrueDamageNova(primaryPt, s);

        VFX.shockwave({
          x: primaryPt.x,
          y: primaryPt.y,
          fromRadius: 12,
          toRadius: 140,
          color: '#ffffff',
          coreColor: '#fef08a',
          lineWidth: 4.8,
          duration: 360,
          double: true,
        });
        VFX.shockwave({
          delay: 36,
          x: primaryPt.x,
          y: primaryPt.y,
          fromRadius: 10,
          toRadius: 102,
          color: '#f5c451',
          coreColor: '#ffffff',
          lineWidth: 3.2,
          duration: 300,
          double: false,
        });

        VFX.groundCrack({
          x: primaryFoot.x,
          y: primaryFoot.y,
          radius: 90,
          branches: 8,
          color: '#f5c451',
          crackColor: '#120c04',
          lineWidth: 3.4,
          duration: 740,
        });

        // 狂暴纯白鎏金震荡气浪、破片与火星雨
        VFX.burst({
          x: primaryPt.x,
          y: primaryPt.y,
          radius: 14 * s,
          count: 18,
          shape: 'wisp',
          colors: ['#ffffff', '#fef08a', '#f5c451', '#f59e0b'],
          size: [4.8 * s, 9.4 * s],
          speed: [105 * s, 240 * s],
          turbulence: 64 * s,
          drag: 0.91,
          alpha: 0.84,
          life: [280, 520],
        });
        VFX.burst({
          x: primaryPt.x,
          y: primaryPt.y,
          radius: 12 * s,
          count: 24,
          shape: 'spark',
          colors: ['#ffffff', '#fef08a', '#f5c451'],
          size: [2.2 * s, 4.6 * s],
          baseAngle: axis.angle,
          spread: Math.PI * 1.7,
          speed: [145 * s, 340 * s],
          drag: 0.92,
          life: [240, 480],
          trail: 4,
        });
        VFX.burst({
          x: primaryPt.x,
          y: primaryPt.y,
          radius: 10 * s,
          count: 16,
          shape: 'shard',
          colors: ['#ffffff', '#fef08a', '#f5c451'],
          size: [2.8 * s, 5.4 * s],
          speed: [120 * s, 290 * s],
          gravity: 110 * s,
          spin: [-10, 10],
          life: [280, 540],
          trail: 3,
        });

        VFX.quake('md', 260);
        if (primaryEl) {
          VFX.impact(primaryEl, { color: '#ffffff', distance: 16, duration: 240 });
        }

        var primaryDmg = resolveTargetDamage(ctx, 0, targetCount);
        if (primaryDmg !== null) {
          VFX.floatText(primaryEl || primaryPt, '-' + Math.round(primaryDmg), ctx.crit ? 'crit' : '#fef08a', {
            scale: ctx.crit ? 1.32 : 1.18,
          });
          VFX.floatText({ x: primaryPt.x, y: primaryPt.y - 26 * s }, '蓄意爆发', '#fef08a', {
            scale: 0.98,
            rise: 36,
          });
        } else {
          VFX.floatText(primaryEl || primaryPt, '蓄意爆发', '#fef08a', {
            scale: 1.08,
            rise: 38,
          });
        }
      });
    });

    // -------------------------------------------------------------------------
    // 阶段三（210ms）：若有溅射副目标，自爆裂中心错峰 50ms 激射赤金物理冲击气浪
    // -------------------------------------------------------------------------
    var totalDuration = 920;
    if (splashTargets.length > 0) {
      VFX.after(210, function () {
        for (var i = 0; i < splashTargets.length; i++) {
          (function (spEl, seq) {
            var stagger = seq * 50;
            VFX.after(stagger, function () {
              var spPt = VFX.clampStage(VFX.center(spEl), 32 * s, 28 * s);
              var origin = Math.hypot(spPt.x - primaryPt.x, spPt.y - primaryPt.y) > 8 ? primaryPt : casterPt;
              var arcSign = seq % 2 === 0 ? -1 : 1;
              var splashDmg = resolveTargetDamage(ctx, seq + 1, targetCount);

              VFX.streamer({
                x1: origin.x,
                y1: origin.y,
                x2: spPt.x,
                y2: spPt.y,
                arc: 18 * arcSign * s,
                wobble: 5 * s,
                width: 8.8 * s,
                tailLength: 0.44,
                color: '#f59e0b',
                coreColor: '#fef08a',
                sparkColors: ['#f59e0b', '#ef4444', '#fef08a'],
                duration: 135,
              });
              VFX.streamer({
                delay: 12,
                x1: origin.x,
                y1: origin.y,
                x2: spPt.x,
                y2: spPt.y,
                arc: -14 * arcSign * s,
                wobble: 4 * s,
                width: 6.0 * s,
                tailLength: 0.4,
                color: '#ef4444',
                coreColor: '#fde047',
                sparkColors: ['#ef4444', '#f59e0b', '#ffffff'],
                duration: 130,
              });

              VFX.after(105, function () {
                VFX.shockwave({
                  x: spPt.x,
                  y: spPt.y,
                  fromRadius: 8,
                  toRadius: 76,
                  color: '#f59e0b',
                  coreColor: '#ef4444',
                  lineWidth: 3.0,
                  duration: 240,
                  double: false,
                });
                VFX.burst({
                  x: spPt.x,
                  y: spPt.y,
                  radius: 12 * s,
                  count: 14,
                  shape: 'shard',
                  colors: ['#f59e0b', '#ef4444', '#fef08a', '#ffffff'],
                  size: [2.6 * s, 5.0 * s],
                  speed: [95 * s, 230 * s],
                  gravity: 125 * s,
                  spin: [-9, 9],
                  life: [240, 440],
                  trail: 3,
                });
                VFX.burst({
                  x: spPt.x,
                  y: spPt.y,
                  radius: 12 * s,
                  count: 14,
                  shape: 'spark',
                  colors: ['#ef4444', '#f59e0b', '#ffffff'],
                  size: [2.0 * s, 4.0 * s],
                  speed: [110 * s, 260 * s],
                  drag: 0.92,
                  life: [200, 380],
                  trail: 4,
                });
                VFX.burst({
                  x: spPt.x,
                  y: spPt.y,
                  radius: 12 * s,
                  count: 8,
                  shape: 'wisp',
                  colors: ['#f59e0b', '#ef4444', '#fef08a'],
                  size: [3.8 * s, 7.0 * s],
                  speed: [70 * s, 165 * s],
                  drag: 0.91,
                  alpha: 0.78,
                  life: [220, 400],
                });

                VFX.impact(spEl, { color: '#f59e0b', distance: 12, duration: 200 });
                VFX.quake('sm', 160);

                if (splashDmg !== null) {
                  VFX.floatText(spEl, '-' + Math.round(splashDmg), ctx.crit ? 'crit' : 'damage', {
                    scale: ctx.crit ? 1.15 : 0.98,
                  });
                }
              });
            });
          })(splashTargets[i], i);
        }
      });
      totalDuration = Math.max(920, 210 + (splashTargets.length - 1) * 50 + 460);
    }

    return totalDuration;
  });

  // ===========================================================================
  // 2. trait.ranhun · 燃烧（每秒持续火伤 DoT，轻快不刺眼、禁暗幕禁震屏）
  // ===========================================================================
  VFX.register('trait.ranhun', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var isEnemyCaster = ctx.side === 'enemy' || isEnemyNode(casterEl);
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';

    var rawTargets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    var targets = [];
    for (var i = 0; i < rawTargets.length; i++) {
      var candidate = rawTargets[i];
      if (candidate && targets.indexOf(candidate) === -1) targets.push(candidate);
    }
    if (!targets.length) {
      var fallbackPool = foeSide === 'ally' ? ctx.allyEls : ctx.enemyEls;
      if (fallbackPool && fallbackPool.length) {
        for (var j = 0; j < fallbackPool.length; j++) {
          if (fallbackPool[j] && !isDeadNode(fallbackPool[j])) {
            targets.push(fallbackPool[j]);
            break;
          }
        }
      }
    }

    var s = VFX.scale();
    var list = targets.length ? targets : [null];
    var count = list.length;

    for (var k = 0; k < count; k++) {
      (function (el, idx) {
        var rawPt = el ? VFX.center(el) : VFX.sideCenter(foeSide);
        var safePt = VFX.clampStage(rawPt, 26 * s, 24 * s);
        var tickDmg = resolveTargetDamage(ctx, idx, count);

        // 1. 心口柔和脉动的暗绯赤金灼烧内晕 + 旋转微缩炽炎弧晕
        VFX.custom({
          layer: 'sky',
          blend: 'lighter',
          duration: 420,
          draw: function (c2d, p) {
            var fade = p < 0.18 ? p / 0.18 : Math.pow(1 - (p - 0.18) / 0.82, 1.35);
            if (fade <= 0.01) return;

            var pulse = 0.86 + 0.14 * Math.sin(p * Math.PI * 3.5);
            var haloR = Math.max(4, (27 + p * 7) * s);
            var arcR = Math.max(4, (14 + (1 - p) * 6) * s);

            c2d.save();
            c2d.translate(safePt.x, safePt.y);

            // 纯径向归零灼烧内晕（亮度克制，多目标同屏不糊屏）
            var grad = c2d.createRadialGradient(0, 0, 1, 0, 0, haloR);
            grad.addColorStop(0, 'rgba(253,224,71,' + (0.34 * fade * pulse).toFixed(3) + ')');
            grad.addColorStop(0.34, 'rgba(255,87,34,' + (0.28 * fade * pulse).toFixed(3) + ')');
            grad.addColorStop(0.68, 'rgba(251,113,133,' + (0.14 * fade).toFixed(3) + ')');
            grad.addColorStop(1, 'rgba(136,19,55,0)');
            c2d.fillStyle = grad;
            c2d.beginPath();
            c2d.arc(0, 0, haloR, 0, TWO_PI);
            c2d.fill();

            // 3 道微缩旋转炽炎弧晕
            c2d.rotate(p * 3.0 + idx * 0.8);
            for (var aIdx = 0; aIdx < 3; aIdx++) {
              var a0 = (aIdx * TWO_PI) / 3;
              var a1 = a0 + 1.05;
              var aMid = (a0 + a1) * 0.5;
              drawFeatheredFlameArc(
                c2d,
                Math.cos(a0) * arcR,
                Math.sin(a0) * arcR * 0.82,
                Math.cos(aMid) * arcR * 1.18,
                Math.sin(aMid) * arcR * 0.96,
                Math.cos(a1) * arcR,
                Math.sin(a1) * arcR * 0.82,
                2.8 * s,
                aIdx === 1 ? '251,113,133' : '255,87,34',
                0.44 * fade * pulse
              );
            }

            c2d.restore();
          },
        });

        // 2. 周身盘旋升起 9 缕赤橙/绛红/炽黄 wisp 噬魂火苗
        VFX.burst({
          x: safePt.x,
          y: safePt.y + 6 * s,
          radius: 19 * s,
          ring: true,
          tiltY: 0.55,
          count: 9,
          shape: 'wisp',
          colors: ['#ff5722', '#fb7185', '#fde047'],
          size: [3.2 * s, 5.6 * s],
          endSize: [1.2 * s, 2.2 * s],
          angle: [-HALF_PI - 0.45, -HALF_PI + 0.45],
          speed: [36 * s, 76 * s],
          vortex: 150 * s,
          vortexCenter: { x: safePt.x, y: safePt.y - 14 * s },
          gravity: -92 * s,
          turbulence: 46 * s,
          drag: 0.94,
          alpha: [0.52, 0.72],
          life: [280, 440],
        });

        // 3. 轻盈上飘 11 粒 ember 业火余烬
        VFX.burst({
          x: safePt.x,
          y: safePt.y + 4 * s,
          radius: 21 * s,
          count: 11,
          shape: 'ember',
          colors: ['#fde047', '#ff5722', '#fb7185', '#fb923c'],
          size: [1.6 * s, 3.0 * s],
          angle: [-HALF_PI - 0.58, -HALF_PI + 0.58],
          speed: [30 * s, 70 * s],
          vortex: -90 * s,
          vortexCenter: safePt,
          gravity: -82 * s,
          turbulence: 40 * s,
          drag: 0.95,
          alpha: [0.58, 0.8],
          life: [300, 460],
          trail: 2,
        });

        // 4. 1 圈轻柔向心收缩火星环
        VFX.shockwave({
          x: safePt.x,
          y: safePt.y,
          fromRadius: 38,
          toRadius: 13,
          tilt: 0.78,
          layer: 'sky',
          color: '#ea580c',
          coreColor: '#fb7185',
          lineWidth: 2.0,
          duration: 320,
          double: false,
        });

        // 5. 轻度受击橙闪与小型跳伤/灼烧浮字（禁止 impact/recoil/quake/cinematicDim）
        if (el) {
          VFX.hitFlash(el, '#fb923c', 140);
        }
        if (tickDmg !== null && tickDmg > 0) {
          VFX.floatText(el || safePt, '-' + Math.round(tickDmg), '#fb923c', {
            scale: 0.9,
            rise: 30,
            duration: 560,
          });
        } else {
          VFX.floatText(el || safePt, '灼烧', '#fb923c', {
            scale: 0.88,
            rise: 28,
            duration: 520,
          });
        }
      })(list[k], k);
    }

    return 480;
  });
});
