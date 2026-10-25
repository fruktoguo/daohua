/* ability.star-piercer · 贯星：星芒聚束、流星贯穿、前后双向碎星。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.star-piercer', ctx => VFX.castParticles(ctx, {
    id: 'ability.star-piercer', motion: 'attack', duration: 800,
    script(tl, fx) {
      const target = fx.targets[0];
      const t = fx.point(target);
      const source = fx.source;
      const s = fx.scale(target);
      const dir = fx.direction(source, t);
      const colors = ['#ffffff', '#fff2b0', '#ffd165', '#f3a84d'];
      fx.stage.orbit(tl, { at: 0, x: source.x, y: source.y - 8, count: 26, radius: [40, 65], endRadius: 3, spin: Math.PI * 2.4, life: [230, 310], colors, squash: 0.7 });
      fx.stage.converge(tl, { at: 110, x: source.x + dir * 20, y: source.y - 8, count: 18, radius: 52, life: [130, 180], colors });
      const at = 240;
      fx.stage.stream(tl, { at, x0: source.x + dir * 24, y0: source.y - 8, x1: t.x + dir * 48 * s, y1: t.y, flight: 110, count: 26, stagger: 4, life: 120, spread: 3, colors, head: 'diamond', headSize: 5, headLength: 20, size: [1.1, 2], length: [10, 17] });
      fx.stage.path(tl, [[t.x - dir * 55 * s, t.y - 8], [t.x + dir * 68 * s, t.y + 8]], { at: at + 110, count: 32, travel: 25, life: [170, 240], colors, size: [1.8, 3], length: [10, 20] });
      fx.stage.path(tl, [[t.x - dir * 42 * s, t.y - 19 * s], [t.x + dir * 48 * s, t.y - 3 * s]], { at: at + 120, count: 18, travel: 30, life: [130, 190], colors: ['#ffffff', '#fff1b3'], size: [1, 1.6], length: [7, 12], glow: false });
      fx.stage.burst(tl, { at: at + 110, x: t.x, y: t.y, count: 24, angle: dir > 0 ? 0 : Math.PI, spread: 1.6, speed: [190, 350], life: [220, 380], colors, size: [1, 2], length: [6, 12] });
      fx.hit(target, at + 110, { colors, count: 34, radius: 68, variant: 'splinter' });
      fx.VFX.quake(true);
    },
  }));
});
