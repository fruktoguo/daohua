/**
 * 道化太初 · 演武画廊 · 战斗符箓特效组 (v2.2 自适应坐标与流体粒子版)
 *
 * 覆盖战斗符箓：
 *   item.leiji-talisman（雷击符 · 1阶瞬发）
 *
 * 视觉重构重点：
 *   彻底删除硬矩形剑芒、直角外框与硬棱形碎片！
 *   1. 飞符起手（0~320ms）：自 combatAxis 掌心起点呼啸飞往安全悬空符燃点，沿途洒落轻烟金粉余烬；
 *   2. 柔边灵符自燃 & 紫雷云涡蓄能（320~520ms）：安全雷云锁定天穹且严格高于符燃点与目标胸心，
 *      四散 rune / ember 金粉，上空聚拢羽化紫雷云团与多股极细 wisp 雷涡蓄能；
 *   3. 天雷狂落爆闪（520ms 起）：狂暴分段柔性天雷劈下，轰出多向 flare 炽白爆闪与漫天游走电弧 spark。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  function resolveCasterEl(ctx) {
    return ctx.casterEl || (ctx.caster && ctx.caster.el) || null;
  }

  function resolveTargetEl(ctx) {
    return (
      ctx.targetEl ||
      (ctx.targetEls && ctx.targetEls[0]) ||
      (ctx.targets && ctx.targets[0] && ctx.targets[0].el) ||
      null
    );
  }

  /**
   * 实时计算雷击符多画幅安全坐标：
   * 保证「施法者掌心 → 悬空符燃点 → 上空紫雷云 → 目标中心/脚底」在横竖屏与顶排目标下均保持正确上下层级且不切顶。
   */
  function resolveTalismanAnchors(casterEl, targetEl) {
    var s = VFX.scale();
    var st = VFX.size();
    var axis = VFX.combatAxis(casterEl, targetEl);
    var castPt = VFX.clampStage(axis.palm(16 * s), 20 * s, 18 * s);
    var tc = VFX.clampStage(VFX.center(targetEl), 24 * s, 20 * s);
    var th = VFX.head(targetEl);
    var tf = VFX.clampStage(VFX.foot(targetEl), 24 * s, 18 * s);

    var cloudPt = {
      x: VFX.clamp(tc.x, 48 * s, Math.max(48 * s, st.w - 48 * s)),
      y: VFX.clamp(th.y - 88 * s, 22, Math.max(26, tc.y - 52)),
    };
    var burnPt = {
      x: VFX.lerp(castPt.x, cloudPt.x, 0.86),
      y: VFX.lerp(cloudPt.y, tc.y, 0.46),
    };

    return {
      s: s,
      st: st,
      axis: axis,
      castPt: castPt,
      tc: tc,
      th: th,
      tf: tf,
      cloudPt: cloudPt,
      burnPt: burnPt,
    };
  }

  /**
   * item.leiji-talisman · 雷击符
   * 消耗 5% 当前内力（造成内力 20% 直伤 + 2.0×法攻），引天地雷霆轰穿单体强敌。
   */
  VFX.register('item.leiji-talisman', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var targetEl = resolveTargetEl(ctx);

    var a0 = resolveTalismanAnchors(casterEl, targetEl);
    var s0 = a0.s;
    var castPt0 = a0.castPt;
    var burnPt0 = a0.burnPt;
    var midY = (castPt0.y + burnPt0.y) * 0.5;
    var arcAmt = -Math.min(64 * s0, Math.max(14 * s0, midY - 20));

    var rawDmg = typeof ctx.damage === 'number' && ctx.damage > 0 ? Math.round(ctx.damage) : 168;
    var manaDirectDmg = Math.max(12, Math.round(rawDmg * 0.25));
    var spellDmg = Math.max(1, rawDmg - manaDirectDmg);

    // ---------------- 第一段：祭符飞掷（0~320ms） ----------------
    VFX.motion(casterEl, 'cast', { duration: 300 });
    VFX.hitFlash(casterEl, '#facc15', 160);

    // 掌心祭符金辉流丝
    VFX.burst({
      x: castPt0.x,
      y: castPt0.y,
      radius: 12 * s0,
      count: 10,
      shape: 'ember',
      colors: ['#fde047', '#f59e0b', '#fef9c3'],
      size: [1.8 * s0, 3.6 * s0],
      speed: [35 * s0, 95 * s0],
      life: [220, 380],
    });

    // 实体朱砂灵符自掌心沿 2D 曲率飞掷至悬空符燃点
    VFX.artifact({
      kind: 'talisman',
      x1: castPt0.x,
      y1: castPt0.y,
      x2: burnPt0.x,
      y2: burnPt0.y,
      arc: arcAmt,
      duration: 340,
      size: 22 * s0,
      scaleFrom: 0.8,
      scaleTo: 1.15,
      spin: 9.0,
      color: '#dc2626',
      accentColor: '#fef08a',
      glowColor: '#facc15',
      trail: true,
    });

    // 沿途洒下金色余烬与微光流雾拖尾
    var trailSteps = [0.26, 0.5, 0.74];
    for (var sIdx = 0; sIdx < trailSteps.length; sIdx++) {
      (function (tFrac, delayMs) {
        VFX.after(delayMs, function () {
          var ai = resolveTalismanAnchors(casterEl, targetEl);
          var si = ai.s;
          var tArc = -Math.min(64 * si, Math.max(14 * si, (ai.castPt.y + ai.burnPt.y) * 0.5 - 20));
          var trailPt = VFX.clampStage(
            {
              x: VFX.lerp(ai.castPt.x, ai.burnPt.x, tFrac),
              y: VFX.lerp(ai.castPt.y, ai.burnPt.y, tFrac) + Math.sin(tFrac * Math.PI) * tArc,
            },
            12 * si,
            12 * si
          );
          VFX.burst({
            x: trailPt.x,
            y: trailPt.y,
            radius: 8 * si,
            count: 7,
            shape: 'ember',
            colors: ['#fde047', '#facc15', '#fb923c'],
            size: [1.8 * si, 3.2 * si],
            speed: [22 * si, 62 * si],
            gravity: 24 * si,
            life: [220, 400],
          });
        });
      })(trailSteps[sIdx], Math.round(sIdx * 80 + 60));
    }

    // ---------------- 第二段：柔边灵符悬空自燃 + 紫雷云涡蓄能（320~520ms） ----------------
    VFX.after(320, function () {
      var a1 = resolveTalismanAnchors(casterEl, targetEl);
      var s1 = a1.s;
      var burn1 = a1.burnPt;
      var cloud1 = a1.cloudPt;

      VFX.cinematicDim(0.28, 620, '#070516');

      // 燃烧青紫篆文微光的柔边灵符悬空自燃（纯径向柔光渐变与曲线篆文，零硬矩形外框）
      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 260,
        state: { x: burn1.x, y: burn1.y, s: s1 },
        draw: function (g, p, ef) {
          var sc = ef.state.s || 1;
          var fade = p < 0.25 ? p / 0.25 : Math.pow(1 - (p - 0.25) / 0.75, 1.3);
          var sway = Math.sin(p * 14) * 0.14;
          var r = 24 * sc * (1 + p * 0.28);

          g.save();
          g.translate(ef.state.x, ef.state.y - p * 10 * sc);
          g.rotate(sway);

          // 柔边燃烧光核（径向高斯渐变替代矩形纸片）
          var aura = g.createRadialGradient(0, 0, 1, 0, 0, r * 1.35);
          aura.addColorStop(0, 'rgba(254,240,138,' + (0.72 * fade).toFixed(3) + ')');
          aura.addColorStop(0.45, 'rgba(168,85,247,' + (0.42 * fade).toFixed(3) + ')');
          aura.addColorStop(0.8, 'rgba(56,189,248,' + (0.14 * fade).toFixed(3) + ')');
          aura.addColorStop(1, 'rgba(56,189,248,0)');
          g.fillStyle = aura;
          g.beginPath();
          g.ellipse(0, 0, r * 0.85, r * 1.35, 0, 0, Math.PI * 2);
          g.fill();

          // 燃烧卷曲的三层柔光青紫篆文笔画：宽幅紫光外晕 + 主色篆文 + 极细炽白灵芯
          var baseLw = Math.max(1.0, 2.0 * sc * (1 - p * 0.4));
          g.lineCap = 'round';
          g.lineJoin = 'round';
          g.beginPath();
          g.moveTo(0, -r * 0.75);
          g.bezierCurveTo(-r * 0.48, -r * 0.3, r * 0.48, -r * 0.05, -r * 0.32, r * 0.38);
          g.quadraticCurveTo(r * 0.15, r * 0.62, r * 0.36, r * 0.76);
          g.moveTo(-r * 0.42, -r * 0.35);
          g.quadraticCurveTo(0, -r * 0.48, r * 0.42, -r * 0.32);

          g.strokeStyle = '#a855f7';
          g.globalAlpha = fade * 0.25;
          g.lineWidth = baseLw * 3.4;
          g.stroke();

          g.strokeStyle = p < 0.45 ? '#e9d5ff' : '#7dd3fc';
          g.globalAlpha = fade * 0.85;
          g.lineWidth = baseLw;
          g.stroke();

          g.strokeStyle = '#ffffff';
          g.globalAlpha = fade * 0.78;
          g.lineWidth = Math.max(0.5, baseLw * 0.45);
          g.stroke();

          g.restore();
        },
      });

      // 四散 rune 篆文粒子与 ember 金粉
      VFX.burst({
        x: burn1.x,
        y: burn1.y,
        radius: 14 * s1,
        count: 14,
        shape: 'rune',
        colors: ['#c084fc', '#fde047', '#38bdf8', '#e9d5ff'],
        size: [2.8 * s1, 5.0 * s1],
        speed: [42 * s1, 118 * s1],
        gravity: -65 * s1,
        spin: [-4.5, 4.5],
        drag: 0.94,
        life: [340, 620],
      });

      VFX.burst({
        x: burn1.x,
        y: burn1.y,
        radius: 14 * s1,
        count: 22,
        shape: 'ember',
        colors: ['#fde047', '#fbbf24', '#c084fc', '#ffffff'],
        size: [1.8 * s1, 3.8 * s1],
        speed: [38 * s1, 125 * s1],
        gravity: -85 * s1,
        turbulence: 52 * s1,
        drag: 0.94,
        life: [360, 640],
      });

      // 符燃轻波纹（core.js 内置缩放，不重复乘 s）
      VFX.shockwave({
        x: burn1.x,
        y: burn1.y,
        tilt: 0.76,
        fromRadius: 8,
        toRadius: 68,
        color: '#c084fc',
        coreColor: '#fef08a',
        lineWidth: 3.0,
        double: false,
        duration: 240,
      });

      // 上空浮现羽化紫雷云团
      VFX.burst({
        x: cloud1.x,
        y: cloud1.y,
        radius: 56 * s1,
        tiltY: 0.42,
        count: 18,
        shape: 'smoke',
        colors: ['#312e81', '#4c1d95', '#1e1b4b', '#3b0764'],
        size: [18 * s1, 32 * s1],
        speed: [14 * s1, 38 * s1],
        vortex: 240 * s1,
        vortexCenter: cloud1,
        drag: 0.95,
        alpha: 0.76,
        life: [460, 740],
      });

      // 多股极细 wisp 雷涡蓄能聚拢于云团中心
      VFX.burst({
        x: cloud1.x,
        y: cloud1.y,
        radius: 78 * s1,
        ring: true,
        tiltY: 0.46,
        count: 26,
        shape: 'wisp',
        colors: ['#a855f7', '#38bdf8', '#e9d5ff', '#bae6fd'],
        size: [1.6 * s1, 3.0 * s1],
        speed: [20 * s1, 44 * s1],
        vortex: -520 * s1,
        vortexCenter: cloud1,
        attractTo: cloud1,
        attractStrength: 860 * s1,
        drag: 0.92,
        alpha: 0.86,
        life: [220, 380],
        trail: 5,
      });

      // 云团内细游走电弧预兆
      VFX.lightning({
        x1: cloud1.x - 40 * s1,
        y1: cloud1.y - 4 * s1,
        x2: cloud1.x + 36 * s1,
        y2: cloud1.y + 8 * s1,
        jagged: 0.34,
        segments: 14,
        branches: 2,
        flicker: 3,
        color: '#c084fc',
        secondaryColor: '#f3e8ff',
        glowColor: '#581c87',
        haloScale: 1.2,
        coreColor: '#ffffff',
        width: 2.8 * s1,
        duration: 185,
      });
    });

    // ---------------- 第三段：狂暴分段柔性天雷劈下 + 多向 flare 爆闪 + 游走电弧（505ms 起） ----------------
    VFX.after(505, function () {
      var a2 = resolveTalismanAnchors(casterEl, targetEl);
      var s2 = a2.s;
      var cloud2 = a2.cloudPt;
      var tc2 = a2.tc;
      var tf2 = a2.tf;

      // 第一段天雷：自紫雷云涡中心劈至敌体胸口，再贯入地面
      VFX.lightning({
        x1: cloud2.x - 4 * s2,
        y1: cloud2.y,
        x2: tc2.x,
        y2: tc2.y - 4 * s2,
        jagged: 0.24,
        segments: 20,
        branches: 5,
        flicker: 5,
        color: '#a855f7',
        secondaryColor: '#f3e8ff',
        glowColor: '#581c87',
        haloScale: 1.24,
        coreColor: '#ffffff',
        width: 7.4 * s2,
        duration: 330,
      });

      // 贯体续落分支至脚底大地
      VFX.lightning({
        x1: tc2.x,
        y1: tc2.y - 4 * s2,
        x2: tf2.x,
        y2: tf2.y - 4 * s2,
        jagged: 0.22,
        segments: 14,
        branches: 3,
        flicker: 4,
        color: '#38bdf8',
        secondaryColor: '#bae6fd',
        glowColor: '#3730a3',
        haloScale: 1.2,
        coreColor: '#ffffff',
        width: 5.8 * s2,
        duration: 300,
        delay: 25,
      });

      // 次级交缠青蓝柔性电弧伴随劈下
      VFX.lightning({
        x1: cloud2.x + 18 * s2,
        y1: cloud2.y + 4 * s2,
        x2: tc2.x + 8 * s2,
        y2: tc2.y + 6 * s2,
        jagged: 0.3,
        segments: 16,
        branches: 3,
        flicker: 4,
        color: '#38bdf8',
        secondaryColor: '#bae6fd',
        glowColor: '#3730a3',
        haloScale: 1.2,
        coreColor: '#e0f2fe',
        width: 4.0 * s2,
        duration: 295,
        delay: 30,
      });
    });

    // 雷击命中爆裂
    VFX.after(535, function () {
      var a3 = resolveTalismanAnchors(casterEl, targetEl);
      var s3 = a3.s;
      var tc3 = a3.tc;
      var tf3 = a3.tf;

      // 靶心柔和紫青双色径向电爆大气光晕
      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 340,
        draw: function (g, p) {
          var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.4);
          var r = 82 * s3 * (0.58 + 0.42 * p);
          g.save();
          g.translate(tc3.x, tc3.y);
          var grad = g.createRadialGradient(0, 0, 1, 0, 0, r);
          grad.addColorStop(0, 'rgba(255, 255, 255,' + (0.78 * fade).toFixed(3) + ')');
          grad.addColorStop(0.28, 'rgba(192, 132, 252,' + (0.52 * fade).toFixed(3) + ')');
          grad.addColorStop(0.6, 'rgba(56, 189, 248,' + (0.26 * fade).toFixed(3) + ')');
          grad.addColorStop(0.84, 'rgba(88, 28, 135,' + (0.12 * fade).toFixed(3) + ')');
          grad.addColorStop(1, 'rgba(88, 28, 135, 0)');
          g.fillStyle = grad;
          g.beginPath();
          g.arc(0, 0, r, 0, Math.PI * 2);
          g.fill();
          g.restore();
        },
      });

      VFX.groundCrack({
        x: tf3.x,
        y: tf3.y,
        radius: 90,
        branches: 8,
        color: '#a855f7',
        crackColor: '#070b14',
        lineWidth: 3.4,
        duration: 1450,
      });

      VFX.quake('lg', 270);
      VFX.impact(targetEl, { color: '#c084fc', duration: 260, distance: 16 });

      // 胸口与地面柔光电磁波纹（core.js 内置缩放，不重复乘 s）
      VFX.shockwave({
        x: tc3.x,
        y: tc3.y,
        fromRadius: 12,
        toRadius: 114,
        color: '#a855f7',
        coreColor: '#ffffff',
        lineWidth: 4.8,
        double: true,
        duration: 420,
      });

      VFX.shockwave({
        x: tf3.x,
        y: tf3.y,
        tilt: 0.44,
        fromRadius: 14,
        toRadius: 112,
        color: '#38bdf8',
        coreColor: '#f3e8ff',
        lineWidth: 3.8,
        double: true,
        duration: 460,
      });

      // 轰出多向 flare 炽白爆闪
      VFX.burst({
        x: tc3.x,
        y: tc3.y,
        radius: 20 * s3,
        count: 14,
        shape: 'flare',
        colors: ['#ffffff', '#e0f2fe', '#e9d5ff', '#38bdf8'],
        size: [2.8 * s3, 5.8 * s3],
        speed: [45 * s3, 165 * s3],
        drag: 0.91,
        alpha: 0.96,
        life: [320, 580],
      });

      // 漫天游走电弧 spark（高扰动与拖尾）
      VFX.burst({
        x: tc3.x,
        y: tc3.y,
        radius: 18 * s3,
        count: 38,
        shape: 'spark',
        colors: ['#38bdf8', '#a855f7', '#e0f2fe', '#ffffff'],
        size: [2.0 * s3, 4.6 * s3],
        speed: [115 * s3, 275 * s3],
        turbulence: 165 * s3,
        drag: 0.91,
        life: [300, 580],
        trail: 6,
      });

      // 柔雾电浆余气（替代硬菱形碎片）
      VFX.burst({
        x: tf3.x,
        y: tf3.y - 6 * s3,
        radius: 22 * s3,
        count: 16,
        shape: 'wisp',
        colors: ['#a855f7', '#38bdf8', '#e9d5ff'],
        size: [2.2 * s3, 4.2 * s3],
        speed: [62 * s3, 155 * s3],
        gravity: -45 * s3,
        turbulence: 88 * s3,
        drag: 0.93,
        alpha: 0.72,
        life: [320, 580],
        trail: 4,
      });

      VFX.floatText(targetEl, '-' + spellDmg, 'crit', { scale: 1.24 });
    });

    VFX.after(645, function () {
      var a4 = resolveTalismanAnchors(casterEl, targetEl);
      var s4 = a4.s;
      VFX.floatText(
        { x: a4.th.x + 24 * s4, y: a4.th.y - 12 * s4 },
        '-' + manaDirectDmg + ' 灵涌直伤',
        '#38bdf8',
        { scale: 0.96, rise: 48 }
      );
    });

    return 1500;
  });
});
