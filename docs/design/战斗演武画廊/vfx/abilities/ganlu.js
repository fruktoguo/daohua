/* ability.ganlu · 甘露 —— 玄水持续回春治疗主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   气血最低友方 / 吟唱 1500ms / 甘露 Buff 10s（每秒回血） / 冷却 20s / 灵力 20 / 五行·水
 * 分镜设计（通透灵动甘霖，零硬边圆环与直筒柱）：
 *   1. 施法者轻扬灵露，清透水珠、水雾与弧形灵流优雅越空飞向目标上空；
 *   2. 目标头顶凝出纯径向渐变柔雾雨幕与轻盈飘云（smoke lighter + wisp），随之分 4 波洒落晶莹 drop 雨丝；
 *   3. 雨丝落于目标脚畔溅起圈圈高斯羽化水波纹与水珠飞沫，同时卷起螺旋碧水 wisp 灵雾与回春翠点 orb/flare 护佑回血。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  VFX.register('ability.ganlu', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && casterEl) {
      targets = [casterEl];
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var allySide = isEnemyCaster ? 'enemy' : 'ally';
    var targetEl = targets[0] || null;
    var s = VFX.scale();
    var st = VFX.size();

    var casterPt = casterEl ? VFX.center(casterEl) : VFX.sideCenter(allySide);
    var targetPt = targetEl ? VFX.center(targetEl) : casterPt;
    var targetHead = targetEl ? VFX.head(targetEl) : { x: targetPt.x, y: targetPt.y - 38 * s };
    var targetFoot = targetEl ? VFX.foot(targetEl) : { x: targetPt.x, y: targetPt.y + 38 * s };
    var axis = VFX.combatAxis(casterEl, targetEl || targetPt);

    var topPadX = Math.min(38 * s, st.w * 0.42);
    var maxTopY = Math.max(28, targetPt.y - 28);
    var topPt = {
      x: VFX.clamp(targetPt.x, topPadX, Math.max(topPadX, st.w - topPadX)),
      y: VFX.clamp(targetHead.y - 54 * s, 24, maxTopY),
    };
    var safeFoot = VFX.clampStage(targetFoot, 34 * s, 20);
    var safeTargetPt = VFX.clampStage(targetPt, 30 * s, 24 * s);

    var isSelf = Math.hypot(targetPt.x - casterPt.x, targetPt.y - casterPt.y) < 28 * s;
    var startPt = VFX.clampStage(
      isSelf ? { x: casterPt.x, y: casterPt.y - 12 * s } : axis.palm(18 * s),
      20 * s,
      18 * s
    );

    // 阶段一：轻扬甘霖，灵珠与水雾随弧线升向目标上空
    VFX.motion(casterEl, 'cast', { duration: 400 });

    VFX.burst({
      x: startPt.x,
      y: startPt.y,
      radius: 16 * s,
      count: 10,
      shape: 'drop',
      colors: ['#38bdf8', '#7dd3fc', '#e0f2fe', '#ffffff'],
      size: [2.0 * s, 3.6 * s],
      speed: [26 * s, 68 * s],
      angle: [-Math.PI * 0.78, -Math.PI * 0.22],
      gravity: 60 * s,
      life: [280, 480],
    });

    VFX.burst({
      x: startPt.x,
      y: startPt.y,
      radius: 18 * s,
      count: 6,
      shape: 'wisp',
      colors: ['#7dd3fc', '#bae6fd', '#5eead4'],
      size: [3.6 * s, 6.4 * s],
      speed: [18 * s, 45 * s],
      gravity: -35 * s,
      alpha: 0.55,
      life: [320, 540],
    });

    VFX.streamer({
      x1: startPt.x,
      y1: startPt.y,
      x2: topPt.x,
      y2: topPt.y,
      arc: (isSelf ? -46 : -58) * s,
      wobble: 7 * s,
      width: 8 * s,
      tailLength: 0.42,
      duration: 390,
      color: '#38bdf8',
      coreColor: '#e0f2fe',
      sparkColors: ['#7dd3fc', '#bae6fd', '#ffffff'],
      onArrive: function () {
        // 阶段二：目标上空凝成纯柔雾雨幕（无硬边圆环），洒落晶莹雨丝
        var rainHeight = Math.max(76 * s, safeFoot.y - topPt.y);

        VFX.custom({
          layer: 'sky',
          blend: 'lighter',
          duration: 960,
          draw: function (c2d, p) {
            var open = p < 0.22 ? 1 - Math.pow(1 - p / 0.22, 3) : 1;
            var fade = p < 0.18 ? p / 0.18 : p > 0.72 ? Math.max(0, (1 - p) / 0.28) : 1;
            var cloudR = 64 * s * (0.65 + 0.35 * open);

            c2d.save();
            c2d.translate(topPt.x, topPt.y);

            // 上空灵露云霭（纯径向渐变）
            c2d.save();
            c2d.scale(1.45, 0.44);
            var cloudG = c2d.createRadialGradient(0, 0, 2, 0, 0, cloudR);
            cloudG.addColorStop(0, 'rgba(224, 242, 254,' + (0.44 * fade).toFixed(3) + ')');
            cloudG.addColorStop(0.42, 'rgba(125, 211, 252,' + (0.28 * fade).toFixed(3) + ')');
            cloudG.addColorStop(0.76, 'rgba(56, 189, 248,' + (0.1 * fade).toFixed(3) + ')');
            cloudG.addColorStop(1, 'rgba(56, 189, 248, 0)');
            c2d.fillStyle = cloudG;
            c2d.beginPath();
            c2d.arc(0, 0, cloudR, 0, TWO_PI);
            c2d.fill();
            c2d.restore();

            // 垂直轻柔雨幕薄纱（横向纵向皆自然归零）
            c2d.save();
            c2d.translate(0, rainHeight * 0.46);
            c2d.scale(0.68, 1.25);
            var veilR = rainHeight * 0.56;
            var veilG = c2d.createRadialGradient(0, 0, 2, 0, 0, veilR);
            veilG.addColorStop(0, 'rgba(125, 211, 252,' + (0.16 * fade).toFixed(3) + ')');
            veilG.addColorStop(0.55, 'rgba(94, 234, 212,' + (0.08 * fade).toFixed(3) + ')');
            veilG.addColorStop(1, 'rgba(56, 189, 248, 0)');
            c2d.fillStyle = veilG;
            c2d.beginPath();
            c2d.arc(0, 0, veilR, 0, TWO_PI);
            c2d.fill();
            c2d.restore();

            c2d.restore();
          },
        });

        // 头顶轻盈飘雾雨云
        VFX.burst({
          x: topPt.x,
          y: topPt.y,
          radius: 42 * s,
          tiltY: 0.34,
          count: 10,
          shape: 'smoke',
          blend: 'lighter',
          colors: ['#e0f2fe', '#bae6fd', '#7dd3fc'],
          size: [14 * s, 24 * s],
          speed: [10 * s, 28 * s],
          alpha: [0.16, 0.28],
          drag: 0.96,
          life: [540, 860],
        });

        VFX.burst({
          x: topPt.x,
          y: topPt.y,
          radius: 38 * s,
          tiltY: 0.36,
          count: 10,
          shape: 'wisp',
          colors: ['#7dd3fc', '#e0f2fe', '#5eead4'],
          size: [4.2 * s, 7.6 * s],
          speed: [14 * s, 40 * s],
          vortex: 140 * s,
          vortexCenter: topPt,
          alpha: 0.55,
          life: [420, 720],
        });

        // 4 波晶莹 drop 雨丝与垂落雨雾自云幕飘洒而下
        for (var r = 0; r < 4; r++) {
          (function (waveIdx) {
            var waveAt = waveIdx * 110;
            VFX.burst({
              delay: waveAt,
              x: topPt.x,
              y: topPt.y + 4 * s,
              radius: 36 * s,
              tiltY: 0.28,
              count: 14,
              shape: 'drop',
              colors: ['#38bdf8', '#7dd3fc', '#e0f2fe', '#ffffff'],
              size: [2.2 * s, 3.8 * s],
              speed: [92 * s, 158 * s],
              angle: [Math.PI * 0.45, Math.PI * 0.55],
              gravity: 175 * s,
              drag: 0.985,
              life: [380, 620],
              trail: 4,
            });

            VFX.burst({
              delay: waveAt + 20,
              x: topPt.x,
              y: topPt.y + 8 * s,
              radius: 30 * s,
              tiltY: 0.25,
              count: 4,
              shape: 'wisp',
              colors: ['#bae6fd', '#7dd3fc'],
              size: [3.0 * s, 5.5 * s],
              speed: [75 * s, 130 * s],
              angle: [Math.PI * 0.46, Math.PI * 0.54],
              gravity: 110 * s,
              alpha: 0.42,
              life: [340, 540],
            });
          })(r);
        }

        // 阶段三：地面水泽润光、圈圈羽化水波与螺旋升腾回春翠点
        VFX.custom({
          delay: 140,
          layer: 'ground',
          blend: 'lighter',
          duration: 1120,
          draw: function (c2d, p) {
            var fade = p < 0.2 ? p / 0.2 : p > 0.72 ? Math.max(0, (1 - p) / 0.28) : 1;
            var r = 68 * s * (0.7 + 0.3 * Math.min(1, p * 3));
            c2d.save();
            c2d.translate(safeFoot.x, safeFoot.y);
            c2d.scale(1, 0.44);
            var pool = c2d.createRadialGradient(0, 0, 2, 0, 0, r);
            pool.addColorStop(0, 'rgba(94, 234, 212,' + (0.36 * fade).toFixed(3) + ')');
            pool.addColorStop(0.42, 'rgba(56, 189, 248,' + (0.24 * fade).toFixed(3) + ')');
            pool.addColorStop(0.78, 'rgba(125, 211, 252,' + (0.08 * fade).toFixed(3) + ')');
            pool.addColorStop(1, 'rgba(56, 189, 248, 0)');
            c2d.fillStyle = pool;
            c2d.beginPath();
            c2d.arc(0, 0, r, 0, TWO_PI);
            c2d.fill();
            c2d.restore();
          },
        });

        // 4 圈交错扩散的羽化水波与雨滴溅珠（内置 shockwave 自动乘以 VFX.scale()）
        var rippleTimes = [180, 330, 490, 660];
        for (var k = 0; k < rippleTimes.length; k++) {
          (function (at, idx) {
            VFX.shockwave({
              delay: at,
              x: safeFoot.x + VFX.rand(-10, 10) * s,
              y: safeFoot.y + VFX.rand(-4, 4) * s,
              fromRadius: 10,
              toRadius: 56 + idx * 9,
              tilt: 0.44,
              layer: 'ground',
              color: idx % 2 === 0 ? '#38bdf8' : '#5eead4',
              coreColor: '#e0f2fe',
              lineWidth: 2.4,
              double: false,
              duration: 500,
            });

            if (idx < 3) {
              VFX.burst({
                delay: at + 20,
                x: safeFoot.x,
                y: safeFoot.y,
                radius: 26 * s,
                tiltY: 0.42,
                count: 6,
                shape: 'drop',
                colors: ['#7dd3fc', '#e0f2fe', '#ffffff'],
                size: [1.8 * s, 3.0 * s],
                speed: [35 * s, 78 * s],
                angle: [-Math.PI * 0.85, -Math.PI * 0.15],
                gravity: 190 * s,
                life: [220, 380],
              });
            }
          })(rippleTimes[k], k);
        }

        // 5 段螺旋升腾的碧水灵雾 wisp 与回春翠点 orb/flare（替代直筒光柱）
        var riseSteps = 5;
        for (var rs = 0; rs < riseSteps; rs++) {
          (function (step) {
            VFX.after(160 + step * 95, function () {
              var u = step / (riseSteps - 1);
              var ang = step * 1.45;
              var rr = VFX.lerp(22, 36, u) * s;
              for (var arm = 0; arm < 2; arm++) {
                var a = ang + arm * Math.PI;
                var sx = safeFoot.x + Math.cos(a) * rr;
                var sy = safeFoot.y - (8 + u * 36) * s + Math.sin(a) * rr * 0.36;
                var vx = -Math.sin(a) * 68 * s;
                var vy = Math.cos(a) * 24 * s - 64 * s;

                VFX.burst({
                  x: sx,
                  y: sy,
                  radius: 10 * s,
                  count: 2,
                  shape: 'wisp',
                  colors: ['#38bdf8', '#7dd3fc', '#5eead4', '#6ee7b7'],
                  size: [4.2 * s, 8.2 * s],
                  endSize: [2.2 * s, 4.2 * s],
                  speed: [6 * s, 16 * s],
                  vx: vx,
                  vy: vy,
                  gravity: -54 * s,
                  turbulence: 58 * s,
                  drag: 0.96,
                  alpha: [0.46, 0.7],
                  life: [500, 820],
                });
              }

              // 回春翠点 orb 与露珠 flare
              VFX.burst({
                x: safeFoot.x,
                y: safeFoot.y - (10 + u * 24) * s,
                radius: 30 * s,
                tiltY: 0.44,
                count: 4,
                shape: 'orb',
                colors: ['#6ee7b7', '#5eead4', '#7dd3fc', '#ffffff'],
                size: [1.8 * s, 3.8 * s],
                speed: [18 * s, 52 * s],
                angle: [-Math.PI * 0.78, -Math.PI * 0.22],
                gravity: -64 * s,
                drag: 0.96,
                life: [520, 880],
                trail: 4,
              });

              VFX.burst({
                x: safeTargetPt.x,
                y: safeTargetPt.y + (8 - u * 18) * s,
                radius: 26 * s,
                count: 3,
                shape: 'flare',
                colors: ['#ffffff', '#a7f3d0', '#bae6fd'],
                size: [2.0 * s, 4.0 * s],
                speed: [16 * s, 48 * s],
                gravity: -54 * s,
                drag: 0.94,
                life: [340, 580],
              });
            });
          })(rs);
        }

        if (targetEl) {
          VFX.hitFlash(targetEl, '#38bdf8', 240);
        }
        var label = ctx.heal != null && ctx.heal > 0 ? '+' + Math.round(ctx.heal) : '甘露 · 回春';
        VFX.floatText(targetEl || safeTargetPt, label, 'heal');
      },
    });

    return 1600;
  });
});
