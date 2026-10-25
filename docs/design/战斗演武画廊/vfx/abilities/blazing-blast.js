/* ability.blazing-blast · 轰炎 —— 轰出一片灼热烈焰
 * 镜像 config/game-data/abilities/spell_abilities.json：
 *   敌方全体 / 吟唱 3000ms / 1.0×AD+20 / 冷却 24s / 灵力 10 / 五行·火
 * 演出（全粒子）：赤金火球（光核+持续火焰拖尾）抛物轰入 → 命中爆成余烬火海 + 烟团升腾 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.injectStyles('ability.blazing-blast', `
.fx-bb-core { position:absolute; left:-13px; top:-13px; width:26px; height:26px; border-radius:50%; background:radial-gradient(circle,#fff 0%,#ffd166 32%,#ff7a30 66%,transparent 80%); mix-blend-mode:screen; filter:blur(.5px); box-shadow:0 0 26px rgba(255,130,40,.95); }
`);

  VFX.register('ability.blazing-blast', (ctx) => {
    VFX.motion(ctx.caster.el, 'cast');
    ctx.targets.forEach((target, i) => {
      const at = 240 + i * 180;
      VFX.after(at, () => {
        const tEl = target.el.querySelector('.arena-avatar-stage') || target.el;
        const s = VFX.center(ctx.caster.el.querySelector('.arena-avatar-stage') || ctx.caster.el);
        const t = VFX.center(tEl);
        // 弹道火球 + 沿途持续播撒火焰拖尾
        VFX.emit(s.x, s.y - 10, { cls: 'vfx-p-ember', interval: 45, duration: 400, perBurst: 4, spread: 10, size: [4, 8], dur: [320, 560], dist: [18, 44], rise: 12 });
        VFX.projectile(ctx.caster.el, tEl, 'fx-bb-core', 380, { lift: 56 }, () => {
          const hit = VFX.node(null, t.x, t.y + 10);
          hit.style.setProperty('--c', '#ff8a3c');
          // 爆轰闪光 + 火海余烬升腾 + 烟团翻滚
          const fl = VFX.child(hit, 'vfx-p-flare');
          fl.style.setProperty('--c', '#ffb050');
          fl.style.setProperty('--fs', '1.7');
          const r = VFX.child(hit, 'vfx-p-ring');
          r.style.setProperty('--c', 'rgba(255,140,50,.8)');
          r.style.setProperty('--ring-scale', '3');
          VFX.particles(t.x, t.y + 12, { cls: 'vfx-p-ember', count: 46, spread: 44, size: [4, 9], dur: [700, 1250], dist: [30, 84], rise: 110 });
          VFX.particles(t.x, t.y + 16, { cls: 'vfx-p-smoke', count: 13, spread: 42, size: [38, 72], dur: [800, 1300], dist: [22, 56] });
          VFX.particles(t.x, t.y + 6, { cls: 'vfx-p-spark', count: 26, directional: true, spread: 22, size: [12, 24], dur: [320, 580], dist: [38, 104], angle: -Math.PI / 2, arc: Math.PI * 1.4 });
          VFX.particles(t.x, t.y + 8, { cls: 'vfx-p-glow', count: 20, spread: 30, size: [5, 10], dur: [450, 800], dist: [30, 80] });
          // 余火持续舔舐
          VFX.emit(t.x, t.y + 18, { cls: 'vfx-p-ember', interval: 65, duration: 620, perBurst: 5, spread: 48, size: [4, 7], dur: [550, 950], dist: [16, 40], rise: 75 });
          VFX.retire(hit, 1700);
          ctx.impact(target, 0);
        });
      });
    });
    return 1900;
  });
});
