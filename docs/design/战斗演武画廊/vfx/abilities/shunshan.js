/* ability.shunshan · 瞬闪 —— 极速瞬影单体突刺；先导移形击杀可重置冷却
 * 真源镜像：config/game-data/abilities/physical_abilities.json
 *   单体敌 / 吟唱 500ms / 2.4×AD+100 / 无移形时赋移形，有移形消耗并击杀刷 CD / 冷却 24s / 灵力 20 / 剑修·金
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  VFX.register('ability.shunshan', function (ctx) {
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
    var axis = VFX.combatAxis(casterEl, targetEl || foeSide);
    var cRaw = casterEl ? VFX.center(casterEl) : VFX.sideCenter(isEnemyCaster ? 'enemy' : 'ally');
    var cCaster = VFX.clampStage(cRaw, 30 * s, 24 * s);
    var tRaw = targetEl ? VFX.center(targetEl) : VFX.sideCenter(foeSide);
    var tSafe = VFX.clampStage(tRaw, 30 * s, 24 * s);
    var dmg = Math.round(ctx.damage != null ? ctx.damage : 260);
    var isCrit = !!ctx.crit;

    VFX.cinematicDim(0.32, 520, '#090b12');

    // 阶段一：施法者残影迸散并疾掠突刺
    if (casterEl) {
      VFX.motion(casterEl, 'dash', { dx: axis.ux, dy: axis.uy, duration: 240 });
    }
    VFX.burst({
      x: cCaster.x,
      y: cCaster.y,
      radius: 18 * s,
      count: 10,
      shape: 'wisp',
      colors: ['#5eead4', '#fef08a', '#e0f2fe'],
      size: [4.2 * s, 8.2 * s],
      speed: [32 * s, 85 * s],
      alpha: 0.65,
      life: [220, 420],
    });

    // 三道错位瞬身金青剑影撕裂虚空直刺目标
    var arcs = [0, -28 * s, 28 * s];
    for (var k = 0; k < 3; k++) {
      VFX.streamer({
        delay: k * 28,
        x1: cCaster.x,
        y1: cCaster.y,
        x2: tSafe.x,
        y2: tSafe.y,
        arc: arcs[k],
        wobble: 0,
        width: (k === 0 ? 11 : 6) * s,
        tailLength: 0.52,
        duration: 135,
        color: k === 0 ? '#facc15' : '#2dd4bf',
        coreColor: '#ffffff',
        sparkColors: ['#ffffff', '#fef08a', '#5eead4'],
        onArrive: k === 0 ? function () {
          // 阶段二：刹那白金星芒斜斩 + 锐光冲击 + 移形护环收招
          VFX.slashLine({
            x: tSafe.x,
            y: tSafe.y,
            angle: axis.angle - 0.35,
            length: 136 * s,
            width: 8 * s,
            color: '#fef08a',
            coreColor: '#ffffff',
            duration: 210,
          });
          VFX.custom({
            layer: 'sky',
            blend: 'lighter',
            duration: 340,
            draw: function (c2d, p) {
              var fade = p < 0.12 ? p / 0.12 : Math.pow(1 - (p - 0.12) / 0.88, 1.5);
              var r = 74 * s * (0.68 + 0.32 * p);
              c2d.save();
              c2d.translate(tSafe.x, tSafe.y);
              var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
              g.addColorStop(0, 'rgba(255,255,255,' + (0.88 * fade).toFixed(3) + ')');
              g.addColorStop(0.26, 'rgba(250,204,21,' + (0.52 * fade).toFixed(3) + ')');
              g.addColorStop(0.65, 'rgba(45,212,191,' + (0.24 * fade).toFixed(3) + ')');
              g.addColorStop(1, 'rgba(45,212,191,0)');
              c2d.fillStyle = g;
              c2d.beginPath();
              c2d.arc(0, 0, r, 0, TWO_PI);
              c2d.fill();
              c2d.restore();
            },
          });
          VFX.burst({
            x: tSafe.x,
            y: tSafe.y,
            radius: 14 * s,
            count: 14,
            shape: 'flare',
            colors: ['#ffffff', '#fef08a', '#5eead4'],
            size: [2.4 * s, 5.0 * s],
            speed: [75 * s, 185 * s],
            life: [190, 340],
          });
          VFX.burst({
            x: tSafe.x,
            y: tSafe.y,
            radius: 16 * s,
            count: 10,
            shape: 'shard',
            colors: ['#facc15', '#fef08a', '#ffffff'],
            size: [2.8 * s, 5.4 * s],
            speed: [70 * s, 165 * s],
            life: [210, 390],
          });

          VFX.quake('md', 240);
          if (targetEl) {
            VFX.impact(targetEl, { color: '#facc15', duration: 240, distance: 16 });
          }
          VFX.floatText(targetEl || tSafe, '-' + dmg, isCrit ? 'crit' : 'damage', {
            scale: isCrit ? 1.32 : 1.12,
          });

          // 施法者身畔移形气旋护身反馈
          VFX.shockwave({
            delay: 75,
            x: cCaster.x,
            y: cCaster.y + 22 * s,
            tilt: 0.44,
            fromRadius: 10,
            toRadius: 72,
            layer: 'ground',
            color: '#2dd4bf',
            coreColor: '#fef08a',
            lineWidth: 2.8,
            duration: 360,
          });
        } : undefined,
      });
    }

    return 760;
  });
});
