/* ability.star-piercer · 贯星 —— 剑气如流星贯空
 * 镜像 config/game-data/abilities/physical_abilities.json：
 *   单体敌 / 吟唱 2000ms / 3.0×AD+50 / 冷却 36s / 灵力 30
 * 演出（全粒子）：星辉向掌心汇聚 → 流星粒子束贯穿 → 命中点星爆（放射光雨 + 光环 + 重震屏） */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.star-piercer', (ctx) => {
    const target = ctx.targets[0];
    if (!target) return 0;
    VFX.motion(ctx.caster.el, 'attack');
    const s = VFX.center(ctx.caster.el.querySelector('.arena-avatar-stage') || ctx.caster.el);
    const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);

    // 蓄力：星辉光点向掌心汇聚（反向粒子 = 预置位移再收敛）
    const chargeHost = VFX.node(null, s.x, s.y - 10);
    chargeHost.style.setProperty('--c', '#ffe9a8');
    for (let i = 0; i < 28; i++) {
      const p = VFX.child(chargeHost, 'vfx-regen-mote');
      p.style.setProperty('--c', '#ffd166');
      const a = Math.random() * Math.PI * 2;
      const d = 40 + Math.random() * 70;
      p.style.setProperty('--dx', Math.cos(a) * d + 'px');
      p.style.setProperty('--dy', Math.sin(a) * d + 'px');
      p.style.setProperty('--dur', (420 + Math.random() * 320) + 'ms');
      const sz = 3 + Math.random() * 5;
      p.style.width = sz + 'px';
      p.style.height = sz + 'px';
    }
    const chargeFlare = VFX.child(chargeHost, 'vfx-p-flare');
    chargeFlare.style.setProperty('--c', '#fff3c8');
    chargeFlare.style.setProperty('--fs', '1.2');
    VFX.retire(chargeHost, 800);

    VFX.after(720, () => {
      // 流星主束：三层 trail —— 亮芯 / 光晕 / 散落碎星
      const core = VFX.trail(s.x, s.y - 10, t.x, t.y, {
        cls: 'vfx-p-mote', count: 48, size: [3, 6], jitter: 4, scatter: 12, dur: [280, 500],
      });
      core.style.setProperty('--c', '#fff6da');
      const glowTrail = VFX.trail(s.x, s.y - 10, t.x, t.y, {
        cls: 'vfx-p-glow', count: 44, size: [7, 14], jitter: 10, scatter: 44, dur: [360, 680],
      });
      glowTrail.style.setProperty('--c', '#ffd166');
      // 沿途星火迸溅
      const midX = (s.x + t.x) / 2;
      const midY = (s.y + t.y) / 2;
      VFX.particles(midX, midY, { cls: 'vfx-p-spark', count: 22, directional: true, spread: 28, size: [12, 22], dur: [300, 540], dist: [36, 90] });

      // 命中星爆
      VFX.after(120, () => {
        const burst = VFX.node(null, t.x, t.y);
        burst.style.setProperty('--c', '#ffd166');
        const flare = VFX.child(burst, 'vfx-p-flare');
        flare.style.setProperty('--dur', '.65s');
        flare.style.setProperty('--fs', '1.8');
        const r = VFX.child(burst, 'vfx-p-ring');
        r.style.setProperty('--c', 'rgba(255,220,140,.9)');
        r.style.setProperty('--ring-scale', '3.4');
        const r2 = VFX.child(burst, 'vfx-p-ring');
        r2.style.setProperty('--c', 'rgba(255,170,80,.5)');
        r2.style.setProperty('--ring-scale', '4.6');
        r2.style.setProperty('--dur', '.9s');
        r2.style.animationDelay = '.08s';
        VFX.particles(t.x, t.y, { cls: 'vfx-p-spark', count: 46, directional: true, spread: 16, size: [14, 30], dur: [360, 720], dist: [48, 150] });
        VFX.particles(t.x, t.y, { cls: 'vfx-p-glow', count: 34, spread: 18, size: [5, 10], dur: [450, 850], dist: [36, 120] });
        VFX.particles(t.x, t.y + 8, { cls: 'vfx-p-smoke', count: 8, spread: 28, size: [34, 58], dur: [700, 1100], dist: [18, 46] });
        VFX.retire(burst, 1400);
        VFX.quake(true);
        ctx.impact(target, 40);
      });
    });
    return 2200;
  });
});
