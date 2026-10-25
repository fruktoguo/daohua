/* ability.pozhen-melody · 破阵子：空心金符阵瞬展，棱光护住友方。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.pozhen-melody', ctx => VFX.castParticles(ctx, {
    id: 'ability.pozhen-melody', duration: 950,
    script(tl, fx) {
      const colors = ['#fff6c7', '#edce77', '#ca9950', '#c5b3ff'];
      const source = fx.source;
      fx.stage.polygon(tl, { at: 0, x: source.x, y: source.y + 24, radius: 65, sides: 8, squash: 0.34, count: 48, travel: 100, life: [210, 280], colors, size: [1, 1.6] });
      fx.stage.orbit(tl, { at: 0, x: source.x, y: source.y, count: 24, radius: [40, 58], endRadius: 15, spin: Math.PI * 1.4, life: [290, 370], colors, squash: 0.55 });
      fx.targets.forEach((target, index) => {
        const t = fx.point(target);
        const s = fx.scale(target);
        const at = 160 + index * 70;
        fx.stage.stream(tl, { at: at - 110, x0: source.x, y0: source.y, x1: t.x, y1: t.y, flight: 110, count: 10, stagger: 6, life: 90, spread: 7, colors });
        fx.stage.polygon(tl, { at, x: t.x, y: t.y, radius: 47 * s, sides: 6, angle: Math.PI / 6, count: 42, travel: 75, life: [260, 360], hold: 60, colors, size: [1, 1.7], length: [4, 8] });
        fx.stage.ring(tl, { at: at + 50, x: t.x, y: t.y + 25 * s, radius: 12, endRadius: 58 * s, squash: 0.28, count: 28, life: [240, 310], colors });
        fx.stage.converge(tl, { at: at + 60, x: t.x, y: t.y, count: 20, radius: 52, life: [170, 240], colors });
        fx.buff(target, at + 110);
      });
    },
  }));
});
