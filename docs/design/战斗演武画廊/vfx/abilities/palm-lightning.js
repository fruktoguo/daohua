/* ability.palm-lightning · 掌心雷 —— 从掌心发出一道雷光
 * 镜像 config/game-data/abilities/spell_abilities.json：
 *   单体敌 / 吟唱 2000ms / 0.6×AD+10 / 无冷却 / 灵力 4 / 五行·木
 * 演出（全粒子）：紫电粒子流以高抖动拖尾模拟电弧击中目标 → 命中点电花爆 + 分叉余电 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.palm-lightning', (ctx) => {
    const target = ctx.targets[0];
    if (!target) return 0;
    VFX.motion(ctx.caster.el, 'cast');
    const s = VFX.center(ctx.caster.el.querySelector('.arena-avatar-stage') || ctx.caster.el);
    const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);

    VFX.after(140, () => {
      // 电弧通道：两层高抖动拖尾 —— 亮芯(mote) + 紫外光晕(glow)，jitter 大制造电弧扭曲感
      const core = VFX.trail(s.x, s.y - 6, t.x, t.y - 4, {
        cls: 'vfx-p-mote', count: 56, size: [3, 5], jitter: 15, scatter: 16, dur: [240, 430],
      });
      core.style.setProperty('--c', '#e9d5ff');
      const halo = VFX.trail(s.x, s.y - 6, t.x, t.y - 4, {
        cls: 'vfx-p-glow', count: 38, size: [7, 13], jitter: 19, scatter: 34, dur: [320, 580],
      });
      halo.style.setProperty('--c', '#a855f7');

      // 命中点：电花爆闪 + 分叉余电 + 电火花
      VFX.after(90, () => {
        const hit = VFX.node(null, t.x, t.y);
        hit.style.setProperty('--c', '#c084fc');
        const fl = VFX.child(hit, 'vfx-p-flare');
        fl.style.setProperty('--c', '#d8b4fe');
        fl.style.setProperty('--fs', '1.4');
        const ring = VFX.child(hit, 'vfx-p-ring');
        ring.style.setProperty('--c', 'rgba(200,150,255,.85)');
        ring.style.setProperty('--ring-scale', '2.4');
        // 三条分叉余电（短程拖尾）
        for (let k = 0; k < 4; k++) {
          const a = (-0.75 + k * 0.5) * Math.PI + (Math.random() - 0.5) * 0.5;
          const bx = Math.cos(a) * (46 + Math.random() * 30);
          const by = Math.sin(a) * (40 + Math.random() * 26);
          const br = VFX.trail(t.x, t.y - 4, t.x + bx, t.y - 4 + by, {
            cls: 'vfx-p-mote', count: 14, size: [2.5, 4], jitter: 10, scatter: 12, dur: [220, 400],
          });
          br.style.setProperty('--c', '#c084fc');
        }
        VFX.particles(t.x, t.y, { cls: 'vfx-p-spark', count: 28, directional: true, spread: 16, size: [11, 22], dur: [280, 520], dist: [34, 92] });
        VFX.particles(t.x, t.y, { cls: 'vfx-p-glow', count: 20, spread: 16, size: [5, 9], dur: [350, 620], dist: [26, 72] });
        VFX.retire(hit, 900);
        ctx.impact(target, 0);
      });
    });
    return 1050;
  });
});
