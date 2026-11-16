/* ability.zhuoxin · 灼心 —— 零直伤隐火直烙心脉施加大量燃烧层数
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   单体敌 / 吟唱 2000ms / 0 直伤 + 50%AP+10 层「燃烧」 / 冷却 20s / 灵力 20 / 五行·火
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  VFX.register('ability.zhuoxin', function (ctx) {
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
    var targetPt = targetEl ? VFX.center(targetEl) : VFX.sideCenter(foeSide);
    var safePt = VFX.clampStage(targetPt, 30 * s, 26 * s);
    var palmPt = VFX.clampStage(axis.palm(18 * s), 18 * s, 18 * s);

    VFX.motion(casterEl, 'cast', { duration: 380 });
    VFX.cinematicDim(0.28, 560, '#160402');

    // 阶段一：施法指尖飞出一道隐晦暗绯业火咒丝穿透向目标胸口
    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      radius: 10 * s,
      count: 6,
      shape: 'rune',
      colors: ['#fb7185', '#f97316', '#fde047'],
      size: [3.2 * s, 5.2 * s],
      speed: [18 * s, 44 * s],
      life: [240, 380],
    });

    VFX.streamer({
      x1: palmPt.x,
      y1: palmPt.y,
      x2: safePt.x,
      y2: safePt.y,
      arc: -24 * s,
      wobble: 10 * s,
      width: 7 * s,
      tailLength: 0.45,
      duration: 250,
      color: '#e11d48',
      coreColor: '#fef08a',
      sparkColors: ['#f97316', '#fb7185', '#fde047'],
      onArrive: function () {
        // 阶段二：心口内部先坍缩吸光，再爆燃剧烈暗金毒炎与多层燃烧炽焰
        VFX.burst({
          x: safePt.x,
          y: safePt.y,
          radius: 54 * s,
          count: 14,
          shape: 'wisp',
          colors: ['#be123c', '#f97316', '#fbbf24'],
          size: [4.0 * s, 7.2 * s],
          attractTo: safePt,
          attractStrength: 280 * s,
          life: [160, 250],
        });

        VFX.after(130, function () {
          VFX.custom({
            layer: 'sky',
            blend: 'lighter',
            duration: 620,
            draw: function (c2d, p) {
              var fade = p < 0.16 ? p / 0.16 : Math.pow(1 - (p - 0.16) / 0.84, 1.35);
              var pulse = 0.82 + 0.18 * Math.sin(p * 24);
              var r = (54 + p * 22) * s;
              c2d.save();
              c2d.translate(safePt.x, safePt.y);
              var g = c2d.createRadialGradient(0, 0, 2, 0, 0, r);
              g.addColorStop(0, 'rgba(254, 240, 138,' + (0.74 * fade * pulse).toFixed(3) + ')');
              g.addColorStop(0.32, 'rgba(249, 115, 22,' + (0.48 * fade).toFixed(3) + ')');
              g.addColorStop(0.68, 'rgba(225, 29, 72,' + (0.24 * fade).toFixed(3) + ')');
              g.addColorStop(1, 'rgba(136, 19, 55, 0)');
              c2d.fillStyle = g;
              c2d.beginPath();
              c2d.arc(0, 0, r, 0, TWO_PI);
              c2d.fill();
              c2d.restore();
            },
          });

          VFX.shockwave({
            x: safePt.x,
            y: safePt.y,
            fromRadius: 8,
            toRadius: 72,
            tilt: 0.85,
            layer: 'sky',
            color: '#f43f5e',
            coreColor: '#fef08a',
            lineWidth: 3.4,
            duration: 340,
          });
          VFX.burst({
            x: safePt.x,
            y: safePt.y,
            radius: 18 * s,
            count: 20,
            shape: 'ember',
            colors: ['#fef08a', '#fb923c', '#e11d48', '#ffffff'],
            size: [2.4 * s, 4.6 * s],
            speed: [35 * s, 105 * s],
            gravity: -65 * s,
            life: [380, 680],
          });
          VFX.burst({
            x: safePt.x,
            y: safePt.y,
            radius: 16 * s,
            count: 10,
            shape: 'wisp',
            colors: ['#f97316', '#e11d48', '#fbbf24'],
            size: [4.8 * s, 8.6 * s],
            speed: [24 * s, 68 * s],
            gravity: -55 * s,
            alpha: 0.72,
            life: [360, 620],
          });

          if (targetEl) {
            VFX.hitFlash(targetEl, '#f43f5e');
          }
          VFX.floatText(targetEl || safePt, '灼心·燃烧', 'debuff', { scale: 1.08 });
        });
      },
    });

    return 1080;
  });
});
