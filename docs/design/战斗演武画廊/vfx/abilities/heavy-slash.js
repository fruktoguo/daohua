/* ability.heavy-slash · 重斩：金刃斜落、棱形冲击与定向碎锋。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.heavy-slash', ctx => VFX.castParticles(ctx, {
    id: 'ability.heavy-slash', motion: 'attack', duration: 640,
    script(tl, fx) {
      const target = fx.targets[0];
      const t = fx.point(target);
      const s = fx.scale(target);
      const dir = fx.direction(fx.source, t);
      const colors = ['#fffbe8', '#ffe09a', '#eab14f'];
      fx.stage.converge(tl, { at: 0, x: t.x + 42 * dir * s, y: t.y - 62 * s, count: 14, radius: 38 * s, life: [100, 150], colors });
      fx.stage.slash(tl, { at: 80, x: t.x, y: t.y - 4 * s, length: 142 * s, bend: -30 * s, angle: dir * 1.12, count: 40, travel: 70, life: [180, 250], hold: 65, colors, size: [2.2, 3.5], length: [9, 17] });
      fx.stage.slash(tl, { at: 94, x: t.x - dir * 5 * s, y: t.y - 8 * s, length: 132 * s, bend: -25 * s, angle: dir * 1.12, count: 30, travel: 55, life: [130, 190], hold: 50, colors: ['#ffffff', '#fff0b3'], size: [1, 1.7], length: [7, 14], glow: false });
      fx.stage.path(tl, [[t.x + 49 * dir * s, t.y - 76 * s], [t.x - 25 * dir * s, t.y + 42 * s]], { at: 105, count: 22, travel: 45, life: [130, 190], colors, size: [1, 1.7], length: [5, 10] });
      fx.stage.burst(tl, { at: 150, x: t.x - 18 * dir * s, y: t.y + 28 * s, count: 20, angle: -Math.PI / 2, spread: 1.9, speed: [90, 250], gravity: 330, life: [230, 390], shape: 'shard', colors, size: [1, 2.1], length: [3, 6] });
      fx.hit(target, 150, { count: 28, radius: 60, angle: dir > 0 ? 0.3 : Math.PI - 0.3, spread: 2.2, variant: 'cleave' });
      fx.VFX.quake(false);
    },
  }));
});
