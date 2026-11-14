/* ability.blazing-blast · 轰炎 —— 五行火系全敌爆破主动技能
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   敌方全体 / 吟唱 3000ms / 1.0×AP+40 / 冷却 24s / 灵力 10 / 五行·火
 * 分镜设计（全粒子流体红莲火球，零硬弧描边与直立光柱）：
 *   1. cinematicDim 压暗全场，施法者前推双掌喷薄柔羽化渐变炎风弧浪与火星气缕，敌方脚下展开焚天炎阵；
 *   2. 天空斜落 4 颗灼热红莲火球，沿途 4 段持续曳尾赤红 wisp 火焰舌并拖出滚滚暗棕焦烟 smoke；
 *   3. 命中瞬间炽焰四溅炸裂出夺目 flare 火心、卷曲飞散 ember 火星、焦土裂纹与全阵红莲热浪震屏。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  function resolveTargetDamage(ctx, idx, targetCount, fallback) {
    if (Array.isArray(ctx.damage)) return ctx.damage[idx] != null ? ctx.damage[idx] : fallback;
    if (ctx.perTargetDamage != null) return ctx.perTargetDamage;
    if (ctx.damage != null) {
      return targetCount > 1 ? Math.round(ctx.damage / targetCount) : ctx.damage;
    }
    return fallback;
  }

  function evalStreamerBezier(x1, y1, x2, y2, arcAmt, t) {
    var dx = x2 - x1;
    var dy = y2 - y1;
    var dist = Math.hypot(dx, dy) || 1;
    var nx = -dy / dist;
    var ny = dx / dist;
    var cx = (x1 + x2) * 0.5 + nx * arcAmt;
    var cy = (y1 + y2) * 0.5 + ny * arcAmt;
    var inv = 1 - t;
    return {
      x: inv * inv * x1 + 2 * inv * t * cx + t * t * x2,
      y: inv * inv * y1 + 2 * inv * t * cy + t * t * y2,
      tx: 2 * inv * (cx - x1) + 2 * t * (x2 - cx),
      ty: 2 * inv * (cy - y1) + 2 * t * (y2 - cy),
    };
  }

  function drawFeatheredFlameCrescent(c2d, reach, halfAngle, thickness, rgb, alpha) {
    if (alpha <= 0.01) return;
    var x0 = Math.cos(-halfAngle) * reach;
    var y0 = Math.sin(-halfAngle) * reach;
    var x1 = Math.cos(halfAngle) * reach;
    var y1 = Math.sin(halfAngle) * reach;
    var midR = reach * 1.18;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.26, 'rgba(' + rgb + ',' + (alpha * 0.65).toFixed(3) + ')');
    grad.addColorStop(0.5, 'rgba(254, 240, 138,' + Math.min(0.95, alpha * 1.1).toFixed(3) + ')');
    grad.addColorStop(0.74, 'rgba(' + rgb + ',' + (alpha * 0.65).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(x0, y0);
    c2d.quadraticCurveTo(midR + thickness, 0, x1, y1);
    c2d.quadraticCurveTo(midR - thickness * 0.85, 0, x0, y0);
    c2d.fill();
  }

  VFX.register('ability.blazing-blast', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.enemyEls && ctx.enemyEls.length) {
      targets = ctx.enemyEls.slice();
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';
    var group = targets.length ? targets : foeSide;

    var s = VFX.scale();
    var st = VFX.size();
    var axis = VFX.combatAxis(casterEl, group);
    var bounds = VFX.groupBounds(group);
    var casterPt = axis.start;
    var enemyCenter = VFX.clampStage({ x: bounds.cx, y: bounds.cy }, 36 * s, 28 * s);
    var enemyFoot = VFX.clampStage({ x: bounds.footX, y: bounds.footY }, 36 * s, 20);
    var isCrit = !!ctx.crit;

    // 阶段一：全景压暗与施法者前推双掌柔羽化炎风冲击（全向 2D 旋转）
    VFX.cinematicDim(0.28, 620, '#120604');
    VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 20, duration: 300 });

    var palmPt = VFX.clampStage(axis.palm(26 * s), 20 * s, 20 * s);

    // 柔羽化扇形炎风弧浪（c2d.rotate(axis.angle) 全向旋转，渐变曲线新月 + 掌前热火晕染）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 360,
      draw: function (c2d, p) {
        var ease = 1 - Math.pow(1 - p, 3);
        var fade = p < 0.24 ? p / 0.24 : 1 - (p - 0.24) / 0.76;
        var reach = (18 + ease * 84) * s;
        var palmR = 38 * s;
        c2d.save();
        c2d.translate(palmPt.x, palmPt.y);
        c2d.rotate(axis.angle);

        var palmG = c2d.createRadialGradient(8 * s, 0, 1, 8 * s, 0, palmR * (0.7 + 0.3 * fade));
        palmG.addColorStop(0, 'rgba(254, 240, 138,' + (0.65 * fade).toFixed(3) + ')');
        palmG.addColorStop(0.45, 'rgba(255, 100, 42,' + (0.35 * fade).toFixed(3) + ')');
        palmG.addColorStop(1, 'rgba(220, 38, 38, 0)');
        c2d.fillStyle = palmG;
        c2d.beginPath();
        c2d.arc(8 * s, 0, palmR, 0, TWO_PI);
        c2d.fill();

        drawFeatheredFlameCrescent(c2d, reach, 0.72, 14 * s * (1 - p * 0.45), '255, 100, 42', 0.78 * fade);
        drawFeatheredFlameCrescent(c2d, reach * 0.84, 0.56, 8 * s * (1 - p * 0.4), '253, 224, 71', 0.86 * fade);
        c2d.restore();
      },
    });

    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      count: 14,
      shape: 'wisp',
      colors: ['#dc2626', '#ff5722', '#ff8c42', '#fde047'],
      size: [4.5 * s, 8.6 * s],
      speed: [95 * s, 225 * s],
      baseAngle: axis.angle,
      spread: 0.95,
      turbulence: 145 * s,
      drag: 0.93,
      life: [220, 400],
    });

    VFX.burst({
      x: palmPt.x,
      y: palmPt.y,
      count: 24,
      shape: 'ember',
      colors: ['#ff5722', '#ff7b47', '#fde047', '#ffffff'],
      size: [2.4 * s, 4.8 * s],
      speed: [110 * s, 265 * s],
      baseAngle: axis.angle,
      spread: 1.05,
      turbulence: 185 * s,
      drag: 0.92,
      life: [240, 440],
      trail: 4,
    });

    // 敌方脚下展开焚天炎阵（内置原语自动乘以 VFX.scale()）
    VFX.magicCircle({
      x: enemyFoot.x,
      y: enemyFoot.y,
      radius: 118,
      tilt: 0.44,
      layer: 'ground',
      style: 'flame',
      color: '#ff7b47',
      secondaryColor: '#fde047',
      rotationSpeed: 1.45,
      lineWidth: 1.9,
      alpha: 0.78,
      duration: 960,
    });

    // 阶段二：4 颗灼热红莲火球自画布可见高空曳尾砸向真实目标
    var meteorCount = 4;
    var realTargetCount = Math.max(1, targets.length);
    for (var i = 0; i < meteorCount; i++) {
      (function (idx) {
        var tEl = targets[idx % realTargetCount] || null;
        var isExtraMeteor = idx >= targets.length;
        var dstPt;
        var dstFoot;
        if (tEl && !isExtraMeteor) {
          dstPt = VFX.center(tEl);
          dstFoot = VFX.foot(tEl);
        } else if (targets.length > 0) {
          dstPt = VFX.clampStage({
            x: enemyCenter.x + VFX.rand(-28, 28) * s,
            y: enemyCenter.y + VFX.rand(-18, 18) * s,
          }, 28, 28);
          dstFoot = { x: dstPt.x, y: enemyFoot.y };
        } else {
          dstPt = {
            x: VFX.clamp(enemyCenter.x + (idx - 1.5) * 44 * s, 32, st.w - 32),
            y: VFX.clamp(enemyCenter.y + ((idx % 2) * 26 - 13) * s, 32, st.h - 24),
          };
          dstFoot = { x: dstPt.x, y: Math.min(st.h - 16, dstPt.y + 34 * s) };
        }

        var skyPt = VFX.clampStage({
          x: VFX.lerp(casterPt.x, dstPt.x, 0.28) + (idx - 1.5) * 10 * s,
          y: Math.min(casterPt.y, dstPt.y) - 115 * s + (idx % 2) * 8 * s,
        }, 28, 22);
        var skyX = skyPt.x;
        var skyY = skyPt.y;
        var launchDelay = 80 + idx * 75;
        var flightDur = 310;
        var arcAmt = (skyX <= dstPt.x ? -1 : 1) * 22 * s;

        // 沿火球坠落曲线 4 段拖出厚重焦烟 smoke 与红莲火焰舌 wisp
        var trailFracs = [0.22, 0.45, 0.68, 0.88];
        for (var tf = 0; tf < trailFracs.length; tf++) {
          (function (u) {
            var sampleAt = launchDelay + Math.round(flightDur * 0.78 * u);
            var pt = evalStreamerBezier(skyX, skyY, dstPt.x, dstPt.y, arcAmt, u);
            var tl = Math.hypot(pt.tx, pt.ty) || 1;
            var ux = pt.tx / tl;
            var uy = pt.ty / tl;

            VFX.after(sampleAt, function () {
              // 灼热红莲火舌 wisp 反向卷曳
              VFX.burst({
                x: pt.x,
                y: pt.y,
                radius: 8 * s,
                count: 3,
                shape: 'wisp',
                colors: ['#dc2626', '#ff5722', '#fb923c', '#fde047'],
                size: [5.5 * s, 10.2 * s],
                endSize: [2.5 * s, 4.8 * s],
                speed: [10 * s, 32 * s],
                vx: -ux * 88 * s,
                vy: -uy * 88 * s - 24 * s,
                turbulence: 145 * s,
                drag: 0.94,
                alpha: 0.76,
                life: [280, 480],
              });

              // 长长拖出的暗褐黑焦烟 smoke
              VFX.burst({
                x: pt.x - ux * 10 * s,
                y: pt.y - uy * 10 * s,
                radius: 10 * s,
                count: 2,
                shape: 'smoke',
                colors: ['#1f0b07', '#3b150c', '#521b0c'],
                size: [14 * s, 24 * s],
                speed: [10 * s, 28 * s],
                vx: -ux * 36 * s,
                vy: -uy * 36 * s - 18 * s,
                gravity: -38 * s,
                turbulence: 85 * s,
                drag: 0.96,
                alpha: [0.34, 0.54],
                life: [520, 880],
              });
            });
          })(trailFracs[tf]);
        }

        VFX.streamer({
          delay: launchDelay,
          x1: skyX,
          y1: skyY,
          x2: dstPt.x,
          y2: dstPt.y,
          arc: arcAmt,
          width: 15 * s,
          tailLength: 0.5,
          duration: flightDur,
          color: '#dc2626',
          coreColor: '#fef9c3',
          sparkColors: ['#ff5722', '#fde047', '#ffffff'],
          onArrive: function (hitPt) {
            var safeHit = VFX.clampStage(hitPt, 30 * s, 26 * s);
            var safeDstFoot = VFX.clampStage(dstFoot, 30 * s, 18);

            // 命中瞬间纯径向灼焰光晕闪爆
            VFX.custom({
              layer: 'sky',
              blend: 'lighter',
              duration: 320,
              draw: function (c2d, p) {
                var fade = 1 - Math.pow(p, 1.45);
                var r = 64 * s * (0.55 + 0.45 * p);
                c2d.save();
                c2d.translate(safeHit.x, safeHit.y);
                var g = c2d.createRadialGradient(0, 0, 1, 0, 0, r);
                g.addColorStop(0, 'rgba(255, 255, 255,' + (0.82 * fade).toFixed(3) + ')');
                g.addColorStop(0.28, 'rgba(254, 240, 138,' + (0.56 * fade).toFixed(3) + ')');
                g.addColorStop(0.62, 'rgba(255, 87, 34,' + (0.28 * fade).toFixed(3) + ')');
                g.addColorStop(1, 'rgba(220, 38, 38, 0)');
                c2d.fillStyle = g;
                c2d.beginPath();
                c2d.arc(0, 0, r, 0, TWO_PI);
                c2d.fill();
                c2d.restore();
              },
            });

            VFX.shockwave({
              x: safeHit.x,
              y: safeHit.y,
              fromRadius: 10,
              toRadius: 82,
              tilt: 0.85,
              layer: 'sky',
              color: '#ff5722',
              coreColor: '#fef08a',
              lineWidth: 4.0,
              duration: 340,
            });

            VFX.groundCrack({
              x: safeDstFoot.x,
              y: safeDstFoot.y,
              radius: 56,
              branches: 5,
              color: '#ff7b47',
              crackColor: '#120805',
              lineWidth: 2.6,
              duration: 1200,
            });

            // 炽焰四溅炸裂出的夺目 flare 火心
            VFX.burst({
              x: safeHit.x,
              y: safeHit.y,
              radius: 12 * s,
              count: 10,
              shape: 'flare',
              colors: ['#ffffff', '#fef08a', '#ff7b47', '#dc2626'],
              size: [3.2 * s, 6.8 * s],
              speed: [75 * s, 210 * s],
              drag: 0.91,
              life: [220, 420],
            });

            // 卷曲飞散的红莲火焰舌 wisp 与火星 ember
            VFX.burst({
              x: safeHit.x,
              y: safeHit.y,
              radius: 16 * s,
              count: 12,
              shape: 'wisp',
              colors: ['#dc2626', '#ff5722', '#fb923c', '#fde047'],
              size: [5.5 * s, 10.5 * s],
              speed: [65 * s, 175 * s],
              vortex: 260 * s,
              vortexCenter: safeHit,
              turbulence: 175 * s,
              drag: 0.93,
              life: [280, 520],
            });

            VFX.burst({
              x: safeHit.x,
              y: safeHit.y,
              count: 20,
              shape: 'ember',
              colors: ['#ffffff', '#fde047', '#ff8c42', '#ff5722'],
              size: [2.2 * s, 4.8 * s],
              speed: [85 * s, 235 * s],
              gravity: -40 * s,
              turbulence: 225 * s,
              drag: 0.93,
              life: [280, 560],
              trail: 4,
            });

            if (!isExtraMeteor) {
              var dmg = Math.round(resolveTargetDamage(ctx, idx, realTargetCount, 95));
              if (tEl) {
                VFX.impact(tEl, { color: '#ff7b47', duration: 240, distance: 14 });
              }
              VFX.floatText(tEl || safeHit, '-' + dmg, isCrit ? 'crit' : 'damage');
            }
          },
        });
      })(i);
    }

    // 阶段三：全阵红莲聚爆高潮（焦土、红金热浪、滚滚焦烟、涡旋火舌、火心与卷曲升腾火星）
    VFX.after(420, function () {
      VFX.shockwave({
        x: enemyCenter.x,
        y: enemyCenter.y + 10 * s,
        fromRadius: 18,
        toRadius: 158,
        tilt: 0.58,
        layer: 'sky',
        color: '#ff5722',
        coreColor: '#fde047',
        lineWidth: 5.2,
        duration: 480,
      });

      VFX.groundCrack({
        x: enemyFoot.x,
        y: enemyFoot.y,
        radius: 112,
        branches: 9,
        color: '#ff6b2b',
        crackColor: '#120805',
        lineWidth: 3.8,
        duration: 1550,
      });

      VFX.burst({
        x: enemyCenter.x,
        y: enemyCenter.y + 8 * s,
        radius: 72 * s,
        tiltY: 0.55,
        count: 22,
        shape: 'smoke',
        colors: ['#1f0b07', '#3f1a10', '#5c2614'],
        size: [18 * s, 34 * s],
        speed: [22 * s, 68 * s],
        gravity: -48 * s,
        drag: 0.96,
        alpha: [0.42, 0.64],
        life: [620, 1080],
      });

      VFX.burst({
        x: enemyCenter.x,
        y: enemyCenter.y + 6 * s,
        radius: 64 * s,
        tiltY: 0.52,
        count: 22,
        shape: 'wisp',
        colors: ['#dc2626', '#ff5722', '#fb923c', '#fde047'],
        size: [6.5 * s, 12.5 * s],
        speed: [55 * s, 145 * s],
        vortex: 260 * s,
        vortexCenter: enemyCenter,
        gravity: -75 * s,
        turbulence: 190 * s,
        drag: 0.95,
        life: [420, 760],
      });

      VFX.burst({
        x: enemyCenter.x,
        y: enemyCenter.y,
        radius: 48 * s,
        count: 14,
        shape: 'flare',
        colors: ['#ffffff', '#fef08a', '#ff7b47'],
        size: [3.2 * s, 6.2 * s],
        speed: [55 * s, 165 * s],
        drag: 0.91,
        life: [260, 480],
      });

      VFX.burst({
        x: enemyFoot.x,
        y: enemyFoot.y,
        radius: 76 * s,
        tiltY: 0.45,
        count: 42,
        shape: 'ember',
        colors: ['#ff5722', '#ff8c42', '#fde047', '#ffffff'],
        size: [2.8 * s, 6.0 * s],
        speed: [48 * s, 155 * s],
        angle: [-Math.PI * 0.85, -Math.PI * 0.15],
        gravity: -95 * s,
        turbulence: 215 * s,
        drag: 0.955,
        life: [520, 960],
        trail: 4,
      });

      VFX.quake('md', 280);
    });

    return 1450;
  });
});
