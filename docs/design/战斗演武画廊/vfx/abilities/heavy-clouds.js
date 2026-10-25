/* ability.heavy-clouds · 重云：云纹收束为水势山印，垂直压落而非烟雾遮挡。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.heavy-clouds', ctx => VFX.castParticles(ctx, {
    id: 'ability.heavy-clouds', duration: 850,
    script(tl, fx) {
      const colors = ['#e3fbff', '#9bdced', '#6d9fc9', '#d8c78f'];
      fx.targets.forEach((target, index) => {
        const t = fx.point(target);
        const s = fx.scale(target);
        const at = 100 + index * 70;
        const hit = at + 150;
        for (let row = 0; row < 3; row++) {
          fx.stage.path(tl, [[t.x - 54 * s, t.y - 75 * s + row * 13], [t.x - 25 * s, t.y - 85 * s + row * 13], [t.x + 12 * s, t.y - 80 * s + row * 13], [t.x + 51 * s, t.y - 72 * s + row * 13]], { at, count: 20, travel: 80, life: [180, 230], colors, size: [1, 1.8], length: [5, 10] });
        }
        fx.stage.stream(tl, { at: at + 35, x0: t.x, y0: t.y - 94 * s, x1: t.x, y1: t.y + 22 * s, flight: 115, count: 20, stagger: 4, life: 100, spread: 24, colors, head: 'diamond', headSize: 5, headLength: 10 });
        fx.stage.path(tl, [[t.x - 38 * s, t.y + 14 * s], [t.x, t.y + 33 * s], [t.x + 38 * s, t.y + 14 * s]], { at: hit, count: 26, travel: 30, life: [170, 240], colors, size: [1.3, 2.2], length: [5, 10] });
        fx.hit(target, hit, { colors, count: 26, radius: 54, angle: Math.PI / 2, spread: 2.8, gravity: 250, variant: 'ripple' });
      });
      fx.VFX.quake(false);
    },
  }));
});
