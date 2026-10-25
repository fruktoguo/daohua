/* ability.chain-strike · 连环：三次明确交替的火影斩，逐段同帧结算。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.chain-strike', ctx => VFX.castParticles(ctx, {
    id: 'ability.chain-strike', motion: 'attack', duration: 820,
    script(tl, fx) {
      const target = fx.targets[0];
      const t = fx.point(target);
      const s = fx.scale(target);
      const dir = fx.direction(fx.source, t);
      const angles = [dir * 0.62, -dir * 0.7, dir * 1.16];
      for (let i = 0; i < 3; i++) {
        const at = 70 + i * 170;
        const x = t.x + (i - 1) * 6 * s;
        const y = t.y + (i === 2 ? 6 : -3) * s;
        const colors = i === 1 ? ['#fff4c6', '#ffcf63', '#ff8b3f'] : ['#fff8da', '#ffbb53', '#ff6941'];
        fx.stage.stream(tl, { at: at - 55, x0: fx.source.x, y0: fx.source.y - 8 * i, x1: x, y1: y, flight: 55, count: 7, stagger: 4, life: 70, spread: 9, colors, headSize: 2.8, headLength: 8 });
        fx.stage.slash(tl, { at, x, y, length: (104 + i * 8) * s, bend: (i % 2 ? 20 : -24) * s, angle: angles[i], count: 27, travel: 60, life: [125, 200], hold: 35, colors, size: [1, 2], length: [5, 10] });
        fx.hit(target, at + 60, { colors, count: 14, ringCount: 16, radius: 34, angle: angles[i], spread: 1.9, variant: i === 1 ? 'splinter' : 'cleave' });
      }
    },
  }));
});
