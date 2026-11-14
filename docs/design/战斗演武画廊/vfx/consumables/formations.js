/**
 * 道化太初 · 演武画廊 · 战斗阵盘与触发反击特效组 (v2.2 自适应比例锚点版)
 *
 * 注册全量 6 个 ID：
 *   部署态（均同步返回 { stop, pulse, moveTo } 常驻阵句柄）：
 *     1. item.juling-formation-disk   · 聚灵阵盘（金青灵气缓缓旋升聚灵）
 *     2. item.mizong-formation-disk   · 迷踪阵盘（全队幽紫幻雾与迷离幻光，横竖屏自适应纵列/宽阵）
 *     3. item.tianlei-formation-disk  · 天雷阵盘（暗蓝碎电星子高速盘旋）
 *     4. item.xuesha-formation-disk   · 血煞阵盘（暗红怨煞流雾与血珠微光翻涌）
 *   触发态（阵法反击/吸血独立演出，可选联动 ctx.formationHandle.pulse）：
 *     5. item.tianlei-formation-disk.proc · 天雷阵触发阵心电浆掣电反击
 *     6. item.xuesha-formation-disk.proc  · 血煞阵触发血灵 wisp 汲取飞流与金红回血星华
 *
 * 视觉规范：
 *   禁止硬描边正多边形与直筒光柱！使用低饱和柔羽化 magicCircle（纯圆弧 water / illusion 样式）
 *   叠加 VFX.after 托管的高频微粒持续 emitter，配合 anchorEl/anchorSide/groupBounds 保障缩放与切阵零残留。
 */
(window.DaohuaVFXModules = window.DaohuaVFXModules || []).push(function (VFX) {
  'use strict';
  if (!VFX || !VFX.isCanvasV2) return;

  var TWO_PI = Math.PI * 2;
  var HALF_PI = Math.PI * 0.5;

  function resolveCasterEl(ctx) {
    return ctx.casterEl || (ctx.caster && ctx.caster.el) || null;
  }

  function resolveTargetEl(ctx) {
    return (
      ctx.targetEl ||
      ctx.attackerEl ||
      (ctx.targetEls && ctx.targetEls[0]) ||
      (ctx.targets && ctx.targets[0] && ctx.targets[0].el) ||
      null
    );
  }

  /** 解析己方全体角色元素列表，服务迷踪阵全队躲闪残影演出（同时兼容画廊与正式前端战场） */
  function resolveAllyEls(ctx, casterEl) {
    if (ctx.allyEls && ctx.allyEls.length) return ctx.allyEls;
    if (ctx.targetEls && ctx.targetEls.length) return ctx.targetEls;
    var root =
      (casterEl && casterEl.closest && casterEl.closest('.combat-arena-shell, .arena-stage')) ||
      (typeof document !== 'undefined' ? document : null);
    if (root) {
      var isEnemy =
        casterEl &&
        casterEl.classList &&
        (casterEl.classList.contains('is-enemy') || casterEl.classList.contains('arena-actor--enemy'));
      var side = isEnemy ? 'enemy' : 'ally';
      var roleCls = isEnemy ? '.arena-actor--enemy' : '.arena-actor--player';
      var nodes = root.querySelectorAll('.arena-unit.is-' + side + ', .arena-actor.is-' + side + ', ' + roleCls);
      var alive = [];
      for (var i = 0; i < nodes.length; i++) {
        var el = nodes[i];
        if (alive.indexOf(el) >= 0) continue;
        if (
          el.classList &&
          (el.classList.contains('is-defeated') ||
            el.classList.contains('is-dead') ||
            el.classList.contains('is-dormant'))
        ) {
          continue;
        }
        if (el.closest && (el.closest('.arena-slot--slain') || el.closest('.arena-slot--corpse'))) {
          continue;
        }
        alive.push(el);
      }
      if (alive.length) return alive;
    }
    return casterEl ? [casterEl] : [];
  }

  /** 实时读取个人阵法施法者锚点与画幅比例，避免读条抖动及左侧裁切 */
  function resolvePersonalAnchors(casterEl) {
    var s = VFX.scale();
    var st = VFX.size();
    var padX = 34 * s;
    var padY = 24 * s;
    return {
      s: s,
      st: st,
      f: VFX.clampStage(VFX.foot(casterEl), padX, padY),
      c: VFX.clampStage(VFX.center(casterEl), padX, padY),
    };
  }

  /** 计算团队迷踪阵在当前横/竖屏站位下的真实椭圆尺寸与中心坐标 */
  function computeMizongLayout(teamSide, casterEl) {
    var s = VFX.scale();
    var st = VFX.size();
    var bounds = VFX.groupBounds(teamSide);
    var halfW = VFX.clamp(bounds.spanX * 0.5 + 46 * s, 44 * s, st.w * 0.24);
    var halfH = VFX.clamp(bounds.spanY * 0.5 + 32 * s, 28 * s, st.h * 0.42);
    var isVertical = bounds.spanY > bounds.spanX + 18 * s;
    var footPt = isVertical
      ? { x: bounds.cx, y: bounds.cy + 8 * s }
      : VFX.sideFootCenter(teamSide);
    if (!footPt && casterEl) footPt = VFX.foot(casterEl);

    var circleR;
    var circleRy;
    if (isVertical) {
      // 竖屏纵列：横向严格限制在己方半场，纵向拉长包住三友
      circleR = VFX.clamp(Math.max(halfW, 74 * s), 42 * s, st.w * 0.21);
      circleRy = VFX.clamp(Math.max(halfH, circleR * 1.15), circleR * 0.85, st.h * 0.42);
    } else {
      // 横屏排阵：保持舒展宽椭圆覆盖并列阵型
      circleR = VFX.clamp(bounds.spanX * 0.5 + 28 * s, 72 * s, st.w * 0.23);
      circleRy = VFX.clamp(Math.max(halfH, circleR * 0.43), 30 * s, st.h * 0.34);
    }
    var circleTilt = VFX.clamp(circleRy / Math.max(1, circleR), 0.38, 3.0);

    var padX = Math.max(24 * s, Math.min(circleR * 0.42, st.w * 0.12));
    var padY = Math.max(22 * s, Math.min(circleRy * 0.32, st.h * 0.14));
    var safeFoot = VFX.clampStage(footPt, padX, padY);

    return {
      s: s,
      st: st,
      bounds: bounds,
      foot: footPt,
      safeFoot: safeFoot,
      halfW: halfW,
      halfH: halfH,
      isVertical: isVertical,
      radius: circleR,
      ry: circleRy,
      tilt: circleTilt,
    };
  }

  /**
   * 构造常驻法阵与高频微粒持续发射器句柄：
   * - 支持 anchorEl / anchorSide / rawScale / resolveCircleMetrics 自适应重锚；
   * - 落地延迟后创建低饱和柔羽化 magicCircle，并启动 VFX.after 链式粒子 emitter；
   * - 同步向外部返回 { stop, pulse, moveTo }，moveTo 显式覆盖位置后接管内部圆环同步。
   */
  function createFormationHandle(delayMs, cfg) {
    var innerHandle = null;
    var canceled = false;
    var stopped = false;
    var mounted = false;
    var manualMoved = false;
    var anchorDetached = false;
    var pendingPulse = null;
    var pos = { x: cfg.x, y: cfg.y };
    var vortexCenter = { x: cfg.x, y: cfg.y };
    var activeRadius = cfg.radius;
    var activeTilt = cfg.tilt;
    var emitTimer = null;
    var unsubResize = null;
    var tickCount = 0;
    var elapsedMs = 0;
    var emitIntervalMs = cfg.emitIntervalMs || 85;
    var maxDurationMs = cfg.maxDurationMs || 360000;

    function buildCircleOpts(useAnchor) {
      var opts = {
        persistent: true,
        layer: 'ground',
        style: cfg.style,
        x: pos.x,
        y: pos.y,
        radius: activeRadius,
        tilt: activeTilt,
        rawScale: !!cfg.rawScale,
        color: cfg.color,
        secondaryColor: cfg.secondaryColor,
        rotationSpeed: cfg.rotationSpeed,
        pulseSpeed: cfg.pulseSpeed || 2.0,
        alpha: cfg.alpha !== undefined ? cfg.alpha : 0.46,
        lineWidth: cfg.lineWidth || 1.45,
      };
      if (useAnchor && !manualMoved && !anchorDetached) {
        if (cfg.anchorEl) opts.anchorEl = cfg.anchorEl;
        if (cfg.anchorSide) opts.anchorSide = cfg.anchorSide;
      }
      return opts;
    }

    function cleanupResize() {
      if (unsubResize) {
        unsubResize();
        unsubResize = null;
      }
    }

    function applyLiveMetrics(m) {
      if (!m) return;
      pos.x = m.x;
      pos.y = m.y;
      vortexCenter.x = m.x;
      vortexCenter.y = m.y;
      var changed = Math.abs(m.radius - activeRadius) > 1.2 || Math.abs(m.tilt - activeTilt) > 0.02;
      activeRadius = m.radius;
      activeTilt = m.tilt;
      if (mounted && innerHandle && !anchorDetached) {
        if (changed) {
          if (typeof innerHandle.stop === 'function') innerHandle.stop(1);
          innerHandle = VFX.magicCircle(buildCircleOpts(true));
        } else if (!cfg.anchorEl && !cfg.anchorSide && typeof innerHandle.moveTo === 'function') {
          innerHandle.moveTo(m.x, m.y);
        }
      }
    }

    function syncLiveAnchor() {
      if (manualMoved) return null;
      if (typeof cfg.resolveCircleMetrics === 'function') {
        var m = cfg.resolveCircleMetrics();
        applyLiveMetrics(m);
        return m;
      }
      if (typeof cfg.resolveAnchor === 'function') {
        var a = cfg.resolveAnchor();
        if (a) {
          pos.x = a.x;
          pos.y = a.y;
          vortexCenter.x = a.x;
          vortexCenter.y = a.y;
          return a;
        }
      }
      return null;
    }

    function emitStep() {
      emitTimer = null;
      if (canceled || stopped || elapsedMs >= maxDurationMs) {
        cleanupResize();
        return;
      }

      syncLiveAnchor();
      var hidden = typeof document !== 'undefined' && document.hidden;
      if (!hidden && typeof cfg.onEmit === 'function') {
        cfg.onEmit(pos, vortexCenter, tickCount, {
          manualMoved: manualMoved,
          s: VFX.scale(),
          st: VFX.size(),
          radius: activeRadius,
          tilt: activeTilt,
        });
      }
      tickCount++;
      elapsedMs += emitIntervalMs;
      emitTimer = VFX.after(emitIntervalMs, emitStep);
    }

    function triggerPulseVisual(pulseColor, intensity) {
      syncLiveAnchor();
      var s = VFX.scale();
      var col = pulseColor || cfg.secondaryColor || cfg.color;
      if (innerHandle && typeof innerHandle.pulse === 'function') {
        innerHandle.pulse(col, VFX.clamp(intensity || 1.15, 0.4, 1.5));
      }
      var swOpts = {
        x: pos.x,
        y: pos.y,
        tilt: activeTilt,
        layer: 'ground',
        fromRadius: activeRadius * 0.34,
        toRadius: activeRadius * 1.38,
        color: col,
        coreColor: cfg.secondaryColor || '#ffffff',
        lineWidth: cfg.rawScale ? 2.8 * s : 2.8,
        double: false,
        duration: 420,
      };
      if (cfg.rawScale) swOpts.rawScale = true;
      VFX.shockwave(swOpts);
      if (typeof cfg.onPulse === 'function') {
        cfg.onPulse(pos, vortexCenter, col, intensity || 1.15, {
          manualMoved: manualMoved,
          s: s,
          st: VFX.size(),
          radius: activeRadius,
          tilt: activeTilt,
        });
      }
    }

    if (typeof cfg.resolveCircleMetrics === 'function' && typeof VFX.onResize === 'function') {
      unsubResize = VFX.onResize(function () {
        if (canceled || stopped) {
          cleanupResize();
          return;
        }
        if (typeof VFX.activeCount === 'function') {
          var ac = VFX.activeCount();
          if (mounted ? ac.persistent === 0 : ac.pendingTimers === 0) {
            stopped = true;
            canceled = true;
            cleanupResize();
            return;
          }
        }
        if (manualMoved || anchorDetached) return;
        applyLiveMetrics(cfg.resolveCircleMetrics());
      });
    }

    var mountTimer = VFX.after(delayMs || 0, function () {
      mountTimer = null;
      if (canceled || stopped) {
        cleanupResize();
        return;
      }
      var m = syncLiveAnchor();
      if (m && typeof cfg.resolveCircleMetrics === 'function' && !manualMoved) {
        activeRadius = m.radius;
        activeTilt = m.tilt;
      }
      mounted = true;
      innerHandle = VFX.magicCircle(buildCircleOpts(true));

      emitStep();

      if (pendingPulse) {
        triggerPulseVisual(pendingPulse.color, pendingPulse.intensity);
        pendingPulse = null;
      }
    });

    return {
      stop: function (fadeMs) {
        if (stopped) return;
        stopped = true;
        canceled = true;
        cleanupResize();
        if (mountTimer && mountTimer.cancel) {
          mountTimer.cancel();
          mountTimer = null;
        }
        if (emitTimer && emitTimer.cancel) {
          emitTimer.cancel();
          emitTimer = null;
        }
        if (innerHandle && typeof innerHandle.stop === 'function') {
          innerHandle.stop(fadeMs);
        }
      },
      pulse: function (color, intensity) {
        if (canceled || stopped) return;
        if (innerHandle) {
          triggerPulseVisual(color, intensity);
        } else {
          pendingPulse = { color: color, intensity: intensity };
        }
      },
      moveTo: function (x, y) {
        if (canceled || stopped) return;
        manualMoved = true;
        pos.x = x;
        pos.y = y;
        vortexCenter.x = x;
        vortexCenter.y = y;
        if (innerHandle) {
          if ((cfg.anchorEl || cfg.anchorSide) && !anchorDetached) {
            anchorDetached = true;
            if (typeof innerHandle.stop === 'function') innerHandle.stop(1);
            innerHandle = VFX.magicCircle(buildCircleOpts(false));
          } else if (typeof innerHandle.moveTo === 'function') {
            innerHandle.moveTo(x, y);
          }
        }
      },
    };
  }

  /**
   * 1. item.juling-formation-disk · 聚灵阵盘（→ formation.juling）
   * 落地柔青金涟漪与灵气聚流，开启低饱和柔圆底盘 + 持续金青灵气点向中心缓缓旋转上升 emitter。
   */
  VFX.register('item.juling-formation-disk', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var init = resolvePersonalAnchors(casterEl);

    VFX.motion(casterEl, 'cast', { duration: 320 });

    VFX.artifact({
      kind: 'disk',
      x1: init.c.x,
      y1: init.c.y,
      x2: init.f.x,
      y2: init.f.y,
      arc: -66 * init.s,
      duration: 460,
      size: 24 * init.s,
      tiltFrom: 0.9,
      tiltTo: 0.44,
      spin: 5.2,
      color: '#34d399',
      accentColor: '#fde047',
      glowColor: '#10b981',
    });

    // 落地展开翠金聚灵柔雾（废除直筒光柱）
    VFX.after(390, function () {
      var live = resolvePersonalAnchors(casterEl);
      var s = live.s;
      var f = live.f;
      var c = live.c;

      VFX.hitFlash(casterEl, '#6ee7b7', 220);

      VFX.shockwave({
        x: f.x,
        y: f.y,
        tilt: 0.44,
        fromRadius: 12,
        toRadius: 96,
        color: '#34d399',
        coreColor: '#fef08a',
        lineWidth: 3.4,
        double: true,
        duration: 500,
      });

      VFX.burst({
        x: f.x,
        y: f.y,
        radius: 48 * s,
        tiltY: 0.44,
        count: 10,
        shape: 'smoke',
        colors: ['#047857', '#0f766e', '#a16207'],
        size: [12 * s, 20 * s],
        speed: [12 * s, 32 * s],
        vortex: 140 * s,
        vortexCenter: f,
        drag: 0.94,
        alpha: 0.3,
        life: [580, 920],
        layer: 'ground',
      });

      VFX.burst({
        x: f.x,
        y: f.y - 6 * s,
        radius: 74 * s,
        ring: true,
        tiltY: 0.48,
        count: 26,
        shape: 'orb',
        colors: ['#34d399', '#6ee7b7', '#fde68a', '#ffffff'],
        size: [2.0 * s, 4.0 * s],
        speed: [14 * s, 34 * s],
        vortex: 180 * s,
        vortexCenter: f,
        attractTo: c,
        attractStrength: 620 * s,
        drag: 0.93,
        life: [480, 760],
        trail: Math.max(2, Math.round(5 * s)),
      });

      VFX.floatText(casterEl, '聚灵阵 · 展开', 'heal');
    });

    return createFormationHandle(390, {
      style: 'water',
      anchorEl: casterEl,
      x: init.f.x,
      y: init.f.y,
      radius: 66,
      tilt: 0.44,
      color: '#2c8c72',
      secondaryColor: '#c2ad65',
      rotationSpeed: 0.62,
      pulseSpeed: 1.9,
      alpha: 0.48,
      lineWidth: 1.5,
      emitIntervalMs: 80,
      resolveAnchor: function () {
        return resolvePersonalAnchors(casterEl).f;
      },
      onEmit: function (pos, vc, tickIdx, meta) {
        var s = meta.s;
        var centerPt = meta.manualMoved
          ? { x: pos.x, y: Math.max(20 * s, pos.y - 38 * s) }
          : resolvePersonalAnchors(casterEl).c;

        // 持续金青灵气点自外环向中心缓缓旋转上升
        var ang = Math.random() * TWO_PI;
        var r = 66 * s * VFX.rand(0.45, 0.96);
        var px = pos.x + Math.cos(ang) * r;
        var py = pos.y + Math.sin(ang) * r * 0.44;
        var tang = ang + HALF_PI;

        VFX.burst({
          x: px,
          y: py,
          radius: 3 * s,
          count: 2,
          shape: 'orb',
          colors: ['#5eead4', '#fde68a', '#a7f3d0', '#fef08a'],
          size: [1.5 * s, 2.9 * s],
          angle: [tang - 0.35, tang + 0.35],
          speed: [14 * s, 28 * s],
          vortex: 135 * s,
          vortexCenter: vc,
          attractTo: centerPt,
          attractStrength: 130 * s,
          gravity: -44 * s,
          drag: 0.96,
          alpha: 0.74,
          life: [720, 1120],
          trail: Math.max(2, Math.round(3 * s)),
        });

        if (tickIdx % 2 === 0) {
          VFX.burst({
            x: px,
            y: py,
            radius: 4 * s,
            count: 1,
            shape: 'wisp',
            colors: ['#34d399', '#fde68a'],
            size: [1.8 * s, 3.0 * s],
            angle: [tang - 0.3, tang + 0.3],
            speed: [18 * s, 32 * s],
            vortex: 155 * s,
            vortexCenter: vc,
            gravity: -48 * s,
            drag: 0.96,
            alpha: 0.56,
            life: [640, 960],
          });
        }

        if (tickIdx % 4 === 0) {
          var sparkPt = VFX.clampStage(
            {
              x: pos.x + VFX.rand(-28, 28) * s,
              y: pos.y + VFX.rand(-12, 8) * s,
            },
            14 * s,
            14 * s
          );
          VFX.burst({
            x: sparkPt.x,
            y: sparkPt.y,
            radius: 6 * s,
            count: 1,
            shape: 'flare',
            colors: ['#fef08a', '#a7f3d0'],
            size: [1.5 * s, 2.4 * s],
            speed: [8 * s, 22 * s],
            gravity: -38 * s,
            alpha: 0.72,
            life: [500, 820],
          });
        }
      },
      onPulse: function (pos, vc, col, intensity, meta) {
        var s = meta.s;
        VFX.burst({
          x: pos.x,
          y: pos.y,
          radius: 56 * s,
          ring: true,
          tiltY: 0.44,
          count: 12,
          shape: 'wisp',
          colors: [col, '#5eead4', '#fde68a'],
          size: [2.2 * s, 3.8 * s],
          speed: [16 * s, 38 * s],
          vortex: 210 * s,
          vortexCenter: vc,
          gravity: -72 * s,
          drag: 0.95,
          alpha: 0.72,
          life: [480, 760],
          trail: Math.max(2, Math.round(4 * s)),
        });
      },
    });
  });

  /**
   * 2. item.mizong-formation-disk · 迷踪阵盘（→ formation.mizong，teamWide: true 全队阵法）
   * 全队幽紫三环错位幻阵：横竖屏自适应纵列/宽阵，脚下持续缭绕幽紫幻雾与迷离微光。
   */
  VFX.register('item.mizong-formation-disk', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var isEnemy = casterEl && casterEl.classList && casterEl.classList.contains('is-enemy');
    var teamSide = isEnemy ? 'enemy' : 'ally';
    var initAnchor = resolvePersonalAnchors(casterEl);
    var initLayout = computeMizongLayout(teamSide, casterEl);

    VFX.motion(casterEl, 'cast', { duration: 340 });

    VFX.artifact({
      kind: 'disk',
      x1: initAnchor.c.x,
      y1: initAnchor.c.y,
      x2: initLayout.safeFoot.x,
      y2: initLayout.safeFoot.y,
      arc: -84 * initLayout.s,
      duration: 500,
      size: 26 * initLayout.s,
      tiltFrom: 0.9,
      tiltTo: Math.min(0.95, initLayout.tilt),
      spin: 5.0,
      color: '#818cf8',
      accentColor: '#e2e8f0',
      glowColor: '#a78bfa',
    });

    // 落地铺开全队幻雾大阵（零硬棱形碎片，按真实 groupBounds 自适应横/纵布阵）
    VFX.after(430, function () {
      var layout = computeMizongLayout(teamSide, casterEl);
      var s = layout.s;
      var centerPt = layout.safeFoot;
      var allyEls = resolveAllyEls(ctx, casterEl);

      VFX.shockwave({
        rawScale: true,
        x: centerPt.x,
        y: centerPt.y,
        tilt: layout.tilt,
        fromRadius: 20 * s,
        toRadius: Math.max(layout.radius * 1.33, 88 * s),
        color: '#818cf8',
        coreColor: '#e2e8f0',
        lineWidth: 4.2 * s,
        double: true,
        duration: 640,
      });

      VFX.burst({
        x: centerPt.x,
        y: centerPt.y,
        radius: Math.max(layout.radius * 0.9, layout.halfW * 0.95),
        ring: true,
        tiltY: layout.tilt,
        count: 24,
        shape: 'smoke',
        colors: ['#4c1d95', '#312e81', '#475569'],
        size: [14 * s, 24 * s],
        speed: [16 * s, 48 * s],
        drag: 0.94,
        alpha: 0.38,
        life: [680, 1080],
        layer: 'ground',
      });

      VFX.burst({
        x: centerPt.x,
        y: centerPt.y,
        radius: Math.max(layout.radius * 0.96, layout.halfW),
        tiltY: layout.tilt,
        count: 28,
        shape: 'wisp',
        colors: ['#a78bfa', '#818cf8', '#bae6fd'],
        size: [2.6 * s, 4.8 * s],
        speed: [16 * s, 46 * s],
        vortex: 95 * s,
        vortexCenter: centerPt,
        gravity: -40 * s,
        drag: 0.95,
        alpha: 0.64,
        life: [580, 960],
        trail: Math.max(2, Math.round(4 * s)),
      });

      for (var i = 0; i < allyEls.length; i++) {
        (function (allyEl, idx) {
          VFX.after(idx * 45, function () {
            var ls = VFX.scale();
            var ac = VFX.clampStage(VFX.center(allyEl), 24 * ls, 20 * ls);
            VFX.motion(allyEl, 'dodge', { duration: 300 });
            VFX.hitFlash(allyEl, '#c4b5fd', 220);
            VFX.burst({
              x: ac.x,
              y: ac.y,
              radius: 24 * ls,
              count: 14,
              shape: 'wisp',
              colors: ['#e2e8f0', '#c4b5fd', '#7dd3fc'],
              size: [2.2 * ls, 4.0 * ls],
              speed: [26 * ls, 78 * ls],
              turbulence: 65 * ls,
              drag: 0.93,
              alpha: 0.72,
              life: [380, 640],
              trail: Math.max(2, Math.round(4 * ls)),
            });
          });
        })(allyEls[i], i);
      }

      VFX.floatText(casterEl, '迷踪阵 · 全队隐匿', 'shield');
    });

    return createFormationHandle(430, {
      style: 'illusion',
      rawScale: true,
      x: initLayout.safeFoot.x,
      y: initLayout.safeFoot.y,
      radius: initLayout.radius,
      tilt: initLayout.tilt,
      color: '#6353a3',
      secondaryColor: '#9fb4d4',
      rotationSpeed: 0.46,
      pulseSpeed: 1.6,
      alpha: 0.42,
      lineWidth: 1.45,
      emitIntervalMs: 95,
      resolveCircleMetrics: function () {
        var l = computeMizongLayout(teamSide, casterEl);
        return {
          x: l.safeFoot.x,
          y: l.safeFoot.y,
          radius: l.radius,
          tilt: l.tilt,
        };
      },
      onEmit: function (pos, vc, tickIdx, meta) {
        var layout = computeMizongLayout(teamSide, casterEl);
        var s = layout.s;
        var st = layout.st;
        var bounds = layout.bounds;
        var halfW = VFX.clamp(bounds.spanX * 0.5 + 46 * s, 44 * s, st.w * 0.24);
        var halfH = VFX.clamp(bounds.spanY * 0.5 + 32 * s, 28 * s, st.h * 0.42);
        var origin = meta.manualMoved ? VFX.clampStage(pos, 22 * s, 18 * s) : pos;

        // 在己方当前横/纵站位包围盒内均匀铺展幽紫幻雾 smoke / wisp
        var ang = Math.random() * TWO_PI;
        var radFrac = Math.sqrt(Math.random()) * 0.92;
        var mistPt = VFX.clampStage(
          {
            x: origin.x + Math.cos(ang) * halfW * radFrac,
            y: origin.y + Math.sin(ang) * halfH * radFrac,
          },
          14 * s,
          14 * s
        );

        if (tickIdx % 2 === 0) {
          VFX.burst({
            x: mistPt.x,
            y: mistPt.y,
            radius: 12 * s,
            count: 1,
            shape: 'smoke',
            colors: ['#4c1d95', '#312e81', '#3b3f6b'],
            size: [14 * s, 23 * s],
            speed: [8 * s, 22 * s],
            vortex: 48 * s,
            vortexCenter: vc,
            drag: 0.96,
            alpha: 0.28,
            life: [920, 1450],
            layer: 'ground',
          });
        }

        VFX.burst({
          x: mistPt.x,
          y: mistPt.y,
          radius: 8 * s,
          count: 2,
          shape: 'wisp',
          colors: ['#8b5cf6', '#a5b4fc', '#7dd3fc'],
          size: [2.4 * s, 4.4 * s],
          speed: [10 * s, 26 * s],
          vortex: 75 * s,
          vortexCenter: vc,
          turbulence: 44 * s,
          gravity: -16 * s,
          drag: 0.96,
          alpha: 0.54,
          life: [720, 1180],
        });

        // 忽明忽暗的迷离幻光随全队实测分布范围铺展
        var shimmerA = tickIdx % 3 === 0 ? 0.78 : 0.38;
        var shimmerPt = VFX.clampStage(
          {
            x: origin.x + VFX.rand(-halfW * 0.85, halfW * 0.85),
            y: origin.y + VFX.rand(-halfH * 0.85, halfH * 0.85),
          },
          14 * s,
          14 * s
        );
        VFX.burst({
          x: shimmerPt.x,
          y: shimmerPt.y,
          radius: 8 * s,
          count: 2,
          shape: tickIdx % 4 === 0 ? 'flare' : 'orb',
          colors: ['#c4b5fd', '#bae6fd', '#e2e8f0'],
          size: [1.4 * s, 2.8 * s],
          speed: [6 * s, 18 * s],
          gravity: -24 * s,
          turbulence: 34 * s,
          alpha: shimmerA,
          life: [580, 980],
        });
      },
      onPulse: function (pos, vc, col, intensity, meta) {
        var s = meta.s;
        VFX.burst({
          x: pos.x,
          y: pos.y,
          radius: meta.radius * 0.74,
          ring: true,
          tiltY: meta.tilt,
          count: 14,
          shape: 'wisp',
          colors: [col, '#a78bfa', '#bae6fd'],
          size: [2.6 * s, 4.6 * s],
          speed: [22 * s, 52 * s],
          vortex: 140 * s,
          vortexCenter: vc,
          gravity: -30 * s,
          drag: 0.95,
          alpha: 0.65,
          life: [520, 840],
        });
      },
    });
  });

  /**
   * 3. item.tianlei-formation-disk · 天雷阵盘（→ formation.tianlei）
   * 暗靛水波雷池底盘 + 高频 emitter 持续沿阵缘高速盘旋暗蓝碎电星子。
   */
  VFX.register('item.tianlei-formation-disk', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var init = resolvePersonalAnchors(casterEl);

    VFX.motion(casterEl, 'cast', { duration: 320 });

    VFX.artifact({
      kind: 'disk',
      x1: init.c.x,
      y1: init.c.y,
      x2: init.f.x,
      y2: init.f.y,
      arc: -70 * init.s,
      duration: 440,
      size: 25 * init.s,
      tiltFrom: 0.9,
      tiltTo: 0.44,
      spin: 6.4,
      color: '#38bdf8',
      accentColor: '#e0f2fe',
      glowColor: '#0284c7',
    });

    VFX.after(380, function () {
      var live = resolvePersonalAnchors(casterEl);
      var s = live.s;
      var f = live.f;

      VFX.quake('sm', 170);
      VFX.hitFlash(casterEl, '#7dd3fc', 220);

      VFX.shockwave({
        x: f.x,
        y: f.y,
        tilt: 0.44,
        fromRadius: 14,
        toRadius: 96,
        color: '#38bdf8',
        coreColor: '#e0f2fe',
        lineWidth: 3.6,
        double: true,
        duration: 440,
      });

      // 三道短促预兆小电弧环绕跃动
      for (var i = 0; i < 3; i++) {
        var a = (i / 3) * TWO_PI + 0.35;
        var edgePt = VFX.clampStage(
          {
            x: f.x + Math.cos(a) * 56 * s,
            y: f.y + Math.sin(a) * 56 * s * 0.44,
          },
          16 * s,
          16 * s
        );
        VFX.lightning({
          x1: edgePt.x,
          y1: Math.max(16, edgePt.y - 42 * s),
          x2: edgePt.x,
          y2: edgePt.y,
          jagged: 0.28,
          segments: 10,
          branches: 2,
          flicker: 2,
          color: '#38bdf8',
          secondaryColor: '#bae6fd',
          glowColor: '#1d4ed8',
          haloScale: 1.15,
          coreColor: '#e0f2fe',
          width: 2.7 * s,
          duration: 210,
          delay: i * 45,
        });
      }

      VFX.burst({
        x: f.x,
        y: f.y,
        radius: 36 * s,
        ring: true,
        tiltY: 0.44,
        count: 22,
        shape: 'spark',
        colors: ['#38bdf8', '#7dd3fc', '#818cf8', '#e0f2fe'],
        size: [1.8 * s, 3.8 * s],
        speed: [75 * s, 175 * s],
        gravity: -35 * s,
        drag: 0.91,
        life: [280, 500],
        trail: Math.max(2, Math.round(4 * s)),
      });

      VFX.floatText(casterEl, '天雷阵 · 护持', '#38bdf8');
    });

    return createFormationHandle(380, {
      style: 'water',
      anchorEl: casterEl,
      x: init.f.x,
      y: init.f.y,
      radius: 70,
      tilt: 0.44,
      color: '#2a5f91',
      secondaryColor: '#7b92c9',
      rotationSpeed: 0.88,
      pulseSpeed: 2.3,
      alpha: 0.46,
      lineWidth: 1.45,
      emitIntervalMs: 75,
      resolveAnchor: function () {
        return resolvePersonalAnchors(casterEl).f;
      },
      onEmit: function (pos, vc, tickIdx, meta) {
        var s = meta.s;
        // 沿阵盘盘旋暗蓝碎电星子
        var ang = Math.random() * TWO_PI;
        var rr = 70 * s * VFX.rand(0.56, 0.98);
        var px = pos.x + Math.cos(ang) * rr;
        var py = pos.y + Math.sin(ang) * rr * 0.44;
        var tang = ang - HALF_PI;

        VFX.burst({
          x: px,
          y: py,
          radius: 4 * s,
          count: 2,
          shape: 'spark',
          colors: ['#2563eb', '#38bdf8', '#818cf8', '#bae6fd'],
          size: [1.3 * s, 2.5 * s],
          angle: [tang - 0.35, tang + 0.35],
          speed: [32 * s, 68 * s],
          vortex: -310 * s,
          vortexCenter: vc,
          attractTo: pos,
          attractStrength: 210 * s,
          drag: 0.93,
          alpha: 0.84,
          life: [380, 620],
          trail: Math.max(2, Math.round(4 * s)),
        });

        if (tickIdx % 2 === 0) {
          VFX.burst({
            x: px,
            y: py,
            radius: 4 * s,
            count: 1,
            shape: 'wisp',
            colors: ['#1d4ed8', '#38bdf8', '#818cf8'],
            size: [1.8 * s, 3.0 * s],
            angle: [tang - 0.25, tang + 0.25],
            speed: [28 * s, 54 * s],
            vortex: -260 * s,
            vortexCenter: vc,
            attractTo: pos,
            attractStrength: 180 * s,
            drag: 0.94,
            alpha: 0.52,
            life: [400, 660],
          });
        }

        if (tickIdx % 3 === 0) {
          var flarePt = VFX.clampStage(
            {
              x: pos.x + VFX.rand(-48, 48) * s,
              y: pos.y + VFX.rand(-20, 20) * s,
            },
            14 * s,
            14 * s
          );
          VFX.burst({
            x: flarePt.x,
            y: flarePt.y,
            radius: 4 * s,
            count: 1,
            shape: 'flare',
            colors: ['#7dd3fc', '#c7d2fe'],
            size: [1.3 * s, 2.2 * s],
            speed: [10 * s, 28 * s],
            alpha: 0.78,
            life: [220, 400],
          });
        }
      },
      onPulse: function (pos, vc, col, intensity, meta) {
        var s = meta.s;
        VFX.burst({
          x: pos.x,
          y: pos.y,
          radius: 58 * s,
          ring: true,
          tiltY: 0.44,
          count: 14,
          shape: 'spark',
          colors: [col, '#7dd3fc', '#c7d2fe'],
          size: [1.8 * s, 3.4 * s],
          speed: [48 * s, 108 * s],
          vortex: -340 * s,
          vortexCenter: vc,
          drag: 0.92,
          alpha: 0.86,
          life: [320, 560],
          trail: Math.max(2, Math.round(5 * s)),
        });
      },
    });
  });

  /**
   * 4. item.xuesha-formation-disk · 血煞阵盘（→ formation.xuesha）
   * 低饱和暗赤柔弧底盘 + 持续翻涌暗红怨煞 wisp 与上升血珠微光。
   */
  VFX.register('item.xuesha-formation-disk', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var init = resolvePersonalAnchors(casterEl);

    VFX.motion(casterEl, 'cast', { duration: 340 });

    VFX.artifact({
      kind: 'disk',
      x1: init.c.x,
      y1: init.c.y,
      x2: init.f.x,
      y2: init.f.y,
      arc: -64 * init.s,
      duration: 460,
      size: 26 * init.s,
      tiltFrom: 0.9,
      tiltTo: 0.44,
      spin: -6.0,
      color: '#dc2626',
      accentColor: '#f59e0b',
      glowColor: '#991b1b',
    });

    VFX.after(400, function () {
      var live = resolvePersonalAnchors(casterEl);
      var s = live.s;
      var f = live.f;

      VFX.quake('sm', 180);
      VFX.hitFlash(casterEl, '#dc2626', 220);

      VFX.shockwave({
        x: f.x,
        y: f.y,
        tilt: 0.44,
        fromRadius: 12,
        toRadius: 98,
        color: '#dc2626',
        coreColor: '#fca5a5',
        lineWidth: 3.8,
        double: true,
        duration: 480,
      });

      VFX.burst({
        x: f.x,
        y: f.y,
        radius: 42 * s,
        tiltY: 0.44,
        count: 12,
        shape: 'smoke',
        colors: ['#7f1d1d', '#450a0a'],
        size: [12 * s, 22 * s],
        speed: [16 * s, 42 * s],
        drag: 0.94,
        alpha: 0.42,
        life: [580, 960],
        layer: 'ground',
      });

      VFX.burst({
        x: f.x,
        y: f.y,
        radius: 42 * s,
        ring: true,
        tiltY: 0.44,
        count: 22,
        shape: 'wisp',
        colors: ['#991b1b', '#dc2626', '#ef4444', '#fca5a5'],
        size: [2.8 * s, 5.4 * s],
        speed: [24 * s, 68 * s],
        vortex: -220 * s,
        vortexCenter: f,
        gravity: -48 * s,
        drag: 0.93,
        alpha: 0.72,
        life: [460, 780],
        trail: Math.max(2, Math.round(4 * s)),
      });

      VFX.floatText(casterEl, '血煞阵 · 噬命', '#fca5a5');
    });

    return createFormationHandle(400, {
      style: 'illusion',
      anchorEl: casterEl,
      x: init.f.x,
      y: init.f.y,
      radius: 70,
      tilt: 0.44,
      color: '#7f212b',
      secondaryColor: '#a65d57',
      rotationSpeed: -0.74,
      pulseSpeed: 1.8,
      alpha: 0.46,
      lineWidth: 1.5,
      emitIntervalMs: 80,
      resolveAnchor: function () {
        return resolvePersonalAnchors(casterEl).f;
      },
      onEmit: function (pos, vc, tickIdx, meta) {
        var s = meta.s;
        // 翻涌暗红怨煞 wisp
        var ang = Math.random() * TWO_PI;
        var rr = 70 * s * VFX.rand(0.28, 0.94);
        var px = pos.x + Math.cos(ang) * rr;
        var py = pos.y + Math.sin(ang) * rr * 0.44;
        var tang = ang - HALF_PI;

        VFX.burst({
          x: px,
          y: py,
          radius: 6 * s,
          count: 2,
          shape: 'wisp',
          colors: ['#7f1d1d', '#991b1b', '#dc2626'],
          size: [2.8 * s, 5.2 * s],
          angle: [tang - 0.4, tang + 0.4],
          speed: [16 * s, 38 * s],
          vortex: -185 * s,
          vortexCenter: vc,
          gravity: -28 * s,
          turbulence: 48 * s,
          drag: 0.95,
          alpha: 0.56,
          life: [620, 980],
        });

        // 血珠微光缓缓上渗
        var dropPt = VFX.clampStage(
          {
            x: pos.x + VFX.rand(-44, 44) * s,
            y: pos.y + VFX.rand(-16, 12) * s,
          },
          14 * s,
          14 * s
        );
        VFX.burst({
          x: dropPt.x,
          y: dropPt.y,
          radius: 4 * s,
          count: 2,
          shape: tickIdx % 3 === 0 ? 'drop' : 'orb',
          colors: ['#dc2626', '#ef4444', '#fca5a5', '#f59e0b'],
          size: [1.4 * s, 2.7 * s],
          angle: [-HALF_PI - 0.35, -HALF_PI + 0.35],
          speed: [18 * s, 42 * s],
          gravity: -38 * s,
          drag: 0.96,
          alpha: 0.78,
          life: [580, 920],
          trail: Math.max(1, Math.round(2 * s)),
        });

        if (tickIdx % 3 === 0) {
          var smokePt = VFX.clampStage(
            {
              x: pos.x + VFX.rand(-32, 32) * s,
              y: pos.y + VFX.rand(-10, 10) * s,
            },
            14 * s,
            14 * s
          );
          VFX.burst({
            x: smokePt.x,
            y: smokePt.y,
            radius: 8 * s,
            count: 1,
            shape: 'smoke',
            colors: ['#450a0a', '#7f1d1d'],
            size: [11 * s, 18 * s],
            speed: [8 * s, 22 * s],
            vortex: -95 * s,
            vortexCenter: vc,
            drag: 0.96,
            alpha: 0.3,
            life: [720, 1140],
            layer: 'ground',
          });
        }
      },
      onPulse: function (pos, vc, col, intensity, meta) {
        var s = meta.s;
        VFX.burst({
          x: pos.x,
          y: pos.y,
          radius: 52 * s,
          ring: true,
          tiltY: 0.44,
          count: 12,
          shape: 'wisp',
          colors: [col, '#dc2626', '#fca5a5'],
          size: [2.8 * s, 5.2 * s],
          speed: [22 * s, 56 * s],
          vortex: -240 * s,
          vortexCenter: vc,
          gravity: -55 * s,
          drag: 0.94,
          alpha: 0.7,
          life: [460, 760],
          trail: Math.max(2, Math.round(4 * s)),
        });
      },
    });
  });

  /**
   * 5. item.tianlei-formation-disk.proc · 天雷阵触发反击
   * 阵心电浆涌起（无硬脉冲柱），旋即沿真实 2D 向量劈出紫白掣电奔雷贯击目标。
   */
  VFX.register('item.tianlei-formation-disk.proc', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var victimEl = resolveTargetEl(ctx);
    var dmg = typeof ctx.damage === 'number' && ctx.damage > 0 ? Math.round(ctx.damage) : 64;

    function readProcAnchors() {
      var s = VFX.scale();
      var cf = VFX.clampStage(VFX.foot(casterEl), 28 * s, 22 * s);
      var cc = VFX.clampStage(VFX.center(casterEl), 28 * s, 22 * s);
      var tc = VFX.clampStage(VFX.center(victimEl), 28 * s, 22 * s);
      var surgePt = VFX.clampStage(
        {
          x: VFX.lerp(cf.x, cc.x, 0.35),
          y: cf.y - Math.max(18 * s, (cf.y - cc.y) * 0.55),
        },
        24 * s,
        20 * s
      );
      return { s: s, cf: cf, cc: cc, tc: tc, surgePt: surgePt };
    }

    var a0 = readProcAnchors();
    var s0 = a0.s;

    if (ctx.formationHandle && typeof ctx.formationHandle.pulse === 'function') {
      ctx.formationHandle.pulse('#a855f7', 1.35);
    }

    // 第一拍：阵心电浆涌起（柔紫电浆流雾 + 聚能星子，无硬光柱）
    VFX.shockwave({
      x: a0.cf.x,
      y: a0.cf.y,
      tilt: 0.44,
      fromRadius: 14,
      toRadius: 88,
      color: '#a855f7',
      coreColor: '#e0f2fe',
      lineWidth: 3.4,
      double: true,
      duration: 360,
    });

    VFX.burst({
      x: a0.cf.x,
      y: a0.cf.y,
      radius: 26 * s0,
      count: 18,
      shape: 'wisp',
      colors: ['#a855f7', '#38bdf8', '#e9d5ff', '#ffffff'],
      size: [2.4 * s0, 4.4 * s0],
      angle: [-HALF_PI - 0.5, -HALF_PI + 0.5],
      speed: [68 * s0, 155 * s0],
      vortex: -280 * s0,
      vortexCenter: a0.surgePt,
      gravity: -110 * s0,
      drag: 0.93,
      alpha: 0.84,
      life: [260, 440],
      trail: Math.max(2, Math.round(5 * s0)),
    });

    VFX.burst({
      x: a0.surgePt.x,
      y: a0.surgePt.y,
      radius: 14 * s0,
      count: 6,
      shape: 'flare',
      colors: ['#e9d5ff', '#ffffff', '#7dd3fc'],
      size: [2.0 * s0, 3.6 * s0],
      speed: [18 * s0, 52 * s0],
      alpha: 0.9,
      life: [240, 400],
    });

    // 第二拍：从最新阵主电浆起点沿真实 2D 向量劈向受击者中心（适配斜向/竖屏上下对位）
    VFX.after(65, function () {
      var a = readProcAnchors();
      var s = a.s;
      var sp = a.surgePt;
      var tc = a.tc;
      var dx = tc.x - sp.x;
      var dy = tc.y - sp.y;
      var dist = Math.hypot(dx, dy) || 1;
      var nx = -dy / dist;
      var ny = dx / dist;

      // 起点柔和电晕发射脉冲
      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 240,
        draw: function (g, p) {
          var fade = p < 0.18 ? p / 0.18 : Math.pow(1 - (p - 0.18) / 0.82, 1.4);
          var r = 46 * s * (0.65 + 0.35 * p);
          g.save();
          g.translate(sp.x, sp.y);
          var grad = g.createRadialGradient(0, 0, 1, 0, 0, r);
          grad.addColorStop(0, 'rgba(255, 255, 255,' + (0.72 * fade).toFixed(3) + ')');
          grad.addColorStop(0.35, 'rgba(192, 132, 252,' + (0.44 * fade).toFixed(3) + ')');
          grad.addColorStop(0.7, 'rgba(109, 40, 217,' + (0.18 * fade).toFixed(3) + ')');
          grad.addColorStop(1, 'rgba(109, 40, 217, 0)');
          g.fillStyle = grad;
          g.beginPath();
          g.arc(0, 0, r, 0, TWO_PI);
          g.fill();
          g.restore();
        },
      });

      // 主干紫白狂雷
      VFX.lightning({
        x1: sp.x,
        y1: sp.y,
        x2: tc.x,
        y2: tc.y,
        jagged: 0.26,
        segments: 16,
        branches: 4,
        flicker: 4,
        color: '#c084fc',
        secondaryColor: '#f5d0fe',
        glowColor: '#6d28d9',
        haloScale: 1.2,
        coreColor: '#ffffff',
        width: 5.2 * s,
        duration: 290,
      });

      // 伴随青白掣电副脉按 2D 垂直法线错开交缠
      VFX.lightning({
        x1: sp.x + nx * 8 * s,
        y1: sp.y + ny * 8 * s,
        x2: tc.x + nx * 5 * s,
        y2: tc.y + ny * 5 * s,
        jagged: 0.3,
        segments: 14,
        branches: 2,
        flicker: 3,
        color: '#38bdf8',
        secondaryColor: '#e0f2fe',
        glowColor: '#1d4ed8',
        haloScale: 1.1,
        coreColor: '#f3e8ff',
        width: 3.4 * s,
        duration: 260,
        delay: 25,
      });

      // 沿真实电浆弹道飞掠的电丝 wisp
      var midX = (sp.x + tc.x) * 0.5;
      var midY = (sp.y + tc.y) * 0.5;
      var boltAng = Math.atan2(dy, dx);
      VFX.burst({
        x: midX,
        y: midY,
        radius: 16 * s,
        count: 10,
        shape: 'wisp',
        colors: ['#e9d5ff', '#7dd3fc', '#ffffff'],
        size: [1.8 * s, 3.2 * s],
        angle: [boltAng - 0.3, boltAng + 0.3],
        speed: [90 * s, 190 * s],
        drag: 0.92,
        life: [220, 380],
        trail: Math.max(2, Math.round(4 * s)),
      });
    });

    // 第三拍：命中紫白电花爆闪（克制光强不刺眼）
    VFX.after(120, function () {
      var a = readProcAnchors();
      var s = a.s;
      var tc = a.tc;

      VFX.quake('sm', 160);
      VFX.impact(victimEl, { color: '#c084fc', duration: 220, distance: Math.max(6, Math.round(12 * s)) });

      // 命中柔和紫青电晕脉冲
      VFX.custom({
        layer: 'sky',
        blend: 'lighter',
        duration: 280,
        draw: function (g, p) {
          var fade = p < 0.14 ? p / 0.14 : Math.pow(1 - (p - 0.14) / 0.86, 1.45);
          var r = 64 * s * (0.62 + 0.38 * p);
          g.save();
          g.translate(tc.x, tc.y);
          var grad = g.createRadialGradient(0, 0, 1, 0, 0, r);
          grad.addColorStop(0, 'rgba(255, 255, 255,' + (0.74 * fade).toFixed(3) + ')');
          grad.addColorStop(0.32, 'rgba(192, 132, 252,' + (0.48 * fade).toFixed(3) + ')');
          grad.addColorStop(0.68, 'rgba(56, 189, 248,' + (0.22 * fade).toFixed(3) + ')');
          grad.addColorStop(1, 'rgba(29, 78, 216, 0)');
          g.fillStyle = grad;
          g.beginPath();
          g.arc(0, 0, r, 0, TWO_PI);
          g.fill();
          g.restore();
        },
      });

      VFX.shockwave({
        x: tc.x,
        y: tc.y,
        fromRadius: 10,
        toRadius: 78,
        color: '#c084fc',
        coreColor: '#ffffff',
        lineWidth: 3.6,
        double: true,
        duration: 340,
      });

      VFX.burst({
        x: tc.x,
        y: tc.y,
        radius: 14 * s,
        count: 8,
        shape: 'flare',
        colors: ['#ffffff', '#e9d5ff', '#7dd3fc'],
        size: [2.2 * s, 4.2 * s],
        speed: [35 * s, 110 * s],
        drag: 0.92,
        alpha: 0.92,
        life: [260, 440],
      });

      VFX.burst({
        x: tc.x,
        y: tc.y,
        radius: 14 * s,
        count: 24,
        shape: 'spark',
        colors: ['#c084fc', '#38bdf8', '#e9d5ff', '#ffffff'],
        size: [1.8 * s, 4.2 * s],
        speed: [90 * s, 215 * s],
        turbulence: 130 * s,
        drag: 0.91,
        life: [240, 480],
        trail: Math.max(2, Math.round(5 * s)),
      });

      VFX.floatText(victimEl, '-' + dmg + ' 天雷反噬', '#c084fc', { scale: 1.06 });
    });

    return 840;
  });

  /**
   * 6. item.xuesha-formation-disk.proc · 血煞阵触发吸血反击
   * 嗜血红煞从受击者奔涌回掠施法者/阵主的鲜活 attractTo 血色 wisp 汲取流（按 2D 法线错开弧道并随距离定速） + 回血柔金红星华。
   */
  VFX.register('item.xuesha-formation-disk.proc', function (ctx) {
    var casterEl = resolveCasterEl(ctx);
    var victimEl = resolveTargetEl(ctx);

    var dmg = typeof ctx.damage === 'number' && ctx.damage > 0 ? Math.round(ctx.damage) : 85;
    var healAmt =
      typeof ctx.lifesteal === 'number' && ctx.lifesteal > 0
        ? Math.round(ctx.lifesteal)
        : typeof ctx.heal === 'number' && ctx.heal > 0
          ? Math.round(ctx.heal)
          : dmg;

    function readSiphonAnchors() {
      var s = VFX.scale();
      var cc = VFX.clampStage(VFX.center(casterEl), 26 * s, 22 * s);
      var cf = VFX.clampStage(VFX.foot(casterEl), 26 * s, 22 * s);
      var tc = VFX.clampStage(VFX.center(victimEl), 26 * s, 22 * s);
      var dx = cc.x - tc.x;
      var dy = cc.y - tc.y;
      var dist = Math.hypot(dx, dy) || 1;
      var nx = -dy / dist;
      var ny = dx / dist;
      return {
        s: s,
        cc: cc,
        cf: cf,
        tc: tc,
        dx: dx,
        dy: dy,
        dist: dist,
        nx: nx,
        ny: ny,
        ang: Math.atan2(dy, dx),
      };
    }

    var a0 = readSiphonAnchors();
    var s0 = a0.s;

    // 第一拍：受击者体内血煞翻涌剥离（全流体血雾取代硬尖刺）
    VFX.impact(victimEl, { color: '#ef4444', duration: 220, distance: Math.max(5, Math.round(10 * s0)) });

    VFX.shockwave({
      x: a0.tc.x,
      y: a0.tc.y,
      fromRadius: 10,
      toRadius: 72,
      color: '#dc2626',
      coreColor: '#fecaca',
      lineWidth: 3.4,
      double: false,
      duration: 320,
    });

    VFX.burst({
      x: a0.tc.x,
      y: a0.tc.y,
      radius: 16 * s0,
      count: 20,
      shape: 'wisp',
      colors: ['#991b1b', '#dc2626', '#ef4444', '#fca5a5'],
      size: [2.8 * s0, 5.2 * s0],
      speed: [55 * s0, 145 * s0],
      turbulence: 75 * s0,
      drag: 0.92,
      alpha: 0.8,
      life: [320, 540],
      trail: Math.max(2, Math.round(4 * s0)),
    });

    VFX.burst({
      x: a0.tc.x,
      y: a0.tc.y,
      radius: 14 * s0,
      count: 16,
      shape: 'drop',
      colors: ['#dc2626', '#ef4444', '#991b1b', '#fecaca'],
      size: [2.2 * s0, 4.4 * s0],
      speed: [60 * s0, 155 * s0],
      gravity: 160 * s0,
      drag: 0.93,
      life: [300, 520],
      trail: Math.max(2, Math.round(3 * s0)),
    });

    VFX.floatText(victimEl, '-' + dmg + ' 血煞', '#f87171', { scale: 1.05 });

    // 第二拍：双股错弧虹吸丝带 + 4 波沿 2D 法线错道回流的血灵 wisp
    var arcMag1 = Math.min(78 * s0, Math.max(36 * s0, a0.dist * 0.24));
    var arcMag2 = Math.min(74 * s0, Math.max(34 * s0, a0.dist * 0.22));
    VFX.streamer({
      x1: a0.tc.x - a0.nx * 5 * s0,
      y1: a0.tc.y - a0.ny * 5 * s0,
      x2: a0.cc.x - a0.nx * 3 * s0,
      y2: a0.cc.y - a0.ny * 3 * s0,
      curveSide: -1,
      arc: -arcMag1,
      width: 7 * s0,
      tailLength: 0.48,
      wobble: 14 * s0,
      color: '#dc2626',
      coreColor: '#fecaca',
      sparkColors: ['#ef4444', '#fca5a5'],
      duration: 430,
      delay: 45,
    });

    VFX.streamer({
      x1: a0.tc.x + a0.nx * 6 * s0,
      y1: a0.tc.y + a0.ny * 6 * s0,
      x2: a0.cc.x + a0.nx * 4 * s0,
      y2: a0.cc.y + a0.ny * 4 * s0,
      curveSide: 1,
      arc: arcMag2,
      width: 6 * s0,
      tailLength: 0.44,
      wobble: 12 * s0,
      color: '#991b1b',
      coreColor: '#fee2e2',
      sparkColors: ['#dc2626', '#fca5a5'],
      duration: 450,
      delay: 85,
    });

    var siphonWaves = 4;
    for (var w = 0; w < siphonWaves; w++) {
      (function (idx) {
        VFX.after(40 + idx * 52, function () {
          var a = readSiphonAnchors();
          var s = a.s;
          var tc = a.tc;
          var cc = a.cc;
          var normalSign = idx % 2 === 0 ? -1 : 1;
          var staggerDist = (10 + Math.floor(idx / 2) * 8) * s;
          var spawnPt = VFX.clampStage(
            {
              x: tc.x + a.nx * normalSign * staggerDist,
              y: tc.y + a.ny * normalSign * staggerDist,
            },
            14 * s,
            14 * s
          );
          var oppSpawnPt = VFX.clampStage(
            {
              x: tc.x - a.nx * normalSign * staggerDist * 0.7,
              y: tc.y - a.ny * normalSign * staggerDist * 0.7,
            },
            14 * s,
            14 * s
          );
          var midCenter = {
            x: (spawnPt.x + cc.x) * 0.5 + a.nx * normalSign * Math.min(48 * s, a.dist * 0.14),
            y: (spawnPt.y + cc.y) * 0.5 + a.ny * normalSign * Math.min(48 * s, a.dist * 0.14),
          };
          var fanOffset = normalSign * 0.85;
          var initSpeedMin = VFX.clamp(a.dist * 1.15, 110 * s, 580 * s);
          var initSpeedMax = VFX.clamp(a.dist * 1.65, 195 * s, 960 * s);
          var pullStrength = Math.max(1150 * s, a.dist * 13.0);
          var vortexSpin = -normalSign * Math.max(180 * s, Math.min(420 * s, a.dist * 0.55));

          VFX.burst({
            x: spawnPt.x,
            y: spawnPt.y,
            radius: 16 * s,
            count: 9,
            shape: 'wisp',
            colors: ['#dc2626', '#ef4444', '#b91c1c', '#fca5a5'],
            size: [2.4 * s, 4.4 * s],
            angle: [a.ang + fanOffset - 0.42, a.ang + fanOffset + 0.42],
            speed: [initSpeedMin, initSpeedMax],
            vortex: vortexSpin,
            vortexCenter: midCenter,
            attractTo: cc,
            attractStrength: pullStrength,
            drag: 0.955,
            alpha: 0.86,
            life: [380, 580],
            trail: Math.max(3, Math.round(6 * s)),
          });

          VFX.burst({
            x: oppSpawnPt.x,
            y: oppSpawnPt.y,
            radius: 18 * s,
            count: 6,
            shape: 'orb',
            colors: ['#ef4444', '#fecaca', '#fde68a'],
            size: [1.8 * s, 3.4 * s],
            angle: [a.ang - fanOffset * 0.8 - 0.38, a.ang - fanOffset * 0.8 + 0.38],
            speed: [initSpeedMin * 0.85, initSpeedMax * 0.88],
            attractTo: cc,
            attractStrength: Math.max(1060 * s, a.dist * 12.4),
            drag: 0.955,
            life: [360, 560],
            trail: Math.max(2, Math.round(5 * s)),
          });
        });
      })(w);
    }

    // 第三拍：血灵汲取回掠抵达阵主最新心口位置，触发阵盘脉冲与回血柔金红星华
    VFX.after(410, function () {
      var a = readSiphonAnchors();
      var s = a.s;
      var cc = a.cc;
      var cf = a.cf;

      if (ctx.formationHandle && typeof ctx.formationHandle.pulse === 'function') {
        ctx.formationHandle.pulse('#ef4444', 1.35);
      }

      VFX.hitFlash(casterEl, '#fca5a5', 220);

      VFX.shockwave({
        x: cf.x,
        y: cf.y,
        tilt: 0.44,
        fromRadius: 14,
        toRadius: 88,
        color: '#ef4444',
        coreColor: '#fde68a',
        lineWidth: 3.4,
        double: true,
        duration: 420,
      });

      // 回血柔金红星华 flare
      VFX.burst({
        x: cc.x,
        y: cc.y,
        radius: 24 * s,
        count: 10,
        shape: 'flare',
        colors: ['#fde68a', '#fda4af', '#fca5a5', '#fef3c7'],
        size: [2.2 * s, 4.4 * s],
        speed: [22 * s, 68 * s],
        gravity: -42 * s,
        drag: 0.94,
        alpha: 0.92,
        life: [420, 720],
      });

      // 温润金红回血灵气上浮
      VFX.burst({
        x: cc.x,
        y: cc.y + 4 * s,
        radius: 22 * s,
        count: 16,
        shape: 'wisp',
        colors: ['#fbbf24', '#fca5a5', '#ef4444', '#bbf7d0'],
        size: [2.0 * s, 3.8 * s],
        speed: [26 * s, 76 * s],
        gravity: -78 * s,
        drag: 0.94,
        alpha: 0.72,
        life: [400, 680],
        trail: Math.max(2, Math.round(4 * s)),
      });

      VFX.burst({
        x: cc.x,
        y: cc.y,
        radius: 20 * s,
        count: 14,
        shape: 'orb',
        colors: ['#fde68a', '#fca5a5', '#64efaa'],
        size: [1.8 * s, 3.6 * s],
        speed: [28 * s, 72 * s],
        gravity: -85 * s,
        drag: 0.94,
        life: [380, 620],
        trail: Math.max(2, Math.round(3 * s)),
      });

      VFX.floatText(casterEl, '+' + healAmt + ' 噬血回春', 'heal', { scale: 1.08 });
    });

    return 1000;
  });
});
