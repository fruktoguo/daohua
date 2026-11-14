/**
 * ability.chain-strike · 连环（火系一阶三段急斩 · 深度流体粒子化重构版）
 * 演出阶段（三段连击节奏递进，每段均配备 wisp 残风、轻脆 flare 火花与烈焰余烬）：
 *   0ms   第一斩：疾步斜劈火焰弧光 + 轻盈火风 wisp + 初燃轻脆 flare 火花
 *   140ms 第二斩：反手交叉上撩 + 炽焰涡旋 wisp 残风增强 + 交锋 flare 火花
 *   300ms 第三斩：蓄势挑斩绝杀 + 流火弧带 streamer + 烈焰 wisp 暴风 + 大号 flare 星芒与热浪环
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  function orientAngle(localAngle, axis) {
    return axis.facingX >= 0 ? axis.angle + localAngle : axis.angle - localAngle;
  }

  /**
   * 沿斩击弧线切向播撒烈焰 wisp 残风，营造刀风拉丝感
   */
  function emitArcWispWind(cx, cy, baseRadius, startAngle, endAngle, colors, count, sizeRange, speedRange, s) {
    var scaleMul = s !== undefined ? s : VFX.scale();
    var radius = baseRadius * scaleMul;
    var steps = Math.max(3, Math.round(count * 0.5));
    var sweepDir = endAngle >= startAngle ? 1 : -1;
    for (var i = 0; i < steps; i++) {
      var u = (i + 0.35) / steps;
      var ang = VFX.lerp(startAngle, endAngle, u);
      var px = cx + Math.cos(ang) * radius * 0.96;
      var py = cy + Math.sin(ang) * radius * 0.96;
      var tangent = ang + sweepDir * Math.PI * 0.5;
      VFX.burst({
        delay: i * 10,
        x: px,
        y: py,
        count: 2,
        shape: 'wisp',
        colors: colors,
        size: [sizeRange[0] * scaleMul, sizeRange[1] * scaleMul],
        baseAngle: tangent,
        spread: 0.65,
        speed: [speedRange[0] * scaleMul, speedRange[1] * scaleMul],
        turbulence: 42 * scaleMul,
        drag: 0.91,
        alpha: 0.82,
        life: [210, 390],
      });
    }
  }

  /**
   * 发射从施法者掌缘掠向目标中心的疾风火弧轨迹
   */
  function emitDashTrail(dashFrom, slashCenter, axis, s, arcSign, dur) {
    if (axis.dist <= 36) return;
    VFX.streamer({
      x1: dashFrom.x,
      y1: dashFrom.y,
      x2: slashCenter.x,
      y2: slashCenter.y,
      arc: arcSign * 14 * s,
      wobble: 3 * s,
      width: 9 * s,
      tailLength: 0.42,
      color: '#ff7b47',
      coreColor: '#fde047',
      sparkColors: ['#fde047', '#ff7b47', '#ffffff'],
      duration: dur || 170,
    });
    for (var tw = 0; tw < 2; tw++) {
      var wu = 0.3 + tw * 0.35;
      VFX.burst({
        delay: tw * 14,
        x: VFX.lerp(dashFrom.x, slashCenter.x, wu),
        y: VFX.lerp(dashFrom.y, slashCenter.y, wu),
        radius: 7 * s,
        count: 4,
        shape: 'wisp',
        colors: ['#ff7b47', '#fde047', '#fb923c'],
        size: [3.0 * s, 5.8 * s],
        baseAngle: axis.angle,
        spread: 0.45,
        speed: [145 * s, 270 * s],
        drag: 0.92,
        alpha: 0.78,
        life: [170, 290],
      });
    }
  }

  VFX.register('ability.chain-strike', function (ctx) {
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
    var dashFrom = axis.palm(14 * s);
    var sideSign = axis.facingX >= 0 ? 1 : -1;

    var totalDmg = ctx.perTargetDamage != null ? Math.round(ctx.perTargetDamage) : (typeof ctx.damage === 'number' ? Math.round(ctx.damage) : null);
    var dmg1 = totalDmg !== null ? Math.max(1, Math.round(totalDmg * 0.3)) : null;
    var dmg2 = totalDmg !== null ? Math.max(1, Math.round(totalDmg * 0.3)) : null;
    var dmg3 = totalDmg !== null ? Math.max(1, totalDmg - dmg1 - dmg2) : null;

    // -------------------------------------------------------------------------
    // 第一段（0ms）：疾步斜劈火焰弧光 + 掌缘疾风火轨 + 火焰 wisp 残风
    // -------------------------------------------------------------------------
    if (casterEl) {
      VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 22, duration: 150 });
    }
    emitDashTrail(dashFrom, slashCenter, axis, s, -sideSign, 170);

    var sa1 = orientAngle(-1.38, axis);
    var ea1 = orientAngle(0.84, axis);
    var sx1 = slashCenter.x - axis.ux * 10 * s;
    var sy1 = slashCenter.y - axis.uy * 10 * s - 4 * s;
    VFX.slash({
      x: sx1,
      y: sy1,
      radius: 78,
      thickness: 19,
      startAngle: sa1,
      endAngle: ea1,
      color: '#ff7b47',
      glowColor: '#ea580c',
      coreColor: '#fff7ed',
      duration: 210,
    });
    VFX.slash({
      x: slashCenter.x - axis.ux * 14 * s,
      y: slashCenter.y - axis.uy * 14 * s - 2 * s,
      delay: 16,
      radius: 84,
      thickness: 11,
      startAngle: orientAngle(-1.32, axis),
      endAngle: orientAngle(0.76, axis),
      color: '#fde047',
      glowColor: '#ff7b47',
      coreColor: '#ffffff',
      afterimage: true,
      duration: 180,
    });
    emitArcWispWind(
      sx1,
      sy1,
      78,
      sa1,
      ea1,
      ['#ff7b47', '#fde047', '#fb923c'],
      8,
      [3.2, 5.8],
      [80, 175],
      s
    );
    VFX.burst({
      x: slashCenter.x,
      y: slashCenter.y,
      radius: 10 * s,
      count: 3,
      shape: 'flare',
      colors: ['#fde047', '#ffffff', '#ff7b47'],
      size: [2.8 * s, 4.6 * s],
      speed: [24 * s, 72 * s],
      spin: [-4, 4],
      life: [160, 270],
    });
    VFX.burst({
      x: slashCenter.x,
      y: slashCenter.y,
      count: 14,
      shape: 'spark',
      colors: ['#ff7b47', '#fde047', '#ffffff'],
      size: [2.0 * s, 4.2 * s],
      speed: [95 * s, 225 * s],
      drag: 0.92,
      life: [190, 360],
      trail: 4,
    });
    VFX.burst({
      x: slashCenter.x,
      y: slashCenter.y,
      count: 8,
      shape: 'ember',
      colors: ['#ff7b47', '#f59e0b'],
      size: [2.2 * s, 4.2 * s],
      speed: [35 * s, 95 * s],
      gravity: -85 * s,
      life: [280, 480],
    });
    VFX.impact(targetEl, { color: '#ff9a3c', distance: 9, duration: 130 });
    if (dmg1 !== null) {
      VFX.floatText(targetEl, '-' + dmg1, 'damage', { scale: 0.94 });
    }

    // -------------------------------------------------------------------------
    // 第二段（140ms）：反手交叉上撩，连击提速递进
    // -------------------------------------------------------------------------
    VFX.after(140, function () {
      if (casterEl) {
        VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 26, duration: 145 });
      }
      emitDashTrail(dashFrom, slashCenter, axis, s, sideSign, 165);

      var sa2 = orientAngle(1.18, axis);
      var ea2 = orientAngle(-1.12, axis);
      var sx2 = slashCenter.x - axis.ux * 6 * s;
      var sy2 = slashCenter.y - axis.uy * 6 * s + 4 * s;
      VFX.slash({
        x: sx2,
        y: sy2,
        radius: 84,
        thickness: 21,
        startAngle: sa2,
        endAngle: ea2,
        color: '#fb923c',
        glowColor: '#ef4444',
        coreColor: '#fef08a',
        duration: 210,
      });
      VFX.slash({
        x: slashCenter.x - axis.ux * 12 * s,
        y: slashCenter.y - axis.uy * 12 * s + 6 * s,
        delay: 14,
        radius: 90,
        thickness: 12,
        startAngle: orientAngle(1.08, axis),
        endAngle: orientAngle(-1.02, axis),
        color: '#fde047',
        glowColor: '#ff7b47',
        coreColor: '#ffffff',
        duration: 185,
      });
      emitArcWispWind(
        sx2,
        sy2,
        84,
        sa2,
        ea2,
        ['#fb923c', '#fde047', '#ef4444'],
        10,
        [3.6, 6.8],
        [95, 205],
        s
      );
      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y,
        radius: 12 * s,
        count: 4,
        shape: 'flare',
        colors: ['#ffffff', '#fde047', '#ff7b47'],
        size: [3.4 * s, 5.4 * s],
        speed: [28 * s, 86 * s],
        spin: [-5, 5],
        life: [180, 300],
      });
      VFX.shockwave({
        x: slashCenter.x,
        y: slashCenter.y,
        fromRadius: 8,
        toRadius: 58,
        color: '#fb923c',
        coreColor: '#fef08a',
        lineWidth: 2.6,
        duration: 240,
        double: false,
      });
      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y,
        count: 18,
        shape: 'spark',
        colors: ['#fde047', '#ff7b47', '#ffffff'],
        size: [2.2 * s, 4.6 * s],
        speed: [115 * s, 265 * s],
        drag: 0.92,
        life: [210, 390],
        trail: 4,
      });
      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y,
        count: 10,
        shape: 'ember',
        colors: ['#fb923c', '#fde047'],
        size: [2.4 * s, 4.4 * s],
        speed: [40 * s, 115 * s],
        gravity: -92 * s,
        life: [320, 540],
      });
      VFX.impact(targetEl, { color: '#fde047', distance: 12, duration: 140 });
      if (dmg2 !== null) {
        VFX.floatText(targetEl, '-' + dmg2, 'damage', { scale: 1.02 });
      }
    });

    // -------------------------------------------------------------------------
    // 第三段（300ms）：蓄势挑斩终结 + 流火弧流 + 炎浪 wisp 暴涌 + 灼烧星芒
    // -------------------------------------------------------------------------
    VFX.after(300, function () {
      if (casterEl) {
        VFX.motion(casterEl, 'slam', { dx: axis.ux, dy: axis.uy, duration: 260 });
      }

      // 沉重炽炎挑斩主弧与白金核心刃光
      var sa3 = orientAngle(1.42, axis);
      var ea3 = orientAngle(-1.38, axis);
      var sx3 = slashCenter.x - axis.ux * 8 * s;
      var sy3 = slashCenter.y - axis.uy * 8 * s - 4 * s;
      VFX.slash({
        x: sx3,
        y: sy3,
        radius: 102,
        tilt: 0.95,
        thickness: 30,
        startAngle: sa3,
        endAngle: ea3,
        color: '#ef4444',
        glowColor: '#f59e0b',
        coreColor: '#fffbeb',
        duration: 280,
      });
      VFX.slash({
        delay: 16,
        x: slashCenter.x - axis.ux * 4 * s,
        y: slashCenter.y - axis.uy * 4 * s - 6 * s,
        radius: 94,
        tilt: 0.95,
        thickness: 16,
        startAngle: sa3,
        endAngle: ea3,
        color: '#fde047',
        glowColor: '#ff7b47',
        coreColor: '#ffffff',
        duration: 240,
      });

      // 沿 2D 攻击坐标系斜挑的弧形流光丝带（代替硬直线并安全钳制）
      var stFrom = VFX.clampStage({
        x: slashCenter.x - axis.ux * 58 * s + axis.nx * 46 * s * sideSign,
        y: slashCenter.y - axis.uy * 58 * s + axis.ny * 46 * s * sideSign,
      }, 22 * s, 22 * s);
      var stTo = VFX.clampStage({
        x: slashCenter.x + axis.ux * 64 * s - axis.nx * 54 * s * sideSign,
        y: slashCenter.y + axis.uy * 64 * s - axis.ny * 54 * s * sideSign,
      }, 22 * s, 22 * s);
      VFX.streamer({
        x1: stFrom.x,
        y1: stFrom.y,
        x2: stTo.x,
        y2: stTo.y,
        arc: -sideSign * 26 * s,
        wobble: 4 * s,
        width: 11 * s,
        tailLength: 0.45,
        color: '#ff7b47',
        coreColor: '#ffffff',
        sparkColors: ['#fde047', '#ff7b47', '#ffffff'],
        duration: 240,
      });

      emitArcWispWind(
        sx3,
        sy3,
        98,
        sa3,
        ea3,
        ['#ef4444', '#ff7b47', '#fde047', '#fff7ed'],
        14,
        [4.2, 8.2],
        [115, 245],
        s
      );

      // 灼烧冲击环（体表 + 脚下热浪）
      VFX.shockwave({
        x: slashCenter.x,
        y: slashCenter.y,
        fromRadius: 10,
        toRadius: 92,
        color: '#ff7b47',
        coreColor: '#fde047',
        lineWidth: 3.8,
        duration: 330,
        double: true,
      });
      VFX.shockwave({
        x: f.x,
        y: f.y,
        tilt: 0.44,
        fromRadius: 12,
        toRadius: 82,
        color: '#ea580c',
        coreColor: '#fde047',
        lineWidth: 2.6,
        duration: 360,
        double: false,
      });

      // 终结瞬爆多向 flare 星芒
      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y,
        radius: 10 * s,
        count: 6,
        shape: 'flare',
        colors: ['#ffffff', '#fde047', '#ff7b47'],
        size: [4.2 * s, 7.6 * s],
        speed: [24 * s, 95 * s],
        spin: [-4.5, 4.5],
        life: [220, 390],
      });

      // 高密度爆裂火花 + 升腾余烬 + 热浪烟尘
      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y,
        radius: 10 * s,
        count: 28,
        shape: 'spark',
        colors: ['#ff7b47', '#fde047', '#ffffff'],
        size: [2.4 * s, 5.0 * s],
        speed: [135 * s, 315 * s],
        drag: 0.92,
        life: [250, 480],
        trail: 5,
      });
      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y + 6 * s,
        radius: 18 * s,
        count: 18,
        shape: 'ember',
        colors: ['#ff7b47', '#f59e0b', '#fef08a'],
        size: [2.6 * s, 5.2 * s],
        speed: [45 * s, 138 * s],
        gravity: -105 * s,
        turbulence: 52 * s,
        life: [440, 780],
        trail: 3,
      });
      VFX.burst({
        x: slashCenter.x,
        y: slashCenter.y + 10 * s,
        radius: 16 * s,
        count: 8,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#451a03', '#7c2d12'],
        size: [11 * s, 20 * s],
        speed: [35 * s, 85 * s],
        gravity: -32 * s,
        alpha: 0.36,
        life: [380, 660],
      });

      VFX.quake('sm', 220);
      VFX.impact(targetEl, { color: '#ffedd5', distance: 16, duration: 240 });
      if (dmg3 !== null) {
        VFX.floatText(targetEl, '-' + dmg3, ctx.crit ? 'crit' : 'damage', {
          scale: ctx.crit ? 1.28 : 1.1,
        });
      }
    });
  });
});
