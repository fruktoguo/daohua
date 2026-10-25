/* 十门常驻内功：只在画廊提供自身运功预览，不施放、不伤害、不伪造 Buff。 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  const palettes = {
    qi: ['#e4fff7', '#8bdac5', '#6bb5c7'],
    force: ['#fff0c5', '#e4b266', '#d67e4d'],
    iron: ['#f5ecd1', '#b9b5a3', '#879b9e'],
    swift: ['#effff5', '#9ee8cf', '#78c9e9'],
    mind: ['#f2edff', '#c6b3fa', '#91c5ed'],
    blood: ['#ffe5b2', '#ef895b', '#c44f4a'],
    cosmic: ['#fff2c6', '#b8d6db', '#b3a0e8'],
  };

  /** 各心法沿自身流派形成不同粒子剪影，不进入正式施法注册表。 */
  const forms = {
    'ability.tuna-gong': 'breath',
    'ability.manniu-jin': 'force',
    'ability.tiebu-shan': 'ward',
    'ability.feiyan-gong': 'wings',
    'ability.qingxin-jue': 'lotus',
    'ability.fenxue-gong': 'blood',
    'ability.xiaozhoutian-gong': 'orbit',
    'ability.cunshen-jue': 'mind',
    'ability.lianjing-huaqi-jue': 'refine',
    'ability.xuanyuan-yiqi-gong': 'cosmos',
  };

  VFX.previewInternal = function (id, ctx) {
    const form = forms[id];
    if (!form) return 0;
    return VFX.castParticles({ ...ctx, ability: ctx.ability || { id, element: 'none' }, clock: 'preview' }, {
      id, self: true, duration: 1050, motion: false,
      script(tl, fx) {
        const t = fx.source;
        const s = fx.scale(fx.caster);
        const colors = form === 'force' ? palettes.force : form === 'ward' ? palettes.iron : form === 'wings' ? palettes.swift : form === 'blood' ? palettes.blood : form === 'mind' || form === 'cosmos' ? palettes.mind : palettes.qi;
        const orbitColors = form === 'orbit' ? palettes.cosmic : form === 'cosmos' ? palettes.cosmic : colors;
        fx.stage.converge(tl, { at: 0, x: t.x, y: t.y, count: 22, radius: 58 * s, life: [190, 260], colors });

        if (form === 'breath' || form === 'refine') {
          for (let i = 0; i < 2; i++) fx.stage.orbit(tl, { at: 120 + i * 220, x: t.x, y: t.y + 8, count: 24, radius: [30, 45], endRadius: i ? 9 : 28, spin: (i ? -1 : 1) * Math.PI * 1.5, life: [320, 400], colors, squash: 0.72 });
          fx.stage.converge(tl, { at: 360, x: t.x, y: t.y + 18 * s, count: 22, radius: 48, life: [200, 280], colors, shape: form === 'refine' ? 'diamond' : 'leaf' });
        } else if (form === 'force' || form === 'blood') {
          fx.stage.path(tl, [[t.x - 34 * s, t.y - 32 * s], [t.x - 15 * s, t.y - 46 * s], [t.x, t.y - 27 * s], [t.x + 15 * s, t.y - 46 * s], [t.x + 34 * s, t.y - 32 * s]], { at: 170, count: 28, travel: 70, life: [210, 280], colors, size: [1.1, 2], length: [4, 9] });
          fx.stage.burst(tl, { at: 210, x: t.x, y: t.y + 16, count: 26, angle: -Math.PI / 2, spread: 1.5, speed: [70, 170], gravity: form === 'blood' ? -35 : 200, life: [280, 390], colors, shape: 'diamond' });
          fx.stage.orbit(tl, { at: 250, x: t.x, y: t.y, count: 26, radius: [35, 48], endRadius: 20, spin: -Math.PI * 1.8, life: [320, 420], colors, shape: 'shard', squash: 0.75 });
        } else if (form === 'ward') {
          fx.stage.polygon(tl, { at: 140, x: t.x, y: t.y, radius: 49 * s, sides: 6, count: 48, travel: 110, life: [350, 470], hold: 80, colors, size: [1, 1.9], length: [5, 8] });
          fx.stage.ring(tl, { at: 260, x: t.x, y: t.y + 25 * s, radius: 18, endRadius: 58 * s, squash: 0.3, count: 32, life: [280, 360], colors });
        } else if (form === 'wings') {
          for (let side = -1; side <= 1; side += 2) {
            for (let feather = 0; feather < 4; feather++) {
              fx.stage.path(tl, [[t.x, t.y + feather * 5], [t.x + side * (24 + feather * 7) * s, t.y - 28 * s + feather * 11], [t.x + side * (47 + feather * 4) * s, t.y - 32 * s + feather * 13]], { at: 100 + feather * 50, count: 14, travel: 65, life: [210, 280], colors, size: [1, 1.7], length: [5, 9] });
            }
          }
          fx.stage.burst(tl, { at: 250, x: t.x, y: t.y, count: 18, angle: -Math.PI / 2, spread: 2.2, speed: [50, 150], life: [250, 400], colors, shape: 'leaf' });
        } else if (form === 'lotus' || form === 'mind') {
          for (let i = 0; i < 6; i++) {
            const a = i / 6 * Math.PI * 2;
            fx.stage.slash(tl, { at: 130 + i * 30, x: t.x + Math.cos(a) * 23 * s, y: t.y + Math.sin(a) * 24 * s, length: 26 * s, bend: -8 * s, angle: a + Math.PI / 2, count: 10, travel: 35, life: [260, 360], colors, shape: 'leaf', size: [1, 1.7], length: [3, 5] });
          }
          fx.stage.converge(tl, { at: 260, x: t.x, y: t.y - 8, count: 20, radius: 56, life: [190, 270], colors });
        } else {
          fx.stage.polygon(tl, { at: 100, x: t.x, y: t.y, radius: 45 * s, sides: form === 'cosmos' ? 5 : 8, count: 40, travel: 80, life: [320, 430], hold: 60, colors: orbitColors, size: [0.9, 1.5], length: [4, 7] });
          fx.stage.orbit(tl, { at: 160, x: t.x, y: t.y, count: 32, radius: [32, 55], endRadius: 18, spin: Math.PI * 2.2, life: [380, 480], colors: orbitColors, squash: 0.62 });
          fx.stage.ring(tl, { at: 330, x: t.x, y: t.y + 23 * s, radius: 15, endRadius: 56 * s, squash: 0.3, count: 30, life: [260, 340], colors: orbitColors });
        }
        fx.stage.ring(tl, { at: 460, x: t.x, y: t.y + 25 * s, radius: 12, endRadius: 49 * s, squash: 0.3, count: 24, life: [260, 340], colors });
      },
    });
  };
});
