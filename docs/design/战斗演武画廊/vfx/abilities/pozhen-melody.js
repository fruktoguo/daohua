/* ability.pozhen-melody · 破阵子 —— 二阶全军终极增益大阵
 * 真源镜像：config/game-data/abilities/spell_abilities.json
 *   我方全体 / 吟唱 5000ms / 破阵子 10s（增伤+40） / 冷却 60s / 灵力 50 / 符箓
 * 分镜设计（全粒子流体弦音震阵，零硬边直筒柱与生硬音圈）：
 *   1. cinematicDim 铺陈五息壮歌仪式感，我方半场足心展开柔光鎏金八卦阵与纯径向绯金日轮光晕；
 *   2. 柔羽化弦音波动弧（两端渐隐为 0 的渐变曲线丝带）在空中震颤拨响，沿五声音阶（宫商角徵羽）频率
 *      分 5 波向外奔涌绯金琴音 wisp 涟漪、flare 弦星与 rune 道纹；
 *   3. 战意弦波掠向敌阵方向碎裂出细密 rune/spark 余韵震荡；同时向每位友方灌注绯金流光，脚下绽开共鸣气缕与增伤浮字。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;

  function drawFeatheredStringArc(c2d, x0, y0, cx, cy, x1, y1, thickness, rgb, alpha) {
    if (alpha <= 0.01) return;
    var dx = x1 - x0;
    var dy = y1 - y0;
    var len = Math.hypot(dx, dy) || 1;
    var nx = -dy / len;
    var ny = dx / len;
    var grad = c2d.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, 'rgba(' + rgb + ',0)');
    grad.addColorStop(0.24, 'rgba(' + rgb + ',' + (alpha * 0.62).toFixed(3) + ')');
    grad.addColorStop(0.5, 'rgba(255, 251, 235,' + Math.min(0.95, alpha * 1.15).toFixed(3) + ')');
    grad.addColorStop(0.76, 'rgba(' + rgb + ',' + (alpha * 0.62).toFixed(3) + ')');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    c2d.fillStyle = grad;
    c2d.beginPath();
    c2d.moveTo(x0, y0);
    c2d.quadraticCurveTo(cx + nx * thickness, cy + ny * thickness, x1, y1);
    c2d.quadraticCurveTo(cx - nx * thickness, cy - ny * thickness, x0, y0);
    c2d.fill();
  }

  VFX.register('ability.pozhen-melody', function (ctx) {
    ctx = ctx || {};
    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length) ? ctx.targetEls : (ctx.targetEl ? [ctx.targetEl] : []);
    if (!targets.length && ctx.allyEls && ctx.allyEls.length) {
      targets = ctx.allyEls.slice();
    }
    if (!targets.length && casterEl) {
      targets = [casterEl];
    }

    var isEnemyCaster = !!(casterEl && casterEl.classList && casterEl.classList.contains('is-enemy'));
    var allySide = isEnemyCaster ? 'enemy' : 'ally';
    var foeSide = isEnemyCaster ? 'ally' : 'enemy';

    var s = VFX.scale();
    var allyBounds = VFX.groupBounds(targets.length ? targets : allySide);
    var enemyBounds = VFX.groupBounds(foeSide);
    var casterPt = casterEl ? VFX.center(casterEl) : { x: allyBounds.cx, y: allyBounds.cy };
    var casterFoot = casterEl ? VFX.foot(casterEl) : { x: allyBounds.footX, y: allyBounds.footY };

    var sunPt = VFX.clampStage(casterPt, 58 * s, 44 * s);
    var footAnchor = VFX.clampStage({ x: sunPt.x, y: casterFoot.y }, 58 * s, 28 * s);

    var abx = enemyBounds.cx - allyBounds.cx;
    var aby = enemyBounds.cy - allyBounds.cy;
    var abLen = Math.hypot(abx, aby) || 1;
    var abUx = abx / abLen;
    var abUy = aby / abLen;

    // 阶段一：五息壮歌暗幕与施法者足底柔光鎏金八卦大阵（内置原语自动乘以 VFX.scale()）
    VFX.cinematicDim(0.28, 780, '#0a0702');
    VFX.motion(casterEl, 'levitate', { duration: 520 });

    VFX.magicCircle({
      x: footAnchor.x,
      y: footAnchor.y,
      radius: 136,
      tilt: 0.42,
      layer: 'ground',
      style: 'bagua',
      color: '#f5c451',
      secondaryColor: '#fffbeb',
      rotationSpeed: 0.85,
      lineWidth: 1.9,
      alpha: 0.78,
      duration: 1500,
    });

    // 柔羽化弦音波动弧与中央绯金温润日轮辉光（锚定于 sunPt，所有 custom 半径按 s 缩放）
    VFX.custom({
      layer: 'sky',
      blend: 'lighter',
      duration: 1080,
      draw: function (c2d, p) {
        var open = p < 0.18 ? 1 - Math.pow(1 - p / 0.18, 3) : 1;
        var fade = p < 0.14 ? p / 0.14 : p > 0.72 ? Math.max(0, (1 - p) / 0.28) : 1;

        c2d.save();
        c2d.translate(sunPt.x, sunPt.y);

        // 中央绯金共鸣光晕（纯径向渐变边缘透明归零）
        var auraR = 128 * s * (0.65 + 0.35 * open);
        c2d.save();
        c2d.scale(1.15, 0.85);
        var coreG = c2d.createRadialGradient(0, 0, 2, 0, 0, auraR);
        coreG.addColorStop(0, 'rgba(255, 251, 235,' + (0.44 * fade).toFixed(3) + ')');
        coreG.addColorStop(0.32, 'rgba(245, 196, 81,' + (0.28 * fade).toFixed(3) + ')');
        coreG.addColorStop(0.65, 'rgba(251, 113, 133,' + (0.14 * fade).toFixed(3) + ')');
        coreG.addColorStop(1, 'rgba(245, 158, 11, 0)');
        c2d.fillStyle = coreG;
        c2d.beginPath();
        c2d.arc(0, 0, auraR, 0, TWO_PI);
        c2d.fill();
        c2d.restore();

        // 5 道按五声音阶频率震颤升腾的柔羽化弦音波动弧
        for (var si = 0; si < 5; si++) {
          var waveP = ((p * 1.65) + si * 0.2) % 1;
          var wEnv = Math.sin(waveP * Math.PI);
          if (wEnv <= 0.05) continue;
          var span = (96 + si * 18 + waveP * 44) * s;
          var yBase = (24 - waveP * 110 - si * 8) * s;
          var vib = Math.sin(p * (26 + si * 6) + si * 1.4) * (16 - waveP * 8) * s;
          var thick = (4.2 - waveP * 2.1) * (0.7 + 0.3 * wEnv) * s;
          var rgb = si % 2 === 0 ? '245, 196, 81' : '251, 113, 133';
          drawFeatheredStringArc(
            c2d,
            -span,
            yBase + 14 * s,
            0,
            yBase - 24 * s + vib,
            span,
            yBase + 14 * s,
            thick,
            rgb,
            0.64 * fade * wEnv
          );
        }

        c2d.restore();
      },
    });

    // 阶段二：沿五声音阶（宫商角徵羽）频率向外奔涌的绯金琴音 wisp 涟漪
    var pentatonicNotes = [
      { delay: 70, radius: 74, spd: 95, count: 14, cols: ['#f5c451', '#fef08a', '#fb7185'] },
      { delay: 165, radius: 92, spd: 118, count: 16, cols: ['#fb7185', '#f5c451', '#fffbeb'] },
      { delay: 260, radius: 110, spd: 142, count: 18, cols: ['#fde047', '#f59e0b', '#fb7185'] },
      { delay: 355, radius: 126, spd: 165, count: 18, cols: ['#fffbeb', '#f5c451', '#f43f5e'] },
      { delay: 450, radius: 144, spd: 192, count: 20, cols: ['#fef08a', '#fb7185', '#ffffff'] },
    ];

    for (var n = 0; n < pentatonicNotes.length; n++) {
      (function (note, idx) {
        VFX.after(note.delay, function () {
          // 高斯柔光环涟漪
          VFX.shockwave({
            x: footAnchor.x,
            y: footAnchor.y,
            fromRadius: 20 + idx * 8,
            toRadius: note.radius + 46,
            tilt: 0.42,
            layer: 'ground',
            color: idx % 2 === 0 ? '#f5c451' : '#fb7185',
            coreColor: '#fffbeb',
            lineWidth: 3.2,
            double: false,
            duration: 460,
          });

          // 奔涌的绯金琴音 wisp 涟漪圈
          VFX.burst({
            x: footAnchor.x,
            y: footAnchor.y - 10 * s,
            radius: note.radius * 0.48 * s,
            ring: true,
            tiltY: 0.42,
            count: note.count,
            shape: 'wisp',
            colors: note.cols,
            size: [5.0 * s, 9.8 * s],
            speed: [note.spd * 0.72 * s, note.spd * 1.15 * s],
            vortex: (idx % 2 === 0 ? 1 : -1) * 145 * s,
            vortexCenter: footAnchor,
            gravity: -48 * s,
            turbulence: 85 * s,
            drag: 0.95,
            alpha: 0.72,
            life: [460, 780],
          });

          // 弦音微星 flare 与腾空战歌道纹 rune
          VFX.burst({
            x: footAnchor.x,
            y: footAnchor.y - 16 * s,
            radius: note.radius * 0.65 * s,
            tiltY: 0.42,
            count: 6,
            shape: 'flare',
            colors: ['#ffffff', '#fef08a', '#fb7185'],
            size: [2.4 * s, 4.6 * s],
            speed: [35 * s, 85 * s],
            gravity: -62 * s,
            drag: 0.94,
            life: [320, 560],
          });

          if (idx % 2 === 0) {
            VFX.burst({
              x: footAnchor.x,
              y: footAnchor.y - 8 * s,
              radius: 86 * s,
              tiltY: 0.42,
              count: 8,
              shape: 'rune',
              colors: ['#fde047', '#fffbeb', '#fb7185'],
              size: [4.6 * s, 7.2 * s],
              speed: [26 * s, 68 * s],
              angle: [-Math.PI * 0.68, -Math.PI * 0.32],
              gravity: -65 * s,
              drag: 0.96,
              life: [580, 940],
            });
          }
        });
      })(pentatonicNotes[n], n);
    }

    // 破阵弦波沿 allyBounds -> enemyBounds 2D 向量直扑敌阵重心，碎裂出细密 rune/spark 震荡余韵
    VFX.after(340, function () {
      var waveOrigin = VFX.clampStage({ x: allyBounds.cx, y: allyBounds.cy }, 32 * s, 28 * s);
      var shatterCenter = VFX.clampStage({
        x: enemyBounds.cx - abUx * 18 * s,
        y: enemyBounds.cy - abUy * 18 * s,
      }, 36 * s, 28 * s);
      VFX.streamer({
        x1: waveOrigin.x,
        y1: waveOrigin.y,
        x2: shatterCenter.x,
        y2: shatterCenter.y,
        arc: -44 * s,
        wobble: 10 * s,
        width: 8 * s,
        tailLength: 0.45,
        duration: 320,
        color: '#fb7185',
        coreColor: '#fef08a',
        sparkColors: ['#f5c451', '#fb7185', '#ffffff'],
        onArrive: function (hitPt) {
          var safeHit = VFX.clampStage(hitPt, 34 * s, 26 * s);
          VFX.shockwave({
            x: safeHit.x,
            y: safeHit.y + 12 * s,
            fromRadius: 14,
            toRadius: 118,
            tilt: 0.44,
            layer: 'ground',
            color: '#fb7185',
            coreColor: '#fef08a',
            lineWidth: 2.8,
            double: false,
            duration: 420,
          });
          VFX.burst({
            x: safeHit.x,
            y: safeHit.y,
            radius: 68 * s,
            tiltY: 0.45,
            count: 14,
            shape: 'rune',
            colors: ['#fb7185', '#fde047', '#f59e0b'],
            size: [4.0 * s, 6.6 * s],
            speed: [48 * s, 125 * s],
            turbulence: 130 * s,
            drag: 0.93,
            life: [420, 740],
          });
          VFX.burst({
            x: safeHit.x,
            y: safeHit.y,
            radius: 54 * s,
            tiltY: 0.45,
            count: 22,
            shape: 'spark',
            colors: ['#fef08a', '#fb7185', '#ffffff'],
            size: [1.8 * s, 3.6 * s],
            speed: [75 * s, 195 * s],
            drag: 0.92,
            life: [280, 520],
            trail: 4,
          });
          VFX.quake('sm', 180);
        },
      });
    });

    // 阶段三：从 casterPt 向每位真实存活友方精准灌注绯金破阵战意流光
    var beamOrigin = VFX.clampStage(casterPt, 24 * s, 24 * s);

    if (!targets.length) {
      VFX.floatText(sunPt, '破阵 · 增伤+40', 'status', { scale: 1.1 });
      return 1600;
    }

    for (var i = 0; i < targets.length; i++) {
      (function (tEl, idx) {
        var tCenter = VFX.center(tEl);
        var tFoot = VFX.clampStage(VFX.foot(tEl), 34 * s, 22);
        var safeTCenter = VFX.clampStage(tCenter, 26 * s, 24 * s);
        var isSelfTarget = Math.hypot(tCenter.x - beamOrigin.x, tCenter.y - beamOrigin.y) < 18 * s;
        var launchPt = isSelfTarget
          ? VFX.clampStage({ x: beamOrigin.x, y: beamOrigin.y - 28 * s }, 24 * s, 18 * s)
          : beamOrigin;

        VFX.streamer({
          delay: 240 + idx * 80,
          x1: launchPt.x,
          y1: launchPt.y,
          x2: tCenter.x,
          y2: tCenter.y,
          arc: (idx % 2 === 0 ? -38 : 38) * s,
          wobble: 8 * s,
          width: 9 * s,
          tailLength: 0.44,
          duration: 360,
          color: '#f5c451',
          coreColor: '#fffbeb',
          sparkColors: ['#fde047', '#fb7185', '#ffffff'],
          onArrive: function () {
            VFX.magicCircle({
              x: tFoot.x,
              y: tFoot.y,
              radius: 50,
              tilt: 0.44,
              layer: 'ground',
              style: 'bagua',
              color: '#fde047',
              secondaryColor: '#ffffff',
              rotationSpeed: -1.65,
              lineWidth: 1.6,
              alpha: 0.78,
              duration: 780,
            });

            VFX.shockwave({
              x: tFoot.x,
              y: tFoot.y,
              fromRadius: 10,
              toRadius: 68,
              tilt: 0.44,
              layer: 'ground',
              color: '#fde047',
              coreColor: '#ffffff',
              lineWidth: 3.0,
              duration: 380,
            });

            VFX.burst({
              x: tFoot.x,
              y: tFoot.y - 8 * s,
              radius: 26 * s,
              tiltY: 0.44,
              count: 10,
              shape: 'wisp',
              colors: ['#f5c451', '#fb7185', '#fef08a'],
              size: [4.0 * s, 7.6 * s],
              speed: [35 * s, 85 * s],
              vortex: 210 * s,
              vortexCenter: safeTCenter,
              gravity: -62 * s,
              life: [420, 720],
            });

            VFX.burst({
              x: tFoot.x,
              y: tFoot.y - 6 * s,
              radius: 26 * s,
              tiltY: 0.44,
              count: 10,
              shape: 'rune',
              colors: ['#f5c451', '#fef08a', '#fb7185', '#ffffff'],
              size: [4.2 * s, 6.8 * s],
              speed: [32 * s, 72 * s],
              angle: [-Math.PI * 0.72, -Math.PI * 0.28],
              gravity: -52 * s,
              drag: 0.95,
              life: [520, 860],
            });

            VFX.burst({
              x: safeTCenter.x,
              y: safeTCenter.y,
              radius: 18 * s,
              count: 6,
              shape: 'flare',
              colors: ['#ffffff', '#fef08a', '#fb7185'],
              size: [2.2 * s, 4.2 * s],
              speed: [30 * s, 85 * s],
              life: [260, 460],
            });

            VFX.hitFlash(tEl, '#fde047', 280);
            VFX.floatText(tEl, '破阵 · 增伤+40', 'status', { scale: 1.06 });
          },
        });
      })(targets[i], i);
    }

    return 1600;
  });
});
