/* ability.tide · 潮汐：三层有方向的水刃浪锋，推进后碎成清澈水珠。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.tide', ctx => VFX.castParticles(ctx, {
    id: 'ability.tide', duration: 1050,
    script(tl, fx) {
      const colors = ['#e1fcff', '#8ae8f4', '#49b8de', '#537fca'];
      fx.targets.forEach((target, index) => {
        const t = fx.point(target);
        const s = fx.scale(target);
        const dir = fx.direction(fx.source, t);
        const at = 90 + index * 70;
        const hit = at + 180;
        for (let wave = 0; wave < 3; wave++) {
          const x = t.x - dir * (58 + wave * 21) * s;
          fx.stage.slash(tl, { at: at + wave * 34, x, y: t.y + (wave - 1) * 10 * s, length: (106 - wave * 12) * s, bend: -28 * s, angle: dir * Math.PI / 2, count: 26, travel: 105, life: [220, 290], hold: 30, colors, size: [1.6, 2.8], length: [8, 14] });
          fx.stage.stream(tl, { at: at + wave * 34, x0: x - dir * 34 * s, y0: t.y + (wave - 1) * 12 * s, x1: t.x + dir * 8 * s, y1: t.y + (wave - 1) * 6 * s, flight: 145, count: 8, stagger: 6, life: 100, spread: 14, colors, headSize: 2.2, headLength: 6 });
        }
        fx.stage.burst(tl, { at: hit, x: t.x, y: t.y + 7, count: 25, angle: -Math.PI / 2, spread: 2.6, speed: [95, 220], gravity: 320, life: [260, 410], colors, shape: 'diamond', size: [1, 2], length: [3, 6] });
        fx.hit(target, hit, { colors, count: 20, radius: 53, angle: dir > 0 ? 0 : Math.PI, spread: 2.4, variant: 'ripple' });
      });
    },
  }));
});
