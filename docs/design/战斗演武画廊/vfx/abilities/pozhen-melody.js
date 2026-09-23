/* ability.pozhen-melody · 破阵子 —— 奏响破阵战歌
 * 镜像 config/game-data/abilities/spell_abilities.json：
 *   我方全体 / 吟唱 5000ms / buff 破阵子 30s（免伤 15%）/ 冷却 60s / 灵力 50 / discipline=internal
 * 演出（全粒子）：金色灵子阵盘旋绕展开 + 光符萤火升空 → 友方落地增益（真实 buff-square） */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.register('ability.pozhen-melody', (ctx) => {
    VFX.motion(ctx.caster.el, 'cast');
    const s = VFX.center(ctx.caster.el.querySelector('.arena-avatar-stage') || ctx.caster.el);
    const host = VFX.node(null, s.x, s.y + 30);
    host.style.setProperty('--c', '#e8d69a');

    // 阵盘：环周均布的光点 + 双层扩散光环（粒子圆阵，非实体盘）
    for (let k = 0; k < 24; k++) {
      const a = (k / 24) * Math.PI * 2;
      const dot = VFX.child(host, 'vfx-p-glow');
      dot.style.left = (Math.cos(a) * 62) + 'px';
      dot.style.top = (Math.sin(a) * 24) + 'px';
      const sz = 4 + Math.random() * 5;
      dot.style.width = sz + 'px';
      dot.style.height = sz + 'px';
      dot.style.setProperty('--dx', Math.cos(a) * 22 + 'px');
      dot.style.setProperty('--dy', (Math.sin(a) * 11 - 40) + 'px');
      dot.style.setProperty('--dur', (950 + Math.random() * 450) + 'ms');
    }
    const r1 = VFX.child(host, 'vfx-p-ring');
    r1.style.setProperty('--c', 'rgba(232,214,154,.9)');
    r1.style.setProperty('--ring-scale', '2.8');
    r1.style.setProperty('--dur', '1s');
    const r2 = VFX.child(host, 'vfx-p-ring');
    r2.style.setProperty('--c', 'rgba(232,214,154,.45)');
    r2.style.setProperty('--ring-scale', '4.2');
    r2.style.setProperty('--dur', '1.3s');
    r2.style.animationDelay = '.15s';

    // 战歌灵符：金色萤火螺旋升空
    VFX.particles(s.x, s.y + 26, { cls: 'vfx-p-ember', count: 34, spread: 76, size: [4, 8], dur: [950, 1500], dist: [24, 60], rise: 130 });
    VFX.emit(s.x, s.y + 24, { cls: 'vfx-p-mote', interval: 75, duration: 1000, perBurst: 4, spread: 66, size: [3, 5], dur: [750, 1150], dist: [18, 48], rise: 112 });
    VFX.retire(host, 1900);

    // 增益落地：友方头顶金光一闪 + 灵尘洒落 + 真实 buff-square
    ctx.targets.forEach((target, i) => {
      VFX.after(520 + i * 130, () => {
        const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
        const ward = VFX.node(null, t.x, t.y - 6);
        ward.style.setProperty('--c', '#e8d69a');
        const fl = VFX.child(ward, 'vfx-p-flare');
        fl.style.setProperty('--c', '#f0e2b0');
        fl.style.setProperty('--dur', '.7s');
        fl.style.setProperty('--fs', '1.5');
        VFX.particles(t.x, t.y - 16, { cls: 'vfx-p-mote', count: 18, spread: 42, size: [3, 5], dur: [650, 1000], dist: [22, 50], rise: 42 });
        VFX.retire(ward, 1200);
        ctx.buffApply(target, 60);
      });
    });
    return 2200;
  });
});
