/* ability.qingping-melody · 清平乐：青叶螺旋、音律光圈、向心回春。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.qingping-melody', ctx => VFX.castParticles(ctx, {
    id: 'ability.qingping-melody', duration: 850,
    script(tl, fx) {
      const colors = ['#e4fff0', '#8de5af', '#49c99b', '#70d9d0'];
      fx.targets.forEach((target, index) => {
        const t = fx.point(target);
        const s = fx.scale(target);
        const at = 60 + index * 60;
        fx.stage.orbit(tl, { at, x: t.x, y: t.y + 6, count: 28, radius: [38, 56], endRadius: 18, squash: 0.8, spin: Math.PI * 2, life: [400, 520], colors, shape: 'leaf', size: [1.2, 2.2], length: [4, 7] });
        for (let i = 0; i < 3; i++) {
          fx.stage.ring(tl, { at: at + 90 + i * 80, x: t.x, y: t.y + 18 * s, radius: 18, endRadius: (52 + i * 10) * s, squash: 0.32, count: 22, life: [240, 310], colors, size: [0.8, 1.3] });
        }
        fx.stage.converge(tl, { at: at + 160, x: t.x, y: t.y - 6, count: 26, radius: 65, life: [190, 260], colors, shape: 'leaf', size: [1, 2], length: [4, 7] });
        fx.heal(target, at + 230);
      });
    },
  }));
});
