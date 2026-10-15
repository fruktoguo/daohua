/* ability.lightning-attraction · 雷光引 —— 引动九天雷光轰击目标
 * 镜像 config/game-data/abilities/spell_abilities.json：
 *   单体 / 吟唱 500ms / 1.5×AP+30 / 冷却 28s / 灵力 20 / 五行·木
 * 演出（全粒子）：目标头顶暗紫雷云（雾团聚合）→ 天雷光柱（密集光点+电花）贯顶 → 落点火花喷泉 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.lightning-attraction', (ctx) => {
    VFX.motion(ctx.caster.el, 'cast');
    ctx.targets.forEach((target, i) => {
      const at = 240 + i * 160;
      VFX.after(at, () => {
        const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
        const host = VFX.node(null, t.x, t.y);
        // 雷云：暗紫雾团在头顶聚合翻涌（更大更浓）
        for (let k = 0; k < 10; k++) {
          const smoke = VFX.child(host, 'vfx-p-smoke');
          const sx = 52 + Math.random() * 44;
          smoke.style.width = sx + 'px';
          smoke.style.height = sx * 0.55 + 'px';
          smoke.style.left = (-72 + k * 18 + Math.random() * 12) + 'px';
          smoke.style.top = (-118 + Math.random() * 18) + 'px';
          smoke.style.setProperty('--c', 'rgba(96,66,152,.62)');
          smoke.style.setProperty('--dx', ((Math.random() - 0.5) * 28) + 'px');
          smoke.style.setProperty('--dy', ((Math.random() - 0.5) * 12) + 'px');
          smoke.style.setProperty('--dur', (950 + Math.random() * 450) + 'ms');
        }
        // 三道天雷：垂直密集光点柱 + 落点爆闪 + 电火花喷泉
        for (let k = 0; k < 3; k++) {
          VFX.after(k * 140, () => {
            const bx = t.x + (k - 1) * 18;
            const bolt = VFX.trail(bx, t.y - 112, bx + (Math.random() - 0.5) * 14, t.y + 6, {
              cls: 'vfx-p-mote', count: 38, size: [3, 5.5], jitter: 9, scatter: 14, dur: [190, 340],
            });
            bolt.style.setProperty('--c', '#e9d5ff');
            const boltGlow = VFX.trail(bx, t.y - 112, bx, t.y + 6, {
              cls: 'vfx-p-glow', count: 28, size: [6, 12], jitter: 12, scatter: 26, dur: [260, 460],
            });
            boltGlow.style.setProperty('--c', '#a855f7');
            const fl = VFX.spawn('vfx-p-flare', bx, t.y, 400);
            fl.style.setProperty('--c', '#d8b4fe');
            fl.style.setProperty('--fs', '1.4');
            VFX.particles(bx, t.y + 6, { cls: 'vfx-p-spark', count: 16, directional: true, spread: 14, size: [10, 20], dur: [260, 460], dist: [28, 72], angle: -Math.PI / 2, arc: Math.PI * 1.3 });
          });
        }
        VFX.retire(host, 1500);
        ctx.impact(target, 340);
      });
    });
    VFX.quake(false);
    return 1800;
  });
});
