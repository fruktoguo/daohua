/* ability.taunt · 嘲讽：胸前怒核爆开三重锐角声纹，不制造虚假伤害。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.taunt', ctx => VFX.castParticles(ctx, {
    id: 'ability.taunt', duration: 650,
    script(tl, fx) {
      const t = fx.source;
      const s = fx.scale(fx.caster);
      const colors = ['#fff1ba', '#ffc354', '#ec7843'];
      fx.stage.converge(tl, { at: 0, x: t.x, y: t.y - 12 * s, count: 18, radius: 48 * s, life: [110, 160], colors });
      for (let i = 0; i < 3; i++) {
        const at = 130 + i * 65;
        fx.stage.ring(tl, { at, x: t.x, y: t.y - 10 * s, radius: 14, endRadius: (72 + i * 20) * s, squash: 0.72, count: 32, life: [210, 300], colors, size: [1, 1.9], length: [5, 9] });
        for (let side = -1; side <= 1; side += 2) {
          fx.stage.path(tl, [[t.x + side * 20 * s, t.y - 16 * s], [t.x + side * (50 + i * 16) * s, t.y - 30 * s], [t.x + side * (68 + i * 16) * s, t.y - 18 * s]], { at, count: 14, travel: 35, life: [150, 230], colors, size: [1, 1.8] });
        }
      }
      fx.stage.burst(tl, { at: 130, x: t.x, y: t.y - 8, count: 22, angle: -Math.PI / 2, spread: 2.7, speed: [80, 200], gravity: 220, life: [230, 400], colors, shape: 'diamond' });
      fx.threat(fx.caster, 130);
      fx.buff(fx.caster, 130);
    },
  }));
});
