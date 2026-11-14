/**
 * 通用战斗反馈特效组（DaohuaVFX v2.3 深度流体粒子化重构版）
 * 注册 ID：
 * - feedback.attack   普攻 / 怪物攻击（5 式通用连段轮换：斜劈/逆撩/疾刺/横扫/双绝交错 + 五行附着）
 * - feedback.impact   通用受击命中反馈（柔光冲击 + 气浪与火花迸射）
 * - feedback.crit     暴击爆裂（柔羽径向金辉 + 多向 flare 星芒 + 炽金火花雨）
 * - feedback.castRing 读条吟唱常驻法阵（返回 { stop, pulse, moveTo } 句柄，含内吸灵尘）
 * - feedback.defeat   阵亡消散（幽魂符文 + 螺旋 wisp 冥雾 + 黑金余烬）
 * - feedback.revive   复活降临（螺旋上升 wisp/orb/flare 金青生机流体柱 + 柔羽八卦阵）
 * - feedback.regen    持续回血 / 回蓝（双旋涡流体 wisp/orb 螺旋升腾，零硬线描边）
 * - feedback.burst    通用元素迸发（柔光阵晕 + 螺旋流体升腾 + 星芒绽放）
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var HALF_PI = Math.PI * 0.5;

  // 五行与无极标准色板（对齐 catalog.js ELEMENT_META）
  var ELEMENT_PALETTES = {
    metal: {
      primary: '#f5c451',
      secondary: '#fef08a',
      core: '#ffffff',
      glow: '#f59e0b',
      flash: '#fef08a',
      style: 'sword',
    },
    wood: {
      primary: '#5be4a8',
      secondary: '#a7f3d0',
      core: '#ffffff',
      glow: '#10b981',
      flash: '#a7f3d0',
      style: 'thunder',
    },
    water: {
      primary: '#4fc3ff',
      secondary: '#bae6fd',
      core: '#ffffff',
      glow: '#0284c7',
      flash: '#bae6fd',
      style: 'water',
    },
    fire: {
      primary: '#ff7b47',
      secondary: '#fde047',
      core: '#fff7ed',
      glow: '#ea580c',
      flash: '#ffedd5',
      style: 'flame',
    },
    earth: {
      primary: '#d8a657',
      secondary: '#fde68a',
      core: '#fffbeb',
      glow: '#b45309',
      flash: '#fde68a',
      style: 'bagua',
    },
    none: {
      primary: '#d7c69a',
      secondary: '#fff5d6',
      core: '#ffffff',
      glow: '#c9b46d',
      flash: '#fff5d6',
      style: 'bagua',
    },
  };

  // 本地离屏柔光精灵缓存（当引擎未直接暴露 getGlowSprite 时作为安全回退）
  var localGlowCache = Object.create(null);

  /**
   * 解析十六进制颜色为 RGB 字符串，供径向渐变停靠点使用
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
   * 获取高斯柔羽化辉光精灵（优先复用 VFX.getGlowSprite，边缘 alpha 严格归零）
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
    grad.addColorStop(0.42, 'rgba(' + rgb + ',0.34)');
    grad.addColorStop(0.75, 'rgba(' + rgb + ',0.08)');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    gctx.fillStyle = grad;
    gctx.beginPath();
    gctx.arc(48, 48, 46, 0, TWO_PI);
    gctx.fill();
    localGlowCache[color] = c;
    return c;
  }

  /**
   * 绘制多层边缘 alpha=0 的柔羽地面光晕，烘托法阵与流体柱基底
   */
  function drawSoftGroundHalo(x, y, radius, tilt, primaryColor, secondaryColor, duration, layer) {
    var rgbMain = colorToRgb(primaryColor);
    var rgbSec = colorToRgb(secondaryColor || primaryColor);
    VFX.custom({
      layer: layer || 'ground',
      blend: 'lighter',
      duration: duration || 650,
      draw: function (g, p) {
        var fadeIn = p < 0.16 ? p / 0.16 : 1;
        var fadeOut = p > 0.65 ? Math.pow(1 - (p - 0.65) / 0.35, 1.35) : 1;
        var alpha = fadeIn * fadeOut;
        if (alpha <= 0.01) return;

        var r = radius * (p < 0.2 ? VFX.lerp(0.6, 1.0, p / 0.2) : 1.0 + Math.sin(p * Math.PI) * 0.06);
        g.save();
        g.translate(x, y);
        g.scale(1, tilt !== undefined ? tilt : 0.44);

        var outerGrad = g.createRadialGradient(0, 0, r * 0.05, 0, 0, r);
        outerGrad.addColorStop(0, 'rgba(' + rgbSec + ',' + (0.38 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(0.4, 'rgba(' + rgbMain + ',' + (0.22 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(0.76, 'rgba(' + rgbMain + ',' + (0.07 * alpha).toFixed(3) + ')');
        outerGrad.addColorStop(1, 'rgba(' + rgbMain + ',0)');
        g.fillStyle = outerGrad;
        g.beginPath();
        g.arc(0, 0, r, 0, TWO_PI);
        g.fill();
        g.restore();
      },
    });
  }

  function getTargets(ctx) {
    if (ctx && ctx.targetEls && ctx.targetEls.length) return ctx.targetEls;
    if (ctx && ctx.targetEl) return [ctx.targetEl];
    return [];
  }

  function getElementKey(ctx) {
    var elem = (ctx && ctx.element) || (ctx && ctx.skill && ctx.skill.element) || 'none';
    return ELEMENT_PALETTES[elem] ? elem : 'none';
  }

  function getPalette(ctx) {
    return ELEMENT_PALETTES[getElementKey(ctx)];
  }

  function mirrorAngle(a, facing) {
    return facing >= 0 ? a : Math.PI - a;
  }

  /**
   * 按 2D 攻击轴方向偏转局部挥斩角（横屏保持上下劈砍语义，竖屏/斜向自然随 axis.angle 旋转）
   */
  function orientAngle(localAngle, axis) {
    return axis.facingX >= 0 ? axis.angle + localAngle : axis.angle - localAngle;
  }

  /**
   * 发射螺旋上升流体柱（完全以高密度涡旋 wisp 气缕 + ember/orb 微粒 + 顶部 flare 星芒替代直筒光柱）
   */
  function emitFluidVortexColumn(opts) {
    var s = VFX.scale();
    var fx = opts.x;
    var fy = opts.y;
    var centerPt = opts.center || { x: fx, y: fy - 42 * s };
    var mainColor = opts.color || '#5eead4';
    var secColor = opts.secondaryColor || '#fef08a';
    var coreColor = opts.coreColor || '#ffffff';
    var baseDelay = opts.delay || 0;
    var waves = opts.waves || 3;
    var radius = (opts.radius || 32) * s;

    // 底部柔和落地光雾（无硬线椭圆）
    VFX.after(baseDelay, function () {
      drawSoftGroundHalo(fx, fy + 2 * s, radius * 2.1, 0.44, mainColor, secColor, 720, 'ground');
    });

    // 多波次高旋度 wisp 气缕形成会呼吸、有湍流的螺旋气柱
    for (var w = 0; w < waves; w++) {
      (function (idx) {
        var at = baseDelay + idx * 95;
        var spinDir = idx % 2 === 0 ? 1 : -1;
        VFX.burst({
          delay: at,
          x: fx,
          y: fy - 4 * s,
          radius: radius * 0.9,
          ring: true,
          tiltY: 0.42,
          count: 16,
          shape: 'wisp',
          colors: [mainColor, secColor, coreColor],
          size: [4.2 * s, 8.8 * s],
          angle: [-HALF_PI - 0.36, -HALF_PI + 0.36],
          speed: [75 * s, 168 * s],
          vortex: 220 * s * spinDir,
          vortexCenter: { x: centerPt.x, y: centerPt.y - 26 * s },
          gravity: -125 * s,
          turbulence: 72 * s,
          drag: 0.95,
          alpha: 0.84,
          life: [520, 860],
          trail: 4,
        });

        VFX.burst({
          delay: at + 20,
          x: fx,
          y: fy - 6 * s,
          radius: radius * 0.78,
          ring: true,
          tiltY: 0.42,
          count: 12,
          shape: idx % 2 === 0 ? 'orb' : 'ember',
          colors: [secColor, mainColor, coreColor],
          size: [2.0 * s, 4.0 * s],
          angle: [-HALF_PI - 0.32, -HALF_PI + 0.32],
          speed: [68 * s, 155 * s],
          vortex: -185 * s * spinDir,
          vortexCenter: { x: centerPt.x, y: centerPt.y - 20 * s },
          gravity: -138 * s,
          turbulence: 54 * s,
          drag: 0.96,
          alpha: 0.92,
          life: [480, 820],
          trail: 5,
        });
      })(w);
    }

    // 顶部星芒与飘散光屑收束
    VFX.burst({
      delay: baseDelay + 120,
      x: centerPt.x,
      y: centerPt.y - 46 * s,
      radius: radius * 0.7,
      count: 6,
      shape: 'flare',
      colors: [coreColor, secColor, mainColor],
      size: [3.0 * s, 5.8 * s],
      angle: [-HALF_PI - 0.85, -HALF_PI + 0.85],
      speed: [32 * s, 88 * s],
      gravity: -45 * s,
      turbulence: 45 * s,
      drag: 0.94,
      spin: [-3.5, 3.5],
      life: [420, 740],
    });
  }

  // 五式通用普攻招式池与无泄漏连段计数
  var BASIC_ATTACK_VARIANT_COUNT = 5;
  var ATTACK_VARIANT_MAP = {
    'slash-down': 0,
    'slash-up': 1,
    thrust: 2,
    sweep: 3,
    cross: 4,
  };
  // 无极默认（element === 'none'）下各招式独立色温分层，强化五式连续出手的视觉区分度
  var NONE_VARIANT_TINTS = [
    { primary: '#e8cb82', secondary: '#fff4cc', subColor: '#fde68a', glow: '#c8963e', core: '#ffffff', flash: '#fff4cc' }, // 0 断岳斜劈
    { primary: '#ded6be', secondary: '#f8fafc', subColor: '#bae6fd', glow: '#94a3b8', core: '#ffffff', flash: '#f1f5f9' }, // 1 惊鸿逆撩
    { primary: '#f4d57c', secondary: '#ffffff', subColor: '#fef08a', glow: '#d97706', core: '#ffffff', flash: '#fef9c3' }, // 2 穿云疾刺
    { primary: '#d9c493', secondary: '#fef3c7', subColor: '#e2e8f0', glow: '#b4883b', core: '#ffffff', flash: '#fef3c7' }, // 3 残月横扫
    { primary: '#f2c45a', secondary: '#fffbeb', subColor: '#fcd34d', glow: '#d97706', core: '#ffffff', flash: '#fef08a' }, // 4 双绝交错
  ];
  var casterComboWeakMap = typeof WeakMap === 'function' ? new WeakMap() : null;
  var attackerKeyComboMap = Object.create(null);
  var attackerKeyCount = 0;
  var fallbackComboSeq = 0;

  function resolveAttackVariant(ctx, casterEl) {
    if (ctx && ctx.attackVariant !== undefined && ctx.attackVariant !== null) {
      if (typeof ctx.attackVariant === 'number' && !isNaN(ctx.attackVariant)) {
        var mod = (ctx.attackVariant | 0) % BASIC_ATTACK_VARIANT_COUNT;
        return mod < 0 ? mod + BASIC_ATTACK_VARIANT_COUNT : mod;
      }
      if (typeof ctx.attackVariant === 'string' && ATTACK_VARIANT_MAP[ctx.attackVariant] !== undefined) {
        return ATTACK_VARIANT_MAP[ctx.attackVariant];
      }
    }
    if (ctx && typeof ctx.attackerKey === 'string' && ctx.attackerKey) {
      var key = ctx.attackerKey;
      var kSeq = attackerKeyComboMap[key];
      if (kSeq === undefined) {
        if (attackerKeyCount > 64) {
          attackerKeyComboMap = Object.create(null);
          attackerKeyCount = 0;
        }
        kSeq = 0;
        attackerKeyCount++;
      }
      attackerKeyComboMap[key] = (kSeq + 1) % BASIC_ATTACK_VARIANT_COUNT;
      return kSeq;
    }
    if (casterEl && casterComboWeakMap) {
      var wSeq = casterComboWeakMap.get(casterEl) || 0;
      casterComboWeakMap.set(casterEl, (wSeq + 1) % BASIC_ATTACK_VARIANT_COUNT);
      return wSeq;
    }
    var seq = fallbackComboSeq % BASIC_ATTACK_VARIANT_COUNT;
    fallbackComboSeq++;
    return seq;
  }

  function resolveVariantPalette(elem, basePal, variantIdx) {
    if (elem !== 'none') {
      return {
        primary: basePal.primary,
        secondary: basePal.secondary,
        subColor: basePal.secondary,
        glow: basePal.glow,
        core: basePal.core,
        flash: basePal.flash,
      };
    }
    return NONE_VARIANT_TINTS[variantIdx % NONE_VARIANT_TINTS.length];
  }

  /**
   * 普攻命中瞬间附加五行元素粒子细节（火余烬 / 土碎岩震波 / 青雷电弧 / 水飞沫 / 无极气缕）
   */
  function emitElementHitDetail(elem, vPal, slashPt, footPt, axis, s) {
    var atkAngle = axis.angle;
    if (elem === 'fire') {
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: 14,
        shape: 'ember',
        colors: ['#ff7b47', '#fde047', '#ef4444'],
        size: [2.4 * s, 4.8 * s],
        speed: [45 * s, 145 * s],
        gravity: -92 * s,
        turbulence: 48 * s,
        life: [280, 540],
        trail: 3,
      });
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: 10,
        shape: 'wisp',
        colors: ['#ff7b47', '#fb923c', '#fde047'],
        size: [4.0 * s, 7.2 * s],
        speed: [70 * s, 175 * s],
        gravity: -65 * s,
        drag: 0.92,
        life: [260, 460],
      });
    } else if (elem === 'earth') {
      VFX.shockwave({
        x: footPt.x,
        y: footPt.y,
        tilt: 0.44,
        fromRadius: 10,
        toRadius: 76,
        color: vPal.primary,
        coreColor: vPal.secondary,
        lineWidth: 3.2,
        duration: 320,
        double: true,
      });
      VFX.burst({
        x: footPt.x,
        y: footPt.y - 6 * s,
        count: 12,
        shape: 'shard',
        colors: ['#d8a657', '#a16207', '#fde68a'],
        size: [2.5 * s, 4.8 * s],
        angle: [-Math.PI * 0.88, -Math.PI * 0.12],
        speed: [75 * s, 175 * s],
        gravity: 240 * s,
        life: [280, 480],
      });
      VFX.burst({
        x: footPt.x,
        y: footPt.y,
        radius: 18 * s,
        count: 10,
        shape: 'smoke',
        colors: ['#57534e', '#78716c', '#a8a29e'],
        size: [10 * s, 18 * s],
        angle: [-Math.PI * 0.92, -Math.PI * 0.08],
        speed: [40 * s, 110 * s],
        gravity: -14 * s,
        alpha: 0.44,
        life: [360, 620],
      });
    } else if (elem === 'wood') {
      var sideSign = axis.facingX >= 0 ? 1 : -1;
      var lFrom = VFX.clampStage({
        x: slashPt.x - axis.ux * 44 * s - axis.nx * 24 * s * sideSign,
        y: slashPt.y - axis.uy * 44 * s - axis.ny * 24 * s * sideSign,
      }, 16 * s, 16 * s);
      var lTo = VFX.clampStage({
        x: slashPt.x + axis.ux * 14 * s + axis.nx * 14 * s * sideSign,
        y: slashPt.y + axis.uy * 14 * s + axis.ny * 14 * s * sideSign,
      }, 16 * s, 16 * s);
      VFX.lightning({
        x1: lFrom.x,
        y1: lFrom.y,
        x2: lTo.x,
        y2: lTo.y,
        color: '#5be4a8',
        secondaryColor: '#ccfbf1',
        glowColor: '#0284c7',
        haloScale: 1.15,
        coreColor: '#ffffff',
        width: 3.0 * s,
        segments: 12,
        branches: 2,
        duration: 190,
      });
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: 9,
        shape: 'wisp',
        colors: ['#5be4a8', '#a7f3d0'],
        size: [3.2 * s, 6.0 * s],
        speed: [60 * s, 155 * s],
        turbulence: 55 * s,
        life: [240, 440],
      });
    } else if (elem === 'water') {
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: 14,
        shape: 'drop',
        colors: ['#4fc3ff', '#bae6fd', '#ffffff'],
        size: [2.4 * s, 4.4 * s],
        speed: [65 * s, 168 * s],
        gravity: 170 * s,
        life: [260, 460],
      });
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: 10,
        shape: 'wisp',
        colors: ['#4fc3ff', '#bae6fd', '#ffffff'],
        size: [3.8 * s, 6.8 * s],
        speed: [55 * s, 145 * s],
        drag: 0.93,
        life: [280, 480],
      });
    } else {
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: 9,
        shape: 'wisp',
        colors: [vPal.primary, vPal.subColor || vPal.secondary, '#ffffff'],
        size: [3.0 * s, 5.8 * s],
        baseAngle: atkAngle + (axis.facingX >= 0 ? 0.18 : -0.18),
        spread: 1.15,
        speed: [85 * s, 200 * s],
        drag: 0.91,
        life: [210, 360],
      });
    }
  }

  /**
   * 普攻落点收官反馈：区分命中（受击震颤 + 冲击波 + 耀星火花）与闪避掠风，并兼容画廊模拟浮字
   */
  function finalizeAttackImpact(ctx, targetEl, vPal, slashPt, atkAngle, s, opts) {
    opts = opts || {};
    var isHit = ctx.hit !== false;

    if (isHit) {
      if (targetEl) {
        VFX.impact(targetEl, {
          color: vPal.flash,
          distance: ctx.crit ? 15 : (opts.impactDist || 11),
          duration: 210,
        });
      }

      VFX.shockwave({
        x: slashPt.x,
        y: slashPt.y,
        fromRadius: opts.fromRadius || 8,
        toRadius: opts.toRadius || 52,
        color: vPal.primary,
        coreColor: '#ffffff',
        lineWidth: opts.lineWidth || 2.6,
        duration: opts.waveDuration || 240,
        double: !!opts.doubleWave,
      });
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: opts.flareCount || 2,
        shape: 'flare',
        colors: [vPal.core, vPal.secondary],
        size: [3.2 * s, 5.0 * s],
        speed: [8 * s, 30 * s],
        life: [170, 270],
      });
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: opts.sparkCount || 18,
        shape: 'spark',
        colors: [vPal.primary, vPal.subColor || vPal.secondary, '#ffffff'],
        size: [2.2 * s, 4.4 * s],
        speed: opts.sparkSpeed || [85 * s, 225 * s],
        baseAngle: atkAngle,
        spread: opts.sparkSpread !== undefined ? opts.sparkSpread : Math.PI * 1.15,
        drag: 0.92,
        life: [210, 390],
        trail: opts.sparkTrail || 4,
      });
    } else {
      // 闪避未命中：不闪烁红光，仅散出轻柔掠风余波
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: 6,
        shape: 'wisp',
        colors: [vPal.secondary, vPal.primary],
        size: [2.6 * s, 4.4 * s],
        baseAngle: atkAngle,
        spread: 0.95,
        speed: [55 * s, 120 * s],
        alpha: 0.45,
        drag: 0.92,
        life: [170, 280],
      });
    }

    if (targetEl) {
      var floatTxt = null;
      if (ctx.text !== undefined && ctx.text !== null) {
        floatTxt = String(ctx.text);
      } else if (typeof ctx.damage === 'number') {
        floatTxt = ctx.damage > 0 ? '-' + Math.round(ctx.damage) : '0';
      } else if (typeof ctx.amount === 'number') {
        floatTxt = ctx.amount > 0 ? '-' + Math.round(ctx.amount) : String(Math.round(ctx.amount));
      }
      if (floatTxt !== null) {
        VFX.floatText(targetEl, floatTxt, ctx.crit ? 'crit' : 'damage');
      }
    }
  }

  /**
   * 1. feedback.attack · 普攻 / 怪物攻击
   * 五式通用招式轮换（不区分武器与怪物）：
   * 0 断岳斜劈 / 1 惊鸿逆撩 / 2 穿云疾刺 / 3 残月横扫 / 4 双绝交错
   * 叠加五行元素特征粒子，全面兼容横竖屏 2D 轴与闪避反馈
   */
  VFX.register('feedback.attack', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = getTargets(ctx);
    var targetEl = targets[0] || null;
    if (!targetEl && !casterEl) return;

    var variantIdx = resolveAttackVariant(ctx, casterEl);
    var elem = getElementKey(ctx);
    var basePal = getPalette(ctx);
    var vPal = resolveVariantPalette(elem, basePal, variantIdx);
    var s = VFX.scale();
    var isEnemyCaster =
      ctx.side === 'enemy' ||
      (casterEl &&
        casterEl.classList &&
        (casterEl.classList.contains('is-enemy') || casterEl.classList.contains('arena-actor--enemy')));
    var axis = VFX.combatAxis(casterEl, targetEl || (isEnemyCaster ? 'ally' : 'enemy'));
    var atkAngle = axis.angle;
    var sideSign = axis.facingX >= 0 ? 1 : -1;
    var t = targetEl ? VFX.center(targetEl) : axis.end;
    var f = targetEl ? VFX.foot(targetEl) : { x: t.x, y: t.y + 42 * s };
    var slashPt = VFX.clampStage(t, 44 * s, 36 * s);
    var footPt = VFX.clampStage(f, 42 * s, 32 * s);
    var palmPt = axis.palm(16 * s);

    if (variantIdx === 0) {
      // 第 0 式 · 断岳斜劈（顺势重劈双弧斩）
      if (casterEl) {
        VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 26, duration: 230 });
      }
      VFX.burst({
        x: palmPt.x,
        y: palmPt.y,
        radius: 14 * s,
        count: 7,
        shape: 'wisp',
        colors: [vPal.primary, vPal.secondary],
        size: [3.0 * s, 5.4 * s],
        baseAngle: atkAngle,
        spread: 0.82,
        speed: [75 * s, 165 * s],
        drag: 0.92,
        alpha: 0.72,
        life: [195, 330],
      });
      if (axis.dist > 70) {
        VFX.streamer({
          x1: palmPt.x,
          y1: palmPt.y,
          x2: slashPt.x,
          y2: slashPt.y,
          arc: -15 * s * sideSign,
          wobble: 2.5 * s,
          width: 7.5 * s,
          tailLength: 0.42,
          color: vPal.primary,
          coreColor: vPal.core,
          sparkColors: [vPal.secondary, vPal.primary, '#ffffff'],
          duration: 180,
        });
      }
      VFX.after(90, function () {
        VFX.slash({
          x: slashPt.x - axis.ux * 8 * s,
          y: slashPt.y - axis.uy * 8 * s - 2 * s,
          radius: elem === 'earth' ? 78 : 72,
          thickness: elem === 'earth' ? 22 : 18,
          startAngle: orientAngle(-1.18, axis),
          endAngle: orientAngle(0.98, axis),
          color: vPal.primary,
          coreColor: vPal.core,
          glowColor: vPal.glow,
          duration: 235,
        });
        VFX.slash({
          x: slashPt.x - axis.ux * 14 * s,
          y: slashPt.y - axis.uy * 14 * s + 6 * s,
          delay: 26,
          radius: 58,
          thickness: 10,
          startAngle: orientAngle(-0.94, axis),
          endAngle: orientAngle(1.08, axis),
          color: vPal.subColor,
          coreColor: '#ffffff',
          glowColor: vPal.primary,
          duration: 190,
        });
        emitElementHitDetail(elem, vPal, slashPt, footPt, axis, s);
        finalizeAttackImpact(ctx, targetEl, vPal, slashPt, atkAngle, s);
      });
      return;
    }

    if (variantIdx === 1) {
      // 第 1 式 · 惊鸿逆撩（下掠逆挑升龙弧 + 升腾旋流）
      if (casterEl) {
        VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 27, duration: 235 });
      }
      VFX.burst({
        x: palmPt.x,
        y: palmPt.y + 8 * s,
        radius: 12 * s,
        count: 7,
        shape: 'wisp',
        colors: [vPal.subColor, vPal.secondary],
        size: [2.8 * s, 5.2 * s],
        baseAngle: orientAngle(0.45, axis),
        spread: 0.72,
        speed: [80 * s, 175 * s],
        drag: 0.92,
        alpha: 0.76,
        life: [195, 320],
      });
      if (axis.dist > 70) {
        VFX.streamer({
          x1: palmPt.x,
          y1: palmPt.y + 8 * s,
          x2: slashPt.x,
          y2: slashPt.y + 10 * s,
          arc: 18 * s * sideSign,
          wobble: 2.2 * s,
          width: 7.2 * s,
          tailLength: 0.4,
          color: vPal.subColor,
          coreColor: vPal.core,
          sparkColors: [vPal.secondary, vPal.subColor, '#ffffff'],
          duration: 180,
        });
      }
      VFX.after(88, function () {
        drawSoftGroundHalo(footPt.x, footPt.y + 2 * s, 52 * s, 0.42, vPal.primary, vPal.subColor, 340, 'ground');
        VFX.slash({
          x: slashPt.x - axis.ux * 7 * s,
          y: slashPt.y - axis.uy * 7 * s + 4 * s,
          radius: 75,
          thickness: 17,
          startAngle: orientAngle(1.24, axis),
          endAngle: orientAngle(-1.08, axis),
          color: vPal.primary,
          coreColor: vPal.core,
          glowColor: vPal.glow,
          duration: 240,
        });
        VFX.slash({
          x: slashPt.x - axis.ux * 12 * s,
          y: slashPt.y - axis.uy * 12 * s - 4 * s,
          delay: 24,
          radius: 60,
          thickness: 9.5,
          startAngle: orientAngle(1.06, axis),
          endAngle: orientAngle(-1.22, axis),
          color: vPal.subColor,
          coreColor: '#ffffff',
          glowColor: vPal.primary,
          duration: 195,
        });
        VFX.burst({
          x: slashPt.x,
          y: slashPt.y + 14 * s,
          radius: 16 * s,
          count: 12,
          shape: 'wisp',
          colors: [vPal.primary, vPal.subColor, '#ffffff'],
          size: [3.0 * s, 5.8 * s],
          angle: [-HALF_PI - 0.55, -HALF_PI + 0.55],
          speed: [75 * s, 165 * s],
          vortex: 165 * s * sideSign,
          vortexCenter: { x: slashPt.x, y: slashPt.y - 16 * s },
          gravity: -125 * s,
          drag: 0.93,
          life: [230, 390],
          trail: 4,
        });
        emitElementHitDetail(elem, vPal, slashPt, footPt, axis, s);
        finalizeAttackImpact(ctx, targetEl, vPal, slashPt, orientAngle(-0.7, axis), s, {
          sparkSpread: Math.PI * 0.9,
        });
      });
      return;
    }

    if (variantIdx === 2) {
      // 第 2 式 · 穿云疾刺（高速直线贯刺锐芒 + 双柔雾音爆环）
      if (casterEl) {
        VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 32, duration: 195 });
      }
      VFX.burst({
        x: palmPt.x,
        y: palmPt.y,
        count: 2,
        shape: 'flare',
        colors: ['#ffffff', vPal.subColor],
        size: [2.8 * s, 4.4 * s],
        speed: [18 * s, 45 * s],
        baseAngle: atkAngle,
        spread: 0.4,
        life: [140, 220],
      });
      if (axis.dist > 52) {
        VFX.streamer({
          x1: palmPt.x,
          y1: palmPt.y,
          x2: slashPt.x,
          y2: slashPt.y,
          arc: 0,
          wobble: 0,
          width: 6.2 * s,
          tailLength: 0.48,
          color: vPal.primary,
          coreColor: '#ffffff',
          sparkColors: [vPal.subColor, vPal.primary, '#ffffff'],
          duration: 145,
        });
      }
      VFX.after(72, function () {
        // 围绕靶点的短促羽化锐刺轨迹，拒绝全屏硬光柱
        var thrustFrom = VFX.clampStage({
          x: slashPt.x - axis.ux * 46 * s,
          y: slashPt.y - axis.uy * 46 * s,
        }, 12 * s, 12 * s);
        var thrustTo = VFX.clampStage({
          x: slashPt.x + axis.ux * 34 * s,
          y: slashPt.y + axis.uy * 34 * s,
        }, 12 * s, 12 * s);
        VFX.slashLine({
          x1: thrustFrom.x,
          y1: thrustFrom.y,
          x2: thrustTo.x,
          y2: thrustTo.y,
          width: 13 * s,
          color: vPal.primary,
          coreColor: '#ffffff',
          pierce: 0.14,
          ringCount: 2,
          duration: 210,
        });
        // 两侧 V 字破风分流气浪
        VFX.burst({
          x: slashPt.x - axis.ux * 10 * s,
          y: slashPt.y - axis.uy * 10 * s,
          count: 8,
          shape: 'wisp',
          colors: [vPal.secondary, vPal.primary, '#ffffff'],
          size: [2.6 * s, 4.8 * s],
          baseAngle: atkAngle + Math.PI,
          spread: 1.5,
          speed: [70 * s, 155 * s],
          drag: 0.91,
          life: [175, 300],
        });
        emitElementHitDetail(elem, vPal, slashPt, footPt, axis, s);
        finalizeAttackImpact(ctx, targetEl, vPal, slashPt, atkAngle, s, {
          impactDist: 13,
          toRadius: 48,
          sparkCount: 20,
          sparkSpread: 0.56,
          sparkSpeed: [130 * s, 290 * s],
          sparkTrail: 5,
        });
      });
      return;
    }

    if (variantIdx === 3) {
      // 第 3 式 · 残月横扫（3D 透视腰际满月旋斩 + 环形气旋）
      if (casterEl) {
        VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 25, duration: 245 });
      }
      VFX.burst({
        x: palmPt.x,
        y: palmPt.y,
        radius: 14 * s,
        count: 8,
        shape: 'wisp',
        colors: [vPal.primary, vPal.subColor],
        size: [3.2 * s, 5.6 * s],
        baseAngle: atkAngle - 0.4 * sideSign,
        spread: 0.95,
        speed: [75 * s, 165 * s],
        drag: 0.92,
        life: [200, 340],
      });
      if (axis.dist > 70) {
        VFX.streamer({
          x1: palmPt.x,
          y1: palmPt.y,
          x2: slashPt.x,
          y2: slashPt.y,
          arc: -24 * s * sideSign,
          wobble: 3.0 * s,
          width: 8.2 * s,
          tailLength: 0.44,
          color: vPal.primary,
          coreColor: vPal.core,
          sparkColors: [vPal.subColor, vPal.primary, '#ffffff'],
          duration: 190,
        });
      }
      VFX.after(92, function () {
        var sweepCenter = VFX.clampStage({
          x: slashPt.x - axis.ux * 22 * s,
          y: slashPt.y - axis.uy * 22 * s,
        }, 28 * s, 28 * s);
        VFX.slash({
          x: sweepCenter.x,
          y: sweepCenter.y,
          radius: 76,
          thickness: 20,
          tilt: 0.5,
          rotation: atkAngle,
          startAngle: -1.42 * sideSign,
          endAngle: 1.42 * sideSign,
          color: vPal.primary,
          coreColor: vPal.core,
          glowColor: vPal.glow,
          duration: 255,
        });
        VFX.slash({
          x: sweepCenter.x,
          y: sweepCenter.y + 3 * s,
          delay: 28,
          radius: 58,
          thickness: 10.5,
          tilt: 0.5,
          rotation: atkAngle,
          startAngle: -1.24 * sideSign,
          endAngle: 1.28 * sideSign,
          color: vPal.subColor,
          coreColor: '#ffffff',
          glowColor: vPal.primary,
          duration: 210,
        });
        VFX.burst({
          x: slashPt.x,
          y: slashPt.y,
          radius: 34 * s,
          ring: true,
          tiltY: 0.5,
          count: 14,
          shape: 'wisp',
          colors: [vPal.primary, vPal.subColor, '#ffffff'],
          size: [3.2 * s, 6.0 * s],
          speed: [55 * s, 125 * s],
          vortex: 235 * s * sideSign,
          vortexCenter: { x: slashPt.x, y: slashPt.y },
          drag: 0.93,
          life: [220, 390],
          trail: 4,
        });
        emitElementHitDetail(elem, vPal, slashPt, footPt, axis, s);
        finalizeAttackImpact(ctx, targetEl, vPal, slashPt, atkAngle, s, {
          toRadius: 58,
          sparkSpread: Math.PI * 1.45,
        });
      });
      return;
    }

    // 第 4 式 · 双绝交错（疾风二连 X 十字剪斩 + 双重震波收官）
    if (casterEl) {
      VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 29, duration: 255 });
    }
    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 14 * s,
      count: 8,
      shape: 'wisp',
      colors: [vPal.primary, vPal.subColor, '#ffffff'],
      size: [2.8 * s, 5.2 * s],
      baseAngle: atkAngle,
      spread: 1.1,
      speed: [85 * s, 185 * s],
      drag: 0.92,
      life: [190, 320],
    });
    if (axis.dist > 70) {
      VFX.streamer({
        x1: palmPt.x,
        y1: palmPt.y,
        x2: slashPt.x,
        y2: slashPt.y,
        arc: -18 * s * sideSign,
        wobble: 2.2 * s,
        width: 6.0 * s,
        tailLength: 0.38,
        color: vPal.primary,
        coreColor: vPal.core,
        sparkColors: [vPal.secondary, vPal.primary, '#ffffff'],
        duration: 168,
      });
      VFX.streamer({
        delay: 42,
        x1: palmPt.x,
        y1: palmPt.y,
        x2: slashPt.x,
        y2: slashPt.y,
        arc: 18 * s * sideSign,
        wobble: 2.2 * s,
        width: 5.8 * s,
        tailLength: 0.38,
        color: vPal.subColor,
        coreColor: '#ffffff',
        sparkColors: [vPal.subColor, '#ffffff'],
        duration: 168,
      });
    }
    // 第一斩：正斜劈
    VFX.after(76, function () {
      VFX.slash({
        x: slashPt.x - axis.ux * 7 * s,
        y: slashPt.y - axis.uy * 7 * s - 2 * s,
        radius: 68,
        thickness: 15.5,
        startAngle: orientAngle(-1.18, axis),
        endAngle: orientAngle(0.95, axis),
        color: vPal.primary,
        coreColor: vPal.core,
        glowColor: vPal.glow,
        duration: 215,
      });
      VFX.burst({
        x: slashPt.x,
        y: slashPt.y,
        count: 10,
        shape: 'spark',
        colors: [vPal.primary, '#ffffff'],
        size: [2.0 * s, 3.8 * s],
        speed: [75 * s, 185 * s],
        baseAngle: atkAngle,
        spread: Math.PI * 0.9,
        life: [170, 310],
        trail: 3,
      });
    });
    // 第二斩（错峰 70ms）：反斜交叉组成 X 剪斩并引爆收官
    VFX.after(146, function () {
      VFX.slash({
        x: slashPt.x - axis.ux * 5 * s,
        y: slashPt.y - axis.uy * 5 * s + 2 * s,
        radius: 72,
        thickness: 17,
        startAngle: orientAngle(1.18, axis),
        endAngle: orientAngle(-0.95, axis),
        color: vPal.subColor,
        coreColor: '#ffffff',
        glowColor: vPal.glow,
        duration: 225,
      });
      emitElementHitDetail(elem, vPal, slashPt, footPt, axis, s);
      finalizeAttackImpact(ctx, targetEl, vPal, slashPt, atkAngle, s, {
        impactDist: 14,
        toRadius: 62,
        doubleWave: true,
        flareCount: 4,
        sparkCount: 22,
      });
    });
  });

  /**
   * 2. feedback.impact · 通用受击命中反馈
   */
  VFX.register('feedback.impact', function (ctx) {
    var targets = getTargets(ctx);
    if (!targets.length && ctx.casterEl) targets = [ctx.casterEl];
    if (!targets.length) return;

    var pal = getPalette(ctx);
    var isCrit = !!ctx.crit;
    var s = VFX.scale();

    for (var i = 0; i < targets.length; i++) {
      var el = targets[i];
      if (!el) continue;
      var t = VFX.clampStage(VFX.center(el), 42 * s, 32 * s);

      VFX.impact(el, {
        color: isCrit ? '#ffd966' : pal.flash,
        distance: isCrit ? 15 : 11,
        duration: 220,
      });

      VFX.shockwave({
        x: t.x,
        y: t.y,
        fromRadius: 6,
        toRadius: isCrit ? 66 : 48,
        color: isCrit ? '#f5c451' : pal.primary,
        coreColor: '#ffffff',
        lineWidth: 2.5,
        duration: 240,
        double: false,
      });

      VFX.burst({
        x: t.x,
        y: t.y,
        count: isCrit ? 4 : 2,
        shape: 'flare',
        colors: ['#ffffff', pal.secondary, pal.primary],
        size: isCrit ? [3.6 * s, 5.8 * s] : [2.6 * s, 4.2 * s],
        speed: [12 * s, 42 * s],
        life: [180, 300],
      });

      VFX.burst({
        x: t.x,
        y: t.y,
        count: 14,
        shape: 'wisp',
        colors: [pal.primary, pal.secondary],
        size: [3.6 * s, 6.8 * s],
        speed: [55 * s, 165 * s],
        drag: 0.91,
        alpha: 0.78,
        life: [220, 410],
      });

      VFX.burst({
        x: t.x,
        y: t.y,
        count: 16,
        shape: 'spark',
        colors: [pal.primary, pal.secondary, '#ffffff'],
        size: [2.0 * s, 4.2 * s],
        speed: [75 * s, 205 * s],
        drag: 0.92,
        life: [200, 380],
        trail: 3,
      });

      VFX.burst({
        x: t.x,
        y: t.y,
        count: 8,
        shape: 'orb',
        colors: [pal.primary, '#ffffff'],
        size: [2.0 * s, 3.8 * s],
        speed: [30 * s, 88 * s],
        life: [230, 420],
      });

      var txt = null;
      if (ctx.text !== undefined && ctx.text !== null) {
        txt = String(ctx.text);
      } else if (typeof ctx.amount === 'number') {
        txt = ctx.amount > 0 ? '-' + Math.round(ctx.amount) : String(Math.round(ctx.amount));
      } else if (typeof ctx.damage === 'number') {
        txt = ctx.damage > 0 ? '-' + Math.round(ctx.damage) : '0';
      }
      if (txt !== null) {
        VFX.floatText(el, txt, isCrit ? 'crit' : 'damage');
      }
    }
  });

  /**
   * 3. feedback.crit · 暴击爆裂
   * 柔羽核心金辉 + 多向 flare 星芒闪爆 + 炽金 spark 雨 + 流体气浪 + 轻震
   */
  VFX.register('feedback.crit', function (ctx) {
    var targets = getTargets(ctx);
    var targetEl = targets[0] || ctx.casterEl || null;
    if (!targetEl) return;

    var s = VFX.scale();
    var t = VFX.clampStage(VFX.center(targetEl), 42 * s, 32 * s);
    VFX.impact(targetEl, { color: '#ffd966', distance: 16, duration: 260 });
    VFX.quake('sm', 220);

    // 多层次边缘 alpha=0 径向渐变柔羽核心辉光（完全替代硬菱形多边形填充）
    VFX.custom({
      duration: 360,
      draw: function (c, p) {
        var reach = p < 0.22 ? VFX.lerp(0.32, 1.05, p / 0.22) : 1.05 - (p - 0.22) * 0.28;
        var alpha = p < 0.12 ? p / 0.12 : Math.pow(1 - (p - 0.12) / 0.88, 1.55);
        if (alpha <= 0.01) return;

        c.save();
        c.translate(t.x, t.y);

        // 外层炽金柔光散射
        var rOuter = 68 * s * reach;
        var gOuter = c.createRadialGradient(0, 0, 1, 0, 0, rOuter);
        gOuter.addColorStop(0, 'rgba(255,255,255,' + (0.94 * alpha).toFixed(3) + ')');
        gOuter.addColorStop(0.24, 'rgba(254,240,138,' + (0.72 * alpha).toFixed(3) + ')');
        gOuter.addColorStop(0.56, 'rgba(245,158,11,' + (0.28 * alpha).toFixed(3) + ')');
        gOuter.addColorStop(1, 'rgba(245,158,11,0)');
        c.fillStyle = gOuter;
        c.beginPath();
        c.arc(0, 0, rOuter, 0, TWO_PI);
        c.fill();

        // 叠涂柔光精灵增强中心爆发过曝感
        var sp = getSoftGlow('#fef08a');
        if (sp) {
          var sr = 42 * s * reach;
          c.globalAlpha = alpha * 0.85;
          c.drawImage(sp, -sr, -sr, sr * 2, sr * 2);
        }
        c.restore();
      },
    });

    // 四向与斜向动态 flare 星芒爆裂（核心 + 次级微旋星芒）
    VFX.burst({
      x: t.x,
      y: t.y,
      radius: 4 * s,
      count: 2,
      shape: 'flare',
      colors: ['#ffffff', '#fef08a'],
      size: [8.5 * s, 12.5 * s],
      speed: [2 * s, 12 * s],
      spin: [-1.5, 1.5],
      alpha: 0.96,
      life: [260, 360],
    });
    VFX.burst({
      x: t.x,
      y: t.y,
      radius: 14 * s,
      count: 6,
      shape: 'flare',
      colors: ['#ffd966', '#fef08a', '#ffffff'],
      size: [3.6 * s, 6.2 * s],
      speed: [45 * s, 135 * s],
      spin: [-4, 4],
      drag: 0.9,
      life: [240, 420],
    });

    // 柔光双层金色冲击波
    VFX.shockwave({
      x: t.x,
      y: t.y,
      fromRadius: 10,
      toRadius: 96,
      color: '#f5c451',
      coreColor: '#ffffff',
      lineWidth: 4.2,
      duration: 360,
      double: true,
    });
    VFX.shockwave({
      x: t.x,
      y: t.y,
      delay: 55,
      fromRadius: 16,
      toRadius: 118,
      color: '#fb923c',
      coreColor: '#fef08a',
      lineWidth: 2.6,
      duration: 400,
      double: false,
    });

    // 炽金流体气浪扩散
    VFX.burst({
      x: t.x,
      y: t.y,
      radius: 10 * s,
      count: 16,
      shape: 'wisp',
      colors: ['#f59e0b', '#fef08a', '#fb923c'],
      size: [4.8 * s, 9.2 * s],
      speed: [90 * s, 220 * s],
      drag: 0.91,
      turbulence: 45 * s,
      alpha: 0.78,
      life: [280, 480],
    });

    // 炽金火花雨与水晶金屑飞溅
    VFX.burst({
      x: t.x,
      y: t.y,
      radius: 10 * s,
      count: 22,
      shape: 'shard',
      colors: ['#ffd966', '#f59e0b', '#fef08a', '#ffffff'],
      size: [3.0 * s, 6.2 * s],
      speed: [120 * s, 290 * s],
      gravity: 160 * s,
      drag: 0.94,
      spin: [-9, 9],
      life: [360, 660],
      trail: 3,
    });
    VFX.burst({
      x: t.x,
      y: t.y,
      count: 34,
      shape: 'spark',
      colors: ['#fef08a', '#fb923c', '#f5c451', '#ffffff'],
      size: [2.2 * s, 5.0 * s],
      speed: [140 * s, 340 * s],
      gravity: 90 * s,
      drag: 0.91,
      life: [260, 500],
      trail: 5,
    });

    var critTxt = '暴击！';
    if (ctx.text !== undefined && ctx.text !== null) {
      critTxt = String(ctx.text);
    } else if (typeof ctx.damage === 'number') {
      critTxt = '暴击 -' + Math.round(ctx.damage);
    } else if (typeof ctx.amount === 'number') {
      critTxt = '暴击 -' + Math.round(ctx.amount);
    }
    VFX.floatText(targetEl, critTxt, 'crit', {
      scale: 1.38,
      rise: 54,
      duration: 960,
    });
  });

  /**
   * 4. feedback.castRing · 读条吟唱常驻法阵
   * 重要：必须返回 { stop, pulse, moveTo } 控制句柄
   */
  VFX.register('feedback.castRing', function (ctx) {
    var casterEl = ctx.casterEl || getTargets(ctx)[0] || null;
    if (!casterEl) {
      return {
        stop: function () {},
        pulse: function () {},
        moveTo: function () {},
      };
    }

    var elem = getElementKey(ctx);
    var pal = getPalette(ctx);
    var style = pal.style;
    var color = pal.primary;
    var secondary = pal.secondary;

    // 符箓类无极技能赋予紫金灵阵质感
    if (elem === 'none' && ctx.skill && ctx.skill.skillGroup === 'talisman') {
      style = 'spirit';
      color = '#c4b5fd';
      secondary = '#fef08a';
    }

    var sInit = VFX.scale();
    var pos = VFX.foot(casterEl);
    var centerNow = VFX.center(casterEl);
    var footSafeInit = VFX.clampStage(pos, 42 * sInit, 32 * sInit);
    var cardRect = VFX.rect(casterEl);
    var baseRadius = cardRect ? VFX.clamp((cardRect.w * 0.44) / Math.max(0.58, sInit), 48, 66) : 56;
    var manualOffset = null;

    // 底层柔羽化地面光雾衬托，消除法阵在暗背景下孤立线框感
    drawSoftGroundHalo(
      footSafeInit.x,
      footSafeInit.y,
      baseRadius * 1.35 * sInit,
      0.44,
      color,
      secondary,
      Math.min(ctx.durationMs || 1800, 3000),
      'ground'
    );

    var circle = VFX.magicCircle({
      x: pos.x,
      y: pos.y,
      anchorEl: casterEl,
      radius: baseRadius,
      tilt: 0.44,
      layer: 'ground',
      color: color,
      secondaryColor: secondary,
      style: style,
      rotationSpeed: 0.95,
      pulseSpeed: 2.8,
      alpha: 0.78,
      lineWidth: 1.8,
      persistent: true,
    });

    function readAnchorState() {
      var sNow = VFX.scale();
      var fNow = VFX.foot(casterEl);
      var cNow = VFX.center(casterEl);
      var emitPt = manualOffset ? { x: manualOffset.x, y: manualOffset.y } : fNow;
      var attractPt = manualOffset ? { x: manualOffset.x, y: manualOffset.y + manualOffset.dy } : cNow;
      return {
        s: sNow,
        fNow: fNow,
        cNow: cNow,
        emitPt: emitPt,
        attractPt: attractPt,
        rNow: baseRadius * sNow,
      };
    }

    /**
     * 发射一波蓄力期灵尘：外围向阵心内吸的 attractTo 灵粒 + 阵缘微旋升腾 wisp
     */
    function emitCastMotes(inwardCount, riseCount) {
      var stNow = readAnchorState();
      var sNow = stNow.s;
      var emitPt = stNow.emitPt;
      var attractPt = stNow.attractPt;
      var rNow = stNow.rNow;

      VFX.burst({
        x: emitPt.x,
        y: emitPt.y - 12 * sNow,
        radius: rNow * 1.45,
        ring: true,
        tiltY: 0.54,
        count: inwardCount,
        shape: 'orb',
        colors: [color, secondary, '#ffffff'],
        size: [1.8 * sNow, 3.4 * sNow],
        speed: [10 * sNow, 26 * sNow],
        vortex: 95 * sNow,
        vortexCenter: attractPt,
        attractTo: attractPt,
        attractStrength: 420 * sNow,
        drag: 0.94,
        alpha: 0.8,
        life: [380, 620],
        trail: 4,
      });
      VFX.burst({
        x: emitPt.x,
        y: emitPt.y,
        radius: rNow * 0.78,
        ring: true,
        tiltY: 0.44,
        count: riseCount,
        shape: 'wisp',
        colors: [color, secondary],
        size: [2.8 * sNow, 5.2 * sNow],
        angle: [-HALF_PI - 0.35, -HALF_PI + 0.35],
        speed: [24 * sNow, 52 * sNow],
        vortex: 80 * sNow,
        vortexCenter: attractPt,
        gravity: -46 * sNow,
        drag: 0.96,
        alpha: 0.62,
        life: [460, 760],
      });
    }

    // 首波聚灵内吸与升腾灵雾
    emitCastMotes(10, 6);

    var stopped = false;
    var moteTimer = null;
    var elapsedEst = 0;
    var maxEmitMs = 15000;

    function scheduleMotes() {
      if (stopped || elapsedEst >= maxEmitMs) return;
      moteTimer = VFX.after(180, function () {
        moteTimer = null;
        if (stopped) return;
        elapsedEst += 180;
        emitCastMotes(5, 3);
        scheduleMotes();
      });
    }
    scheduleMotes();

    return {
      stop: function (fadeMs) {
        stopped = true;
        if (moteTimer && moteTimer.cancel) {
          moteTimer.cancel();
          moteTimer = null;
        }
        if (circle && circle.stop) {
          circle.stop(fadeMs);
        }
      },
      pulse: function (pulseColor, intensity) {
        var cCol = pulseColor || secondary;
        var stNow = readAnchorState();
        var sNow = stNow.s;
        if (circle && circle.pulse) {
          circle.pulse(cCol, intensity);
        }
        VFX.burst({
          x: stNow.emitPt.x,
          y: stNow.emitPt.y,
          radius: stNow.rNow * 0.76,
          ring: true,
          tiltY: 0.44,
          count: 10,
          shape: 'wisp',
          colors: [cCol, color, '#ffffff'],
          size: [3.2 * sNow, 6.0 * sNow],
          angle: [-HALF_PI - 0.35, -HALF_PI + 0.35],
          speed: [38 * sNow, 86 * sNow],
          gravity: -64 * sNow,
          alpha: 0.85,
          life: [400, 700],
          trail: 4,
        });
        VFX.burst({
          x: stNow.attractPt.x,
          y: stNow.attractPt.y,
          radius: 16 * sNow,
          count: 3,
          shape: 'flare',
          colors: [cCol, '#ffffff'],
          size: [2.6 * sNow, 4.4 * sNow],
          speed: [18 * sNow, 48 * sNow],
          life: [240, 380],
        });
      },
      moveTo: function (x, y) {
        var cCur = VFX.center(casterEl);
        var fCur = VFX.foot(casterEl);
        manualOffset = { x: x, y: y, dy: cCur.y - fCur.y };
        if (circle && circle.moveTo) {
          circle.moveTo(x, y);
        }
      },
    };
  });

  /**
   * 5. feedback.defeat · 阵亡消散
   */
  VFX.register('feedback.defeat', function (ctx) {
    var list = getTargets(ctx);
    if (!list.length && ctx.casterEl) list = [ctx.casterEl];
    if (!list.length) return;

    var s = VFX.scale();
    for (var i = 0; i < list.length; i++) {
      var el = list[i];
      if (!el) continue;
      VFX.defeat(el);

      var c = VFX.clampStage(VFX.center(el), 42 * s, 32 * s);
      var f = VFX.clampStage(VFX.foot(el), 42 * s, 32 * s);

      // 升空消散幽魂符文
      VFX.burst({
        x: c.x,
        y: c.y + 8 * s,
        radius: 26 * s,
        count: 10,
        shape: 'rune',
        colors: ['#cbd5e1', '#94a3b8', '#e2e8f0'],
        size: [4.8 * s, 8.2 * s],
        angle: [-Math.PI * 0.68, -Math.PI * 0.32],
        speed: [26 * s, 62 * s],
        gravity: -38 * s,
        turbulence: 34 * s,
        drag: 0.97,
        spin: [-1.8, 1.8],
        alpha: 0.85,
        life: [760, 1260],
      });

      // 螺旋升散幽魂 wisp 冥气（替代生硬丝带）
      VFX.burst({
        x: f.x,
        y: f.y - 6 * s,
        radius: 28 * s,
        count: 20,
        shape: 'wisp',
        colors: ['#94a3b8', '#cbd5e1', '#d4a448'],
        size: [4.8 * s, 9.5 * s],
        angle: [-HALF_PI - 0.38, -HALF_PI + 0.38],
        speed: [42 * s, 108 * s],
        vortex: 135 * s,
        vortexCenter: { x: c.x, y: c.y - 24 * s },
        gravity: -72 * s,
        turbulence: 68 * s,
        drag: 0.95,
        alpha: 0.72,
        life: [620, 1080],
        trail: 4,
      });

      // 黑金余烬与魂火微粒
      VFX.burst({
        x: f.x,
        y: f.y - 8 * s,
        radius: 28 * s,
        count: 22,
        shape: 'ember',
        colors: ['#d4a448', '#854d0e', '#cbd5e1'],
        size: [2.4 * s, 5.0 * s],
        angle: [-Math.PI * 0.72, -Math.PI * 0.28],
        speed: [24 * s, 72 * s],
        gravity: -55 * s,
        turbulence: 48 * s,
        life: [700, 1220],
        trail: 3,
      });

      // 暗影余烟
      VFX.burst({
        x: c.x,
        y: c.y + 10 * s,
        radius: 24 * s,
        count: 10,
        shape: 'smoke',
        blend: 'source-over',
        colors: ['#1e293b', '#334155'],
        size: [14 * s, 24 * s],
        angle: [-Math.PI * 0.65, -Math.PI * 0.35],
        speed: [20 * s, 48 * s],
        gravity: -30 * s,
        alpha: 0.48,
        life: [660, 1080],
      });
    }
  });

  /**
   * 6. feedback.revive · 复活降临
   * 去除任何直筒光柱，改用金青/翠绿螺旋上升 wisp + orb/ember + flare 星芒流体柱
   */
  VFX.register('feedback.revive', function (ctx) {
    var list = getTargets(ctx);
    if (!list.length && ctx.casterEl) list = [ctx.casterEl];
    if (!list.length) return;

    var s = VFX.scale();
    for (var i = 0; i < list.length; i++) {
      var el = list[i];
      if (!el) continue;
      VFX.revive(el);

      var f = VFX.clampStage(VFX.foot(el), 42 * s, 32 * s);
      var c = VFX.clampStage(VFX.center(el), 42 * s, 32 * s);

      // 地上金碧柔光八卦阵（降硬线感 + 地面柔光底晕）
      drawSoftGroundHalo(f.x, f.y + 2 * s, 92 * s, 0.44, '#34d399', '#fef08a', 1050, 'ground');
      VFX.magicCircle({
        x: f.x,
        y: f.y + 2 * s,
        radius: 68,
        tilt: 0.44,
        layer: 'ground',
        color: '#34d399',
        secondaryColor: '#fef08a',
        style: 'bagua',
        rotationSpeed: 1.35,
        duration: 1050,
        alpha: 0.78,
        lineWidth: 1.9,
      });

      VFX.shockwave({
        x: f.x,
        y: f.y + 2 * s,
        tilt: 0.44,
        fromRadius: 12,
        toRadius: 98,
        color: '#5eead4',
        coreColor: '#fef08a',
        lineWidth: 3.2,
        duration: 520,
        double: true,
      });

      // 金青/翠绿螺旋上升流体气柱（零直筒柱）
      emitFluidVortexColumn({
        x: f.x,
        y: f.y,
        center: c,
        color: '#34d399',
        secondaryColor: '#fef08a',
        coreColor: '#ffffff',
        radius: 30,
        waves: 3,
      });

      // 金碧生机花瓣环升
      VFX.burst({
        delay: 120,
        x: c.x,
        y: f.y - 8 * s,
        radius: 26 * s,
        ring: true,
        tiltY: 0.45,
        count: 16,
        shape: 'petal',
        colors: ['#5eead4', '#fef08a', '#a7f3d0'],
        size: [2.8 * s, 5.2 * s],
        angle: [-Math.PI * 0.65, -Math.PI * 0.35],
        speed: [28 * s, 68 * s],
        gravity: -64 * s,
        vortex: 130 * s,
        vortexCenter: c,
        life: [580, 960],
      });
    }
  });

  /**
   * 7. feedback.regen · 持续回血 / 回蓝
   * 彻底去除硬线条螺旋 stroke，改用双旋涡流体 wisp + orb 灵缕螺旋上升
   */
  VFX.register('feedback.regen', function (ctx) {
    var targetEl = getTargets(ctx)[0] || ctx.casterEl || null;
    if (!targetEl) return;

    var isMana =
      ctx.resource === 'mp' ||
      ctx.resource === 'mana' ||
      ctx.kind === 'mp' ||
      ctx.kind === 'mana' ||
      ctx.element === 'water';

    var colors = isMana
      ? ['#38bdf8', '#7dd3fc', '#e0f2fe']
      : ['#34d399', '#6ee7b7', '#dcfce7'];
    var mainColor = colors[0];
    var secColor = colors[2];

    var s = VFX.scale();
    var c = VFX.clampStage(VFX.center(targetEl), 42 * s, 32 * s);
    var f = VFX.clampStage(VFX.foot(targetEl), 42 * s, 32 * s);

    // 柔羽地面微澜与底光晕
    drawSoftGroundHalo(f.x, f.y, 64 * s, 0.44, mainColor, secColor, 680, 'ground');
    VFX.shockwave({
      x: f.x,
      y: f.y,
      tilt: 0.44,
      fromRadius: 12,
      toRadius: 56,
      color: mainColor,
      coreColor: secColor,
      lineWidth: 2.0,
      duration: 560,
      double: false,
    });

    // 正反双螺旋流体气缕与灵珠（第一波）
    VFX.burst({
      x: f.x,
      y: f.y - 4 * s,
      radius: 26 * s,
      ring: true,
      tiltY: 0.44,
      count: 14,
      shape: 'wisp',
      colors: colors,
      size: [3.2 * s, 6.2 * s],
      angle: [-Math.PI * 0.6, -Math.PI * 0.4],
      speed: [34 * s, 68 * s],
      vortex: 195 * s,
      vortexCenter: c,
      gravity: -88 * s,
      turbulence: 45 * s,
      drag: 0.96,
      alpha: 0.78,
      life: [520, 880],
      trail: 4,
    });
    VFX.burst({
      x: f.x,
      y: f.y - 4 * s,
      radius: 26 * s,
      ring: true,
      tiltY: 0.44,
      count: 14,
      shape: 'orb',
      colors: colors,
      size: [2.2 * s, 4.0 * s],
      angle: [-Math.PI * 0.6, -Math.PI * 0.4],
      speed: [28 * s, 56 * s],
      vortex: 185 * s,
      vortexCenter: c,
      gravity: -85 * s,
      drag: 0.96,
      alpha: 0.88,
      life: [540, 900],
      trail: 5,
    });

    // 反旋第二波接续（取代原生硬单线螺旋）
    VFX.burst({
      delay: 150,
      x: f.x,
      y: f.y - 6 * s,
      radius: 22 * s,
      ring: true,
      tiltY: 0.44,
      count: 12,
      shape: 'wisp',
      colors: colors,
      size: [2.8 * s, 5.4 * s],
      angle: [-Math.PI * 0.62, -Math.PI * 0.38],
      speed: [36 * s, 72 * s],
      vortex: -195 * s,
      vortexCenter: c,
      gravity: -96 * s,
      turbulence: 40 * s,
      drag: 0.96,
      alpha: 0.74,
      life: [500, 840],
      trail: 4,
    });
    VFX.burst({
      delay: 160,
      x: f.x,
      y: f.y - 6 * s,
      radius: 22 * s,
      ring: true,
      tiltY: 0.44,
      count: 12,
      shape: 'orb',
      colors: colors,
      size: [2.0 * s, 3.8 * s],
      angle: [-Math.PI * 0.62, -Math.PI * 0.38],
      speed: [32 * s, 62 * s],
      vortex: -175 * s,
      vortexCenter: c,
      gravity: -92 * s,
      drag: 0.96,
      alpha: 0.85,
      life: [500, 840],
      trail: 4,
    });

    // 顶部一两枚微光星芒闪烁收束
    VFX.burst({
      delay: 240,
      x: c.x,
      y: c.y - 26 * s,
      radius: 16 * s,
      count: 3,
      shape: 'flare',
      colors: [secColor, mainColor, '#ffffff'],
      size: [2.4 * s, 3.8 * s],
      angle: [-HALF_PI - 0.6, -HALF_PI + 0.6],
      speed: [18 * s, 42 * s],
      gravity: -36 * s,
      life: [360, 620],
    });

    var amountVal = typeof ctx.amount === 'number' ? ctx.amount : ctx.heal;
    if (typeof amountVal === 'number' && amountVal !== 0) {
      VFX.floatText(
        targetEl,
        (amountVal > 0 ? '+' : '') + Math.round(amountVal),
        isMana ? 'mana' : 'heal',
        { scale: 0.92, rise: 34, duration: 760 }
      );
    } else if (ctx.text !== undefined && ctx.text !== null) {
      VFX.floatText(targetEl, String(ctx.text), isMana ? 'mana' : 'heal', {
        scale: 0.92,
        rise: 34,
        duration: 760,
      });
    }
  });

  /**
   * 8. feedback.burst · 通用元素迸发
   * 绝不使用直筒光柱，改用高密度螺旋上升 wisp/ember 流体柱 + flare 星芒迸发
   */
  VFX.register('feedback.burst', function (ctx) {
    var list = getTargets(ctx);
    if (!list.length && ctx.casterEl) list = [ctx.casterEl];
    if (!list.length) return;

    var pal = getPalette(ctx);
    var s = VFX.scale();
    if (ctx.casterEl) {
      VFX.motion(ctx.casterEl, 'cast', { duration: 280 });
    }

    for (var i = 0; i < list.length; i++) {
      (function (el, idx) {
        VFX.after(idx * 90, function () {
          if (!el) return;
          var t = VFX.clampStage(VFX.center(el), 42 * s, 32 * s);
          var f = VFX.clampStage(VFX.foot(el), 42 * s, 32 * s);

          drawSoftGroundHalo(f.x, f.y, 76 * s, 0.44, pal.primary, pal.secondary, 680, 'ground');

          VFX.magicCircle({
            x: f.x,
            y: f.y,
            radius: 52,
            tilt: 0.44,
            layer: 'ground',
            color: pal.primary,
            secondaryColor: pal.secondary,
            style: pal.style,
            duration: 620,
            alpha: 0.76,
            lineWidth: 1.8,
          });

          VFX.shockwave({
            x: t.x,
            y: t.y,
            fromRadius: 10,
            toRadius: 90,
            color: pal.primary,
            coreColor: pal.core,
            lineWidth: 3.4,
            duration: 380,
            double: true,
          });

          // 螺旋上升元素流体柱（零直筒光柱）
          emitFluidVortexColumn({
            x: f.x,
            y: f.y,
            center: t,
            color: pal.primary,
            secondaryColor: pal.secondary,
            coreColor: pal.core,
            radius: 26,
            waves: 2,
          });

          // 中心 flare 星芒与外扩气浪火花
          VFX.burst({
            x: t.x,
            y: t.y,
            radius: 10 * s,
            count: 5,
            shape: 'flare',
            colors: [pal.core, pal.secondary, pal.primary],
            size: [3.6 * s, 6.2 * s],
            speed: [24 * s, 78 * s],
            life: [260, 460],
          });

          VFX.burst({
            x: t.x,
            y: t.y,
            radius: 14 * s,
            count: 24,
            shape: 'spark',
            colors: [pal.primary, pal.secondary, '#ffffff'],
            size: [2.2 * s, 4.6 * s],
            speed: [90 * s, 240 * s],
            drag: 0.93,
            life: [280, 520],
            trail: 4,
          });

          VFX.burst({
            x: t.x,
            y: t.y,
            radius: 18 * s,
            count: 14,
            shape: 'wisp',
            colors: [pal.primary, pal.secondary],
            size: [4.0 * s, 7.6 * s],
            speed: [55 * s, 145 * s],
            gravity: -55 * s,
            turbulence: 52 * s,
            life: [340, 620],
            trail: 3,
          });

          VFX.impact(el, { color: pal.flash, distance: 12, duration: 220 });
        });
      })(list[i], i);
    }
  });
});
