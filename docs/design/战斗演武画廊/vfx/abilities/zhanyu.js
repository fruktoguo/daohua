/**
 * ability.zhanyu & ability.zhanyu.tayun · 斩玉 & 移形变招「踏云」（深度流体粒子化重构版）
 * 契约规范：
 * - 同文件同时注册 'ability.zhanyu' 与 'ability.zhanyu.tayun'
 * - 播放 'ability.zhanyu' 且 ctx.morphed === true 时自动转入 'ability.zhanyu.tayun' 执行
 *
 * 演出设计（各自独立高辨识度，零硬直线与生硬框线）：
 * - 常态「斩玉」：聚刃清风（attractTo）→ 玉青寒芒斜上挑劈（slash + 弧曲寒芒 streamer + wisp 剑风）
 *   → 十字收刃脆斩 + 碎玉飞花（高密度 shard 碎玉结晶 + petal 青玉飞花 + flare 脆芒）
 * - 变招「踏云」（全敌）：步踏青霄云气涡旋离身（海量轻柔青白 wisp 与 smoke 云烟翻卷，零硬弧线）
 *   → 4 道青金流光分身（streamer）携云雾俯冲穿袭 → 敌阵逐个云气旋斩 + 飞花爆发
 *   → 终结「斜贯百炼寒锋」（双股寒白百炼流光丝 + 沿斜贯路径爆发的高速寒锋 wisp 剑气暴风 + 破云星芒 flare）
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var localPlumeCache = Object.create(null);

  /**
   * 十六进制颜色转 RGB 元组字符串
   */
  function colorToRgb(hex) {
    if (typeof hex !== 'string' || hex.charAt(0) !== '#') return '255,255,255';
    var h = hex.slice(1);
    if (h.length === 3) {
      h = h.charAt(0) + h.charAt(0) + h.charAt(1) + h.charAt(1) + h.charAt(2) + h.charAt(2);
    }
    if (h.length < 6) return '255,255,255';
    var r = parseInt(h.slice(0, 2), 16);
    var g = parseInt(h.slice(2, 4), 16);
    var b = parseInt(h.slice(4, 6), 16);
    return (isNaN(r) ? 255 : r) + ',' + (isNaN(g) ? 255 : g) + ',' + (isNaN(b) ? 255 : b);
  }

  /**
   * 获取柔羽气团精灵（边缘 alpha 严格归零）
   */
  function getSoftPlume(color) {
    if (typeof VFX.getPlumeSprite === 'function') return VFX.getPlumeSprite(color);
    if (localPlumeCache[color]) return localPlumeCache[color];
    if (typeof document === 'undefined') return null;

    var c = document.createElement('canvas');
    c.width = 128;
    c.height = 128;
    var gctx = c.getContext('2d');
    var rgb = colorToRgb(color);
    var lobes = [
      { x: 64, y: 64, r: 58, peak: 0.68 },
      { x: 52, y: 58, r: 40, peak: 0.28 },
      { x: 76, y: 70, r: 38, peak: 0.26 },
    ];
    for (var i = 0; i < lobes.length; i++) {
      var lb = lobes[i];
      var grad = gctx.createRadialGradient(lb.x, lb.y, 1, lb.x, lb.y, lb.r);
      grad.addColorStop(0, 'rgba(' + rgb + ',' + lb.peak + ')');
      grad.addColorStop(0.38, 'rgba(' + rgb + ',' + (lb.peak * 0.42).toFixed(3) + ')');
      grad.addColorStop(0.72, 'rgba(' + rgb + ',' + (lb.peak * 0.1).toFixed(3) + ')');
      grad.addColorStop(1, 'rgba(' + rgb + ',0)');
      gctx.fillStyle = grad;
      gctx.beginPath();
      gctx.arc(lb.x, lb.y, lb.r, 0, TWO_PI);
      gctx.fill();
    }
    localPlumeCache[color] = c;
    return c;
  }

  function orientAngle(localAngle, axis) {
    return axis.facingX >= 0 ? axis.angle + localAngle : axis.angle - localAngle;
  }

  function resolveEnemyList(ctx) {
    if (ctx.targetEls && ctx.targetEls.length) return ctx.targetEls;
    if (ctx.enemyEls && ctx.enemyEls.length) return ctx.enemyEls;
    if (ctx.targetEl) return [ctx.targetEl];
    return [];
  }

  /**
   * 发射沿弧切线扩散的玉青寒芒 wisp 剑风，增强劈斩流体拖曳感
   */
  function emitBladeWind(cx, cy, baseRadius, startA, endA, colors, steps, s) {
    var scaleMul = s !== undefined ? s : VFX.scale();
    var radius = baseRadius * scaleMul;
    var sweepDir = endA >= startA ? 1 : -1;
    for (var i = 0; i < steps; i++) {
      var u = (i + 0.3) / steps;
      var a = VFX.lerp(startA, endA, u);
      var px = cx + Math.cos(a) * radius * 0.94;
      var py = cy + Math.sin(a) * radius * 0.94;
      VFX.burst({
        delay: i * 10,
        x: px,
        y: py,
        count: 3,
        shape: 'wisp',
        colors: colors,
        size: [3.2 * scaleMul, 6.2 * scaleMul],
        baseAngle: a + sweepDir * Math.PI * 0.5,
        spread: 0.58,
        speed: [85 * scaleMul, 195 * scaleMul],
        turbulence: 40 * scaleMul,
        drag: 0.91,
        alpha: 0.8,
        life: [210, 380],
      });
    }
  }

  /** 移形变招 · 踏云（步踏青霄云气涡旋 + 全体流光穿袭 + 斜贯百炼寒锋） */
  function playTayun(ctx) {
    var casterEl = ctx.casterEl || null;
    var enemies = resolveEnemyList(ctx);
    if (!enemies.length) return;

    var s = VFX.scale();
    var isEnemyCaster = casterEl && casterEl.classList && casterEl.classList.contains('is-enemy');
    var targetSide = isEnemyCaster ? 'ally' : 'enemy';
    var axis = VFX.combatAxis(casterEl, enemies.length ? enemies : targetSide);
    var enemyBounds = VFX.groupBounds(enemies.length ? enemies : targetSide);

    var cRaw = casterEl ? VFX.center(casterEl) : VFX.sideCenter(isEnemyCaster ? 'enemy' : 'ally');
    var cCaster = VFX.clampStage(cRaw, 46 * s, 36 * s);
    var sfRaw = casterEl ? VFX.foot(casterEl) : VFX.sideFootCenter(isEnemyCaster ? 'enemy' : 'ally');
    var sf = VFX.clampStage(sfRaw, 46 * s, 30 * s);
    var formCenter = VFX.clampStage({ x: enemyBounds.cx, y: enemyBounds.cy }, 46 * s, 36 * s);

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：步踏青霄云气涡旋（海量轻柔青白 wisp 与云烟翻卷，彻底替代硬弧线）
    // -------------------------------------------------------------------------
    if (casterEl) {
      VFX.motion(casterEl, 'dodge', {
        dx: axis.ux,
        dy: axis.uy,
        duration: 300,
      });
    }

    VFX.magicCircle({
      x: sf.x,
      y: sf.y + 2 * s,
      radius: 68,
      tilt: 0.44,
      layer: 'ground',
      color: '#2dd4bf',
      secondaryColor: '#fde047',
      style: 'illusion',
      rotationSpeed: 2.3,
      duration: 780,
      alpha: 0.72,
      lineWidth: 1.8,
    });

    VFX.shockwave({
      x: sf.x,
      y: sf.y + 2 * s,
      tilt: 0.44,
      fromRadius: 12,
      toRadius: 98,
      color: '#5eead4',
      coreColor: '#fef08a',
      lineWidth: 3.2,
      duration: 420,
      double: true,
    });

    // 青金云岚幻影柔晕（仅用边缘 alpha=0 径向渐变与旋舞软羽云团，零硬线条）
    var cloudPlumeLight = getSoftPlume('#f0fdfa');
    var cloudPlumeTeal = getSoftPlume('#5eead4');
    VFX.custom({
      duration: 580,
      draw: function (g, p) {
        var fade = Math.pow(1 - p, 1.35);
        if (fade <= 0.01) return;
        var liftY = -p * 20 * s;

        g.save();
        g.translate(cCaster.x, cCaster.y + liftY);

        var rad = 60 * s * (1 + p * 0.45);
        var grad = g.createRadialGradient(0, 0, 2 * s, 0, 0, rad);
        grad.addColorStop(0, 'rgba(240,253,250,' + (0.65 * fade).toFixed(3) + ')');
        grad.addColorStop(0.4, 'rgba(94,234,212,' + (0.42 * fade).toFixed(3) + ')');
        grad.addColorStop(0.75, 'rgba(254,240,138,' + (0.16 * fade).toFixed(3) + ')');
        grad.addColorStop(1, 'rgba(45,212,191,0)');
        g.fillStyle = grad;
        g.beginPath();
        g.arc(0, 0, rad, 0, TWO_PI);
        g.fill();

        // 双旋云岚羽团绕身渐散
        for (var j = 0; j < 4; j++) {
          var orbA = p * 5.8 + (j / 4) * TWO_PI;
          var dist = (20 + p * 34) * s;
          var ox = Math.cos(orbA) * dist;
          var oy = Math.sin(orbA) * dist * 0.65;
          var sp = j % 2 === 0 ? cloudPlumeLight : cloudPlumeTeal;
          var sr = 24 * s * (1 + p * 0.35);
          if (sp) {
            g.globalAlpha = fade * 0.46;
            g.drawImage(sp, ox - sr, oy - sr, sr * 2, sr * 2);
          }
        }
        g.restore();
      },
    });

    // 步踏青霄云气涡旋：大量轻柔青白 wisp + 云烟 smoke 盘旋起风
    VFX.burst({
      x: sf.x,
      y: sf.y - 6 * s,
      radius: 36 * s,
      ring: true,
      tiltY: 0.46,
      count: 26,
      shape: 'wisp',
      colors: ['#f0fdfa', '#ccfbf1', '#5eead4', '#99f6e4'],
      size: [5.4 * s, 10.8 * s],
      speed: [36 * s, 98 * s],
      vortex: 265 * s,
      vortexCenter: { x: cCaster.x, y: cCaster.y - 8 * s },
      gravity: -62 * s,
      turbulence: 72 * s,
      drag: 0.95,
      alpha: 0.72,
      life: [520, 880],
    });
    VFX.burst({
      x: sf.x,
      y: sf.y - 4 * s,
      radius: 30 * s,
      tiltY: 0.44,
      count: 14,
      shape: 'smoke',
      blend: 'source-over',
      colors: ['#99f6e4', '#ccfbf1', '#e2e8f0'],
      size: [14 * s, 24 * s],
      speed: [28 * s, 72 * s],
      vortex: 185 * s,
      vortexCenter: { x: cCaster.x, y: cCaster.y - 6 * s },
      gravity: -34 * s,
      alpha: 0.34,
      life: [520, 860],
    });

    // 踏云飞瓣起风
    VFX.burst({
      x: sf.x,
      y: sf.y - 6 * s,
      radius: 26 * s,
      count: 16,
      shape: 'petal',
      colors: ['#5eead4', '#fef08a', '#ffffff'],
      size: [2.8 * s, 5.2 * s],
      speed: [55 * s, 145 * s],
      gravity: -55 * s,
      turbulence: 55 * s,
      spin: [-6, 6],
      life: [420, 740],
    });

    // -------------------------------------------------------------------------
    // 阶段二（40ms..260ms）：4 道高速流光分身（streamer）携轻云 wisp 俯冲穿袭敌阵
    // -------------------------------------------------------------------------
    var arcs = [-115, 88, -148, 62];
    var streamerColors = ['#2dd4bf', '#f5c451', '#67e8f9', '#fef08a'];
    for (var k = 0; k < 4; k++) {
      (function (idx) {
        var targetEl = enemies[idx % enemies.length];
        var eAxis = VFX.combatAxis(casterEl, targetEl);
        var startPt = VFX.clampStage(eAxis.palm(12 * s), 20 * s, 20 * s);
        var epRaw = VFX.center(targetEl);
        var epSafe = VFX.clampStage(epRaw, 46 * s, 36 * s);
        var overshoot = 46 * s;
        var sideOffset = (idx % 2 === 0 ? -16 : 16) * s;
        var endPt = VFX.clampStage({
          x: epRaw.x + eAxis.ux * overshoot + eAxis.nx * sideOffset,
          y: epRaw.y + eAxis.uy * overshoot + eAxis.ny * sideOffset,
        }, 28 * s, 28 * s);

        VFX.streamer({
          delay: 40 + idx * 55,
          x1: startPt.x,
          y1: startPt.y,
          x2: endPt.x,
          y2: endPt.y,
          arc: arcs[idx] * s,
          wobble: (idx % 2 === 0 ? 5 : 0) * s,
          width: 12 * s,
          tailLength: 0.46,
          color: streamerColors[idx],
          coreColor: '#ffffff',
          sparkColors: ['#5eead4', '#fef08a', '#ffffff'],
          duration: 280,
          onArrive: function () {
            VFX.burst({
              x: epSafe.x,
              y: epSafe.y,
              count: 10,
              shape: 'wisp',
              colors: [streamerColors[idx], '#f0fdfa', '#ccfbf1'],
              size: [4.2 * s, 7.8 * s],
              speed: [55 * s, 145 * s],
              vortex: 160 * s * (idx % 2 === 0 ? 1 : -1),
              vortexCenter: epSafe,
              alpha: 0.72,
              life: [260, 460],
            });
            VFX.burst({
              x: epSafe.x,
              y: epSafe.y,
              count: 8,
              shape: 'spark',
              colors: [streamerColors[idx], '#ffffff'],
              size: [2.0 * s, 3.8 * s],
              speed: [90 * s, 210 * s],
              life: [180, 320],
              trail: 4,
            });
          },
        });
      })(k);
    }

    // -------------------------------------------------------------------------
    // 阶段三（210ms 起）：对每个敌方目标连环迸发云气涡旋 + 交叉水青/金白剑舞
    // -------------------------------------------------------------------------
    var tayunDmg = ctx.perTargetDamage != null
      ? Math.round(ctx.perTargetDamage)
      : (typeof ctx.damage === 'number' ? Math.round(ctx.damage / Math.max(1, enemies.length)) : null);

    for (var i = 0; i < enemies.length; i++) {
      (function (en, idx) {
        var at = 210 + idx * 70;
        VFX.after(at, function () {
          var ep = VFX.clampStage(VFX.center(en), 46 * s, 36 * s);
          var eAxis = VFX.combatAxis(casterEl, en);

          // 敌身周遭卷起轻柔青白云气涡旋
          VFX.burst({
            x: ep.x,
            y: ep.y + 8 * s,
            radius: 34 * s,
            ring: true,
            tiltY: 0.48,
            count: 14,
            shape: 'wisp',
            colors: ['#f0fdfa', '#99f6e4', '#5eead4'],
            size: [4.8 * s, 9.2 * s],
            speed: [28 * s, 76 * s],
            vortex: 240 * s * (idx % 2 === 0 ? 1 : -1),
            vortexCenter: ep,
            gravity: -45 * s,
            alpha: 0.68,
            life: [380, 660],
          });

          // 第一道：水青高速斜斩
          var saT1 = orientAngle(-1.25, eAxis);
          var eaT1 = orientAngle(1.05, eAxis);
          var s1x = ep.x - eAxis.ux * 6 * s;
          var s1y = ep.y - eAxis.uy * 6 * s - 4 * s;
          VFX.slash({
            x: s1x,
            y: s1y,
            radius: 80,
            thickness: 18,
            startAngle: saT1,
            endAngle: eaT1,
            color: '#2dd4bf',
            glowColor: '#fde047',
            coreColor: '#ffffff',
            duration: 210,
          });
          emitBladeWind(s1x, s1y, 80, saT1, eaT1, ['#5eead4', '#f0fdfa', '#fef08a'], 4, s);

          // 第二道：金白交叉返挑斩
          VFX.slash({
            delay: 55,
            x: ep.x + eAxis.ux * 6 * s,
            y: ep.y + eAxis.uy * 6 * s + 4 * s,
            radius: 84,
            thickness: 18,
            startAngle: orientAngle(1.18, eAxis),
            endAngle: orientAngle(-1.18, eAxis),
            color: '#f5c451',
            glowColor: '#5eead4',
            coreColor: '#ffffff',
            duration: 210,
          });

          // 凌空踏云环状风压
          VFX.shockwave({
            x: ep.x,
            y: ep.y - 6 * s,
            tilt: 0.48,
            fromRadius: 10,
            toRadius: 98,
            color: '#5eead4',
            coreColor: '#fef08a',
            lineWidth: 3.2,
            duration: 340,
            double: true,
          });

          // 星芒闪爆 + 花瓣 + 火花
          VFX.burst({
            x: ep.x,
            y: ep.y,
            radius: 8 * s,
            count: 3,
            shape: 'flare',
            colors: ['#ffffff', '#5eead4', '#fef08a'],
            size: [3.6 * s, 5.8 * s],
            speed: [18 * s, 60 * s],
            life: [200, 340],
          });
          VFX.burst({
            x: ep.x,
            y: ep.y,
            radius: 16 * s,
            count: 16,
            shape: 'petal',
            colors: ['#5eead4', '#ccfbf1', '#fef08a', '#ffffff'],
            size: [3.0 * s, 5.6 * s],
            speed: [65 * s, 180 * s],
            gravity: -32 * s,
            turbulence: 65 * s,
            spin: [-7, 7],
            life: [460, 840],
          });

          VFX.burst({
            x: ep.x,
            y: ep.y,
            count: 16,
            shape: 'spark',
            colors: ['#2dd4bf', '#fef08a', '#ffffff'],
            size: [2.0 * s, 4.2 * s],
            speed: [115 * s, 265 * s],
            drag: 0.92,
            life: [220, 420],
            trail: 4,
          });

          VFX.impact(en, { color: '#99f6e4', distance: 12, duration: 190 });
          if (tayunDmg !== null) {
            VFX.floatText(en, '-' + tayunDmg, ctx.crit ? 'crit' : 'damage', {
              scale: ctx.crit ? 1.24 : 1.06,
            });
          }
          VFX.quake('sm', 160);
        });
      })(enemies[i], i);
    }

    // -------------------------------------------------------------------------
    // 阶段四（270 + enemies.length * 70）：沿敌阵真实主轴斜贯百炼寒锋终结一闪（彻底告别硬直线）
    // -------------------------------------------------------------------------
    VFX.after(270 + enemies.length * 70, function () {
      var finBounds = VFX.groupBounds(enemies.length ? enemies : targetSide);
      var finCenter = VFX.clampStage({ x: finBounds.cx, y: finBounds.cy }, 46 * s, 36 * s);
      var mainAngle = finBounds.count > 1 ? finBounds.axisAngle : axis.angle;
      var mx = Math.cos(mainAngle);
      var my = Math.sin(mainAngle);
      if (mx * axis.ux + my * axis.uy < 0) {
        mx = -mx;
        my = -my;
      }
      var px = -my;
      var py = mx;
      var halfAlong = Math.max(110 * s, finBounds.majorSpan * 0.5 + 84 * s);
      var halfCross = 46 * s;
      var arcSign = axis.facingX >= 0 ? 1 : -1;

      var slashFrom = VFX.clampStage({
        x: finBounds.cx - mx * halfAlong + px * halfCross,
        y: finBounds.cy - my * halfAlong + py * halfCross,
      }, 22 * s, 22 * s);
      var slashTo = VFX.clampStage({
        x: finBounds.cx + mx * halfAlong - px * halfCross,
        y: finBounds.cy + my * halfAlong - py * halfCross,
      }, 22 * s, 22 * s);
      var slashDir = VFX.dir(slashFrom, slashTo);

      // 双股极速百炼寒锋流光（微弧交错 + 羽化尾迹）
      VFX.streamer({
        x1: slashFrom.x,
        y1: slashFrom.y,
        x2: slashTo.x,
        y2: slashTo.y,
        arc: -arcSign * 22 * s,
        wobble: 3 * s,
        width: 15 * s,
        tailLength: 0.56,
        color: '#2dd4bf',
        coreColor: '#ffffff',
        sparkColors: ['#f0fdfa', '#5eead4', '#ffffff'],
        duration: 280,
      });
      VFX.streamer({
        delay: 18,
        x1: slashFrom.x - px * 4 * s,
        y1: slashFrom.y - py * 4 * s,
        x2: slashTo.x + px * 4 * s,
        y2: slashTo.y + py * 4 * s,
        arc: arcSign * 18 * s,
        wobble: 4 * s,
        width: 10 * s,
        tailLength: 0.5,
        color: '#fef08a',
        coreColor: '#ffffff',
        sparkColors: ['#fef08a', '#ffffff'],
        duration: 270,
      });

      // 沿斜贯轨迹依次炸开百炼寒锋 wisp 剑气风柱与清冽 flare 剑芒
      var segments = 8;
      for (var sg = 0; sg <= segments; sg++) {
        (function (idx) {
          var u = idx / segments;
          var segX = VFX.lerp(slashFrom.x, slashTo.x, u);
          var segY = VFX.lerp(slashFrom.y, slashTo.y, u);
          VFX.burst({
            delay: idx * 14,
            x: segX,
            y: segY,
            radius: 10 * s,
            count: 5,
            shape: 'wisp',
            colors: ['#ffffff', '#f0fdfa', '#5eead4', '#2dd4bf'],
            size: [4.2 * s, 8.6 * s],
            baseAngle: slashDir.angle,
            spread: 0.85,
            speed: [140 * s, 320 * s],
            turbulence: 65 * s,
            drag: 0.91,
            alpha: 0.84,
            life: [260, 480],
          });
          VFX.burst({
            delay: idx * 14 + 6,
            x: segX,
            y: segY,
            radius: 8 * s,
            count: 4,
            shape: 'spark',
            colors: ['#ffffff', '#99f6e4', '#fef08a'],
            size: [1.8 * s, 3.8 * s],
            baseAngle: slashDir.angle,
            spread: 0.72,
            speed: [220 * s, 440 * s],
            drag: 0.92,
            life: [200, 380],
            trail: 5,
          });
          if (idx % 2 === 1) {
            VFX.burst({
              delay: idx * 14,
              x: segX,
              y: segY,
              radius: 8 * s,
              count: 2,
              shape: 'flare',
              colors: ['#ffffff', '#5eead4'],
              size: [4.4 * s, 7.2 * s],
              speed: [16 * s, 48 * s],
              life: [200, 350],
            });
          }
        })(sg);
      }

      // 全敌阵凌空踏云大风压与漫天流云飞花
      var shockRadius = Math.max(122, Math.min(184, (finBounds.majorSpan * 0.5) / Math.max(0.58, s) + 78));
      var formRadius = Math.max(70 * s, Math.min(135 * s, finBounds.majorSpan * 0.45 + 32 * s));
      VFX.shockwave({
        x: finCenter.x,
        y: finCenter.y,
        tilt: 0.45,
        fromRadius: 20,
        toRadius: shockRadius,
        color: '#2dd4bf',
        coreColor: '#fef08a',
        lineWidth: 4.0,
        duration: 420,
        double: true,
      });

      VFX.burst({
        x: finCenter.x,
        y: finCenter.y + 6 * s,
        radius: formRadius,
        tiltY: 0.48,
        count: 20,
        shape: 'wisp',
        colors: ['#f0fdfa', '#ccfbf1', '#5eead4'],
        size: [6.0 * s, 11.5 * s],
        speed: [48 * s, 125 * s],
        gravity: -48 * s,
        turbulence: 68 * s,
        alpha: 0.58,
        life: [480, 860],
      });

      VFX.burst({
        x: finCenter.x,
        y: finCenter.y,
        radius: formRadius * 0.86,
        tiltY: 0.48,
        count: 24,
        shape: 'petal',
        colors: ['#5eead4', '#fef08a', '#ccfbf1', '#ffffff'],
        size: [3.2 * s, 5.8 * s],
        speed: [55 * s, 160 * s],
        gravity: -40 * s,
        turbulence: 60 * s,
        life: [520, 920],
      });

      VFX.quake('md', 240);
    });
  }

  /** 常态 · 斩玉（玉青寒芒斜劈 + 碎玉飞花粒子） */
  function playZhanyu(ctx) {
    if (ctx && ctx.morphed === true) {
      return playTayun(ctx);
    }

    var casterEl = ctx.casterEl || null;
    var targets = (ctx.targetEls && ctx.targetEls.length)
      ? ctx.targetEls
      : (ctx.targetEl ? [ctx.targetEl] : []);
    var targetEl = targets[0] || null;
    if (!targetEl) return;

    var s = VFX.scale();
    var axis = VFX.combatAxis(casterEl, targetEl);
    var t = VFX.clampStage(VFX.center(targetEl), 46 * s, 38 * s);
    var palmPt = VFX.clampStage(axis.palm(16 * s), 20 * s, 20 * s);
    var sideSign = axis.facingX >= 0 ? 1 : -1;

    var zhanyuDmg = ctx.perTargetDamage != null
      ? Math.round(ctx.perTargetDamage)
      : (typeof ctx.damage === 'number' ? Math.round(ctx.damage) : null);
    var zhanyuHit1 = zhanyuDmg !== null ? Math.max(1, Math.round(zhanyuDmg * 0.4)) : null;
    var zhanyuHit2 = zhanyuDmg !== null ? Math.max(1, zhanyuDmg - zhanyuHit1) : null;

    // -------------------------------------------------------------------------
    // 阶段一（0ms）：轻快突进 + 拔刀起手流光 + 玉青寒芒斜上挑劈
    // -------------------------------------------------------------------------
    if (casterEl) {
      VFX.motion(casterEl, 'lunge', { dx: axis.ux, dy: axis.uy, distance: 26, duration: 190 });
    }

    // 施法者拔刀掌位掠向目标的玉青破风流光
    if (VFX.dir(palmPt, t).dist > 12) {
      VFX.streamer({
        x1: palmPt.x,
        y1: palmPt.y,
        x2: t.x,
        y2: t.y,
        arc: -sideSign * 18 * s,
        wobble: 2 * s,
        width: 9 * s,
        tailLength: 0.44,
        color: '#2dd4bf',
        coreColor: '#ffffff',
        sparkColors: ['#5eead4', '#ccfbf1', '#ffffff'],
        duration: 165,
      });
    }

    // 剑刃前置内聚冷风
    VFX.burst({
      x: t.x,
      y: t.y,
      radius: 42 * s,
      ring: true,
      count: 10,
      shape: 'wisp',
      colors: ['#5eead4', '#ccfbf1'],
      size: [2.6 * s, 4.8 * s],
      speed: [14 * s, 34 * s],
      attractTo: t,
      attractStrength: 760 * s,
      life: [110, 170],
    });

    var sa1 = orientAngle(1.12, axis);
    var ea1 = orientAngle(-1.22, axis);
    var slash1Pt = {
      x: t.x - axis.ux * 8 * s,
      y: t.y - axis.uy * 8 * s + 4 * s,
    };
    VFX.slash({
      x: slash1Pt.x,
      y: slash1Pt.y,
      radius: 86,
      thickness: 20,
      startAngle: sa1,
      endAngle: ea1,
      color: '#2dd4bf',
      glowColor: '#5eead4',
      coreColor: '#ffffff',
      duration: 215,
    });

    // 弧曲玉青寒芒尾流（沿攻击轴偏转并约束舞台安全区）
    var cut1A = VFX.clampStage({
      x: t.x + axis.ux * (-54 * s) + axis.nx * (44 * s * sideSign),
      y: t.y + axis.uy * (-54 * s) + axis.ny * (44 * s * sideSign),
    }, 22 * s, 22 * s);
    var cut1B = VFX.clampStage({
      x: t.x + axis.ux * (56 * s) + axis.nx * (-48 * s * sideSign),
      y: t.y + axis.uy * (56 * s) + axis.ny * (-48 * s * sideSign),
    }, 22 * s, 22 * s);
    VFX.streamer({
      x1: cut1A.x,
      y1: cut1A.y,
      x2: cut1B.x,
      y2: cut1B.y,
      arc: -sideSign * 20 * s,
      wobble: 3 * s,
      width: 10 * s,
      tailLength: 0.45,
      color: '#5eead4',
      coreColor: '#ffffff',
      sparkColors: ['#99f6e4', '#ffffff'],
      duration: 205,
    });
    emitBladeWind(slash1Pt.x, slash1Pt.y, 84, sa1, ea1, ['#2dd4bf', '#99f6e4', '#f0fdfa'], 5, s);

    VFX.burst({
      x: t.x,
      y: t.y,
      radius: 8 * s,
      count: 3,
      shape: 'flare',
      colors: ['#ffffff', '#5eead4'],
      size: [3.4 * s, 5.2 * s],
      speed: [18 * s, 56 * s],
      life: [160, 260],
    });

    VFX.impact(targetEl, { color: '#99f6e4', distance: 10, duration: 160 });
    if (zhanyuHit1 !== null) {
      VFX.floatText(targetEl, '-' + zhanyuHit1, 'damage', { scale: 0.96 });
    }

    // -------------------------------------------------------------------------
    // 阶段二（135ms）：十字收刃脆斩 + 碎玉结晶与玉青飞花四散飞舞
    // -------------------------------------------------------------------------
    VFX.after(135, function () {
      var sa2 = orientAngle(-1.18, axis);
      var ea2 = orientAngle(1.12, axis);
      var slash2Pt = {
        x: t.x - axis.ux * 6 * s,
        y: t.y - axis.uy * 6 * s - 4 * s,
      };
      VFX.slash({
        x: slash2Pt.x,
        y: slash2Pt.y,
        radius: 92,
        thickness: 22,
        startAngle: sa2,
        endAngle: ea2,
        color: '#5eead4',
        glowColor: '#14b8a6',
        coreColor: '#ffffff',
        duration: 230,
      });

      var cut2A = VFX.clampStage({
        x: t.x + axis.ux * (-52 * s) + axis.nx * (-46 * s * sideSign),
        y: t.y + axis.uy * (-52 * s) + axis.ny * (-46 * s * sideSign),
      }, 22 * s, 22 * s);
      var cut2B = VFX.clampStage({
        x: t.x + axis.ux * (58 * s) + axis.nx * (46 * s * sideSign),
        y: t.y + axis.uy * (58 * s) + axis.ny * (46 * s * sideSign),
      }, 22 * s, 22 * s);
      VFX.streamer({
        x1: cut2A.x,
        y1: cut2A.y,
        x2: cut2B.x,
        y2: cut2B.y,
        arc: sideSign * 22 * s,
        wobble: 3 * s,
        width: 11 * s,
        tailLength: 0.48,
        color: '#2dd4bf',
        coreColor: '#ffffff',
        sparkColors: ['#ccfbf1', '#5eead4', '#ffffff'],
        duration: 220,
      });
      emitBladeWind(slash2Pt.x, slash2Pt.y, 90, sa2, ea2, ['#5eead4', '#ccfbf1', '#ffffff'], 6, s);

      VFX.shockwave({
        x: t.x,
        y: t.y,
        fromRadius: 8,
        toRadius: 74,
        color: '#2dd4bf',
        coreColor: '#ffffff',
        lineWidth: 2.8,
        duration: 270,
        double: true,
      });

      // 脆亮碎玉 flare 星芒绽放
      VFX.burst({
        x: t.x,
        y: t.y,
        radius: 10 * s,
        count: 5,
        shape: 'flare',
        colors: ['#ffffff', '#ccfbf1', '#5eead4'],
        size: [4.0 * s, 6.8 * s],
        speed: [22 * s, 78 * s],
        spin: [-4, 4],
        life: [210, 370],
      });

      // 碎玉结晶粒子（shard）清脆四溅
      VFX.burst({
        x: t.x,
        y: t.y,
        radius: 10 * s,
        count: 26,
        shape: 'shard',
        colors: ['#5eead4', '#2dd4bf', '#ccfbf1', '#ffffff'],
        size: [2.6 * s, 5.4 * s],
        speed: [115 * s, 285 * s],
        gravity: 155 * s,
        drag: 0.94,
        spin: [-11, 11],
        life: [340, 600],
        trail: 3,
      });

      // 碎玉伴生玉青飞花粒子（petal）随剑风打旋飘散
      VFX.burst({
        x: t.x,
        y: t.y,
        radius: 14 * s,
        count: 18,
        shape: 'petal',
        colors: ['#99f6e4', '#ccfbf1', '#5eead4', '#ffffff'],
        size: [2.8 * s, 5.2 * s],
        speed: [55 * s, 160 * s],
        gravity: -28 * s,
        turbulence: 72 * s,
        drag: 0.95,
        spin: [-8, 8],
        life: [460, 820],
      });

      // 清冷玉雾 wisp 与寒芒火花
      VFX.burst({
        x: t.x,
        y: t.y,
        radius: 12 * s,
        count: 12,
        shape: 'wisp',
        colors: ['#2dd4bf', '#99f6e4', '#f0fdfa'],
        size: [4.2 * s, 8.0 * s],
        speed: [65 * s, 165 * s],
        turbulence: 48 * s,
        drag: 0.91,
        alpha: 0.72,
        life: [260, 480],
      });
      VFX.burst({
        x: t.x,
        y: t.y,
        count: 16,
        shape: 'spark',
        colors: ['#99f6e4', '#ffffff'],
        size: [2.0 * s, 4.0 * s],
        speed: [120 * s, 270 * s],
        life: [200, 380],
        trail: 4,
      });

      VFX.impact(targetEl, { color: '#ffffff', distance: 13, duration: 200 });
      if (zhanyuHit2 !== null) {
        VFX.floatText(targetEl, '-' + zhanyuHit2, ctx.crit ? 'crit' : 'damage', {
          scale: ctx.crit ? 1.24 : 1.06,
        });
      }
    });
  }

  VFX.register('ability.zhanyu', playZhanyu);
  VFX.register('ability.zhanyu.tayun', playTayun);
});
