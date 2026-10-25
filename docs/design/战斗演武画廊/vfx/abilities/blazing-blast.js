/* ability.blazing-blast · 轰炎：赤金火楔贴地轰入，爆成锐亮焰瓣后迅速熄灭。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.blazing-blast', ctx => VFX.castParticles(ctx, {
    id: 'ability.blazing-blast', duration: 900,
    script(tl, fx) {
      const colors = ['#fff3c3', '#ffc257', '#ff813f', '#ee4f38'];
      fx.stage.converge(tl, { at: 0, x: fx.source.x, y: fx.source.y - 8, count: 20, radius: 46, life: [110, 170], colors });
      fx.targets.forEach((target, index) => {
        const t = fx.point(target);
        const s = fx.scale(target);
        const at = 120 + index * 65;
        const hit = at + 135;
        fx.stage.stream(tl, { at, x0: fx.source.x, y0: fx.source.y - 8, x1: t.x, y1: t.y + 15 * s, flight: 135, count: 16, stagger: 5, life: 100, spread: 10, bend: -16, colors, headSize: 4.5, headLength: 13 });
        for (let petal = -2; petal <= 2; petal++) {
          const angle = -Math.PI / 2 + petal * 0.37;
          fx.stage.slash(tl, { at: hit, x: t.x + Math.cos(angle) * 18 * s, y: t.y + 14 * s + Math.sin(angle) * 8 * s, length: (42 + (2 - Math.abs(petal)) * 9) * s, bend: -13 * s, angle, count: 11, travel: 45, life: [170, 260], colors, size: [1.1, 2.1], length: [5, 10] });
        }
        fx.stage.burst(tl, { at: hit, x: t.x, y: t.y + 12, count: 24, angle: -Math.PI / 2, spread: 2.6, speed: [120, 290], gravity: 380, life: [240, 420], colors, shape: 'shard', size: [1, 2], length: [3, 7] });
        fx.hit(target, hit, { colors, count: 20, radius: 56, variant: 'ripple' });
      });
    },
  }));
});
