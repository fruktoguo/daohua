/* =========================================================================
 * 通用战斗反馈特效组（非功法，全单位共用）
 * —— 普攻出手 / 受击震颤 / 暴击 / 阵亡消散 / 复活 / 脱战调息 / 施法前摇法阵
 *
 * 粒子体系（全部 radial-gradient 光点 + screen 混合，无硬边多边形）：
 *   .vfx-p-glow   柔和光点（blur + screen，拖尾/爆闪主力）
 *   .vfx-p-spark  方向性火花条（--rot 对齐飞行方向，沿长轴飞出）
 *   .vfx-p-ember  余烬（缓慢上飘 + 明暗闪烁）
 *   .vfx-p-mote   微尘（细小悬浮光尘）
 *   .vfx-p-smoke  雾团（大面积 blur 晕染）
 * 色相经 --c/--c2 CSS 变量定制，宿主节点设置一次全粒子继承。
 * ========================================================================= */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  VFX.injectStyles('feedback', `
/* ============ 粒子基元（CSS 变量驱动：--dx --dy --dur --rot --c --c2） ============ */
.vfx-p-glow { position:absolute; border-radius:50%; background:radial-gradient(circle,#fff 0%,var(--c,#ffd166) 38%,transparent 72%); mix-blend-mode:screen; filter:blur(.8px); animation:vfx-p-fade var(--dur,700ms) ease-out both; }
.vfx-p-mote { position:absolute; border-radius:50%; background:radial-gradient(circle,#fff 0%,var(--c,#e8d69a) 45%,transparent 75%); mix-blend-mode:screen; animation:vfx-p-fade var(--dur,900ms) ease-out both; }
.vfx-p-spark { position:absolute; width:2.5px; border-radius:3px; background:linear-gradient(180deg,#fff,var(--c,#ffd166) 70%,transparent); box-shadow:0 0 6px var(--c,#ffd166); mix-blend-mode:screen; transform-origin:50% 0; animation:vfx-p-fly-dir var(--dur,460ms) cubic-bezier(.15,.6,.35,1) both; }
.vfx-p-ember { position:absolute; border-radius:50%; background:radial-gradient(circle,#fff2c8 0%,var(--c,#ff8a3c) 45%,transparent 75%); mix-blend-mode:screen; animation:vfx-ember var(--dur,1200ms) ease-out both; }
.vfx-p-smoke { position:absolute; border-radius:50%; background:radial-gradient(circle,var(--c,rgba(160,140,110,.5)) 0%,transparent 68%); filter:blur(7px); animation:vfx-smoke var(--dur,1100ms) ease-out both; }
.vfx-p-ring { position:absolute; left:0; top:0; width:60px; height:60px; margin:-30px 0 0 -30px; border-radius:50%; border:1.5px solid var(--c,rgba(232,214,154,.85)); box-shadow:0 0 14px var(--c,rgba(232,214,154,.5)),inset 0 0 12px var(--c,rgba(232,214,154,.3)); mix-blend-mode:screen; filter:blur(.5px); animation:vfx-ring-expand var(--dur,.6s) ease-out both; }
.vfx-p-flare { position:absolute; left:0; top:0; width:160px; height:160px; margin:-80px 0 0 -80px; border-radius:50%; background:radial-gradient(circle,#fff 0%,var(--c,#ffd166) 22%,transparent 68%); mix-blend-mode:screen; animation:vfx-flare var(--dur,.5s) ease-out both; }

@keyframes vfx-p-fade { 0%{opacity:0;transform:translate(0,0) scale(.4)} 18%{opacity:1;transform:translate(0,0) scale(1)} 100%{opacity:0;transform:translate(var(--dx),var(--dy)) scale(.15)} }
@keyframes vfx-p-fly-dir { 0%{opacity:0;transform:rotate(var(--rot)) translate(0,0) scaleY(.6)} 15%{opacity:1;transform:rotate(var(--rot)) translate(0,0) scaleY(1)} 100%{opacity:0;transform:rotate(var(--rot)) translate(var(--dx),var(--dy)) scaleY(.25)} }
@keyframes vfx-ember { 0%{opacity:0;transform:translate(0,0) scale(.5)} 15%{opacity:1} 45%{opacity:.55} 62%{opacity:.9} 100%{opacity:0;transform:translate(var(--dx),var(--dy)) scale(.2)} }
@keyframes vfx-smoke { 0%{opacity:0;transform:translate(0,0) scale(.5)} 30%{opacity:.85} 100%{opacity:0;transform:translate(var(--dx),var(--dy)) scale(1.8)} }
@keyframes vfx-ring-expand { 0%{opacity:.95;transform:scale(.15)} 100%{opacity:0;transform:scale(var(--ring-scale,1.7))} }
@keyframes vfx-flare { 0%{opacity:0;transform:scale(calc(.2*var(--fs,1)))} 30%{opacity:1} 100%{opacity:0;transform:scale(calc(1.45*var(--fs,1)))} }
@keyframes vfx-target-spin { to { transform:rotate(360deg) } }

/* ---- 阵亡残魂 / 复活灵尘 / 调息灵子 ---- */
.vfx-soul-particle { position:absolute; border-radius:50%; background:radial-gradient(circle,rgba(215,225,255,.95),var(--c,rgba(140,150,200,.55)) 60%,transparent 74%); mix-blend-mode:screen; animation:vfx-p-fade var(--dur,900ms) ease-out both; }
.vfx-revive-mote { position:absolute; border-radius:50%; background:radial-gradient(circle,#eafff3,var(--c,#34d399) 55%,transparent 74%); mix-blend-mode:screen; animation:vfx-p-fade var(--dur,800ms) ease-out both; }
.vfx-regen-mote { position:absolute; border-radius:50%; background:radial-gradient(circle,#eafff3,var(--c,#10b981) 58%,transparent 74%); mix-blend-mode:screen; animation:vfx-regen-converge var(--dur,900ms) cubic-bezier(.4,.1,.4,1) both; }
@keyframes vfx-regen-converge { 0%{opacity:0;transform:translate(var(--dx),var(--dy)) scale(.6)} 30%{opacity:1} 100%{opacity:0;transform:translate(0,0) scale(.12)} }

/* ---- 通用施法前摇：聚气收束（向心流光 + 充能光核，无环无旋转几何） ----
   --c 主色相 / --c2 副色相由宿主按功法变体下发 */
/* 向心流光：外圈光点被吸入身位，ease-in 加速——聚气主视觉 */
.vfx-p-stream { position:absolute; border-radius:50%; background:radial-gradient(circle,#fff 0%,var(--c,#c4b5fd) 42%,transparent 72%); mix-blend-mode:screen; filter:blur(.4px); animation:vfx-stream-in var(--dur,800ms) cubic-bezier(.55,.08,.78,.45) both; }
@keyframes vfx-stream-in { 0%{opacity:0;transform:translate(0,0) scale(.45)} 22%{opacity:.95} 82%{opacity:1} 100%{opacity:0;transform:translate(var(--dx),var(--dy)) scale(.22)} }
/* 聚气核心：身位光核随吟唱充能，明暗脉动渐强 */
.vfx-cast-core { position:absolute; left:0; top:0; width:44px; height:44px; margin:-22px 0 0 -22px; border-radius:50%; background:radial-gradient(circle,#fff 0%,var(--c,#c4b5fd) 32%,transparent 70%); mix-blend-mode:screen; filter:blur(1.8px); animation:vfx-core-charge var(--dur,3000ms) ease-in-out both; }
@keyframes vfx-core-charge { 0%{opacity:0;transform:scale(.22)} 18%{opacity:.7;transform:scale(.5)} 34%{transform:scale(.44)} 52%{opacity:.85;transform:scale(.66)} 70%{transform:scale(.58)} 88%{transform:scale(.95)} 100%{opacity:1;transform:scale(1.12)} }

/* ---- 普攻刀光粒子束 ---- */
.vfx-slash-swarm { position:absolute; }
`);

  /** 普攻出手：真实 is-action-attack 突进 + 命中点粒子爆闪 + 受击震颤 */
  VFX.register('feedback.attack', (ctx) => {
    const target = ctx.targets[0];
    VFX.motion(ctx.caster.el, 'attack');
    if (target) {
      VFX.after(300, () => {
        const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
        const host = VFX.node(null, t.x, t.y);
        host.style.setProperty('--c', '#ffd166');
        // 命中闪光 + 放射火花 + 光尘
        const fl = VFX.child(host, 'vfx-p-flare');
        fl.style.setProperty('--fs', '1.2');
        VFX.particles(t.x, t.y, { cls: 'vfx-p-spark', count: 22, directional: true, spread: 12, size: [11, 22], dur: [300, 520], dist: [36, 86] });
        VFX.particles(t.x, t.y, { cls: 'vfx-p-glow', count: 16, spread: 14, size: [4, 8], dur: [380, 700], dist: [26, 70] });
        VFX.retire(host, 800);
        VFX.impact(target.el, 0.5);
        ctx.impact(target, 0);
      });
    }
    return 620;
  });

  /** 受击震颤（独立演示入口） */
  VFX.register('feedback.impact', (ctx) => {
    const target = ctx.targets[0];
    if (!target) return 0;
    VFX.impact(target.el, 0.6);
    const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
    const host = VFX.node(null, t.x, t.y);
    host.style.setProperty('--c', '#ffb08a');
    VFX.particles(t.x, t.y, { cls: 'vfx-p-glow', count: 18, spread: 18, size: [4, 8], dur: [350, 600], dist: [28, 66] });
    VFX.retire(host, 750);
    ctx.impact(target, 0);
    return 560;
  });

  /** 暴击：光核爆闪 + 双层冲击环 + 大量金红火花喷泉 + 重震屏 */
  VFX.register('feedback.crit', (ctx) => {
    const target = ctx.targets[0];
    if (!target) return 0;
    const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
    const host = VFX.node(null, t.x, t.y - 8);
    host.style.setProperty('--c', '#ff9a3c');
    const flare = VFX.child(host, 'vfx-p-flare');
    flare.style.setProperty('--c', '#ffd166');
    flare.style.setProperty('--fs', '1.6');
    const r1 = VFX.child(host, 'vfx-p-ring');
    r1.style.setProperty('--c', 'rgba(255,200,90,.9)');
    r1.style.setProperty('--ring-scale', '2.6');
    const r2 = VFX.child(host, 'vfx-p-ring');
    r2.style.setProperty('--c', 'rgba(255,120,60,.55)');
    r2.style.setProperty('--ring-scale', '3.6');
    r2.style.setProperty('--dur', '.85s');
    // 火花喷泉（向上主方向）+ 光尘云
    VFX.particles(t.x, t.y + 8, { cls: 'vfx-p-spark', count: 40, directional: true, spread: 14, size: [14, 28], dur: [360, 660], dist: [46, 130], angle: -Math.PI / 2, arc: Math.PI * 1.5 });
    VFX.particles(t.x, t.y, { cls: 'vfx-p-glow', count: 28, spread: 16, size: [5, 9], dur: [450, 850], dist: [34, 104] });
    VFX.particles(t.x, t.y + 10, { cls: 'vfx-p-smoke', count: 8, spread: 24, size: [34, 60], dur: [750, 1150], dist: [16, 42] });
    VFX.retire(host, 1400);
    VFX.quake(true);
    ctx.impact(target, 120, { crit: true });
    return 1100;
  });

  /** 力竭阵亡：残魂萤火四散 + 灵光熄灭 */
  VFX.register('feedback.defeat', (ctx) => {
    const target = ctx.targets[0];
    if (!target) return 0;
    ctx.kill(target);
    return 1300;
  });

  /** 复活：青绿光柱 + 灵尘回旋上升 */
  VFX.register('feedback.revive', (ctx) => {
    ctx.targets.forEach(t => ctx.reviveActor(t));
    return 900;
  });

  /** 脱战调息：周天灵子向丹田收束 + 地脉灵环 */
  VFX.register('feedback.regen', (ctx) => {
    const actor = ctx.caster;
    const t = VFX.center(actor.el.querySelector('.arena-avatar-stage') || actor.el);
    const host = VFX.node(null, t.x, t.y + 18);
    host.style.setProperty('--c', '#34d399');
    for (let i = 0; i < 32; i++) {
      const p = VFX.child(host, 'vfx-regen-mote');
      const a = Math.random() * Math.PI * 2;
      const d = 44 + Math.random() * 66;
      p.style.setProperty('--dx', Math.cos(a) * d + 'px');
      p.style.setProperty('--dy', Math.sin(a) * d + 'px');
      p.style.setProperty('--dur', (700 + Math.random() * 600) + 'ms');
      const sz = 3.5 + Math.random() * 4.5;
      p.style.width = sz + 'px';
      p.style.height = sz + 'px';
    }
    const ring = VFX.child(host, 'vfx-p-ring');
    ring.style.setProperty('--c', 'rgba(52,211,153,.85)');
    ring.style.setProperty('--ring-scale', '2.2');
    VFX.retire(host, 1500);
    ctx.regen(actor);
    return 1400;
  });

  /**
   * 施法前摇聚气：身位充能光核 + 外圈向心流光被持续吸入（随 cast 演出自动调用）。
   * 门槛：仅吟唱 ≥3s 的仪式级功法显示（当前命中轰炎 3s / 破阵子 5s），
   * 其余吟唱只走真实行动条读条，避免短吟唱技能稀释战场信息。
   *
   * 变体表按功法 id 定制色相/粒径/流光圈径；default 兜底未登记的仪式级功法。
   */
  const CAST_RING_MIN_MS = 3000;
  const CAST_RING_VARIANTS = {
    default: { c: '#c4b5fd', c2: '#8b7cf6', size: [3, 5], spawnRadius: 76 },
    'ability.blazing-blast': { c: '#ffa54d', c2: '#ff5f2e', size: [3, 6], spawnRadius: 80 },
    'ability.pozhen-melody': { c: '#ffd76a', c2: '#b08cff', size: [3, 5], spawnRadius: 86 },
  };

  VFX.register('feedback.castRing', (ctx) => {
    if ((ctx.castMs || 0) < CAST_RING_MIN_MS) return 0;
    const v = CAST_RING_VARIANTS[(ctx.ability && ctx.ability.id) || ''] || CAST_RING_VARIANTS.default;
    const stage = ctx.caster.el.querySelector('.arena-avatar-stage') || ctx.caster.el;
    const t = VFX.center(stage);
    const dur = Math.min(ctx.castMs || 900, 9000);

    // 聚气核心：身位光核随吟唱充能渐亮（被吸入能量的汇聚点）
    const hostBody = VFX.node(null, t.x, t.y + 2);
    hostBody.style.setProperty('--c', v.c);
    hostBody.style.setProperty('--c2', v.c2);
    const core = VFX.child(hostBody, 'vfx-cast-core');
    core.style.setProperty('--dur', dur + 'ms');
    VFX.retire(hostBody, dur + 260);

    // 向心流光：外圈光点被持续吸入身位——主色相密流 + 副色相稀疏点缀
    VFX.emit(t.x, t.y + 2, { cls: 'vfx-p-stream', interval: 62, duration: dur * 0.96, perBurst: 3, inward: true, spawnRadius: v.spawnRadius, spread: 30, squash: 0.62, size: v.size, dur: [500, 720], c: v.c });
    VFX.emit(t.x, t.y + 2, { cls: 'vfx-p-stream', interval: 140, duration: dur * 0.96, perBurst: 2, inward: true, spawnRadius: v.spawnRadius + 12, spread: 26, squash: 0.62, size: [2, 4], dur: [440, 640], c: v.c2 });

    // 吟唱完成前一瞬：汇入一波密流 + 核心辉光闪爆，无缝交棒给技能演出
    VFX.after(Math.max(0, dur - 240), () => {
      const fl = VFX.child(hostBody, 'vfx-p-flare');
      fl.style.setProperty('--fs', '0.7');
      fl.style.setProperty('--dur', '340ms');
      VFX.particles(t.x, t.y + 2, { cls: 'vfx-p-stream', count: 12, inward: true, spawnRadius: v.spawnRadius, spread: 24, squash: 0.62, size: [3, 5], dur: [220, 340], c: v.c });
    });
    return dur;
  });

  /** 未知功法通用爆发兜底：五行未明的紫金灵爆（无专属演出的 abilityId 出手时使用） */
  VFX.register('feedback.burst', (ctx) => {
    ctx.targets.forEach((target, i) => {
      VFX.after(i * 120, () => {
        const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
        const host = VFX.node(null, t.x, t.y);
        host.style.setProperty('--c', '#c4b5fd');
        const fl = VFX.child(host, 'vfx-p-flare');
        fl.style.setProperty('--fs', '1.4');
        const r = VFX.child(host, 'vfx-p-ring');
        r.style.setProperty('--ring-scale', '2.6');
        VFX.particles(t.x, t.y, { cls: 'vfx-p-glow', count: 24, spread: 20, size: [5, 9], dur: [420, 780], dist: [34, 92] });
        VFX.particles(t.x, t.y, { cls: 'vfx-p-spark', count: 18, directional: true, spread: 14, size: [10, 20], dur: [300, 540], dist: [40, 96] });
        VFX.retire(host, 1100);
      });
    });
    return 900;
  });
});
