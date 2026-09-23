/* ability.heavy-clouds · 重云 —— 掌势重云压顶，席卷敌阵
 * 镜像 config/game-data/abilities/physical_abilities.json：
 *   敌方全体 / 吟唱 2000ms / 1.2×AD+40 / 冷却 24s / 灵力 40
 * 演出（全粒子）：白金粒子云浪自左向右横扫敌阵，雾团层叠推进，逐目标压顶命中 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.heavy-clouds', (ctx) => {
    VFX.motion(ctx.caster.el, 'cast');
    ctx.targets.forEach((target, i) => {
      const at = 220 + i * 160;
      VFX.after(at, () => {
        const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
        const host = VFX.node(null, t.x, t.y);
        host.style.setProperty('--c', '#e8d69a');
        // 云浪主体：大片雾团自左向右滚过（更大更厚）
        for (let k = 0; k < 12; k++) {
          const smoke = VFX.child(host, 'vfx-p-smoke');
          const sx = 40 + Math.random() * 44;
          smoke.style.width = sx + 'px';
          smoke.style.height = sx * 0.7 + 'px';
          smoke.style.left = (-96 + k * 20 + Math.random() * 14) + 'px';
          smoke.style.top = (-44 + Math.random() * 50) + 'px';
          smoke.style.setProperty('--dx', (120 + Math.random() * 70) + 'px');
          smoke.style.setProperty('--dy', ((Math.random() - 0.5) * 26) + 'px');
          smoke.style.setProperty('--dur', (650 + Math.random() * 450) + 'ms');
          smoke.style.setProperty('--c', 'rgba(215,205,175,.55)');
        }
        // 云浪内裹挟的剑气光点
        VFX.particles(t.x - 40, t.y, { cls: 'vfx-p-glow', count: 26, spread: 56, size: [5, 9], dur: [400, 680], dist: [70, 150], angle: 0, arc: 0.9 });
        VFX.particles(t.x - 26, t.y - 8, { cls: 'vfx-p-spark', count: 16, directional: true, spread: 30, size: [12, 22], dur: [320, 540], dist: [48, 110], angle: 0.15, arc: 0.7 });
        VFX.retire(host, 1300);
        ctx.impact(target, 200);
      });
    });
    VFX.quake(false);
    return 1600;
  });
});
