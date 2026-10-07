/* ability.heavy-slash · 重斩 —— 蓄势挥出沉重一剑
 * 镜像 config/game-data/abilities/physical_abilities.json：
 *   单体敌 / 吟唱 1000ms / 1.8×AD+20 / 冷却 18s / 灵力 10
 * 演出（全粒子）：金色弧光粒子群自上而下扫落 → 落点火花喷泉 + 尘雾 + 冲击环 + 震屏 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.heavy-slash', (ctx) => {
    const target = ctx.targets[0];
    if (!target) return 0;
    VFX.motion(ctx.caster.el, 'attack');
    const s = VFX.center(ctx.caster.el.querySelector('.arena-avatar-stage') || ctx.caster.el);
    const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
    // 攻击行进方向：出手方在目标右侧时斩击几何整体水平镜像
    const dir = t.x < s.x ? -1 : 1;

    VFX.after(120, () => {
      const host = VFX.node(null, t.x, t.y);
      host.style.setProperty('--c', '#ffd166');
      // 弧光主刃：自上斜向劈落的一串密集光点（拖尾成刃形，迎击侧起手）
      VFX.trail(t.x + 52 * dir, t.y - 110, t.x - 18 * dir, t.y + 38, {
        cls: 'vfx-p-glow', count: 46, size: [7, 14], jitter: 8, scatter: 42, dur: [360, 620],
      });
      // 刃缘亮芯
      VFX.trail(t.x + 46 * dir, t.y - 100, t.x - 14 * dir, t.y + 32, {
        cls: 'vfx-p-mote', count: 26, size: [3, 6], jitter: 4, scatter: 18, dur: [280, 460],
      });
      // 落点：火花喷泉 + 尘雾 + 冲击环 + 爆闪
      VFX.particles(t.x, t.y + 24, { cls: 'vfx-p-spark', count: 36, directional: true, spread: 20, size: [14, 30], dur: [340, 640], dist: [44, 130], angle: -Math.PI / 2, arc: Math.PI * 1.6 });
      VFX.particles(t.x, t.y + 26, { cls: 'vfx-p-smoke', count: 10, spread: 36, size: [36, 66], dur: [700, 1100], dist: [20, 55] });
      VFX.particles(t.x, t.y + 18, { cls: 'vfx-p-glow', count: 22, spread: 22, size: [5, 10], dur: [400, 760], dist: [32, 84] });
      const ring = VFX.spawn('vfx-p-ring', t.x, t.y + 28, 700);
      ring.style.setProperty('--ring-scale', '2.8');
      const fl = VFX.spawn('vfx-p-flare', t.x, t.y, 600);
      fl.style.setProperty('--fs', '1.5');
      VFX.retire(host, 1200);
      VFX.quake(false);
      ctx.impact(target, 80);
    });
    return 1200;
  });
});
