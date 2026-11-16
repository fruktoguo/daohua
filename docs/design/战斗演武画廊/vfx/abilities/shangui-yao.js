/* ability.shangui-yao · 山鬼谣 —— 二阶符箓全体甘露群疗主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   己方全体 / 吟唱 3000ms / 施加 12%AP+5 层「甘露」（10s） / 冷却 35s / 灵力 40 / 符箓·水
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  VFX.register('ability.shangui-yao', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var allySide = isEnemyCaster ? 'enemy' : 'ally';
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls.slice() : [];
    if (!targets.length) {
      var rawPool = isEnemyCaster ? ctx.enemyEls : ctx.allyEls;
      if (rawPool && rawPool.length) targets = rawPool.slice();
      else if (ctx.targetEl) targets = [ctx.targetEl];
      else if (casterEl) targets = [casterEl];
    }

    var s = VFX.scale();
    var st = VFX.size();
    var cRaw = casterEl ? VFX.center(casterEl) : VFX.sideCenter(allySide);
    var cPt = VFX.clampStage(cRaw, 26 * s, 26 * s);

    VFX.motion(casterEl, 'cast', { duration: 480 });
    VFX.cinematicDim(0.24, 620, '#031514');

    // 阶段一：施法者脚下展开幽翠山水灵阵并扬起飞瓣与碧水灵雾
    var cFoot = casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(allySide);
    VFX.magicCircle({
      x: cFoot.x,
      y: cFoot.y,
      radius: 68,
      tilt: 0.44,
      layer: 'ground',
      color: '#2dd4bf',
      secondaryColor: '#38bdf8',
      style: 'water',
      rotationSpeed: 1.6,
      duration: 840,
      alpha: 0.72,
      lineWidth: 2.0,
    });
    VFX.burst({
      x: cPt.x,
      y: cPt.y,
      radius: 22 * s,
      count: 14,
      shape: 'petal',
      colors: ['#99f6e4', '#5eead4', '#bae6fd', '#ffffff'],
      size: [3.0 * s, 5.4 * s],
      speed: [30 * s, 85 * s],
      gravity: -42 * s,
      life: [480, 820],
    });

    // 阶段二：碧水青岚流光横贯全体存活友方，各处齐降清透甘露灵雨
    var totalT = 1100;
    var count = Math.max(1, targets.length);
    for (var i = 0; i < count; i++) {
      (function (idx) {
        var el = targets[idx] || null;
        var pt = el ? VFX.center(el) : VFX.sideCenter(allySide);
        var hd = el ? VFX.head(el) : { x: pt.x, y: pt.y - 38 * s };
        var ft = el ? VFX.foot(el) : { x: pt.x, y: pt.y + 38 * s };
        var safePt = VFX.clampStage(pt, 28 * s, 24 * s);
        var safeFoot = VFX.clampStage(ft, 28 * s, 18);
        var topPt = {
          x: safePt.x,
          y: VFX.clamp(hd.y - 46 * s, 24, Math.max(28, safePt.y - 26)),
        };

        VFX.streamer({
          delay: idx * 65,
          x1: cPt.x,
          y1: cPt.y - 10 * s,
          x2: topPt.x,
          y2: topPt.y,
          arc: -48 * s,
          wobble: 8 * s,
          width: 7 * s,
          tailLength: 0.44,
          duration: 300,
          color: '#2dd4bf',
          coreColor: '#e0f2fe',
          sparkColors: ['#5eead4', '#7dd3fc', '#ffffff'],
          onArrive: function () {
            VFX.custom({
              layer: 'sky',
              blend: 'lighter',
              duration: 720,
              draw: function (c2d, p) {
                var fade = p < 0.18 ? p / 0.18 : Math.pow(1 - (p - 0.18) / 0.82, 1.35);
                var r = 52 * s * (0.7 + 0.3 * Math.sin(p * Math.PI));
                c2d.save();
                c2d.translate(topPt.x, topPt.y);
                c2d.scale(1.38, 0.44);
                var g = c2d.createRadialGradient(0, 0, 2, 0, 0, r);
                g.addColorStop(0, 'rgba(224,242,254,' + (0.48 * fade).toFixed(3) + ')');
                g.addColorStop(0.46, 'rgba(94,234,212,' + (0.28 * fade).toFixed(3) + ')');
                g.addColorStop(1, 'rgba(56,189,248,0)');
                c2d.fillStyle = g;
                c2d.beginPath();
                c2d.arc(0, 0, r, 0, TWO_PI);
                c2d.fill();
                c2d.restore();
              },
            });

            VFX.burst({
              x: topPt.x,
              y: topPt.y + 6 * s,
              radius: 26 * s,
              count: 14,
              shape: 'drop',
              colors: ['#5eead4', '#7dd3fc', '#bae6fd', '#ffffff'],
              size: [2.2 * s, 3.8 * s],
              speed: [40 * s, 72 * s],
              baseAngle: Math.PI * 0.5,
              spread: 0.35,
              gravity: 85 * s,
              life: [320, 520],
            });
            VFX.burst({
              x: safePt.x,
              y: safePt.y,
              radius: 24 * s,
              count: 10,
              shape: 'orb',
              colors: ['#2dd4bf', '#34d399', '#e0f2fe'],
              size: [2.6 * s, 4.8 * s],
              speed: [18 * s, 52 * s],
              gravity: -45 * s,
              life: [420, 740],
            });
            VFX.shockwave({
              x: safeFoot.x,
              y: safeFoot.y,
              tilt: 0.44,
              fromRadius: 10,
              toRadius: 76,
              layer: 'ground',
              color: '#38bdf8',
              coreColor: '#ccfbf1',
              lineWidth: 2.8,
              duration: 420,
            });
            VFX.floatText(el || safePt, '甘露', 'heal', { scale: 0.96 });
          },
        });
        totalT = Math.max(totalT, idx * 65 + 1020);
      })(i);
    }

    return Math.min(totalT, Math.max(1200, st.w > 0 ? totalT : 1200));
  });
});
