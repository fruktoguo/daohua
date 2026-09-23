/* ability.taunt · 嘲讽 —— 运功怒喝，激将敌手
 * 镜像 config/game-data/abilities/physical_abilities.json：
 *   自身 / 吟唱 200ms / 仇恨+500（2×仇恨倍率）/ 冷却 55s / 灵力 20 / discipline=internal
 * 演出（全粒子）：金色粒子冲击波双环炸开 + 怒气火星升腾 + 护身光晕 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.taunt', (ctx) => {
    const caster = ctx.caster;
    VFX.motion(caster.el, 'cast');
    const t = VFX.center(caster.el.querySelector('.arena-avatar-stage') || caster.el);
    const host = VFX.node(null, t.x, t.y + 30);
    host.style.setProperty('--c', '#ffc450');

    // 双重冲击环（粒子光晕环）
    const r1 = VFX.child(host, 'vfx-p-ring');
    r1.style.setProperty('--c', 'rgba(255,196,80,.95)');
    r1.style.setProperty('--ring-scale', '3.2');
    r1.style.setProperty('--dur', '.75s');
    const r2 = VFX.child(host, 'vfx-p-ring');
    r2.style.setProperty('--c', 'rgba(255,226,150,.55)');
    r2.style.setProperty('--ring-scale', '4.4');
    r2.style.setProperty('--dur', '.95s');
    r2.style.animationDelay = '.1s';

    // 环周金色粒子向外炸开（环形均布）
    VFX.particles(t.x, t.y + 26, { cls: 'vfx-p-glow', count: 42, spread: 40, size: [4, 9], dur: [460, 780], dist: [64, 140] });
    // 怒气火星自头顶升腾
    VFX.particles(t.x, t.y - 20, { cls: 'vfx-p-ember', count: 24, spread: 30, size: [4, 8], dur: [650, 1100], dist: [18, 48], rise: 100 });
    VFX.emit(t.x, t.y - 26, { cls: 'vfx-p-ember', interval: 80, duration: 560, perBurst: 4, spread: 26, size: [3, 6], dur: [550, 850], dist: [14, 34], rise: 90 });
    // 护身光晕一闪
    const ward = VFX.spawn('vfx-p-flare', t.x, t.y - 12, 700);
    ward.style.setProperty('--c', '#ffcf5e');
    ward.style.setProperty('--fs', '1.5');

    ctx.threat(caster, 150);
    VFX.retire(host, 1200);
    return 1100;
  });
});
