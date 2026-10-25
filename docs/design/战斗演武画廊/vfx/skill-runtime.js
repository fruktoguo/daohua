/* =========================================================================
 * 全技能 Canvas 编排公共接口：锚点、短命中反馈、目标与配表镜像字段归一。
 * 技能文件只描述自己的演出，不重复实现时钟、DOM、随机或清理逻辑。
 * ========================================================================= */
(function () {
  'use strict';

  const MODULES = window.DaohuaVFXModules = window.DaohuaVFXModules || [];
  const ELEMENT_COLORS = {
    none: ['#fff1c7', '#e7bd65'],
    metal: ['#fff8da', '#ffd16a', '#f0a648'],
    wood: ['#f0edff', '#c6b4ff', '#9477f5'],
    water: ['#e2fbff', '#79deef', '#3c9ddd'],
    fire: ['#fff2c1', '#ffb04c', '#ff6341'],
  };

  MODULES.push(function (VFX) {
    /** 取立绘中心，避免粒子覆盖血条/名字/行动条所在的 DOM 区域。 */
    function point(actor) {
      return VFX.center(actor.el.querySelector('.arena-avatar-stage') || actor.el);
    }

    function scale(actor) {
      const stage = actor.el.querySelector('.arena-avatar-stage');
      const width = stage ? VFX.rect(stage).width : 90;
      return Math.max(0.68, Math.min(1.65, width / 90));
    }

    function direction(from, to) {
      return to.x < from.x ? -1 : 1;
    }

    function colors(element) {
      return ELEMENT_COLORS[element] || ELEMENT_COLORS.none;
    }

    /**
     * 主动技/内功预览共用的粒子演出入口。
     * 所有技能只用 script(tl, tools) 编排；finish 会自动容纳最后一批粒子。
     */
    VFX.castParticles = function (ctx, options) {
      const targets = options.self ? [ctx.caster] : ctx.targets;
      if (!ctx.caster || !targets.length) return 0;
      const stage = VFX.particleStage();
      const tl = stage.timeline({
        seed: ctx.seed || options.id,
        clock: ctx.clock || 'preview',
        speed: ctx.particleSpeed || 1,
      });
      if (options.motion !== false) VFX.motion(ctx.caster.el, options.motion || 'cast');

      const tools = {
        VFX,
        stage,
        caster: ctx.caster,
        targets,
        ability: ctx.ability || null,
        source: point(ctx.caster),
        point,
        scale,
        direction,
        colors: colors(ctx.ability && ctx.ability.element),
        /**
         * 目标生效帧：颗粒冲击与演示结算同刻，杜绝爆闪与掉血各走一条时间线。
         * 正式前端的这些回调为 no-op，由既有权威事件驱动受击和跳字。
         */
        hit(target, at, settings) {
          const o = settings || {};
          const t = point(target);
          const s = scale(target);
          const palette = o.colors || colors(ctx.ability && ctx.ability.element);
          stage.ring(tl, {
            at, x: t.x, y: t.y + 16 * s, endRadius: (o.radius || 49) * s,
            life: [190, 280], count: o.ringCount || 28, squash: 0.36,
            colors: palette, size: [1.1, 2], length: [6, 12],
          });
          // 命中帧的六根短冲击刃：瞬时、方向明确，不形成盖住立绘的圆形光团。
          const facing = o.angle == null ? 0.15 : o.angle;
          for (let ray = 0; ray < 6; ray++) {
            const angle = facing + ray / 6 * Math.PI * 2;
            const near = 7 * s;
            const far = (o.radius || 49) * 0.7 * s;
            stage.path(tl, [
              [t.x + Math.cos(angle) * near, t.y + Math.sin(angle) * near],
              [t.x + Math.cos(angle) * far, t.y + Math.sin(angle) * far],
            ], {
              at, count: 4, travel: 20, life: [90, 150], colors: palette,
              size: [1.3, 2.4], length: [7, 13], opacity: 0.95,
            });
          }
          stage.burst(tl, {
            at, x: t.x, y: t.y, count: o.count || 22,
            angle: facing, spread: o.spread == null ? Math.PI * 2 : o.spread,
            speed: [130, 320], life: [160, 290], drag: 4.5,
            size: [1.1, 2.2], length: [7, 16], colors: palette,
            gravity: o.gravity || 45, shape: o.shape,
          });
          tl.at(at, () => {
            if (ctx.impact) ctx.impact(target, 0, { impactVariant: o.variant || 'cleave' });
          });
        },
        heal(target, at) {
          tl.at(at, () => { if (ctx.heal) ctx.heal(target, 0); });
        },
        buff(target, at) {
          tl.at(at, () => {
            if (!ctx.buffApply) return;
            const specs = ctx.ability && ctx.ability.buffs;
            if (specs && specs.length) specs.forEach(buff => ctx.buffApply(target, 0, buff));
            else ctx.buffApply(target, 0, ctx.ability && ctx.ability.buff);
          });
        },
        threat(target, at) {
          tl.at(at, () => { if (ctx.threat) ctx.threat(target, 0); });
        },
      };
      options.script(tl, tools);
      return tl.finish(options.duration || 850);
    };

    /** 未配表专属演出也必须保持粒子风格；不会回退到旧烟雾 DOM。 */
    VFX.register('feedback.burst', function (ctx) {
      return VFX.castParticles(ctx, {
        id: 'feedback.burst',
        duration: 580,
        script(tl, fx) {
          fx.targets.forEach((target, index) => fx.hit(target, 80 + index * 60, {
            colors: ['#f2e6ff', '#c4b5fd', '#a78bfa'], count: 24,
          }));
        },
      });
    });
  });
})();
