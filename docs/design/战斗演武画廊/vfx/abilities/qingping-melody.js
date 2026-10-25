/* ability.qingping-melody · 清平乐 —— 奏响清平雅乐
 * 镜像 config/game-data/abilities/spell_abilities.json：
 *   治疗 / 吟唱 500ms / 0.6×+20 / 目标=气血最低友方 / 冷却 30s / 灵力 10 / discipline=internal
 * 演出（全粒子）：青绿灵子光羽环绕升腾 + 双环生机光晕 + 灵露花粉飘散 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.qingping-melody', (ctx) => {
    VFX.motion(ctx.caster.el, 'cast');
    ctx.targets.forEach((target, i) => {
      const at = 140 + i * 120;
      VFX.after(at, () => {
        const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
        const host = VFX.node(null, t.x, t.y + 12);
        host.style.setProperty('--c', '#34d399');
        // 双环生机光晕（柔和光波扩散）
        const r1 = VFX.child(host, 'vfx-p-ring');
        r1.style.setProperty('--c', 'rgba(52,211,153,.9)');
        r1.style.setProperty('--ring-scale', '2.4');
        const r2 = VFX.child(host, 'vfx-p-ring');
        r2.style.setProperty('--c', 'rgba(110,231,183,.5)');
        r2.style.setProperty('--ring-scale', '3.4');
        r2.style.setProperty('--dur', '1s');
        r2.style.animationDelay = '.15s';
        // 灵子光羽螺旋上升（青色光点主升 + 白色灵露碎光）
        VFX.particles(t.x, t.y + 16, { cls: 'vfx-p-glow', count: 38, spread: 56, size: [4, 9], dur: [800, 1350], dist: [22, 60], rise: 120 });
        VFX.particles(t.x, t.y + 12, { cls: 'vfx-p-mote', count: 24, spread: 46, size: [3, 5], dur: [700, 1200], dist: [16, 46], rise: 96 });
        // 持续灵尘萦绕
        VFX.emit(t.x, t.y + 18, { cls: 'vfx-p-mote', interval: 70, duration: 800, perBurst: 3, spread: 52, size: [3, 5], dur: [600, 900], dist: [14, 36], rise: 84 });
        // 顶部柔和闪光
        const fl = VFX.spawn('vfx-p-flare', t.x, t.y - 30, 800);
        fl.style.setProperty('--c', '#a7f3d0');
        fl.style.setProperty('--fs', '1.4');
        VFX.retire(host, 1900);
        ctx.heal(target, 260);
      });
    });
    return 1900;
  });
});
