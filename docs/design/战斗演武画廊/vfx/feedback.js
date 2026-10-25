/* =========================================================================
 * 通用战斗反馈：普攻、暴击、聚灵吟唱均使用共享 Canvas 程序化粒子。
 * 局部受击/阵亡 DOM 是角色状态反馈，不是技能粒子贴图；仍由 core 统一管理。
 * ========================================================================= */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';

  const variants = ['cleave', 'thrust', 'sweep', 'pulse'];
  const sequences = new WeakMap();
  const nextVariant = (caster, explicit) => {
    if (variants.includes(explicit)) return explicit;
    const index = sequences.get(caster) || 0;
    sequences.set(caster, index + 1);
    return variants[index % variants.length];
  };

  /** 普攻最多八枚粒子，保留原有四式节奏和唯一命中结算。 */
  VFX.register('feedback.attack', ctx => {
    const target = ctx.targets[0];
    VFX.motion(ctx.caster.el, 'attack');
    if (!target) return 0;
    const variant = nextVariant(ctx.caster.el, ctx.variant);
    const s = VFX.center(ctx.caster.el.querySelector('.arena-avatar-stage') || ctx.caster.el);
    const t = VFX.center(target.el.querySelector('.arena-avatar-stage') || target.el);
    const dir = t.x < s.x ? -1 : 1;
    const stage = VFX.particleStage();
    const tl = stage.timeline({ seed: 'attack-' + variant, clock: ctx.clock || 'battle' });
    const colors = ['#fff7d8', '#e9cd91'];
    const at = 300;
    if (variant === 'cleave') {
      stage.slash(tl, { at: at - 45, x: t.x, y: t.y, length: 80, bend: -15, angle: dir * 0.58, count: 8, travel: 45, life: [150, 230], colors, size: [0.8, 1.3], length: [5, 8] });
    } else if (variant === 'thrust') {
      stage.stream(tl, { at: at - 110, x0: s.x, y0: s.y, x1: t.x, y1: t.y, flight: 110, count: 6, stagger: 3, life: 70, spread: 0, colors, size: [0.8, 1.2], length: [5, 8], headSize: 2, headLength: 7 });
    } else if (variant === 'sweep') {
      stage.slash(tl, { at: at - 50, x: t.x, y: t.y, length: 66, bend: 21, angle: dir * -0.48, count: 8, travel: 50, life: [160, 240], colors, size: [0.8, 1.3], length: [4, 7] });
    } else {
      stage.ring(tl, { at: at - 10, x: t.x, y: t.y + 15, count: 8, radius: 6, endRadius: 28, squash: 0.45, life: [170, 240], colors, size: [0.8, 1.2], length: [3, 5] });
    }
    stage.burst(tl, { at, x: t.x, y: t.y, count: 4, angle: dir > 0 ? 0 : Math.PI, spread: 1.4, speed: [60, 110], life: [150, 230], colors, size: [0.7, 1.1], length: [3, 5], glow: false });
    tl.at(at, () => ctx.impact(target, 0, { impactVariant: variant === 'pulse' ? 'ripple' : variant === 'sweep' ? 'splinter' : 'cleave' }));
    return tl.finish(620);
  });

  VFX.register('feedback.impact', ctx => {
    const target = ctx.targets[0];
    if (!target) return 0;
    ctx.impact(target, 0, { impactVariant: ctx.variant });
    return 340;
  });

  /** 暴击只放局部大冲击，绝不使用全屏闪光或烟团。 */
  VFX.register('feedback.crit', ctx => VFX.castParticles(ctx, {
    id: 'feedback.crit', duration: 650,
    script(tl, fx) {
      const target = fx.targets[0];
      const t = fx.point(target);
      const colors = ['#fff3b8', '#ffc45e', '#ff784a'];
      fx.stage.slash(tl, { at: 60, x: t.x, y: t.y - 5, length: 112, bend: -24, angle: 0.72, count: 24, travel: 55, life: [170, 250], colors, size: [1.2, 2], length: [6, 12] });
      fx.hit(target, 120, { colors, count: 34, radius: 65, variant: 'splinter' });
      fx.VFX.quake(true);
    },
  }));

  VFX.register('feedback.defeat', ctx => {
    const target = ctx.targets[0];
    if (!target) return 0;
    ctx.kill(target);
    return 800;
  });

  VFX.register('feedback.revive', ctx => {
    ctx.targets.forEach(target => ctx.reviveActor(target));
    return 900;
  });

  VFX.register('feedback.regen', ctx => VFX.castParticles(ctx, {
    id: 'feedback.regen', self: true, duration: 800, motion: false,
    script(tl, fx) {
      const t = fx.source;
      const colors = ['#e3fff0', '#80d8b2', '#4bbaa4'];
      fx.stage.converge(tl, { at: 0, x: t.x, y: t.y + 16, count: 30, radius: 75, life: [320, 440], colors, squash: 0.8 });
      fx.stage.ring(tl, { at: 140, x: t.x, y: t.y + 23, count: 28, endRadius: 48, squash: 0.28, life: [240, 310], colors });
      tl.at(300, () => ctx.regen(ctx.caster));
    },
  }));

  /** 仪式级吟唱以清晰硬芯向心收束；保留配表吟唱时长，不延长后摇。 */
  const CAST_MIN_MS = 3000;
  const CAST = {
    default: ['#e8deff', '#b8a0f1'],
    'ability.blazing-blast': ['#fff0ba', '#ff9547'],
    'ability.pozhen-melody': ['#fff0b3', '#cbb0f0'],
    'ability.sui-gang': ['#ffe5b1', '#d9644c'],
    'ability.tide': ['#d5faff', '#65c3e2'],
  };

  VFX.register('feedback.castRing', ctx => {
    if ((ctx.castMs || 0) < CAST_MIN_MS) return 0;
    const stage = VFX.particleStage();
    const tl = stage.timeline({ seed: 'cast-' + ((ctx.ability && ctx.ability.id) || 'default'), clock: 'battle' });
    const t = VFX.center(ctx.caster.el.querySelector('.arena-avatar-stage') || ctx.caster.el);
    const colors = CAST[(ctx.ability && ctx.ability.id) || ''] || CAST.default;
    const duration = Math.min(ctx.castMs || 900, 9000);
    stage.orbit(tl, { at: 0, x: t.x, y: t.y, count: 24, radius: [48, 66], endRadius: 22, squash: 0.65, spin: Math.PI * 2.2, life: [duration * 0.65, duration * 0.8], colors, size: [1, 2], length: [3, 6] });
    for (let at = 0; at < duration - 200; at += 320) {
      stage.converge(tl, { at, x: t.x, y: t.y, count: 9, radius: 66, life: [230, 320], colors, squash: 0.62, size: [1, 1.8], length: [4, 7] });
    }
    stage.converge(tl, { at: Math.max(0, duration - 230), x: t.x, y: t.y, count: 18, radius: 56, life: [160, 230], colors, squash: 0.6 });
    return tl.finish(duration);
  });
});
