/* ability.palm-lightning · 掌心雷：细亮锯齿电桥、短分叉与针状电花。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  VFX.register('ability.palm-lightning', ctx => VFX.castParticles(ctx, {
    id: 'ability.palm-lightning', duration: 640,
    script(tl, fx) {
      const target = fx.targets[0];
      const t = fx.point(target);
      const colors = ['#fbf5ff', '#ddc5ff', '#a983f7'];
      fx.stage.converge(tl, { at: 0, x: fx.source.x, y: fx.source.y - 5, count: 18, radius: 46, life: [90, 140], colors });
      // 主干电桥：双重错峰锯齿电矢
      fx.stage.bolt(tl, { at: 90, x0: fx.source.x, y0: fx.source.y - 5, x1: t.x, y1: t.y - 4, count: 56, travel: 45, life: [140, 210], hold: 60, colors, jitter: 24, segments: 10, size: [1.2, 2.2], length: [6, 12] });
      fx.stage.bolt(tl, { at: 110, x0: fx.source.x, y0: fx.source.y - 5, x1: t.x, y1: t.y - 4, count: 32, travel: 35, life: [100, 160], hold: 45, colors: ['#ffffff', '#fffbe8'], jitter: 14, segments: 8, size: [0.9, 1.5], length: [5, 9], glow: false });
      // 命中爆散的三向分支电弧与电针残芒
      for (let i = 0; i < 4; i++) {
        const angle = i * Math.PI / 2 + 0.35;
        fx.stage.bolt(tl, { at: 145, x0: t.x, y0: t.y - 4, x1: t.x + Math.cos(angle) * 52, y1: t.y + Math.sin(angle) * 44, count: 16, travel: 30, life: [130, 200], colors, jitter: 12, segments: 5, size: [0.9, 1.6], length: [4, 9] });
      }
      fx.stage.burst(tl, { at: 145, x: t.x, y: t.y - 4, count: 28, angle: 0, spread: Math.PI * 2, speed: [120, 280], life: [180, 320], shape: 'needle', colors, size: [1.1, 2.2], length: [6, 14] });
      fx.hit(target, 145, { colors, count: 30, radius: 48, variant: 'cleave' });
    },
  }));
});
