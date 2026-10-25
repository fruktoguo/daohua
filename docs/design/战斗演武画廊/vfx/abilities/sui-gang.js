/* ability.sui-gang · 碎罡：护体棱甲崩解，碎片向心炼为赤金煞环。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.sui-gang', ctx => VFX.castParticles(ctx, {
    id: 'ability.sui-gang', duration: 800,
    script(tl, fx) {
      const t = fx.source;
      const s = fx.scale(fx.caster);
      const steel = ['#f3e9d2', '#b6b7ae', '#8b9799'];
      const sha = ['#ffe2a3', '#f09552', '#ce4d43'];
      fx.stage.polygon(tl, { at: 0, x: t.x, y: t.y, radius: 44 * s, sides: 6, count: 36, travel: 70, life: [160, 210], hold: 60, colors: steel, size: [1, 1.8] });
      for (let i = 0; i < 14; i++) {
        const angle = i / 14 * Math.PI * 2;
        fx.stage.burst(tl, { at: 160, x: t.x + Math.cos(angle) * 38 * s, y: t.y + Math.sin(angle) * 43 * s, count: 1, angle, spread: 0, speed: [90, 160], life: [260, 330], drag: 3, shape: 'shard', spin: [2, 6], colors: steel, size: [2, 3.2], length: [3, 5] });
      }
      fx.stage.orbit(tl, { at: 240, x: t.x, y: t.y + 4, count: 28, radius: [48, 62], endRadius: 21, spin: -Math.PI * 2.2, life: [310, 400], colors: sha, squash: 0.6, shape: 'shard', size: [1, 2.3], length: [4, 8] });
      fx.stage.converge(tl, { at: 250, x: t.x, y: t.y - 4, count: 20, radius: 58, life: [170, 220], colors: sha, shape: 'diamond' });
      fx.stage.ring(tl, { at: 360, x: t.x, y: t.y + 24 * s, radius: 12, endRadius: 56 * s, squash: 0.26, count: 32, life: [220, 290], colors: sha });
      fx.stage.path(tl, [[t.x - 30 * s, t.y - 30 * s], [t.x, t.y - 13 * s], [t.x + 30 * s, t.y - 30 * s]], { at: 350, count: 22, travel: 60, life: [180, 240], colors: sha, size: [1, 1.8] });
      fx.buff(fx.caster, 380);
    },
  }));
});
