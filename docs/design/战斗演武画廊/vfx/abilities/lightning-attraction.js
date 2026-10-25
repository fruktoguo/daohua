/* ability.lightning-attraction · 雷光引：雷符锁定、单道贯顶天雷、折枝放电。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.lightning-attraction', ctx => VFX.castParticles(ctx, {
    id: 'ability.lightning-attraction', duration: 720,
    script(tl, fx) {
      const target = fx.targets[0];
      const t = fx.point(target);
      const s = fx.scale(target);
      const colors = ['#ffffff', '#e8d6ff', '#bb91ff', '#9670ef'];
      fx.stage.polygon(tl, { at: 0, x: t.x, y: t.y - 87 * s, radius: 32 * s, sides: 6, squash: 0.4, count: 32, travel: 80, life: [180, 240], colors, size: [0.9, 1.5], length: [3, 6] });
      fx.stage.converge(tl, { at: 0, x: t.x, y: t.y - 72 * s, count: 16, radius: 58, life: [100, 155], colors, squash: 0.25 });
      fx.stage.bolt(tl, { at: 150, x0: t.x, y0: t.y - 116 * s, x1: t.x, y1: t.y + 10 * s, count: 46, travel: 35, life: [150, 210], hold: 45, colors, jitter: 16, segments: 8, size: [1.1, 2], length: [5, 10] });
      for (let side = -1; side <= 1; side += 2) {
        fx.stage.bolt(tl, { at: 190, x0: t.x, y0: t.y - 55 * s, x1: t.x + side * 42 * s, y1: t.y + 7 * s, count: 17, travel: 30, life: [130, 180], colors, jitter: 12, segments: 5, size: [0.8, 1.4], length: [4, 7] });
      }
      fx.stage.burst(tl, { at: 195, x: t.x, y: t.y + 16 * s, count: 24, angle: -Math.PI / 2, spread: 2.3, speed: [110, 280], gravity: 380, life: [200, 360], colors, size: [1, 1.7], length: [4, 10] });
      fx.hit(target, 195, { colors, count: 30, radius: 58, variant: 'splinter' });
      fx.VFX.quake(false);
    },
  }));
});
