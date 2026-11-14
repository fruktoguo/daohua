/* ability.lightning-attraction · 雷光引 —— 二阶极品天雷引落单体主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   单体敌 / 吟唱 500ms / 1.5×AP+60 / 冷却 28s / 灵力 20 / 五行·木
 * 分镜设计（全粒子流体雷暴天罚，零直筒刚性柱）：
 *   1. cinematicDim 压暗天幕，高空凝结翻滚暗紫深靛雷云（大团软 smoke + 云内暗紫电闪 + 柔羽化雷眼辉光）；
 *   2. 120ms 后极高频率锯齿软羽化主副雷链连续贯穿而下，沿电离通道裂散苍蓝/紫电 wisp 气缕与 flare 电星；
 *   3. 落点瞬间轰出苍蓝电浆 wisp 漩涡、辐射电火花 spark、地面焦裂纹与四射 flare 电星，伴随重度震屏。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  VFX.register('ability.lightning-attraction', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.enemyEls && ctx.enemyEls.length) {
      targets = [ctx.enemyEls[0]];
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    var targetEl = targets[0] || null;
    var s = VFX.scale();
    var st = VFX.size();
    var axis = VFX.combatAxis(casterEl, targetEl || foeSide);
    var targetPt = targetEl ? VFX.center(targetEl) : VFX.sideCenter(foeSide);
    var targetHead = targetEl ? VFX.head(targetEl) : { x: targetPt.x, y: targetPt.y - 38 * s };
    var targetFoot = targetEl ? VFX.foot(targetEl) : { x: targetPt.x, y: targetPt.y + 38 * s };

    var dmg = Math.round(ctx.damage != null ? ctx.damage : 155);
    var isCrit = !!ctx.crit;

    // 阶段一：全景暗幕、施法指尖引雷链与高空阴云翻滚蓄雷
    VFX.cinematicDim(0.42, 700, '#040614');
    VFX.motion(casterEl, 'cast', { duration: 360 });

    var cloudPadX = Math.min(54 * s, st.w * 0.42);
    var maxCloudY = Math.max(30, targetPt.y - 44);
    var cloudPt = {
      x: VFX.clamp(targetPt.x, cloudPadX, Math.max(cloudPadX, st.w - cloudPadX)),
      y: VFX.clamp(targetHead.y - 110 * s, 26, maxCloudY),
    };
    var fingerPt = VFX.clampStage(axis.palm(20 * s), 20 * s, 20 * s);

    // 施法者指尖 → 雷云 紫青引雷电弧/流光链路
    VFX.burst({
      x: fingerPt.x,
      y: fingerPt.y,
      radius: 10 * s,
      count: 6,
      shape: 'flare',
      colors: ['#ffffff', '#a78bfa', '#67e8f9'],
      size: [2.0 * s, 3.8 * s],
      speed: [24 * s, 68 * s],
      life: [160, 280],
    });
    VFX.streamer({
      x1: fingerPt.x,
      y1: fingerPt.y,
      x2: cloudPt.x,
      y2: cloudPt.y,
      arc: -26 * s,
      wobble: 7 * s,
      width: 6.5 * s,
      tailLength: 0.48,
      duration: 170,
      color: '#8b5cf6',
      coreColor: '#67e8f9',
      sparkColors: ['#a78bfa', '#67e8f9', '#ffffff'],
    });
    VFX.lightning({
      delay: 18,
      x1: fingerPt.x,
      y1: fingerPt.y,
      x2: cloudPt.x,
      y2: cloudPt.y,
      width: 3.4 * s,
      jagged: 0.28,
      segments: 14,
      branches: 2,
      flicker: 4,
      color: '#a78bfa',
      secondaryColor: '#e0f2fe',
      glowColor: '#4f46e5',
      haloScale: 1.15,
      coreColor: '#ffffff',
      duration: 155,
    });

    // 高空柔羽化暗紫雷云心辉光（纯径向渐变边缘归零）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 560,
      draw: function (c2d, p) {
        var fade = p < 0.2 ? p / 0.2 : p > 0.72 ? Math.max(0, (1 - p) / 0.28) : 1;
        var flash = 0.5 + 0.5 * Math.abs(Math.sin(p * 26));
        var r = (92 + 24 * Math.sin(p * Math.PI)) * s;
        c2d.save();
        c2d.translate(cloudPt.x, cloudPt.y);
        c2d.scale(1.35, 0.48);
        var g = c2d.createRadialGradient(0, 0, 2, 0, 0, r);
        g.addColorStop(0, 'rgba(224, 242, 254,' + (0.42 * fade * flash).toFixed(3) + ')');
        g.addColorStop(0.32, 'rgba(139, 92, 246,' + (0.34 * fade * (0.6 + 0.4 * flash)).toFixed(3) + ')');
        g.addColorStop(0.68, 'rgba(56, 189, 248,' + (0.16 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(30, 27, 75, 0)');
        c2d.fillStyle = g;
        c2d.beginPath();
        c2d.arc(0, 0, r, 0, TWO_PI);
        c2d.fill();
        c2d.restore();
      },
    });

    // 阴云翻滚：大团软 smoke 与云际游走电浆 wisp
    VFX.burst({
      x: cloudPt.x,
      y: cloudPt.y,
      radius: 76 * s,
      tiltY: 0.36,
      count: 24,
      shape: 'smoke',
      colors: ['#0f172a', '#1e1b4b', '#312e81', '#164e63'],
      size: [22 * s, 42 * s],
      speed: [18 * s, 48 * s],
      vortex: 240 * s,
      vortexCenter: cloudPt,
      drag: 0.96,
      alpha: [0.58, 0.84],
      life: [540, 860],
    });

    VFX.burst({
      x: cloudPt.x,
      y: cloudPt.y,
      radius: 62 * s,
      tiltY: 0.36,
      count: 16,
      shape: 'wisp',
      colors: ['#8b5cf6', '#67e8f9', '#38bdf8'],
      size: [5.5 * s, 10.0 * s],
      speed: [26 * s, 68 * s],
      vortex: 310 * s,
      vortexCenter: cloudPt,
      drag: 0.95,
      alpha: 0.56,
      life: [380, 620],
    });

    // 高空暗紫与青白交错电闪预兆
    var cPreA1 = VFX.clampStage({ x: cloudPt.x - 56 * s, y: cloudPt.y - 8 * s }, 12, 12);
    var cPreA2 = VFX.clampStage({ x: cloudPt.x + 48 * s, y: cloudPt.y + 10 * s }, 12, 12);
    VFX.lightning({
      x1: cPreA1.x,
      y1: cPreA1.y,
      x2: cPreA2.x,
      y2: cPreA2.y,
      width: 3.4 * s,
      jagged: 0.36,
      segments: 12,
      branches: 3,
      flicker: 4,
      color: '#a78bfa',
      secondaryColor: '#e0f2fe',
      glowColor: '#4f46e5',
      haloScale: 1.15,
      coreColor: '#ffffff',
      duration: 155,
    });
    var cPreB1 = VFX.clampStage({ x: cloudPt.x + 44 * s, y: cloudPt.y - 12 * s }, 12, 12);
    var cPreB2 = VFX.clampStage({ x: cloudPt.x - 38 * s, y: cloudPt.y + 14 * s }, 12, 12);
    VFX.lightning({
      delay: 45,
      x1: cPreB1.x,
      y1: cPreB1.y,
      x2: cPreB2.x,
      y2: cPreB2.y,
      width: 3.0 * s,
      jagged: 0.34,
      segments: 12,
      branches: 2,
      flicker: 3,
      color: '#67e8f9',
      secondaryColor: '#e0f2fe',
      glowColor: '#4f46e5',
      haloScale: 1.15,
      coreColor: '#ffffff',
      duration: 135,
    });

    // 阶段二：120ms 后云口电离吞吐光晕 + 高密度立体分层主副雷链贯穿而下 + 靶心爆闪光晕
    VFX.after(120, function () {
      var safePt = VFX.clampStage(targetPt, 32 * s, 26 * s);
      var safeFoot = VFX.clampStage(targetFoot, 34 * s, 18);
      var skyTopY = Math.max(12, cloudPt.y - 10 * s);
      var strikeY = safeFoot.y - 4 * s;

      // 云口电离吞吐光晕：令天雷自发光云涡中劈出
      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 360,
        draw: function (c2d, p) {
          var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.45);
          var r = 82 * s * (0.72 + 0.28 * Math.sin(p * Math.PI));
          c2d.save();
          c2d.translate(cloudPt.x, skyTopY + 10 * s);
          c2d.scale(1.45, 0.55);
          var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
          g.addColorStop(0, 'rgba(240, 249, 255,' + (0.82 * fade).toFixed(3) + ')');
          g.addColorStop(0.28, 'rgba(56, 189, 248,' + (0.5 * fade).toFixed(3) + ')');
          g.addColorStop(0.62, 'rgba(139, 92, 246,' + (0.28 * fade).toFixed(3) + ')');
          g.addColorStop(1, 'rgba(79, 70, 229, 0)');
          c2d.fillStyle = g;
          c2d.beginPath();
          c2d.arc(0, 0, r, 0, TWO_PI);
          c2d.fill();
          c2d.restore();
        },
      });

      // 靶心胸口瞬态电离大气光晕（与地面光斑上下呼应）
      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 380,
        draw: function (c2d, p) {
          var fade = p < 0.12 ? p / 0.12 : Math.pow(1 - (p - 0.12) / 0.88, 1.4);
          var r = 86 * s * (0.62 + 0.38 * Math.min(1, p * 3));
          c2d.save();
          c2d.translate(safePt.x, safePt.y);
          var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
          g.addColorStop(0, 'rgba(255, 255, 255,' + (0.74 * fade).toFixed(3) + ')');
          g.addColorStop(0.28, 'rgba(103, 232, 249,' + (0.46 * fade).toFixed(3) + ')');
          g.addColorStop(0.62, 'rgba(139, 92, 246,' + (0.24 * fade).toFixed(3) + ')');
          g.addColorStop(1, 'rgba(99, 102, 241, 0)');
          c2d.fillStyle = g;
          c2d.beginPath();
          c2d.arc(0, 0, r, 0, TWO_PI);
          c2d.fill();
          c2d.restore();
        },
      });

      // 三重错峰高频锯齿分形主副雷链（高密分支 + 多色温光晕）
      VFX.lightning({
        x1: cloudPt.x - 10 * s,
        y1: skyTopY,
        x2: safeFoot.x - 4 * s,
        y2: strikeY,
        width: 9.2 * s,
        jagged: 0.25,
        segments: 24,
        branches: 6,
        flicker: 6,
        color: '#38bdf8',
        secondaryColor: '#bae6fd',
        glowColor: '#6366f1',
        haloScale: 1.3,
        coreColor: '#ffffff',
        duration: 400,
      });

      VFX.lightning({
        delay: 28,
        x1: cloudPt.x + 18 * s,
        y1: skyTopY,
        x2: safeFoot.x + 8 * s,
        y2: strikeY - 2 * s,
        width: 6.4 * s,
        jagged: 0.3,
        segments: 20,
        branches: 5,
        flicker: 6,
        color: '#a855f7',
        secondaryColor: '#f3e8ff',
        glowColor: '#4338ca',
        haloScale: 1.15,
        coreColor: '#e0f2fe',
        duration: 360,
      });

      VFX.lightning({
        delay: 85,
        x1: cloudPt.x - 4 * s,
        y1: skyTopY + 6 * s,
        x2: safeFoot.x + 2 * s,
        y2: strikeY,
        width: 5.0 * s,
        jagged: 0.28,
        segments: 18,
        branches: 4,
        flicker: 5,
        color: '#5be4a8',
        secondaryColor: '#ccfbf1',
        glowColor: '#0284c7',
        haloScale: 1.1,
        coreColor: '#ffffff',
        duration: 320,
      });

      // 沿雷链电离通道溅开游离电浆 wisp 与零星 flare（营造空气撕裂体积感而非直筒柱）
      for (var c = 0; c < 4; c++) {
        var frac = (c + 0.4) / 4.2;
        var cy = VFX.lerp(skyTopY, strikeY, frac);
        var cx = VFX.lerp(cloudPt.x, safeFoot.x, frac) + VFX.rand(-12, 12) * s;
        VFX.burst({
          delay: c * 22,
          x: cx,
          y: cy,
          radius: 16 * s,
          count: 5,
          shape: 'wisp',
          colors: ['#38bdf8', '#67e8f9', '#a78bfa'],
          size: [4.2 * s, 7.8 * s],
          speed: [35 * s, 110 * s],
          turbulence: 140 * s,
          drag: 0.93,
          alpha: 0.68,
          life: [220, 420],
        });
      }

      // 落点柔羽化电浆地面光斑
      VFX.custom({
        layer: 'ground',
        blend: 'lighter',
        duration: 520,
        draw: function (c2d, p) {
          var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.5);
          var r = 108 * s * (0.65 + 0.35 * Math.min(1, p * 3.5));
          c2d.save();
          c2d.translate(safeFoot.x, safeFoot.y);
          c2d.scale(1, 0.44);
          var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
          g.addColorStop(0, 'rgba(255, 255, 255,' + (0.78 * fade).toFixed(3) + ')');
          g.addColorStop(0.24, 'rgba(103, 232, 249,' + (0.52 * fade).toFixed(3) + ')');
          g.addColorStop(0.58, 'rgba(139, 92, 246,' + (0.24 * fade).toFixed(3) + ')');
          g.addColorStop(1, 'rgba(56, 189, 248, 0)');
          c2d.fillStyle = g;
          c2d.beginPath();
          c2d.arc(0, 0, r, 0, TWO_PI);
          c2d.fill();
          c2d.restore();
        },
      });

      // 落点电磁高斯波纹与地面焦裂纹（内置原语自动乘以 VFX.scale()）
      VFX.shockwave({
        x: safeFoot.x,
        y: safeFoot.y,
        fromRadius: 14,
        toRadius: 142,
        tilt: 0.44,
        layer: 'ground',
        color: '#38bdf8',
        coreColor: '#ffffff',
        lineWidth: 5.0,
        duration: 440,
      });

      VFX.shockwave({
        x: safePt.x,
        y: safePt.y,
        fromRadius: 10,
        toRadius: 98,
        tilt: 0.9,
        layer: 'sky',
        color: '#a78bfa',
        coreColor: '#ffffff',
        lineWidth: 4.0,
        duration: 360,
      });

      VFX.groundCrack({
        x: safeFoot.x,
        y: safeFoot.y,
        radius: 100,
        branches: 9,
        color: '#38bdf8',
        crackColor: '#060912',
        lineWidth: 3.6,
        duration: 1700,
      });

      // 落点苍蓝电浆 wisp 漩涡
      VFX.burst({
        x: safePt.x,
        y: safePt.y + 8 * s,
        radius: 26 * s,
        count: 26,
        shape: 'wisp',
        colors: ['#38bdf8', '#67e8f9', '#a78bfa', '#e0f2fe'],
        size: [5.0 * s, 10.5 * s],
        speed: [55 * s, 165 * s],
        vortex: 440 * s,
        vortexCenter: safePt,
        gravity: -45 * s,
        turbulence: 145 * s,
        drag: 0.94,
        life: [320, 620],
      });

      // 辐射电火花 spark
      VFX.burst({
        x: safePt.x,
        y: safePt.y + 6 * s,
        count: 42,
        shape: 'spark',
        colors: ['#38bdf8', '#67e8f9', '#5be4a8', '#ffffff'],
        size: [2.2 * s, 4.6 * s],
        speed: [130 * s, 330 * s],
        gravity: 175 * s,
        drag: 0.93,
        life: [260, 520],
        trail: 5,
      });

      // 四射 flare 电星
      VFX.burst({
        x: safePt.x,
        y: safePt.y,
        radius: 16 * s,
        count: 16,
        shape: 'flare',
        colors: ['#ffffff', '#67e8f9', '#a78bfa', '#5be4a8'],
        size: [3.2 * s, 6.8 * s],
        speed: [80 * s, 235 * s],
        drag: 0.91,
        life: [240, 460],
      });

      // 受击后余波跳跃电弧与星点
      VFX.after(140, function () {
        var arc1 = VFX.clampStage({ x: safePt.x - 30 * s, y: safePt.y - 26 * s }, 10, 10);
        var arc2 = VFX.clampStage({ x: safePt.x + 28 * s, y: safePt.y + 24 * s }, 10, 10);
        VFX.lightning({
          x1: arc1.x,
          y1: arc1.y,
          x2: arc2.x,
          y2: arc2.y,
          width: 3.2 * s,
          jagged: 0.34,
          segments: 12,
          branches: 2,
          flicker: 3,
          color: '#67e8f9',
          secondaryColor: '#e0f2fe',
          glowColor: '#6366f1',
          haloScale: 1.15,
          coreColor: '#ffffff',
          duration: 185,
        });
        VFX.burst({
          x: safePt.x,
          y: safePt.y,
          radius: 22 * s,
          count: 8,
          shape: 'flare',
          colors: ['#ffffff', '#38bdf8'],
          size: [2.2 * s, 4.2 * s],
          speed: [35 * s, 110 * s],
          life: [180, 320],
        });
      });

      VFX.quake('lg', 340);
      if (targetEl) {
        VFX.impact(targetEl, { color: '#38bdf8', duration: 280, distance: 18 });
      }
      VFX.floatText(targetEl || safePt, '-' + dmg, isCrit ? 'crit' : 'damage', {
        scale: isCrit ? 1.35 : 1.15,
      });
    });

    return 1800;
  });
});
