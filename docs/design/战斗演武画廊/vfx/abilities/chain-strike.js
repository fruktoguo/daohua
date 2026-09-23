/* ability.chain-strike · 连环 —— 剑势连绵，连环进击
 * 镜像 config/game-data/abilities/physical_abilities.json：
 *   单体敌 / 吟唱 1000ms / 1.0×AD+15 / 冷却 16s / 灵力 10
 * 演出（全粒子）：三段交错粒子刀光快速切过目标，每段附带火花迸溅 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.chain-strike', (ctx) => {
    const target = ctx.targets[0];
    if (!target) return 0;
    VFX.motion(ctx.caster.el, 'attack');
    const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);

    // 三段斩：角度交替的粒子刀光（跨度加大、光点更粗）
    const cuts = [
      { x1: -64, y1: -42, x2: 58, y2: 20 },
      { x1: 60, y1: -36, x2: -56, y2: 26 },
      { x1: -46, y1: -62, x2: 48, y2: 42 },
    ];
    cuts.forEach((c, i) => {
      const at = 120 + i * 150;
      VFX.after(at, () => {
        const host = VFX.node(null, t.x, t.y - 6);
        host.style.setProperty('--c', i === 1 ? '#ffe9a8' : '#ffd166');
        VFX.trail(t.x + c.x1, t.y - 6 + c.y1, t.x + c.x2, t.y - 6 + c.y2, {
          cls: 'vfx-p-glow', count: 32, size: [5, 11], jitter: 6, scatter: 30, dur: [260, 460],
        });
        VFX.trail(t.x + c.x1, t.y - 6 + c.y1, t.x + c.x2, t.y - 6 + c.y2, {
          cls: 'vfx-p-mote', count: 18, size: [3, 5], jitter: 4, scatter: 14, dur: [220, 380],
        });
        VFX.particles(t.x, t.y - 4, { cls: 'vfx-p-spark', count: 15, directional: true, spread: 16, size: [11, 20], dur: [260, 460], dist: [30, 74] });
        const fl = VFX.spawn('vfx-p-flare', t.x, t.y - 6, 380);
        fl.style.setProperty('--fs', '0.7');
        VFX.retire(host, 700);
        ctx.impact(target, 0);
      });
    });
    return 950;
  });
});
