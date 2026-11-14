/**
 * 道化太初 · 演武画廊 · 战斗丹药特效组 (v2.2 自适应坐标与流体粒子版)
 *
 * 覆盖全量 6 种战斗丹药（按 0 阶 → 1 阶 → 2 阶呈阶位递进流体粒子美学）：
 *   0 阶：item.zhixue-powder（止血散）、item.shunqi-pill（顺气丸）
 *   1 阶：item.shengji-balm（生肌膏）、item.ningqi-powder（凝气散）
 *   2 阶：item.huangya-pill（黄芽丹）、item.heqi-pill（合气丹）
 *
 * 视觉规范：零硬几何框线、零直筒光柱、零硬菱形，全量采用 wisp/flare/orb/petal/smoke 流体粒子构建；
 *           全部延时分步回调实时重取施法者锚点与 VFX.scale()，并以 VFX.clampStage 护边防裁切。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  /** 解析施法者 DOM 节点（兼容标准 ctx.casterEl 与历史 ctx.caster.el） */
  function resolveCasterEl(ctx) {
    return ctx.casterEl || (ctx.caster && ctx.caster.el) || null;
  }

  /** 读取配表兜底或上下文传入的数值 */
  function resolveAmount(ctx, key, fallback) {
    var val = ctx && ctx[key];
    return typeof val === 'number' && val > 0 ? Math.round(val) : fallback;
  }

  /** 实时读取施法者锚点与当前画幅缩放比例，对靠近舞台左侧/边缘的单位执行安全内缩 */
  function resolveAnchors(casterEl) {
    var s = VFX.scale();
    var padX = 36 * s;
    var padY = 26 * s;
    return {
      s: s,
      c: VFX.clampStage(VFX.center(casterEl), padX, padY),
      h: VFX.clampStage(VFX.head(casterEl), padX, padY),
      f: VFX.clampStage(VFX.foot(casterEl), padX, padY),
    };
  }

  /**
   * 1. item.zhixue-powder · 止血散（0 阶疗伤：30s 回 36 血 + 5 双减）
   * 暖金药粉星尘柔缓撒落并向伤口 attractTo 汇集止血，点缀轻柔翠金星芒 flare 与微光 orb。
   */
  VFX.register('item.zhixue-powder', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var a0 = resolveAnchors(casterEl);
    var s0 = a0.s;
    var healAmount = resolveAmount(ctx, 'heal', 36);

    VFX.motion(casterEl, 'cast', { duration: 380 });

    // 灵丹自胸前旋升，弧线盘旋入体
    VFX.artifact({
      kind: 'pill',
      x1: a0.c.x,
      y1: a0.c.y + 8 * s0,
      x2: a0.h.x,
      y2: a0.h.y + 12 * s0,
      arc: -48 * s0,
      duration: 400,
      size: 18 * s0,
      scaleFrom: 0.65,
      scaleTo: 1.0,
      spin: 4.8,
      color: '#34d399',
      accentColor: '#fef3c7',
      glowColor: '#fbbf24',
    });

    // 第一拍：暖金药粉星尘自肩头上空柔缓撒落
    VFX.after(180, function () {
      var a1 = resolveAnchors(casterEl);
      var s1 = a1.s;
      var dustPt = VFX.clampStage({ x: a1.h.x, y: a1.h.y - 10 * s1 }, 36 * s1, 18 * s1);

      VFX.burst({
        x: dustPt.x,
        y: dustPt.y,
        radius: 36 * s1,
        count: 24,
        shape: 'orb',
        colors: ['#fde68a', '#fbbf24', '#bbf7d0', '#fef9c3'],
        size: [1.6 * s1, 3.2 * s1],
        speed: [14 * s1, 42 * s1],
        angle: [Math.PI * 0.2, Math.PI * 0.8],
        gravity: 38 * s1,
        turbulence: 46 * s1,
        drag: 0.96,
        alpha: 0.82,
        life: [520, 860],
        trail: 3,
      });

      VFX.burst({
        x: dustPt.x,
        y: dustPt.y + 4 * s1,
        radius: 28 * s1,
        count: 14,
        shape: 'ember',
        colors: ['#fbbf24', '#f59e0b', '#86efac'],
        size: [1.5 * s1, 2.8 * s1],
        speed: [12 * s1, 34 * s1],
        gravity: 26 * s1,
        turbulence: 38 * s1,
        drag: 0.96,
        life: [480, 780],
      });
    });

    // 第二拍：药粉星尘向伤口 attractTo 汇集止血，点缀轻柔翠金星芒 flare 与微光 orb
    VFX.after(340, function () {
      var a2 = resolveAnchors(casterEl);
      var s2 = a2.s;
      var c2 = a2.c;
      var f2 = a2.f;

      VFX.hitFlash(casterEl, '#86efac', 220);

      // 外圈暖金翠玉药粉向伤口核心柔和收束
      VFX.burst({
        x: c2.x,
        y: c2.y,
        radius: 62 * s2,
        ring: true,
        tiltY: 0.72,
        count: 26,
        shape: 'orb',
        colors: ['#fde68a', '#4ade80', '#bbf7d0', '#fbbf24'],
        size: [1.8 * s2, 3.4 * s2],
        speed: [14 * s2, 34 * s2],
        vortex: 140 * s2,
        vortexCenter: c2,
        attractTo: c2,
        attractStrength: 540 * s2,
        drag: 0.91,
        life: [380, 580],
        trail: 4,
      });

      // 伤处温润翠金流雾轻舒
      VFX.burst({
        x: c2.x,
        y: c2.y + 4 * s2,
        radius: 24 * s2,
        count: 14,
        shape: 'wisp',
        colors: ['#6ee7b7', '#fde68a', '#a7f3d0'],
        size: [2.4 * s2, 4.4 * s2],
        speed: [22 * s2, 58 * s2],
        gravity: -62 * s2,
        turbulence: 36 * s2,
        drag: 0.95,
        alpha: 0.62,
        life: [480, 780],
        trail: 3,
      });

      // 点缀轻柔翠金星芒 flare 与微光 orb
      VFX.burst({
        x: c2.x,
        y: c2.y - 2 * s2,
        radius: 30 * s2,
        count: 9,
        shape: 'flare',
        colors: ['#fde68a', '#6ee7b7', '#fef9c3'],
        size: [1.8 * s2, 3.2 * s2],
        speed: [18 * s2, 48 * s2],
        gravity: -44 * s2,
        drag: 0.94,
        alpha: 0.88,
        life: [420, 720],
      });

      VFX.burst({
        x: c2.x,
        y: c2.y + 6 * s2,
        radius: 26 * s2,
        count: 18,
        shape: 'orb',
        colors: ['#86efac', '#fde68a', '#dcfce7'],
        size: [1.8 * s2, 3.8 * s2],
        speed: [20 * s2, 56 * s2],
        gravity: -72 * s2,
        drag: 0.95,
        life: [480, 780],
        trail: 3,
      });

      // 地面浅翠金柔光涟漪（core.js 内置缩放，不重复乘 s）
      VFX.shockwave({
        x: f2.x,
        y: f2.y,
        fromRadius: 12,
        toRadius: 72,
        tilt: 0.44,
        color: '#4ade80',
        coreColor: '#fef3c7',
        lineWidth: 2.8,
        double: true,
        duration: 460,
      });

      VFX.floatText(casterEl, '+' + healAmount + ' 止血', 'heal');
    });

    VFX.after(490, function () {
      VFX.floatText(casterEl, '双减 +5', 'shield', { scale: 0.88, rise: 36 });
    });

    return 1150;
  });

  /**
   * 2. item.shunqi-pill · 顺气丸（0 阶回蓝提速：30s 回 24 内力 + 5% 行动速度）
   * 通透翡翠丹雾 + 双股交叉柔顺上升 wisp 灵脉丝，疏通周身经络并散出细碎 petal / orb。
   */
  VFX.register('item.shunqi-pill', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var a0 = resolveAnchors(casterEl);
    var s0 = a0.s;
    var manaAmount = resolveAmount(ctx, 'heal', 24);

    VFX.motion(casterEl, 'cast', { duration: 340 });

    // 翡翠天蓝灵丸旋舞入体
    VFX.artifact({
      kind: 'pill',
      x1: a0.c.x,
      y1: a0.c.y + 6 * s0,
      x2: a0.h.x,
      y2: a0.h.y + 10 * s0,
      arc: -54 * s0,
      duration: 380,
      size: 18 * s0,
      scaleFrom: 0.68,
      scaleTo: 1.04,
      spin: 6.0,
      color: '#10b981',
      accentColor: '#d1fae5',
      glowColor: '#5eead4',
    });

    // 通透翡翠丹雾在周身铺开
    VFX.after(290, function () {
      var a1 = resolveAnchors(casterEl);
      var s1 = a1.s;

      VFX.hitFlash(casterEl, '#5eead4', 220);

      VFX.burst({
        x: a1.c.x,
        y: a1.c.y + 10 * s1,
        radius: 36 * s1,
        count: 10,
        shape: 'smoke',
        colors: ['#10b981', '#2dd4bf', '#38bdf8'],
        size: [10 * s1, 17 * s1],
        speed: [14 * s1, 38 * s1],
        gravity: -45 * s1,
        drag: 0.94,
        alpha: 0.36,
        life: [540, 860],
      });

      VFX.shockwave({
        x: a1.f.x,
        y: a1.f.y,
        fromRadius: 12,
        toRadius: 76,
        tilt: 0.44,
        color: '#34d399',
        coreColor: '#e0f2fe',
        lineWidth: 2.6,
        double: false,
        duration: 420,
      });
    });

    // 双股交叉柔顺上升 wisp 灵脉丝，疏通周身经络（每步实时重取施法者锚点）
    var stepCount = 7;
    var stepInterval = 38;
    for (var sIdx = 0; sIdx < stepCount; sIdx++) {
      (function (idx) {
        VFX.after(300 + idx * stepInterval, function () {
          var ai = resolveAnchors(casterEl);
          var si = ai.s;
          var u = idx / (stepCount - 1);
          var py = VFX.lerp(ai.f.y - 4 * si, ai.h.y - 10 * si, u);
          var rx = 28 * si * (1 - u * 0.25);
          var basePhase = u * Math.PI * 2.2;

          for (var strand = 0; strand < 2; strand++) {
            var ph = basePhase + strand * Math.PI;
            var pt = VFX.clampStage({ x: ai.c.x + Math.cos(ph) * rx, y: py }, 14 * si, 14 * si);
            var tangentAng = Math.atan2(-56 * si, -Math.sin(ph) * rx * 2.2);
            var col = strand === 0 ? '#34d399' : '#38bdf8';
            var sec = strand === 0 ? '#a7f3d0' : '#bae6fd';

            VFX.burst({
              x: pt.x,
              y: pt.y,
              radius: 4 * si,
              count: 2,
              shape: 'wisp',
              colors: [col, sec, '#ecfdf5'],
              size: [2.0 * si, 3.6 * si],
              angle: [tangentAng - 0.18, tangentAng + 0.18],
              speed: [75 * si, 118 * si],
              gravity: -40 * si,
              drag: 0.95,
              alpha: 0.78,
              life: [360, 560],
              trail: 5,
            });
          }
        });
      })(sIdx);
    }

    // 经络贯通后散出细碎翡翠 petal 与清亮 orb
    VFX.after(430, function () {
      var a2 = resolveAnchors(casterEl);
      var s2 = a2.s;

      VFX.burst({
        x: a2.c.x,
        y: a2.c.y,
        radius: 32 * s2,
        count: 18,
        shape: 'petal',
        colors: ['#6ee7b7', '#a7f3d0', '#bae6fd', '#ecfdf5'],
        size: [2.4 * s2, 4.4 * s2],
        speed: [32 * s2, 82 * s2],
        gravity: -78 * s2,
        turbulence: 48 * s2,
        spin: [-4.0, 4.0],
        drag: 0.95,
        life: [480, 820],
      });

      VFX.burst({
        x: a2.c.x,
        y: a2.c.y - 4 * s2,
        radius: 28 * s2,
        count: 22,
        shape: 'orb',
        colors: ['#34d399', '#7dd3fc', '#d1fae5', '#ffffff'],
        size: [1.6 * s2, 3.4 * s2],
        speed: [36 * s2, 98 * s2],
        gravity: -85 * s2,
        drag: 0.94,
        life: [420, 740],
        trail: 4,
      });

      VFX.floatText(casterEl, '+' + manaAmount + ' 顺气', 'mana');
    });

    VFX.after(580, function () {
      VFX.floatText(casterEl, '速度 +5%', '#6ee7b7', { scale: 0.88, rise: 36 });
    });

    return 1200;
  });

  /**
   * 3. item.shengji-balm · 生肌膏（1 阶高阶疗伤+双减：30s 回 72 血 + 15 双减）
   * 厚润生机玉髓螺旋覆身，温煦粉翠 wisp 贴体缠绕流转，大量再生微尘缓缓向上漂浮。
   */
  VFX.register('item.shengji-balm', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var a0 = resolveAnchors(casterEl);
    var s0 = a0.s;
    var healAmount = resolveAmount(ctx, 'heal', 72);

    VFX.motion(casterEl, 'cast', { duration: 420 });

    // 一阶粉翠玉髓灵丹旋升入体
    VFX.artifact({
      kind: 'pill',
      x1: a0.c.x,
      y1: a0.c.y + 6 * s0,
      x2: a0.h.x,
      y2: a0.h.y + 10 * s0,
      arc: -58 * s0,
      duration: 420,
      size: 21 * s0,
      scaleFrom: 0.72,
      scaleTo: 1.12,
      spin: 4.6,
      color: '#14b8a6',
      accentColor: '#fbcfe8',
      glowColor: '#5eead4',
    });

    // 第一拍：厚润玉髓螺旋覆身 + 温煦粉翠流体环抱
    VFX.after(350, function () {
      var a1 = resolveAnchors(casterEl);
      var s1 = a1.s;
      var c1 = a1.c;
      var f1 = a1.f;

      VFX.hitFlash(casterEl, '#5eead4', 240);

      // 柔和生机玉髓地面涟漪
      VFX.shockwave({
        x: f1.x,
        y: f1.y,
        fromRadius: 14,
        toRadius: 88,
        tilt: 0.44,
        color: '#2dd4bf',
        coreColor: '#fce7f3',
        lineWidth: 3.4,
        double: true,
        duration: 500,
      });

      // 厚润玉髓半透柔雾包裹身周
      VFX.burst({
        x: c1.x,
        y: c1.y + 4 * s1,
        radius: 34 * s1,
        count: 14,
        shape: 'smoke',
        colors: ['#2dd4bf', '#5eead4', '#f9a8d4'],
        size: [11 * s1, 19 * s1],
        speed: [12 * s1, 32 * s1],
        gravity: -35 * s1,
        drag: 0.94,
        alpha: 0.34,
        life: [580, 920],
      });

      // 外围粉翠玉髓螺旋流环贴体向内旋覆
      VFX.burst({
        x: c1.x,
        y: c1.y,
        radius: 50 * s1,
        ring: true,
        tiltY: 0.62,
        count: 24,
        shape: 'wisp',
        colors: ['#2dd4bf', '#99f6e4', '#f9a8d4', '#fbcfe8'],
        size: [3.2 * s1, 5.8 * s1],
        speed: [22 * s1, 48 * s1],
        vortex: 310 * s1,
        vortexCenter: c1,
        attractTo: c1,
        attractStrength: 460 * s1,
        drag: 0.93,
        alpha: 0.72,
        life: [520, 820],
        trail: 6,
      });

      // 反向第二股贴身玉髓缠绕流转
      VFX.burst({
        x: c1.x,
        y: c1.y - 6 * s1,
        radius: 42 * s1,
        ring: true,
        tiltY: 0.76,
        count: 18,
        shape: 'wisp',
        colors: ['#f9a8d4', '#5eead4', '#ccfbf1'],
        size: [2.8 * s1, 5.2 * s1],
        speed: [20 * s1, 44 * s1],
        vortex: -280 * s1,
        vortexCenter: c1,
        attractTo: c1,
        attractStrength: 420 * s1,
        drag: 0.93,
        alpha: 0.68,
        life: [520, 800],
        trail: 5,
      });

      VFX.floatText(casterEl, '+' + healAmount + ' 生肌', 'heal', { scale: 1.08 });
    });

    // 温煦粉翠 wisp 贴体缠绕流转轨迹（分段切向流丝，每步重取施法者锚点）
    var wrapSteps = 8;
    for (var w = 0; w < wrapSteps; w++) {
      (function (idx) {
        VFX.after(370 + idx * 42, function () {
          var ai = resolveAnchors(casterEl);
          var si = ai.s;
          var u = idx / (wrapSteps - 1);
          var ang = u * TWO_PI * 1.15;
          var rx = 32 * si;
          var ry = 24 * si;
          for (var arm = 0; arm < 2; arm++) {
            var a = ang + arm * Math.PI;
            var pt = VFX.clampStage(
              {
                x: ai.c.x + Math.cos(a) * rx,
                y: ai.c.y + 12 * si - u * 28 * si + Math.sin(a) * ry * 0.6,
              },
              14 * si,
              14 * si
            );
            var tang = a + Math.PI * 0.45;
            VFX.burst({
              x: pt.x,
              y: pt.y,
              radius: 5 * si,
              count: 2,
              shape: 'wisp',
              colors: arm === 0 ? ['#5eead4', '#ccfbf1'] : ['#f9a8d4', '#fbcfe8'],
              size: [2.6 * si, 4.8 * si],
              angle: [tang - 0.25, tang + 0.25],
              speed: [58 * si, 96 * si],
              gravity: -42 * si,
              drag: 0.94,
              alpha: 0.74,
              life: [420, 680],
              trail: 5,
            });
          }
        });
      })(w);
    }

    // 大量再生微尘缓缓向上漂浮（两波交错延绵）
    VFX.after(430, function () {
      var a2 = resolveAnchors(casterEl);
      var s2 = a2.s;
      var c2 = a2.c;

      VFX.burst({
        x: c2.x,
        y: c2.y + 16 * s2,
        radius: 42 * s2,
        count: 34,
        shape: 'orb',
        colors: ['#5eead4', '#99f6e4', '#fbcfe8', '#fef3c7'],
        size: [1.6 * s2, 3.6 * s2],
        speed: [16 * s2, 46 * s2],
        angle: [-Math.PI * 0.8, -Math.PI * 0.2],
        gravity: -52 * s2,
        turbulence: 44 * s2,
        drag: 0.96,
        alpha: 0.85,
        life: [720, 1180],
        trail: 3,
      });

      VFX.burst({
        x: c2.x,
        y: c2.y + 8 * s2,
        radius: 36 * s2,
        count: 16,
        shape: 'petal',
        colors: ['#5eead4', '#f9a8d4', '#ccfbf1'],
        size: [2.6 * s2, 4.8 * s2],
        speed: [18 * s2, 52 * s2],
        angle: [-Math.PI * 0.85, -Math.PI * 0.15],
        gravity: -46 * s2,
        turbulence: 52 * s2,
        spin: [-3.2, 3.2],
        drag: 0.96,
        life: [680, 1120],
      });

      VFX.burst({
        x: c2.x,
        y: c2.y - 4 * s2,
        radius: 32 * s2,
        count: 8,
        shape: 'flare',
        colors: ['#99f6e4', '#fbcfe8', '#ffffff'],
        size: [1.8 * s2, 3.4 * s2],
        speed: [14 * s2, 38 * s2],
        gravity: -38 * s2,
        drag: 0.96,
        alpha: 0.84,
        life: [540, 880],
      });
    });

    VFX.after(550, function () {
      VFX.floatText(casterEl, '双减 +15', 'shield', { scale: 0.96, rise: 40 });
    });

    return 1350;
  });

  /**
   * 4. item.ningqi-powder · 凝气散（1 阶回蓝+10%速：30s 回 48 内力 + 10% 行动速度）
   * 天地散逸灵微被中央 attractTo 强劲涡旋吸聚而来，转为靛青水雾凝实脉冲。
   */
  VFX.register('item.ningqi-powder', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var a0 = resolveAnchors(casterEl);
    var s0 = a0.s;
    var manaAmount = resolveAmount(ctx, 'heal', 48);

    VFX.motion(casterEl, 'cast', { duration: 360 });

    // 一阶靛青晶丸疾旋升空
    VFX.artifact({
      kind: 'pill',
      x1: a0.c.x,
      y1: a0.c.y + 4 * s0,
      x2: a0.h.x,
      y2: a0.h.y + 8 * s0,
      arc: -62 * s0,
      duration: 380,
      size: 21 * s0,
      scaleFrom: 0.72,
      scaleTo: 1.14,
      spin: 7.4,
      color: '#3b82f6',
      accentColor: '#dbeafe',
      glowColor: '#60a5fa',
    });

    // 第一拍：天地散逸灵微被中央 attractTo 强劲涡旋吸聚而来
    VFX.after(240, function () {
      var a1 = resolveAnchors(casterEl);
      var s1 = a1.s;
      var c1 = a1.c;

      // 外层天地灵微（大半径逆时针涡旋高速向内吸聚）
      VFX.burst({
        x: c1.x,
        y: c1.y - 4 * s1,
        radius: 122 * s1,
        ring: true,
        tiltY: 0.68,
        count: 32,
        shape: 'wisp',
        colors: ['#38bdf8', '#6366f1', '#93c5fd', '#e0f2fe'],
        size: [2.2 * s1, 4.2 * s1],
        speed: [18 * s1, 42 * s1],
        vortex: -420 * s1,
        vortexCenter: c1,
        attractTo: c1,
        attractStrength: 880 * s1,
        drag: 0.91,
        alpha: 0.8,
        life: [420, 620],
        trail: 6,
      });

      // 散布天地的游离灵尘微粒向中心聚拢
      VFX.burst({
        x: c1.x,
        y: c1.y,
        radius: 96 * s1,
        tiltY: 0.76,
        count: 28,
        shape: 'orb',
        colors: ['#60a5fa', '#a5b4fc', '#e0f2fe', '#ffffff'],
        size: [1.6 * s1, 3.4 * s1],
        speed: [12 * s1, 30 * s1],
        vortex: -360 * s1,
        vortexCenter: c1,
        attractTo: c1,
        attractStrength: 820 * s1,
        drag: 0.92,
        life: [400, 600],
        trail: 5,
      });
    });

    // 第二拍：汇流凝实，转为靛青水雾凝实脉冲
    VFX.after(460, function () {
      var a2 = resolveAnchors(casterEl);
      var s2 = a2.s;
      var c2 = a2.c;
      var f2 = a2.f;

      VFX.hitFlash(casterEl, '#60a5fa', 240);
      VFX.motion(casterEl, 'dodge', { duration: 280 });

      // 靛青水雾在胸口与周身凝实炸开
      VFX.burst({
        x: c2.x,
        y: c2.y,
        radius: 24 * s2,
        count: 16,
        shape: 'smoke',
        colors: ['#1e40af', '#0284c7', '#38bdf8', '#4f46e5'],
        size: [12 * s2, 22 * s2],
        speed: [26 * s2, 72 * s2],
        drag: 0.92,
        alpha: 0.44,
        life: [460, 780],
      });

      // 凝实靛青水雾灵流向外舒展荡开
      VFX.burst({
        x: c2.x,
        y: c2.y,
        radius: 18 * s2,
        count: 24,
        shape: 'wisp',
        colors: ['#38bdf8', '#818cf8', '#bae6fd', '#ffffff'],
        size: [2.8 * s2, 5.2 * s2],
        speed: [68 * s2, 165 * s2],
        vortex: 160 * s2,
        vortexCenter: c2,
        drag: 0.92,
        alpha: 0.78,
        life: [380, 660],
        trail: 5,
      });

      // 中心清冽脉冲波（天空与地面双层羽化呼应）
      VFX.shockwave({
        x: c2.x,
        y: c2.y,
        fromRadius: 12,
        toRadius: 96,
        color: '#6366f1',
        coreColor: '#e0f2fe',
        lineWidth: 4.2,
        double: true,
        duration: 420,
      });

      VFX.shockwave({
        x: f2.x,
        y: f2.y,
        fromRadius: 14,
        toRadius: 90,
        tilt: 0.44,
        color: '#38bdf8',
        coreColor: '#dbeafe',
        lineWidth: 3.4,
        double: true,
        duration: 440,
      });

      // 靛青星芒 flare 点缀提速锐气
      VFX.burst({
        x: c2.x,
        y: c2.y,
        radius: 26 * s2,
        count: 10,
        shape: 'flare',
        colors: ['#93c5fd', '#c7d2fe', '#ffffff'],
        size: [2.0 * s2, 3.8 * s2],
        speed: [48 * s2, 118 * s2],
        drag: 0.92,
        life: [360, 620],
      });

      VFX.floatText(casterEl, '+' + manaAmount + ' 凝气', 'mana', { scale: 1.08 });
    });

    VFX.after(600, function () {
      VFX.floatText(casterEl, '速度 +10%', '#93c5fd', { scale: 0.96, rise: 42 });
    });

    return 1300;
  });

  /**
   * 5. item.huangya-pill · 黄芽丹（2 阶大药，30s 回 150 血 + 40 固定双减）
   * 「丹芽吐翠」全粒子意象：脚下暖金厚土柔雾盘旋，三股金琥珀 wisp 螺旋藤蔓贴身攀升开花，
   * 顶心绽放温暖耀眼的金辉 flare 星华与大片金色草木芽粒 orb / petal。
   */
  VFX.register('item.huangya-pill', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var a0 = resolveAnchors(casterEl);
    var s0 = a0.s;
    var healAmount = resolveAmount(ctx, 'heal', 150);
    var hoverPt0 = VFX.clampStage({ x: a0.h.x, y: a0.h.y - 16 * s0 }, 36 * s0, 20 * s0);

    // 二阶大药聚光暗幕与轻浮起身
    VFX.cinematicDim(0.16, 660);
    VFX.motion(casterEl, 'levitate', { duration: 540 });

    // 煌煌先天黄金大丹升空悬照
    VFX.artifact({
      kind: 'pill',
      x1: a0.c.x,
      y1: a0.c.y,
      x2: hoverPt0.x,
      y2: hoverPt0.y,
      arc: -72 * s0,
      duration: 480,
      size: 25 * s0,
      scaleFrom: 0.78,
      scaleTo: 1.28,
      spin: 5.4,
      color: '#f59e0b',
      accentColor: '#fef3c7',
      glowColor: '#fbbf24',
    });

    // 第一拍：脚下暖金厚土柔雾盘旋孕育
    VFX.after(260, function () {
      var a1 = resolveAnchors(casterEl);
      var s1 = a1.s;
      var f1 = a1.f;

      VFX.burst({
        x: f1.x,
        y: f1.y,
        radius: 44 * s1,
        tiltY: 0.44,
        count: 14,
        shape: 'smoke',
        colors: ['#b45309', '#d97706', '#f59e0b'],
        size: [12 * s1, 20 * s1],
        speed: [14 * s1, 34 * s1],
        vortex: 160 * s1,
        vortexCenter: f1,
        drag: 0.94,
        alpha: 0.36,
        life: [680, 1080],
        layer: 'ground',
      });

      VFX.burst({
        x: f1.x,
        y: f1.y - 2 * s1,
        radius: 52 * s1,
        ring: true,
        tiltY: 0.44,
        count: 20,
        shape: 'wisp',
        colors: ['#f59e0b', '#fbbf24', '#fde68a'],
        size: [2.8 * s1, 5.2 * s1],
        speed: [18 * s1, 44 * s1],
        vortex: 240 * s1,
        vortexCenter: f1,
        attractTo: f1,
        attractStrength: 240 * s1,
        drag: 0.94,
        alpha: 0.68,
        life: [620, 980],
        trail: 4,
      });

      VFX.shockwave({
        x: f1.x,
        y: f1.y,
        fromRadius: 12,
        toRadius: 96,
        tilt: 0.44,
        color: '#f59e0b',
        coreColor: '#fef3c7',
        lineWidth: 3.6,
        double: true,
        duration: 540,
      });
    });

    // 第二拍：三股金琥珀 wisp 螺旋藤蔓贴身攀升开花（12 步高频引导，实时重取锚点）
    var vineSteps = 12;
    var vineStepMs = 34;
    for (var step = 0; step < vineSteps; step++) {
      (function (idx) {
        VFX.after(340 + idx * vineStepMs, function () {
          var ai = resolveAnchors(casterEl);
          var si = ai.s;
          var crownPt = VFX.clampStage({ x: ai.h.x, y: ai.h.y - 24 * si }, 36 * si, 22 * si);
          var u = idx / (vineSteps - 1);
          var py = VFX.lerp(ai.f.y + 2 * si, crownPt.y, u);
          var r = (VFX.lerp(36, 10, u * 0.72) + Math.sin(u * Math.PI) * 14) * si;
          var spinAngle = u * Math.PI * 2.6;

          for (var branch = 0; branch < 3; branch++) {
            var a = spinAngle + (branch * TWO_PI) / 3;
            var pt = VFX.clampStage({ x: ai.c.x + Math.cos(a) * r, y: py }, 14 * si, 14 * si);
            var tang = Math.atan2(-64 * si, -Math.sin(a) * r * 2.6);

            // 金琥珀藤蔓流丝
            VFX.burst({
              x: pt.x,
              y: pt.y,
              radius: 4 * si,
              count: 2,
              shape: 'wisp',
              colors: ['#f59e0b', '#fbbf24', '#fde68a'],
              size: [2.4 * si, 4.2 * si],
              angle: [tang - 0.16, tang + 0.16],
              speed: [76 * si, 122 * si],
              gravity: -35 * si,
              drag: 0.95,
              alpha: 0.8,
              life: [380, 620],
              trail: 5,
            });

            // 沿藤蔓吐翠生芽（金色草木芽粒与嫩翠叶瓣）
            if (idx % 2 === 1) {
              VFX.burst({
                x: pt.x,
                y: pt.y,
                radius: 8 * si,
                count: 2,
                shape: branch === 0 ? 'petal' : 'orb',
                colors: branch === 0 ? ['#86efac', '#bbf7d0', '#fde047'] : ['#fde68a', '#fbbf24', '#fef9c3'],
                size: [2.2 * si, 4.0 * si],
                speed: [18 * si, 48 * si],
                gravity: -48 * si,
                turbulence: 40 * si,
                drag: 0.95,
                life: [520, 860],
              });
            }
          }
        });
      })(step);
    }

    // 第三拍：顶心绽放温暖耀眼的金辉 flare 星华与大片金色草木芽粒 orb / petal
    VFX.after(740, function () {
      var a2 = resolveAnchors(casterEl);
      var s2 = a2.s;
      var c2 = a2.c;
      var crown2 = VFX.clampStage({ x: a2.h.x, y: a2.h.y - 24 * s2 }, 36 * s2, 22 * s2);

      VFX.quake('sm', 170);
      VFX.hitFlash(casterEl, '#fbbf24', 280);

      // 顶心温暖绽放花冠柔波
      VFX.shockwave({
        x: crown2.x,
        y: crown2.y,
        fromRadius: 8,
        toRadius: 92,
        color: '#fbbf24',
        coreColor: '#ffffff',
        lineWidth: 3.8,
        double: true,
        duration: 500,
      });

      // 温暖耀眼的金辉 flare 星华
      VFX.burst({
        x: crown2.x,
        y: crown2.y,
        radius: 18 * s2,
        count: 14,
        shape: 'flare',
        colors: ['#fef08a', '#fbbf24', '#fde68a', '#ffffff'],
        size: [2.8 * s2, 5.6 * s2],
        speed: [24 * s2, 96 * s2],
        gravity: -18 * s2,
        drag: 0.93,
        alpha: 0.94,
        life: [520, 860],
      });

      // 大片金色草木芽粒 orb 自顶心花雨舒展护体
      VFX.burst({
        x: crown2.x,
        y: crown2.y + 8 * s2,
        radius: 26 * s2,
        count: 34,
        shape: 'orb',
        colors: ['#fbbf24', '#fde68a', '#fef3c7', '#bbf7d0'],
        size: [2.0 * s2, 4.6 * s2],
        speed: [42 * s2, 138 * s2],
        gravity: 34 * s2,
        turbulence: 54 * s2,
        drag: 0.94,
        life: [640, 1080],
        trail: 4,
      });

      // 大片嫩翠与暖金叶瓣 petal 漫卷飘落
      VFX.burst({
        x: crown2.x,
        y: crown2.y + 6 * s2,
        radius: 28 * s2,
        count: 24,
        shape: 'petal',
        colors: ['#fde047', '#fbbf24', '#86efac', '#fef3c7'],
        size: [3.0 * s2, 5.4 * s2],
        speed: [36 * s2, 118 * s2],
        gravity: 28 * s2,
        turbulence: 62 * s2,
        spin: [-4.2, 4.2],
        drag: 0.95,
        life: [680, 1140],
      });

      // 周身暖金余晖柔流护持
      VFX.burst({
        x: c2.x,
        y: c2.y,
        radius: 44 * s2,
        ring: true,
        tiltY: 0.65,
        count: 16,
        shape: 'wisp',
        colors: ['#fbbf24', '#fde68a', '#86efac'],
        size: [2.6 * s2, 4.6 * s2],
        speed: [18 * s2, 46 * s2],
        vortex: 210 * s2,
        vortexCenter: c2,
        gravity: -40 * s2,
        drag: 0.94,
        alpha: 0.65,
        life: [560, 920],
        trail: 4,
      });

      VFX.floatText(casterEl, '+' + healAmount + ' 黄芽回春', 'heal', { scale: 1.2 });
    });

    VFX.after(900, function () {
      VFX.floatText(casterEl, '双减 +40', 'shield', { scale: 1.06, rise: 46 });
    });

    return 1700;
  });

  /**
   * 6. item.heqi-pill · 合气丹（2 阶大药，30s 回 100 内力 + 15% 全伤）
   * 阴阳二气（温金阳气 + 澄澈灵青阴气）双螺旋高速绕体太极盘旋交融，
   * 形成通透立体的流体粒子气场球与漫天四溢星芒。
   */
  VFX.register('item.heqi-pill', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var a0 = resolveAnchors(casterEl);
    var s0 = a0.s;
    var manaAmount = resolveAmount(ctx, 'heal', 100);
    var hoverPt0 = VFX.clampStage({ x: a0.h.x, y: a0.h.y - 14 * s0 }, 36 * s0, 20 * s0);

    VFX.cinematicDim(0.18, 660);
    VFX.motion(casterEl, 'levitate', { duration: 520 });

    // 二阶太极双色灵丹升空悬照
    VFX.artifact({
      kind: 'pill',
      x1: a0.c.x,
      y1: a0.c.y,
      x2: hoverPt0.x,
      y2: hoverPt0.y,
      arc: -70 * s0,
      duration: 460,
      size: 25 * s0,
      scaleFrom: 0.78,
      scaleTo: 1.28,
      spin: 6.8,
      color: '#fbbf24',
      accentColor: '#bae6fd',
      glowColor: '#38bdf8',
    });

    // 第一拍：形成通透立体的流体粒子气场球（多层不同倾角环流编织）
    VFX.after(340, function () {
      var a1 = resolveAnchors(casterEl);
      var s1 = a1.s;
      var c1 = a1.c;

      VFX.hitFlash(casterEl, '#bae6fd', 240);

      var shellTilts = [0.42, 0.74, 0.96];
      for (var t = 0; t < shellTilts.length; t++) {
        VFX.burst({
          x: c1.x,
          y: c1.y,
          radius: 56 * s1,
          ring: true,
          tiltY: shellTilts[t],
          count: 14,
          shape: 'wisp',
          colors: t % 2 === 0 ? ['#fbbf24', '#fde68a', '#fef08a'] : ['#38bdf8', '#2dd4bf', '#bae6fd'],
          size: [2.6 * s1, 4.6 * s1],
          speed: [22 * s1, 48 * s1],
          vortex: (t === 1 ? -420 : 420) * s1,
          vortexCenter: c1,
          attractTo: c1,
          attractStrength: 440 * s1,
          drag: 0.94,
          alpha: 0.64,
          life: [580, 920],
          trail: 5,
        });
      }

      // 球心通透微光雾韵
      VFX.burst({
        x: c1.x,
        y: c1.y,
        radius: 36 * s1,
        count: 12,
        shape: 'smoke',
        colors: ['#0284c7', '#d97706', '#0d9488'],
        size: [12 * s1, 20 * s1],
        speed: [10 * s1, 26 * s1],
        drag: 0.95,
        alpha: 0.28,
        life: [580, 920],
      });
    });

    // 第二拍：阴阳二气（温金阳气 + 澄澈灵青阴气）双螺旋高速绕体太极盘旋交融
    var helixSteps = 12;
    var helixInterval = 34;
    for (var sIdx = 0; sIdx < helixSteps; sIdx++) {
      (function (idx) {
        VFX.after(360 + idx * helixInterval, function () {
          var ai = resolveAnchors(casterEl);
          var si = ai.s;
          var u = idx / (helixSteps - 1);
          var r = 48 * si * (0.45 + 0.55 * Math.sin(u * Math.PI * 0.85 + 0.2));
          var theta = u * TWO_PI * 1.8;

          // 温金阳气股
          var yangPt = VFX.clampStage(
            { x: ai.c.x + Math.cos(theta) * r, y: ai.c.y + Math.sin(theta) * r * 0.72 },
            14 * si,
            14 * si
          );
          var yangAng = theta + Math.PI * 0.52;
          VFX.burst({
            x: yangPt.x,
            y: yangPt.y,
            radius: 4 * si,
            count: 2,
            shape: 'wisp',
            colors: ['#fbbf24', '#f59e0b', '#fef08a'],
            size: [2.6 * si, 4.6 * si],
            angle: [yangAng - 0.16, yangAng + 0.16],
            speed: [95 * si, 148 * si],
            drag: 0.94,
            alpha: 0.84,
            life: [360, 560],
            trail: 6,
          });
          VFX.burst({
            x: yangPt.x,
            y: yangPt.y,
            radius: 6 * si,
            count: 2,
            shape: 'orb',
            colors: ['#fde68a', '#ffffff'],
            size: [1.8 * si, 3.2 * si],
            angle: [yangAng - 0.25, yangAng + 0.25],
            speed: [75 * si, 125 * si],
            drag: 0.94,
            life: [340, 520],
            trail: 4,
          });

          // 澄澈灵青阴气股（反相 180°）
          var yinA = theta + Math.PI;
          var yinPt = VFX.clampStage(
            { x: ai.c.x + Math.cos(yinA) * r, y: ai.c.y + Math.sin(yinA) * r * 0.72 },
            14 * si,
            14 * si
          );
          var yinAng = yinA + Math.PI * 0.52;
          VFX.burst({
            x: yinPt.x,
            y: yinPt.y,
            radius: 4 * si,
            count: 2,
            shape: 'wisp',
            colors: ['#38bdf8', '#2dd4bf', '#bae6fd'],
            size: [2.6 * si, 4.6 * si],
            angle: [yinAng - 0.16, yinAng + 0.16],
            speed: [95 * si, 148 * si],
            drag: 0.94,
            alpha: 0.84,
            life: [360, 560],
            trail: 6,
          });
          VFX.burst({
            x: yinPt.x,
            y: yinPt.y,
            radius: 6 * si,
            count: 2,
            shape: 'orb',
            colors: ['#bae6fd', '#ffffff'],
            size: [1.8 * si, 3.2 * si],
            angle: [yinAng - 0.25, yinAng + 0.25],
            speed: [75 * si, 125 * si],
            drag: 0.94,
            life: [340, 520],
            trail: 4,
          });
        });
      })(sIdx);
    }

    // 第三拍：阴阳太极交融奇点绽放，气场球扩散出漫天四溢星芒
    VFX.after(640, function () {
      var a2 = resolveAnchors(casterEl);
      var s2 = a2.s;
      var c2 = a2.c;
      var f2 = a2.f;

      VFX.quake('sm', 200);
      VFX.hitFlash(casterEl, '#fde68a', 260);

      // 温金与灵青双色柔和像素高斯波纹
      VFX.shockwave({
        x: c2.x,
        y: c2.y,
        fromRadius: 18,
        toRadius: 126,
        color: '#38bdf8',
        coreColor: '#ffffff',
        lineWidth: 4.4,
        double: true,
        duration: 500,
      });

      VFX.shockwave({
        x: f2.x,
        y: f2.y,
        fromRadius: 18,
        toRadius: 130,
        tilt: 0.44,
        color: '#fbbf24',
        coreColor: '#fef9c3',
        lineWidth: 4.0,
        double: true,
        duration: 540,
        delay: 70,
      });

      // 漫天四溢双色星芒 flare
      VFX.burst({
        x: c2.x,
        y: c2.y,
        radius: 26 * s2,
        count: 22,
        shape: 'flare',
        colors: ['#fbbf24', '#38bdf8', '#fde68a', '#bae6fd', '#ffffff'],
        size: [2.4 * s2, 5.2 * s2],
        speed: [58 * s2, 175 * s2],
        drag: 0.92,
        alpha: 0.94,
        life: [480, 820],
      });

      // 流体球漫天散溢金青光珠与柔雾流丝
      VFX.burst({
        x: c2.x,
        y: c2.y,
        radius: 28 * s2,
        count: 34,
        shape: 'orb',
        colors: ['#fbbf24', '#38bdf8', '#5eead4', '#fef3c7', '#ffffff'],
        size: [2.0 * s2, 4.4 * s2],
        speed: [72 * s2, 205 * s2],
        drag: 0.93,
        life: [460, 820],
        trail: 5,
      });

      VFX.burst({
        x: c2.x,
        y: c2.y,
        radius: 22 * s2,
        count: 18,
        shape: 'wisp',
        colors: ['#fde68a', '#7dd3fc', '#5eead4'],
        size: [2.4 * s2, 4.4 * s2],
        speed: [80 * s2, 185 * s2],
        drag: 0.92,
        alpha: 0.74,
        life: [400, 680],
        trail: 4,
      });

      VFX.floatText(casterEl, '+' + manaAmount + ' 合气', 'mana', { scale: 1.15 });
    });

    VFX.after(800, function () {
      VFX.floatText(casterEl, '全伤 +15%', '#fbbf24', { scale: 1.12, rise: 48 });
    });

    return 1600;
  });
});
