/* ability.ganlu · 甘露：水露垂直滴落、细环涟漪与木灵新叶。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.ganlu', ctx => VFX.castParticles(ctx, {
    id: 'ability.ganlu', duration: 950,
    script(tl, fx) {
      const colors = ['#e6fff6', '#91ead3', '#69cfe1', '#74c6a5'];
      fx.targets.forEach((target, index) => {
        const t = fx.point(target);
        const s = fx.scale(target);
        const at = 60 + index * 60;
        for (let i = 0; i < 13; i++) {
          const x = t.x + Math.sin(i * 2.4) * 33 * s;
          fx.stage.stream(tl, { at: at + i * 18, x0: x, y0: t.y - 78 * s - (i % 3) * 10, x1: t.x + (i % 3 - 1) * 8 * s, y1: t.y + 4 * s, flight: 230, count: 2, stagger: 0, life: 70, spread: 0, colors, head: 'diamond', headSize: 2.4, headLength: 6, shape: 'spark', size: [0.8, 1.1], length: [3, 5] });
        }
        for (let i = 0; i < 3; i++) {
          fx.stage.ring(tl, { at: at + 240 + i * 80, x: t.x, y: t.y + 23 * s, radius: 5, endRadius: (42 + i * 9) * s, squash: 0.28, count: 26, life: [230, 320], colors, size: [0.8, 1.3], length: [3, 6] });
        }
        fx.stage.orbit(tl, { at: at + 240, x: t.x, y: t.y + 2, count: 16, radius: [22, 36], endRadius: 10, spin: Math.PI * 1.3, life: [340, 420], colors, shape: 'leaf', size: [1, 1.7], length: [3, 6] });
        fx.stage.converge(tl, { at: at + 250, x: t.x, y: t.y, count: 18, radius: 49, life: [180, 260], colors, shape: 'leaf' });
        fx.buff(target, at + 260);
      });
    },
  }));
});
