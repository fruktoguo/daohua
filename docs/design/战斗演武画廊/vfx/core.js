/**
 * 道化太初 · 演武画廊 双层 High-DPI Canvas 2D 流体粒子与羽化光效引擎 (DaohuaVFX v2.2)
 *
 * 视觉重构铁律：
 * 1. 全量消除硬边几何块与突兀矩形：废除一维渐变 fillRect、硬描边圆环与梯形锥体填充。
 * 2. 四套 96px/128px 离屏高斯羽化纹理精灵缓存（边缘严格归零）：
 *    - glowSprite（径向辉光球）
 *    - plumeSprite（双向高斯柔雾羽流，专用于流体气柱、浪潮与 wisp 气旋粒子）
 *    - softRingSprite（像素级高斯分布柔光冲击波环，无钢笔线框感）
 *    - smokeSprite（多瓣交叠体积烟云）
 * 3. 新增 'wisp'（速度拉伸气体流雾）与 'flare'（柔边星芒闪烁）粒子形态，上限提升至 4500。
 * 4. v2.2 自适应布局坐标系：纯布局 offset 锚点免疫震屏与 WAAPI 偏移，内置 VFX.scale/clampStage/groupBounds/combatAxis 与 onResize。
 */
(function (global) {
  'use strict';

  var TWO_PI = Math.PI * 2;
  var HALF_PI = Math.PI * 0.5;
  var MAX_PARTICLES = 4500;

  // ---------------------------------------------------------------------------
  // 数学与缓动工具
  // ---------------------------------------------------------------------------
  function clamp(v, min, max) {
    return v < min ? min : v > max ? max : v;
  }

  function lerp(a, b, t) {
    return a + (b - a) * t;
  }

  function rand(min, max) {
    if (Array.isArray(min)) {
      max = min[1];
      min = min[0];
    }
    if (max === undefined) return min;
    return min + Math.random() * (max - min);
  }

  function pick(arr) {
    if (!arr || !arr.length) return '#ffffff';
    return arr[(Math.random() * arr.length) | 0];
  }

  function rangeVal(v, fallback) {
    if (v === undefined || v === null) return fallback;
    return Array.isArray(v) ? rand(v[0], v[1]) : Number(v);
  }

  function easeOutCubic(t) {
    return 1 - Math.pow(1 - t, 3);
  }

  function easeInOutQuad(t) {
    return t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
  }

  function easeOutQuart(t) {
    return 1 - Math.pow(1 - t, 4);
  }

  function dirBetween(a, b) {
    var dx = b.x - a.x;
    var dy = b.y - a.y;
    var dist = Math.hypot(dx, dy) || 1;
    return {
      dx: dx / dist,
      dy: dy / dist,
      dist: dist,
      angle: Math.atan2(dy, dx),
    };
  }

  function parseColorRgbTuple(hex) {
    if (typeof hex !== 'string') return [255, 255, 255];
    var s = hex.trim();
    if (s[0] === '#') {
      var h = s.slice(1);
      if (h.length === 3) {
        h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
      }
      if (h.length >= 6) {
        var r = parseInt(h.slice(0, 2), 16);
        var g = parseInt(h.slice(2, 4), 16);
        var b = parseInt(h.slice(4, 6), 16);
        return [isNaN(r) ? 255 : r, isNaN(g) ? 255 : g, isNaN(b) ? 255 : b];
      }
    }
    return [255, 255, 255];
  }

  function parseColorRgb(hex) {
    var t = parseColorRgbTuple(hex);
    return t[0] + ',' + t[1] + ',' + t[2];
  }

  function rgbaStr(color, alpha) {
    alpha = clamp(alpha, 0, 1);
    if (typeof color === 'string' && color.indexOf('#') === 0) {
      return 'rgba(' + parseColorRgb(color) + ',' + alpha.toFixed(3) + ')';
    }
    return color;
  }

  function toHexColor(r, g, b) {
    function ch(v) {
      var h = clamp(Math.round(v), 0, 255).toString(16);
      return h.length < 2 ? '0' + h : h;
    }
    return '#' + ch(r) + ch(g) + ch(b);
  }

  // 生成高明度内层电浆过渡色（向炽白线性插值）
  function tintTowardWhite(color, ratio) {
    var rgb = parseColorRgbTuple(color);
    var t = clamp(ratio !== undefined ? ratio : 0.48, 0, 1);
    return toHexColor(lerp(rgb[0], 255, t), lerp(rgb[1], 255, t), lerp(rgb[2], 255, t));
  }

  // 当未显式传入 glowColor 时，根据主色色相自动派生具有色温纵深感的外圈大气光晕色
  function deriveHaloColor(color) {
    var rgb = parseColorRgbTuple(color);
    var r = rgb[0];
    var g = rgb[1];
    var b = rgb[2];
    if (b > r + 35 && g >= 140) return '#6366f1';
    if (g > r + 40 && g >= b) return '#0ea5e9';
    if (r > 110 && b > 180) return '#4f46e5';
    if (r > 200 && g > 100 && b < 120) return '#ea580c';
    return toHexColor(r * 0.78, g * 0.78, Math.min(255, b * 0.78 + 24));
  }

  // ---------------------------------------------------------------------------
  // 离屏高斯羽化纹理缓存（所有半径严格收束于边界内，杜绝方形截断边）
  // ---------------------------------------------------------------------------
  var glowSpriteCache = Object.create(null);
  function getGlowSprite(color) {
    if (glowSpriteCache[color]) return glowSpriteCache[color];
    var c = document.createElement('canvas');
    c.width = 96;
    c.height = 96;
    var gctx = c.getContext('2d');
    var rgb = parseColorRgb(color);
    var grad = gctx.createRadialGradient(48, 48, 0, 48, 48, 45);
    grad.addColorStop(0, 'rgba(255,255,255,0.98)');
    grad.addColorStop(0.12, 'rgba(' + rgb + ',0.86)');
    grad.addColorStop(0.32, 'rgba(' + rgb + ',0.44)');
    grad.addColorStop(0.62, 'rgba(' + rgb + ',0.13)');
    grad.addColorStop(0.86, 'rgba(' + rgb + ',0.025)');
    grad.addColorStop(1, 'rgba(' + rgb + ',0)');
    gctx.fillStyle = grad;
    gctx.beginPath();
    gctx.arc(48, 48, 46, 0, TWO_PI);
    gctx.fill();
    glowSpriteCache[color] = c;
    return c;
  }

  var plumeSpriteCache = Object.create(null);
  function getPlumeSprite(color) {
    if (plumeSpriteCache[color]) return plumeSpriteCache[color];
    var c = document.createElement('canvas');
    c.width = 128;
    c.height = 128;
    var gctx = c.getContext('2d');
    var rgb = parseColorRgb(color);

    // 主体羽化气团
    var lobes = [
      { x: 64, y: 64, r: 58, peak: 0.72 },
      { x: 52, y: 58, r: 40, peak: 0.28 },
      { x: 76, y: 70, r: 38, peak: 0.26 },
    ];
    for (var i = 0; i < lobes.length; i++) {
      var lb = lobes[i];
      var grad = gctx.createRadialGradient(lb.x, lb.y, 1, lb.x, lb.y, lb.r);
      grad.addColorStop(0, 'rgba(' + rgb + ',' + lb.peak + ')');
      grad.addColorStop(0.35, 'rgba(' + rgb + ',' + (lb.peak * 0.45).toFixed(3) + ')');
      grad.addColorStop(0.68, 'rgba(' + rgb + ',' + (lb.peak * 0.12).toFixed(3) + ')');
      grad.addColorStop(1, 'rgba(' + rgb + ',0)');
      gctx.fillStyle = grad;
      gctx.beginPath();
      gctx.arc(lb.x, lb.y, lb.r, 0, TWO_PI);
      gctx.fill();
    }

    plumeSpriteCache[color] = c;
    return c;
  }

  var softRingSpriteCache = Object.create(null);
  function getSoftRingSprite(color) {
    if (softRingSpriteCache[color]) return softRingSpriteCache[color];
    var size = 128;
    var half = 64;
    var c = document.createElement('canvas');
    c.width = size;
    c.height = size;
    var gctx = c.getContext('2d');
    var img = gctx.createImageData(size, size);
    var data = img.data;
    var rgb = parseColorRgbTuple(color);

    for (var py = 0; py < size; py++) {
      var dy = py - half;
      for (var px = 0; px < size; px++) {
        var dx = px - half;
        var normR = Math.hypot(dx, dy) / 60;
        if (normR >= 0.98) continue;

        var dHalo = (normR - 0.72) / 0.15;
        var dCore = (normR - 0.72) / 0.048;
        var halo = Math.exp(-dHalo * dHalo) * 0.72;
        var core = Math.exp(-dCore * dCore) * 0.92;
        // 外边缘与内圈柔和收尾至 0
        var edgeMask = normR > 0.88 ? Math.max(0, 1 - (normR - 0.88) / 0.1) : 1;
        var a = clamp((halo + core * 0.75) * edgeMask, 0, 1);
        if (a <= 0.002) continue;

        var mixWhite = clamp(core * 0.65, 0, 0.82);
        var idx = (py * size + px) * 4;
        data[idx] = Math.round(lerp(rgb[0], 255, mixWhite));
        data[idx + 1] = Math.round(lerp(rgb[1], 255, mixWhite));
        data[idx + 2] = Math.round(lerp(rgb[2], 255, mixWhite));
        data[idx + 3] = Math.round(a * 255);
      }
    }
    gctx.putImageData(img, 0, 0);
    softRingSpriteCache[color] = c;
    return c;
  }

  var smokeSpriteCache = Object.create(null);
  function getSmokeSprite(color) {
    if (smokeSpriteCache[color]) return smokeSpriteCache[color];
    var c = document.createElement('canvas');
    c.width = 128;
    c.height = 128;
    var gctx = c.getContext('2d');
    var rgb = parseColorRgb(color);
    var puffs = [
      { x: 64, y: 64, r: 56, a: 0.44 },
      { x: 50, y: 54, r: 38, a: 0.26 },
      { x: 78, y: 58, r: 36, a: 0.24 },
      { x: 60, y: 78, r: 36, a: 0.22 },
    ];
    for (var i = 0; i < puffs.length; i++) {
      var pf = puffs[i];
      var grad = gctx.createRadialGradient(pf.x, pf.y, 2, pf.x, pf.y, pf.r);
      grad.addColorStop(0, 'rgba(' + rgb + ',' + pf.a + ')');
      grad.addColorStop(0.45, 'rgba(' + rgb + ',' + (pf.a * 0.48).toFixed(3) + ')');
      grad.addColorStop(0.8, 'rgba(' + rgb + ',' + (pf.a * 0.1).toFixed(3) + ')');
      grad.addColorStop(1, 'rgba(' + rgb + ',0)');
      gctx.fillStyle = grad;
      gctx.beginPath();
      gctx.arc(pf.x, pf.y, pf.r, 0, TWO_PI);
      gctx.fill();
    }
    smokeSpriteCache[color] = c;
    return c;
  }

  // ---------------------------------------------------------------------------
  // 引擎状态与生命周期管理
  // ---------------------------------------------------------------------------
  var state = {
    hostMode: 'gallery',
    stageEl: null,
    fieldEl: null,
    skyOverlayEl: null,
    groundOverlayEl: null,
    skyCanvas: null,
    groundCanvas: null,
    skyCtx: null,
    groundCtx: null,
    floatLayerEl: null,
    width: 960,
    height: 440,
    dpr: 1,
    rafId: 0,
    lastTime: 0,
    resizeObserver: null,

    particles: [],
    effects: [],
    persistentCircles: [],
    dims: [],
    shakes: [],
    timers: new Set(),
    resizeListeners: new Set(),
    resizeRafId: 0,
    camX: 0,
    camY: 0,

    registry: Object.create(null),
  };

  function isProductionHidden() {
    return (
      state.hostMode === 'production' &&
      typeof document !== 'undefined' &&
      document.visibilityState === 'hidden'
    );
  }

  function ensureLoop() {
    if (state.rafId || isProductionHidden()) return;
    if (!state.skyCtx && !state.groundCtx) return;
    state.lastTime = performance.now();
    state.rafId = requestAnimationFrame(tick);
  }

  function hasActiveVisuals() {
    return (
      state.particles.length > 0 ||
      state.effects.length > 0 ||
      state.persistentCircles.length > 0 ||
      state.dims.length > 0 ||
      state.shakes.length > 0
    );
  }

  function notifyResizeListeners() {
    for (var i = 0; i < state.persistentCircles.length; i++) {
      syncPersistentCircleAnchor(state.persistentCircles[i]);
    }
    state.resizeListeners.forEach(function (fn) {
      try {
        fn({ width: state.width, height: state.height, scale: scale() });
      } catch (err) {
        console.error('[DaohuaVFX resize listener]', err);
      }
    });
  }

  function resize(notify) {
    if (!state.stageEl) return;
    var rectObj = state.stageEl.getBoundingClientRect();
    var w = Math.max(240, Math.round(rectObj.width || state.stageEl.clientWidth || 960));
    var h = Math.max(180, Math.round(rectObj.height || state.stageEl.clientHeight || 440));
    var dpr = clamp(global.devicePixelRatio || 1, 1, 2.25);

    state.width = w;
    state.height = h;
    state.dpr = dpr;

    [state.groundCanvas, state.skyCanvas].forEach(function (cv) {
      if (!cv) return;
      cv.width = Math.round(w * dpr);
      cv.height = Math.round(h * dpr);
      cv.style.width = w + 'px';
      cv.style.height = h + 'px';
    });

    if (notify) {
      notifyResizeListeners();
    }
  }

  function onResize(fn) {
    if (typeof fn !== 'function') return function () {};
    state.resizeListeners.add(fn);
    return function () {
      state.resizeListeners.delete(fn);
    };
  }

  function createLayerCanvas(container, role) {
    container.innerHTML = '';
    var cv = document.createElement('canvas');
    cv.className = 'vfx-canvas vfx-canvas--' + role;
    cv.style.position = 'absolute';
    cv.style.inset = '0';
    cv.style.width = '100%';
    cv.style.height = '100%';
    cv.style.pointerEvents = 'none';
    cv.style.display = 'block';
    container.appendChild(cv);
    return cv;
  }

  function unmount(expectedStageEl) {
    if (expectedStageEl && state.stageEl && state.stageEl !== expectedStageEl) {
      return;
    }
    clear();
    if (state.resizeObserver) {
      state.resizeObserver.disconnect();
      state.resizeObserver = null;
    }
    if (state.resizeRafId) {
      cancelAnimationFrame(state.resizeRafId);
      state.resizeRafId = 0;
    }
    if (state.rafId) {
      cancelAnimationFrame(state.rafId);
      state.rafId = 0;
    }
    state.resizeListeners.clear();
    if (state.groundCanvas && state.groundCanvas.parentNode) {
      state.groundCanvas.parentNode.removeChild(state.groundCanvas);
    }
    if (state.skyCanvas && state.skyCanvas.parentNode) {
      state.skyCanvas.parentNode.removeChild(state.skyCanvas);
    }
    if (state.floatLayerEl && state.floatLayerEl.parentNode) {
      state.floatLayerEl.parentNode.removeChild(state.floatLayerEl);
    }
    state.stageEl = null;
    state.fieldEl = null;
    state.skyOverlayEl = null;
    state.groundOverlayEl = null;
    state.skyCanvas = null;
    state.groundCanvas = null;
    state.skyCtx = null;
    state.groundCtx = null;
    state.floatLayerEl = null;
    state.hostMode = 'gallery';
  }

  function mount(stageEl, skyOverlayEl, groundOverlayEl, opts) {
    // 兼容 React StrictMode 重挂载：先清理上一个宿主绑定
    if (state.stageEl || state.skyCanvas || state.groundCanvas || state.resizeObserver) {
      unmount();
    }
    state.hostMode = opts && opts.mode === 'production' ? 'production' : 'gallery';
    state.stageEl = stageEl || null;
    state.skyOverlayEl = skyOverlayEl || null;
    state.groundOverlayEl = groundOverlayEl || null;
    state.fieldEl = stageEl ? stageEl.querySelector('#arenaField') || stageEl.querySelector('.combat-arena-field') : null;

    if (state.groundOverlayEl) {
      state.groundCanvas = createLayerCanvas(state.groundOverlayEl, 'ground');
      state.groundCtx = state.groundCanvas.getContext('2d');
    }

    if (state.skyOverlayEl) {
      state.skyCanvas = createLayerCanvas(state.skyOverlayEl, 'sky');
      state.skyCtx = state.skyCanvas.getContext('2d');

      if (state.hostMode !== 'production') {
        var floatLayer = document.createElement('div');
        floatLayer.className = 'vfx-float-layer';
        floatLayer.style.position = 'absolute';
        floatLayer.style.inset = '0';
        floatLayer.style.pointerEvents = 'none';
        floatLayer.style.overflow = 'hidden';
        state.skyOverlayEl.appendChild(floatLayer);
        state.floatLayerEl = floatLayer;
      }
    }

    resize();

    if (typeof ResizeObserver !== 'undefined' && state.stageEl) {
      state.resizeObserver = new ResizeObserver(function () {
        resize(true);
        if (state.resizeRafId) cancelAnimationFrame(state.resizeRafId);
        // 等待 CSS Grid / Flex 重排稳定后次帧再次校对画布尺寸与常驻锚点
        state.resizeRafId = requestAnimationFrame(function () {
          state.resizeRafId = 0;
          resize(true);
        });
      });
      state.resizeObserver.observe(state.stageEl);
    }
  }

  function after(delayMs, fn) {
    if (isProductionHidden()) {
      return { cancel: function () {} };
    }
    if (!delayMs || delayMs <= 0) {
      fn();
      return { cancel: function () {} };
    }
    var token = setTimeout(function () {
      state.timers.delete(token);
      if (isProductionHidden()) return;
      fn();
    }, delayMs);
    state.timers.add(token);
    return {
      cancel: function () {
        clearTimeout(token);
        state.timers.delete(token);
      },
    };
  }

  function clear() {
    if (state.rafId) {
      cancelAnimationFrame(state.rafId);
      state.rafId = 0;
    }
    if (state.resizeRafId) {
      cancelAnimationFrame(state.resizeRafId);
      state.resizeRafId = 0;
    }
    state.timers.forEach(function (id) {
      clearTimeout(id);
    });
    state.timers.clear();

    state.particles.length = 0;
    state.effects.length = 0;
    state.persistentCircles.length = 0;
    state.dims.length = 0;
    state.shakes.length = 0;
    state.camX = 0;
    state.camY = 0;

    if (state.fieldEl) {
      state.fieldEl.style.transform = '';
    }
    if (state.floatLayerEl) {
      state.floatLayerEl.innerHTML = '';
    }
    if (state.stageEl && state.hostMode !== 'production') {
      var units = state.stageEl.querySelectorAll('.arena-unit');
      for (var i = 0; i < units.length; i++) {
        units[i].classList.remove('is-defeated');
      }
    }
    clearCanvases();
  }

  function clearCanvases() {
    var w = state.width;
    var h = state.height;
    if (state.groundCtx) {
      state.groundCtx.setTransform(state.dpr, 0, 0, state.dpr, 0, 0);
      state.groundCtx.clearRect(0, 0, w, h);
    }
    if (state.skyCtx) {
      state.skyCtx.setTransform(state.dpr, 0, 0, state.dpr, 0, 0);
      state.skyCtx.clearRect(0, 0, w, h);
    }
  }

  // ---------------------------------------------------------------------------
  // 坐标与几何锚点
  // ---------------------------------------------------------------------------
  function arenaCenter() {
    return { x: state.width * 0.5, y: state.height * 0.52 };
  }

  function isUnitDead(el) {
    if (!el || !el.classList) return true;
    if (
      el.classList.contains('is-defeated') ||
      el.classList.contains('is-dead') ||
      el.classList.contains('is-dormant')
    ) {
      return true;
    }
    if (el.closest && (el.closest('.arena-slot--slain') || el.closest('.arena-slot--corpse'))) {
      return true;
    }
    return false;
  }

  function queryUnitsBySide(side, aliveOnly) {
    if (!state.stageEl) return [];
    var roleCls = side === 'enemy' ? '.arena-actor--enemy' : '.arena-actor--player';
    var raw = state.stageEl.querySelectorAll(
      '.arena-unit.is-' + side + ', .arena-actor.is-' + side + ', ' + roleCls
    );
    var seen = [];
    for (var i = 0; i < raw.length; i++) {
      var node = raw[i];
      if (seen.indexOf(node) >= 0) continue;
      if (aliveOnly && isUnitDead(node)) continue;
      seen.push(node);
    }
    return seen;
  }

  function getVisualTargetEl(el) {
    if (!el || !el.querySelector) return el;
    return el.querySelector('.arena-unit-avatar, .arena-avatar-stage') || el;
  }

  /**
   * 沿 offsetParent 链累加 el 相对指定祖先容器 boundaryEl（缺省为 stageEl）的静态布局矩形，
   * 天然免疫 CSS transform（震屏 translate3d 与 WAAPI 动作位移）。
   */
  function layoutOffsetRect(el, boundaryEl) {
    var root = boundaryEl || state.stageEl;
    if (!el || !root || !root.contains(el)) return null;
    var cur = el;
    var lx = 0;
    var ly = 0;
    var reached = false;
    while (cur) {
      lx += cur.offsetLeft || 0;
      ly += cur.offsetTop || 0;
      var op = cur.offsetParent;
      if (op && op !== root) {
        lx += op.clientLeft || 0;
        ly += op.clientTop || 0;
      }
      if (op === root) {
        reached = true;
        break;
      }
      if (!op || op === document.body || op === document.documentElement) break;
      cur = op;
    }
    if (!reached) return null;
    var sp = el.parentElement;
    while (sp && sp !== root) {
      lx -= sp.scrollLeft || 0;
      ly -= sp.scrollTop || 0;
      sp = sp.parentElement;
    }
    var ow = el.offsetWidth;
    var oh = el.offsetHeight;
    if (!ow && !oh) return null;
    return {
      x: lx,
      y: ly,
      w: ow,
      h: oh,
      cx: lx + ow * 0.5,
      cy: ly + oh * 0.5,
      left: lx,
      right: lx + ow,
      top: ly,
      bottom: ly + oh,
    };
  }

  function rect(el) {
    if (!el || !state.stageEl) return null;
    if (state.stageEl.contains(el)) {
      // 正式前端中 .arena-actor-card 桌面端采用 translate(-50%, -50%) scale(var(--arena-scale))，
      // 竖屏退化为 position: static; transform: none；先取不受动画干扰的 .arena-actor 布局框再做按需换算
      if (el.closest) {
        var actorEl = el.closest('.arena-actor');
        var cardEl = el.closest('.arena-actor-card');
        if (actorEl && cardEl && el !== actorEl && el !== cardEl) {
          var actorRect = layoutOffsetRect(actorEl, state.stageEl);
          var innerInCard = layoutOffsetRect(el, cardEl);
          var cardW = cardEl.offsetWidth || 0;
          var cardH = cardEl.offsetHeight || 0;
          if (actorRect && innerInCard && cardW > 0 && cardH > 0) {
            var sFactor = actorRect.w / cardW;
            var rx = actorRect.cx + (innerInCard.x - cardW * 0.5) * sFactor;
            var ry = actorRect.cy + (innerInCard.y - cardH * 0.5) * sFactor;
            var rw = innerInCard.w * sFactor;
            var rh = innerInCard.h * sFactor;
            return {
              x: rx,
              y: ry,
              w: rw,
              h: rh,
              cx: rx + rw * 0.5,
              cy: ry + rh * 0.5,
              left: rx,
              right: rx + rw,
              top: ry,
              bottom: ry + rh,
            };
          }
        }
      }

      var lr = layoutOffsetRect(el, state.stageEl);
      if (lr) return lr;
    }
    if (!el.getBoundingClientRect) return null;
    var sr = state.stageEl.getBoundingClientRect();
    var er = el.getBoundingClientRect();
    if (!er.width && !er.height) return null;
    var gx = er.left - sr.left - (state.fieldEl && state.fieldEl.contains(el) ? state.camX : 0);
    var gy = er.top - sr.top - (state.fieldEl && state.fieldEl.contains(el) ? state.camY : 0);
    return {
      x: gx,
      y: gy,
      w: er.width,
      h: er.height,
      cx: gx + er.width * 0.5,
      cy: gy + er.height * 0.5,
      left: gx,
      right: gx + er.width,
      top: gy,
      bottom: gy + er.height,
    };
  }

  function scale() {
    var normH = 104;
    if (state.stageEl) {
      var av =
        state.stageEl.querySelector(
          '.arena-unit:not(.is-defeated) .arena-unit-avatar, .arena-actor:not(.is-dead):not(.is-dormant) .arena-avatar-stage'
        ) || state.stageEl.querySelector('.arena-unit-avatar, .arena-avatar-stage');
      if (av) {
        var ar = rect(av);
        if (ar && ar.h > 10) {
          // 正式前端 .arena-avatar-stage 基础高为 120px（画廊为 104px），按同一比例映射到 scale 区间
          var baseRef = av.classList && av.classList.contains('arena-avatar-stage') ? 116 : 104;
          normH = (ar.h / baseRef) * 104;
        }
      }
    }
    return clamp(
      Math.min(normH / 104, Math.max(0.62, Math.min(state.width / 820, state.height / 360))),
      0.58,
      1.08
    );
  }

  function clampStage(pt, padX, padY) {
    var px = clamp(padX !== undefined ? padX : 24, 0, Math.max(0, state.width * 0.45));
    var py = clamp(padY !== undefined ? padY : 24, 0, Math.max(0, state.height * 0.45));
    var bx = pt && typeof pt.x === 'number' ? pt.x : state.width * 0.5;
    var by = pt && typeof pt.y === 'number' ? pt.y : state.height * 0.5;
    return {
      x: clamp(bx, px, state.width - px),
      y: clamp(by, py, state.height - py),
    };
  }

  function center(el) {
    var target = getVisualTargetEl(el);
    var r = rect(target);
    if (!r) return arenaCenter();
    return { x: r.cx, y: r.cy };
  }

  function cardCenter(el) {
    var r = rect(el);
    if (!r) return arenaCenter();
    return { x: r.cx, y: r.cy };
  }

  function foot(el) {
    var avatar = getVisualTargetEl(el);
    var ar = rect(avatar);
    var cr = rect(el);
    if (ar && cr) {
      return {
        x: ar.cx,
        y: clamp(ar.bottom + 8, cr.top + cr.h * 0.52, cr.bottom - 6),
      };
    }
    if (ar) return { x: ar.cx, y: ar.bottom + 6 };
    return { x: state.width * 0.5, y: state.height * 0.68 };
  }

  function head(el) {
    var avatar = getVisualTargetEl(el);
    var ar = rect(avatar);
    if (ar) return { x: ar.cx, y: ar.top + 6 };
    return { x: state.width * 0.5, y: state.height * 0.3 };
  }

  function sideCenter(side) {
    if (state.stageEl) {
      var list = queryUnitsBySide(side, true);
      if (!list.length) {
        list = queryUnitsBySide(side, false);
      }
      if (list.length) {
        var sx = 0;
        var sy = 0;
        var count = 0;
        for (var i = 0; i < list.length; i++) {
          var p = center(list[i]);
          sx += p.x;
          sy += p.y;
          count++;
        }
        if (count > 0) return { x: sx / count, y: sy / count };
      }
    }
    return {
      x: state.width * (side === 'enemy' ? 0.74 : 0.26),
      y: state.height * 0.5,
    };
  }

  function sideFootCenter(side) {
    if (state.stageEl) {
      var list = queryUnitsBySide(side, true);
      if (!list.length) {
        list = queryUnitsBySide(side, false);
      }
      if (list.length) {
        var sx = 0;
        var sy = 0;
        var count = 0;
        for (var i = 0; i < list.length; i++) {
          var p = foot(list[i]);
          sx += p.x;
          sy += p.y;
          count++;
        }
        if (count > 0) return { x: sx / count, y: sy / count };
      }
    }
    return {
      x: state.width * (side === 'enemy' ? 0.74 : 0.26),
      y: state.height * 0.64,
    };
  }

  function resolveGroupElements(elsOrSide) {
    if (typeof elsOrSide === 'string' && state.stageEl) {
      var live = queryUnitsBySide(elsOrSide, true);
      if (!live.length) live = queryUnitsBySide(elsOrSide, false);
      return live;
    }
    if (elsOrSide && typeof elsOrSide.length === 'number') {
      var out = [];
      for (var i = 0; i < elsOrSide.length; i++) {
        if (elsOrSide[i]) out.push(elsOrSide[i]);
      }
      return out;
    }
    if (elsOrSide && elsOrSide.nodeType === 1) return [elsOrSide];
    return [];
  }

  function groupBounds(elsOrSide) {
    var s = scale();
    var els = resolveGroupElements(elsOrSide);
    var sideHint = typeof elsOrSide === 'string' ? elsOrSide : 'enemy';
    if (!els.length) {
      var sc = sideCenter(sideHint);
      var sf = sideFootCenter(sideHint);
      return {
        cx: sc.x,
        cy: sc.y,
        footX: sf.x,
        footY: sf.y,
        bottomFootY: sf.y,
        topHeadY: sc.y - 38 * s,
        minX: sc.x - 45 * s,
        maxX: sc.x + 45 * s,
        minY: sc.y - 38 * s,
        maxY: sc.y + 38 * s,
        spanX: 90 * s,
        spanY: 76 * s,
        majorSpan: 90 * s,
        axisAngle: 0,
        skyY: clamp(sc.y - 106 * s, 24, Math.max(28, sc.y - 42)),
        count: 0,
      };
    }

    var minX = Infinity;
    var maxX = -Infinity;
    var minY = Infinity;
    var maxY = -Infinity;
    var topHeadY = Infinity;
    var bottomFootY = -Infinity;
    var sumX = 0;
    var sumY = 0;
    var sumFX = 0;
    var sumFY = 0;
    var minXPt = null;
    var maxXPt = null;
    var minYPt = null;
    var maxYPt = null;

    for (var i = 0; i < els.length; i++) {
      var c = center(els[i]);
      var h = head(els[i]);
      var f = foot(els[i]);
      sumX += c.x;
      sumY += c.y;
      sumFX += f.x;
      sumFY += f.y;
      if (c.x < minX) {
        minX = c.x;
        minXPt = c;
      }
      if (c.x > maxX) {
        maxX = c.x;
        maxXPt = c;
      }
      if (c.y < minY) {
        minY = c.y;
        minYPt = c;
      }
      if (c.y > maxY) {
        maxY = c.y;
        maxYPt = c;
      }
      if (h.y < topHeadY) topHeadY = h.y;
      if (f.y > bottomFootY) bottomFootY = f.y;
    }

    var n = els.length;
    var spanX = maxX - minX;
    var spanY = maxY - minY;
    var cx = sumX / n;
    var cy = sumY / n;
    var axisAngle = 0;
    if (n > 1) {
      var aPt = spanY > spanX ? minYPt : minXPt;
      var bPt = spanY > spanX ? maxYPt : maxXPt;
      if (aPt && bPt) axisAngle = Math.atan2(bPt.y - aPt.y, bPt.x - aPt.x);
    }

    return {
      cx: cx,
      cy: cy,
      footX: sumFX / n,
      footY: sumFY / n,
      bottomFootY: bottomFootY,
      topHeadY: topHeadY,
      minX: minX,
      maxX: maxX,
      minY: minY,
      maxY: maxY,
      spanX: spanX,
      spanY: spanY,
      majorSpan: Math.max(spanX, spanY),
      axisAngle: axisAngle,
      skyY: clamp(topHeadY - 68 * s, 24, Math.max(28, cy - 42)),
      count: n,
    };
  }

  function combatAxis(casterEl, targetsOrSide) {
    var isEnemyCaster = !!(casterEl && casterEl.classList && (
      casterEl.classList.contains('is-enemy') ||
      casterEl.classList.contains('arena-actor--enemy')
    ));
    var defaultFoe = isEnemyCaster ? 'ally' : 'enemy';
    var start = casterEl
      ? center(casterEl)
      : sideCenter(targetsOrSide === 'ally' ? 'enemy' : 'ally');
    var end;
    if (typeof targetsOrSide === 'string') {
      end = sideCenter(targetsOrSide);
    } else if (targetsOrSide && typeof targetsOrSide.x === 'number' && typeof targetsOrSide.y === 'number') {
      end = { x: targetsOrSide.x, y: targetsOrSide.y };
    } else if (targetsOrSide && typeof targetsOrSide.length === 'number') {
      if (targetsOrSide.length > 0) {
        var gb = groupBounds(targetsOrSide);
        end = { x: gb.cx, y: gb.cy };
      } else {
        end = sideCenter(defaultFoe);
      }
    } else if (targetsOrSide) {
      end = center(targetsOrSide);
    } else {
      end = sideCenter(defaultFoe);
    }

    var dx = end.x - start.x;
    var dy = end.y - start.y;
    var rawDist = Math.hypot(dx, dy);
    var ux;
    var uy;
    var facingX;
    if (rawDist < 0.001) {
      facingX = isEnemyCaster ? -1 : 1;
      ux = facingX;
      uy = 0;
    } else {
      ux = dx / rawDist;
      uy = dy / rawDist;
      facingX = Math.abs(dx) < 2 ? (isEnemyCaster ? -1 : 1) : (dx >= 0 ? 1 : -1);
    }
    var nx = -uy;
    var ny = ux;
    var angle = Math.atan2(uy, ux);
    var dist = rawDist || 1;
    return {
      start: start,
      end: end,
      dx: dx,
      dy: dy,
      ux: ux,
      uy: uy,
      nx: nx,
      ny: ny,
      dist: dist,
      angle: angle,
      facingX: facingX,
      palm: function (offsetPx) {
        var off = offsetPx !== undefined ? offsetPx : 24 * scale();
        return { x: start.x + ux * off, y: start.y + uy * off };
      },
    };
  }

  // ---------------------------------------------------------------------------
  // 镜头震动、电影暗幕、角色受击与浮字
  // ---------------------------------------------------------------------------
  var QUAKE_MAP = { sm: 3.5, md: 7, lg: 12, xl: 18 };

  function quake(intensity, duration) {
    var px = typeof intensity === 'number' ? intensity : QUAKE_MAP[intensity] || 7;
    if (state.hostMode === 'production') {
      px *= 0.62;
    }
    state.shakes.push({
      amp: px,
      duration: duration || 260,
      elapsed: 0,
      seed: Math.random() * 100,
    });
    ensureLoop();
  }

  function cinematicDim(opacity, duration, color) {
    state.dims.push({
      opacity: opacity === undefined ? 0.36 : opacity,
      duration: duration || 650,
      elapsed: 0,
      color: color || '#04060d',
    });
    ensureLoop();
  }

  function motion(el, kind, opts) {
    if (!el || !el.animate) return;
    opts = opts || {};
    var isEnemy = el.classList && (el.classList.contains('is-enemy') || el.classList.contains('arena-actor--enemy'));
    var hasVec = opts.dx !== undefined || opts.dy !== undefined;
    var d = opts.dir !== undefined ? opts.dir : isEnemy ? -1 : 1;
    var vxU = hasVec ? Number(opts.dx || 0) : d;
    var vyU = hasVec ? Number(opts.dy || 0) : 0;
    if (hasVec) {
      var vLen = Math.hypot(vxU, vyU);
      if (vLen > 0.001) {
        vxU /= vLen;
        vyU /= vLen;
      }
      d = vxU >= 0 ? 1 : -1;
    }
    var dur = opts.duration || 260;
    var sMul = clamp(scale(), 0.62, 1.05);
    // 正式前端中 ArenaActor 已具备基于事件的 CSS 突进/受击位移动画，生产模式下降低叠加幅度
    var prodMul = state.hostMode === 'production' ? 0.32 : 1;
    var prodCastMul = state.hostMode === 'production' ? 0.45 : 1;

    if (kind === 'lunge') {
      var dist = (opts.distance || 26) * sMul * prodMul;
      el.animate(
        [
          { transform: 'translate3d(0,0,0) scale(1)' },
          { transform: 'translate3d(' + (vxU * dist).toFixed(2) + 'px,' + (vyU * dist - 4 * prodMul).toFixed(2) + 'px,0) scale(' + (1 + 0.05 * prodMul).toFixed(3) + ')' },
          { transform: 'translate3d(' + (vxU * dist * 0.2).toFixed(2) + 'px,' + (vyU * dist * 0.2).toFixed(2) + 'px,0) scale(' + (1 + 0.01 * prodMul).toFixed(3) + ')', offset: 0.65 },
          { transform: 'translate3d(0,0,0) scale(1)' },
        ],
        { duration: dur, easing: 'cubic-bezier(0.22, 1, 0.36, 1)' }
      );
    } else if (kind === 'recoil') {
      var kb = (opts.distance || 14) * sMul * prodMul;
      el.animate(
        [
          { transform: 'translate3d(0,0,0) rotate(0deg)' },
          { transform: 'translate3d(' + (-vxU * kb).toFixed(2) + 'px,' + (-vyU * kb - 2 * prodMul).toFixed(2) + 'px,0) rotate(' + (-d * 3.5 * prodMul).toFixed(2) + 'deg)' },
          { transform: 'translate3d(' + (vxU * kb * 0.25).toFixed(2) + 'px,' + (vyU * kb * 0.25 + 1 * prodMul).toFixed(2) + 'px,0) rotate(' + (d * 1 * prodMul).toFixed(2) + 'deg)' },
          { transform: 'translate3d(0,0,0) rotate(0deg)' },
        ],
        { duration: opts.duration || 240, easing: 'ease-out' }
      );
    } else if (kind === 'slam') {
      var sx1 = (vxU * 12 * sMul * prodMul).toFixed(2);
      var sy1 = (vyU * 6 * sMul * prodMul - 18 * sMul * prodMul).toFixed(2);
      var sx2 = (vxU * 28 * sMul * prodMul).toFixed(2);
      var sy2 = (vyU * 14 * sMul * prodMul + 4 * prodMul).toFixed(2);
      el.animate(
        [
          { transform: 'translate3d(0,0,0) scale(1)' },
          { transform: 'translate3d(' + sx1 + 'px,' + sy1 + 'px,0) scale(' + (1 + 0.08 * prodMul).toFixed(3) + ')', offset: 0.35 },
          { transform: 'translate3d(' + sx2 + 'px,' + sy2 + 'px,0) scale(' + (1 + 0.02 * prodMul).toFixed(3) + ',' + (1 - 0.06 * prodMul).toFixed(3) + ')', offset: 0.6 },
          { transform: 'translate3d(0,0,0) scale(1)' },
        ],
        { duration: opts.duration || 380, easing: 'cubic-bezier(0.2, 0.85, 0.3, 1)' }
      );
    } else if (kind === 'cast' || kind === 'levitate') {
      var lift = (-9 * prodCastMul).toFixed(2);
      el.animate(
        [
          { transform: 'translate3d(0,0,0) scale(1)' },
          { transform: 'translate3d(0,' + lift + 'px,0) scale(' + (1 + 0.04 * prodCastMul).toFixed(3) + ')' },
          { transform: 'translate3d(0,0,0) scale(1)' },
        ],
        { duration: opts.duration || 460, easing: 'ease-in-out' }
      );
    } else if (kind === 'dodge') {
      var dx1 = (-vxU * 24 * sMul * prodMul).toFixed(2);
      var dy1 = (-vyU * 24 * sMul * prodMul - 6 * prodMul).toFixed(2);
      el.animate(
        [
          { transform: 'translate3d(0,0,0)', opacity: 1 },
          { transform: 'translate3d(' + dx1 + 'px,' + dy1 + 'px,0)', opacity: state.hostMode === 'production' ? 0.72 : 0.5 },
          { transform: 'translate3d(0,0,0)', opacity: 1 },
        ],
        { duration: opts.duration || 280, easing: 'ease-out' }
      );
    }
  }

  function hitFlash(el, color, duration) {
    var av = getVisualTargetEl(el);
    if (!av || !av.animate) return;
    var c = color || '#ffffff';
    var peakBrightness = state.hostMode === 'production' ? 1.7 : 2.3;
    av.animate(
      [
        {
          filter: 'brightness(' + peakBrightness + ') contrast(1.18) drop-shadow(0 0 12px ' + c + ')',
        },
        {
          filter: 'brightness(1.2) drop-shadow(0 0 6px ' + c + ')',
          offset: 0.45,
        },
        {
          filter: 'brightness(1)',
        },
      ],
      { duration: duration || 220, easing: 'ease-out' }
    );
  }

  function impact(el, colorOrOpts) {
    var color = typeof colorOrOpts === 'string' ? colorOrOpts : (colorOrOpts && colorOrOpts.color) || '#ffffff';
    var dur = (typeof colorOrOpts === 'object' && colorOrOpts && colorOrOpts.duration) || 220;
    hitFlash(el, color, dur);
    motion(el, 'recoil', { distance: (colorOrOpts && colorOrOpts.distance) || 12 });
  }

  var FLOAT_STYLE_MAP = {
    damage: { color: '#ff8e72', scale: 1 },
    crit: { color: '#ffd966', scale: 1.28 },
    heal: { color: '#64efaa', scale: 1.05 },
    mana: { color: '#6fd0ff', scale: 1.0 },
    shield: { color: '#78e6d0', scale: 1.02 },
    miss: { color: '#b4becd', scale: 0.94 },
    status: { color: '#e6c97a', scale: 0.98 },
  };

  function floatText(targetOrPt, text, kindOrColor, opts) {
    // 正式前端伤害与恢复浮字由服务端快照驱动的 ArenaResource 统一渲染，绝不输出演示假浮字
    if (state.hostMode === 'production' || !state.floatLayerEl) return;
    opts = opts || {};
    var pt = targetOrPt && typeof targetOrPt.x === 'number' ? targetOrPt : head(targetOrPt);
    var preset = FLOAT_STYLE_MAP[kindOrColor];
    var color = preset ? preset.color : kindOrColor || '#ffffff';
    var scale = opts.scale || (preset ? preset.scale : 1);

    var el = document.createElement('div');
    el.className = 'vfx-float-num';
    el.textContent = text;
    var rise = opts.rise || 42;
    var jx = rand(-18, 18);
    var jy = rand(-10, 4);
    var fx = clamp(pt.x + jx, 36, state.width - 36);
    var fy = clamp(pt.y + jy, Math.max(38, rise + 10), state.height - 14);
    el.style.left = fx + 'px';
    el.style.top = fy + 'px';
    el.style.color = color;
    el.style.fontSize = Math.round(15 * scale) + 'px';
    el.style.textShadow = '0 2px 6px rgba(0,0,0,0.92), 0 0 10px ' + rgbaStr(color, 0.55);
    state.floatLayerEl.appendChild(el);

    var anim = el.animate(
      [
        { opacity: 0, transform: 'translate(-50%, -10%) scale(' + scale * 0.72 + ')' },
        { opacity: 1, transform: 'translate(-50%, -55%) scale(' + scale * 1.12 + ')', offset: 0.16 },
        { opacity: 1, transform: 'translate(-50%, -' + (55 + rise * 0.65) + '%) scale(' + scale + ')', offset: 0.65 },
        { opacity: 0, transform: 'translate(-50%, -' + (65 + rise) + '%) scale(' + scale * 0.92 + ')' },
      ],
      { duration: opts.duration || 880, easing: 'cubic-bezier(0.22, 1, 0.36, 1)', fill: 'forwards' }
    );
    anim.onfinish = function () {
      if (el.parentNode) el.parentNode.removeChild(el);
    };
  }

  function resourceSlice(targetOrPt, kind, amount) {
    var pt = targetOrPt && typeof targetOrPt.x === 'number' ? targetOrPt : center(targetOrPt);
    var isMana = kind === 'mp' || kind === 'mana';
    var colors = isMana ? ['#66d4ff', '#bfefff', '#38bdf8'] : ['#5eead4', '#86efac', '#fef08a'];
    burst({
      x: pt.x,
      y: pt.y,
      count: 16,
      shape: 'wisp',
      colors: colors,
      size: [3.2, 6.2],
      speed: [36, 102],
      gravity: -88,
      drag: 0.93,
      life: [420, 760],
      trail: 5,
    });
    if (amount && state.hostMode !== 'production') {
      floatText(pt, '+' + Math.round(amount), isMana ? 'mana' : 'heal');
    }
  }

  function defeat(el) {
    if (!el) return;
    if (el.classList && state.hostMode !== 'production') el.classList.add('is-defeated');
    var c = center(el);
    burst({
      x: c.x,
      y: c.y,
      radius: 28,
      count: 34,
      shape: 'shard',
      colors: ['#94a3b8', '#cbd5e1', '#f8fafc', '#64748b'],
      size: [2.5, 6.2],
      speed: [45, 165],
      gravity: -65,
      drag: 0.94,
      life: [520, 950],
      trail: 4,
    });
    burst({
      x: c.x,
      y: c.y,
      radius: 22,
      count: 20,
      shape: 'wisp',
      colors: ['#cbd5e1', '#94a3b8', '#e2e8f0'],
      size: [4.5, 9.0],
      speed: [30, 110],
      gravity: -95,
      turbulence: 65,
      drag: 0.94,
      life: [580, 980],
    });
    shockwave({
      x: c.x,
      y: c.y,
      fromRadius: 10,
      toRadius: 76,
      color: '#cbd5e1',
      duration: 440,
      tilt: 0.85,
    });
  }

  function revive(el) {
    if (!el) return;
    if (el.classList && state.hostMode !== 'production') el.classList.remove('is-defeated');
    var c = center(el);
    var f = foot(el);
    burst({
      x: f.x,
      y: f.y,
      radius: 26,
      count: 26,
      shape: 'wisp',
      colors: ['#5eead4', '#fef08a', '#ffffff'],
      size: [4.0, 8.2],
      speed: [55, 155],
      angle: [-Math.PI * 0.72, -Math.PI * 0.28],
      gravity: -115,
      vortex: 140,
      vortexCenter: c,
      life: [540, 880],
      trail: 5,
    });
    burst({
      x: f.x,
      y: f.y,
      radius: 24,
      count: 22,
      shape: 'flare',
      colors: ['#5eead4', '#fef08a', '#ffffff'],
      size: [2.8, 5.4],
      speed: [45, 130],
      angle: [-Math.PI * 0.85, -Math.PI * 0.15],
      gravity: -120,
      life: [480, 820],
    });
  }

  // ---------------------------------------------------------------------------
  // 多物理行为粒子系统 (含新增 wisp 与 flare 形态、逐段羽化拖尾)
  // ---------------------------------------------------------------------------
  function spawnSingleParticle(opts) {
    if (state.particles.length >= MAX_PARTICLES) {
      state.particles.shift();
    }
    var ox = opts.x !== undefined ? opts.x : state.width * 0.5;
    var oy = opts.y !== undefined ? opts.y : state.height * 0.5;
    var scatterR = opts.radius || 0;
    var px = ox;
    var py = oy;
    if (scatterR > 0) {
      var angSpawn = Math.random() * TWO_PI;
      var rr = opts.ring ? scatterR * rand(0.9, 1.06) : Math.sqrt(Math.random()) * scatterR;
      px += Math.cos(angSpawn) * rr;
      py += Math.sin(angSpawn) * rr * (opts.tiltY !== undefined ? opts.tiltY : 1);
    }

    var ang;
    if (opts.angle !== undefined) {
      ang = Array.isArray(opts.angle) ? rand(opts.angle[0], opts.angle[1]) : opts.angle;
    } else if (opts.baseAngle !== undefined) {
      var sp = opts.spread !== undefined ? opts.spread : Math.PI;
      ang = opts.baseAngle + rand(-sp * 0.5, sp * 0.5);
    } else {
      ang = Math.random() * TWO_PI;
    }

    var spd = rangeVal(opts.speed, 90);
    var vx = opts.vx !== undefined ? opts.vx + Math.cos(ang) * spd : Math.cos(ang) * spd;
    var vy = opts.vy !== undefined ? opts.vy + Math.sin(ang) * spd : Math.sin(ang) * spd;

    var sizeStart = rangeVal(opts.size, 4);
    var sizeEnd = opts.endSize !== undefined ? rangeVal(opts.endSize, 0.2) : sizeStart * 0.15;
    var color = opts.color || pick(opts.colors);
    var trailLen = opts.trail === true ? 6 : Math.min(12, opts.trail || 0);

    state.particles.push({
      layer: opts.layer === 'ground' ? 'ground' : 'sky',
      blend: opts.blend || (opts.shape === 'smoke' ? 'source-over' : 'lighter'),
      shape: opts.shape || 'spark',
      x: px,
      y: py,
      vx: vx,
      vy: vy,
      originX: ox,
      originY: oy,
      color: color,
      sizeStart: sizeStart,
      sizeEnd: sizeEnd,
      alphaMax: rangeVal(opts.alpha, 0.95),
      life: Math.max(80, rangeVal(opts.life, 520)),
      elapsed: 0,
      gravity: opts.gravity || 0,
      drag: opts.drag !== undefined ? opts.drag : 0.95,
      vortex: opts.vortex || 0,
      vortexCenter: opts.vortexCenter || { x: ox, y: oy },
      attractTo: opts.attractTo || null,
      attractStrength: opts.attractStrength || 600,
      turbulence: opts.turbulence || 0,
      rot: Math.random() * TWO_PI,
      spin: rangeVal(opts.spin, 4.5),
      trailMax: trailLen,
      trail: trailLen > 0 ? [{ x: px, y: py }] : null,
      wobbleSeed: Math.random() * 10,
    });
  }

  function burst(opts) {
    opts = opts || {};
    var exec = function () {
      var count = Math.max(1, opts.count || 16);
      for (var i = 0; i < count; i++) {
        spawnSingleParticle(opts);
      }
      ensureLoop();
    };
    if (opts.delay && opts.delay > 0) {
      return after(opts.delay, exec);
    }
    exec();
  }

  function updateParticles(dtSec, dtMs) {
    var list = state.particles;
    var dragPow = dtSec * 60;
    for (var i = list.length - 1; i >= 0; i--) {
      var p = list[i];
      p.elapsed += dtMs;
      if (p.elapsed >= p.life) {
        list.splice(i, 1);
        continue;
      }

      if (p.vortex) {
        var dxv = p.x - p.vortexCenter.x;
        var dyv = p.y - p.vortexCenter.y;
        var distV = Math.hypot(dxv, dyv) || 1;
        p.vx += (-dyv / distV) * p.vortex * dtSec;
        p.vy += (dxv / distV) * p.vortex * dtSec;
      }

      if (p.attractTo) {
        var dxa = p.attractTo.x - p.x;
        var dya = p.attractTo.y - p.y;
        var distA = Math.hypot(dxa, dya) || 1;
        var pull = p.attractStrength * dtSec;
        p.vx += (dxa / distA) * pull;
        p.vy += (dya / distA) * pull;
      }

      if (p.turbulence) {
        p.vx += Math.sin(p.elapsed * 0.02 + p.wobbleSeed) * p.turbulence * dtSec;
        p.vy += Math.cos(p.elapsed * 0.017 + p.wobbleSeed * 1.7) * p.turbulence * dtSec;
      }

      p.vy += p.gravity * dtSec;

      if (p.drag < 1) {
        var factor = Math.pow(p.drag, dragPow);
        p.vx *= factor;
        p.vy *= factor;
      }

      p.x += p.vx * dtSec;
      p.y += p.vy * dtSec;
      p.rot += p.spin * dtSec;

      if (p.trail) {
        p.trail.push({ x: p.x, y: p.y });
        if (p.trail.length > p.trailMax) p.trail.shift();
      }
    }
  }

  // 双层柔和光晕底衬：外层大气扩散晕 + 内层本色聚光晕
  function drawUnderglow(ctx, p, sz, alpha) {
    var outerSp = getGlowSprite(deriveHaloColor(p.color));
    var outerR = sz * 3.5;
    var sp = getGlowSprite(p.color);
    var r = sz * 1.85;
    ctx.save();
    ctx.globalAlpha = alpha * 0.22;
    ctx.drawImage(outerSp, p.x - outerR, p.y - outerR, outerR * 2, outerR * 2);
    ctx.globalAlpha = alpha * 0.48;
    ctx.drawImage(sp, p.x - r, p.y - r, r * 2, r * 2);
    ctx.restore();
  }

  function drawTaperedFlareArm(ctx, len, width, color) {
    var grad = ctx.createLinearGradient(-len, 0, len, 0);
    grad.addColorStop(0, rgbaStr(color, 0));
    grad.addColorStop(0.28, rgbaStr(color, 0.45));
    grad.addColorStop(0.5, 'rgba(255,255,255,0.98)');
    grad.addColorStop(0.72, rgbaStr(color, 0.45));
    grad.addColorStop(1, rgbaStr(color, 0));
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.moveTo(-len, 0);
    ctx.quadraticCurveTo(0, -width, len, 0);
    ctx.quadraticCurveTo(0, width, -len, 0);
    ctx.fill();
  }

  function renderParticle(ctx, p) {
    var t = clamp(p.elapsed / p.life, 0, 1);
    var fadeIn = t < 0.14 ? t / 0.14 : 1;
    var fadeOut = Math.pow(1 - t, 1.4);
    var alpha = p.alphaMax * fadeIn * fadeOut;
    if (alpha <= 0.01) return;

    var sz = lerp(p.sizeStart, p.sizeEnd, t);
    if (sz <= 0.15) return;

    ctx.save();
    ctx.globalCompositeOperation = p.blend;
    ctx.globalAlpha = alpha;

    // 逐段渐细渐隐羽化拖尾（增补外层柔晕轨）
    if (p.trail && p.trail.length > 1) {
      var n = p.trail.length - 1;
      ctx.lineCap = 'round';
      for (var k = 0; k < n; k++) {
        var frac = (k + 1) / n;
        ctx.beginPath();
        ctx.moveTo(p.trail[k].x, p.trail[k].y);
        ctx.lineTo(p.trail[k + 1].x, p.trail[k + 1].y);
        ctx.strokeStyle = p.color;
        ctx.lineWidth = Math.max(1.4, sz * 2.4 * frac);
        ctx.globalAlpha = alpha * 0.15 * frac * frac;
        ctx.stroke();

        ctx.strokeStyle = p.color;
        ctx.lineWidth = Math.max(0.6, sz * 1.15 * frac);
        ctx.globalAlpha = alpha * 0.48 * frac * frac;
        ctx.stroke();

        ctx.strokeStyle = '#ffffff';
        ctx.lineWidth = Math.max(0.4, sz * 0.4 * frac);
        ctx.globalAlpha = alpha * 0.36 * frac * frac;
        ctx.stroke();
      }
      ctx.globalAlpha = alpha;
    }

    var shape = p.shape;
    if (shape === 'orb') {
      var sprite = getGlowSprite(p.color);
      var r = sz * 2.9;
      ctx.drawImage(sprite, p.x - r, p.y - r, r * 2, r * 2);
    } else if (shape === 'smoke') {
      var smk = getSmokeSprite(p.color);
      var sr = sz * 2.4 * (1 + t * 0.95);
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot * 0.35);
      ctx.drawImage(smk, -sr, -sr, sr * 2, sr * 2);
    } else if (shape === 'wisp') {
      var wispAng = Math.atan2(p.vy, p.vx);
      var wispSpd = Math.hypot(p.vx, p.vy);
      var stretch = clamp(1.35 + wispSpd * 0.008, 1.35, 3.6);
      var plm = getPlumeSprite(p.color);
      var rw = sz * 2.3;
      ctx.translate(p.x, p.y);
      ctx.rotate(wispAng);
      ctx.scale(stretch, 0.72);
      ctx.drawImage(plm, -rw, -rw, rw * 2, rw * 2);
      ctx.globalAlpha = alpha * 0.55;
      var innerSp = getGlowSprite(p.color);
      var ir = sz * 1.05;
      ctx.drawImage(innerSp, -ir, -ir, ir * 2, ir * 2);
    } else if (shape === 'flare') {
      var fSp = getGlowSprite(p.color);
      var fr = sz * 2.7;
      ctx.drawImage(fSp, p.x - fr, p.y - fr, fr * 2, fr * 2);
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot * 0.25);
      drawTaperedFlareArm(ctx, sz * 4.4, Math.max(0.9, sz * 0.34), p.color);
      ctx.rotate(HALF_PI);
      drawTaperedFlareArm(ctx, sz * 4.4, Math.max(0.9, sz * 0.34), p.color);
      ctx.rotate(Math.PI * 0.25);
      ctx.globalAlpha = alpha * 0.6;
      drawTaperedFlareArm(ctx, sz * 2.4, Math.max(0.7, sz * 0.22), '#ffffff');
      ctx.rotate(HALF_PI);
      drawTaperedFlareArm(ctx, sz * 2.4, Math.max(0.7, sz * 0.22), '#ffffff');
    } else if (shape === 'spark' || shape === 'drop') {
      drawUnderglow(ctx, p, sz, alpha);
      var velAngle = Math.atan2(p.vy, p.vx);
      var len = shape === 'drop' ? sz * 2.9 : clamp(Math.hypot(p.vx, p.vy) * 0.055, sz * 1.9, sz * 6.8);
      ctx.translate(p.x, p.y);
      ctx.rotate(velAngle);
      var sGrad = ctx.createLinearGradient(-len * 0.9, 0, len, 0);
      sGrad.addColorStop(0, rgbaStr(p.color, 0));
      sGrad.addColorStop(0.55, rgbaStr(p.color, 0.92));
      sGrad.addColorStop(1, 'rgba(255,255,255,0.98)');
      ctx.fillStyle = sGrad;
      ctx.beginPath();
      ctx.moveTo(len, 0);
      ctx.quadraticCurveTo(0, sz * 0.62, -len * 0.9, 0);
      ctx.quadraticCurveTo(0, -sz * 0.62, len, 0);
      ctx.fill();
      ctx.fillStyle = 'rgba(255,255,255,0.9)';
      ctx.beginPath();
      ctx.ellipse(len * 0.18, 0, len * 0.34, sz * 0.22, 0, 0, TWO_PI);
      ctx.fill();
    } else if (shape === 'shard') {
      drawUnderglow(ctx, p, sz, alpha);
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.fillStyle = p.color;
      ctx.beginPath();
      ctx.moveTo(sz * 1.5, 0);
      ctx.lineTo(0, sz * 0.56);
      ctx.lineTo(-sz * 1.5, 0);
      ctx.lineTo(0, -sz * 0.56);
      ctx.closePath();
      ctx.fill();
      ctx.fillStyle = '#ffffff';
      ctx.globalAlpha = alpha * 0.72;
      ctx.beginPath();
      ctx.moveTo(sz * 0.78, 0);
      ctx.lineTo(0, sz * 0.22);
      ctx.lineTo(-sz * 0.78, 0);
      ctx.lineTo(0, -sz * 0.22);
      ctx.closePath();
      ctx.fill();
    } else if (shape === 'ember') {
      var emberSprite = getGlowSprite(p.color);
      var er = sz * 2.5;
      ctx.drawImage(emberSprite, p.x - er, p.y - er, er * 2, er * 2);
      ctx.fillStyle = '#fff9d6';
      ctx.beginPath();
      ctx.arc(p.x, p.y, Math.max(0.9, sz * 0.34), 0, TWO_PI);
      ctx.fill();
    } else if (shape === 'petal') {
      drawUnderglow(ctx, p, sz, alpha * 0.9);
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.fillStyle = p.color;
      ctx.beginPath();
      ctx.moveTo(0, -sz * 1.4);
      ctx.bezierCurveTo(sz * 1.2, -sz * 0.5, sz * 0.9, sz * 1.1, 0, sz * 1.3);
      ctx.bezierCurveTo(-sz * 0.9, sz * 1.1, -sz * 1.2, -sz * 0.5, 0, -sz * 1.4);
      ctx.fill();
      // 柔亮瓣芯高光
      ctx.fillStyle = tintTowardWhite(p.color, 0.65);
      ctx.globalAlpha = alpha * 0.65;
      ctx.beginPath();
      ctx.moveTo(0, -sz * 0.82);
      ctx.bezierCurveTo(sz * 0.68, -sz * 0.28, sz * 0.52, sz * 0.64, 0, sz * 0.76);
      ctx.bezierCurveTo(-sz * 0.52, sz * 0.64, -sz * 0.68, -sz * 0.28, 0, -sz * 0.82);
      ctx.fill();
    } else if (shape === 'rune') {
      drawUnderglow(ctx, p, sz, alpha * 0.95);
      ctx.translate(p.x, p.y);
      ctx.rotate(p.rot);
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.beginPath();
      ctx.moveTo(-sz, -sz * 0.5);
      ctx.lineTo(sz, -sz * 0.5);
      ctx.moveTo(0, -sz);
      ctx.lineTo(0, sz);
      ctx.moveTo(-sz * 0.65, sz * 0.55);
      ctx.lineTo(sz * 0.65, -sz * 0.1);
      // 三层发光符纹：外柔晕 + 主色符纹 + 炽白灵芯
      ctx.strokeStyle = p.color;
      ctx.lineWidth = Math.max(2.6, sz * 0.62);
      ctx.globalAlpha = alpha * 0.28;
      ctx.stroke();
      ctx.lineWidth = Math.max(1.3, sz * 0.30);
      ctx.globalAlpha = alpha * 0.9;
      ctx.stroke();
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = Math.max(0.65, sz * 0.13);
      ctx.globalAlpha = alpha * 0.82;
      ctx.stroke();
    }

    ctx.restore();
  }

  // ---------------------------------------------------------------------------
  // 深度羽化与流体化参数基元
  // ---------------------------------------------------------------------------
  function addEffect(ef) {
    ef.elapsed = 0;
    ef.duration = Math.max(60, ef.duration || 400);
    ef.layer = ef.layer === 'ground' ? 'ground' : 'sky';
    ef.blend = ef.blend || 'lighter';

    var spawn = function () {
      state.effects.push(ef);
      ensureLoop();
    };
    if (ef.delay && ef.delay > 0) {
      return after(ef.delay, spawn);
    }
    spawn();
    return ef;
  }

  // 1. 5层高斯羽化弯月刀光 slash
  function slash(opts) {
    opts = opts || {};
    var sMul = opts.rawScale ? 1 : scale();
    var startA = opts.startAngle !== undefined ? opts.startAngle : (opts.angle || 0) - (opts.arcSpread || 2.1) * 0.5;
    var endA = opts.endAngle !== undefined ? opts.endAngle : (opts.angle || 0) + (opts.arcSpread || 2.1) * 0.5;
    if (opts.cw === false && startA < endA) {
      var tmp = startA;
      startA = endA;
      endA = tmp;
    }

    return addEffect({
      kind: 'slash',
      layer: opts.layer || 'sky',
      blend: opts.blend || 'lighter',
      delay: opts.delay || 0,
      duration: opts.duration || 280,
      x: opts.x !== undefined ? opts.x : state.width * 0.5,
      y: opts.y !== undefined ? opts.y : state.height * 0.5,
      radius: (opts.radius || 82) * sMul,
      tilt: opts.tilt !== undefined ? opts.tilt : 1,
      rotation: opts.rotation || 0,
      startAngle: startA,
      endAngle: endA,
      thickness: (opts.thickness || 20) * sMul,
      color: opts.color || '#f5c451',
      coreColor: opts.coreColor || '#ffffff',
      glowColor: opts.glowColor || opts.color || '#f59e0b',
      afterimage: opts.afterimage !== false,
      sparks: opts.sparks !== false,
    });
  }

  function drawSlash(ctx, p, ef) {
    var headP = clamp(easeOutCubic(Math.min(1, p * 1.45)), 0, 1);
    var tailP = clamp(Math.pow(Math.max(0, (p - 0.14) / 0.86), 1.45), 0, 1);
    if (headP - tailP <= 0.015) return;

    var curStart = lerp(ef.startAngle, ef.endAngle, tailP);
    var curEnd = lerp(ef.startAngle, ef.endAngle, headP);
    var steps = 26;
    var fade = p < 0.68 ? 1 : (1 - p) / 0.32;

    if (ef.sparks && p < 0.78 && Math.random() < 0.82) {
      var lx = Math.cos(curEnd) * ef.radius;
      var ly = Math.sin(curEnd) * ef.radius * ef.tilt;
      var localTan = curEnd + (ef.endAngle >= ef.startAngle ? HALF_PI : -HALF_PI);
      var tanDx = Math.cos(localTan);
      var tanDy = Math.sin(localTan) * ef.tilt;
      var rot = ef.rotation || 0;
      var cosR = rot ? Math.cos(rot) : 1;
      var sinR = rot ? Math.sin(rot) : 0;
      var sx = ef.x + lx * cosR - ly * sinR;
      var sy = ef.y + lx * sinR + ly * cosR;
      var worldTan = rot
        ? Math.atan2(tanDx * sinR + tanDy * cosR, tanDx * cosR - tanDy * sinR)
        : Math.atan2(tanDy, tanDx);
      spawnSingleParticle({
        x: sx,
        y: sy,
        shape: Math.random() < 0.42 ? 'wisp' : 'spark',
        color: Math.random() < 0.35 ? '#ffffff' : ef.color,
        size: [1.6, 3.6],
        speed: [65, 190],
        baseAngle: worldTan,
        spread: 1.05,
        life: [190, 380],
        trail: 4,
      });
    }

    ctx.save();
    ctx.translate(ef.x, ef.y);
    if (ef.rotation) ctx.rotate(ef.rotation);
    ctx.scale(1, ef.tilt);

    function buildCrescent(thickScale) {
      ctx.beginPath();
      var wMax = ef.thickness * thickScale * (0.42 + 0.58 * fade);
      for (var i = 0; i <= steps; i++) {
        var u = i / steps;
        var a = lerp(curStart, curEnd, u);
        var bell = Math.pow(Math.sin(u * Math.PI), 0.9);
        var rOut = ef.radius + wMax * bell * 0.52;
        var px = Math.cos(a) * rOut;
        var py = Math.sin(a) * rOut;
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      }
      for (var j = steps; j >= 0; j--) {
        var u2 = j / steps;
        var a2 = lerp(curStart, curEnd, u2);
        var bell2 = Math.pow(Math.sin(u2 * Math.PI), 0.9);
        var rIn = ef.radius - wMax * bell2 * 0.48;
        ctx.lineTo(Math.cos(a2) * rIn, Math.sin(a2) * rIn);
      }
      ctx.closePath();
    }

    // 沿刀弧切向垫入柔和刀气光晕床
    var arcPlm = getPlumeSprite(ef.glowColor);
    var haloSamples = [0.25, 0.55, 0.82];
    for (var hs = 0; hs < haloSamples.length; hs++) {
      var ha = lerp(curStart, curEnd, haloSamples[hs]);
      var hx = Math.cos(ha) * ef.radius;
      var hy = Math.sin(ha) * ef.radius;
      var tangent = ha + (curEnd >= curStart ? HALF_PI : -HALF_PI);
      ctx.save();
      ctx.translate(hx, hy);
      ctx.rotate(tangent);
      ctx.globalAlpha = fade * 0.28;
      ctx.drawImage(arcPlm, -ef.thickness * 1.6, -ef.thickness * 0.95, ef.thickness * 3.2, ef.thickness * 1.9);
      ctx.restore();
    }

    // 5 层高斯羽化刀弧叠绘
    var passes = ef.afterimage
      ? [
          { scale: 2.8, alpha: 0.06, col: ef.glowColor },
          { scale: 1.9, alpha: 0.15, col: ef.glowColor },
          { scale: 1.25, alpha: 0.34, col: ef.color },
          { scale: 0.72, alpha: 0.68, col: ef.color },
          { scale: 0.28, alpha: 0.92, col: ef.coreColor },
        ]
      : [
          { scale: 1.5, alpha: 0.16, col: ef.glowColor },
          { scale: 0.95, alpha: 0.48, col: ef.color },
          { scale: 0.32, alpha: 0.9, col: ef.coreColor },
        ];

    for (var ps = 0; ps < passes.length; ps++) {
      ctx.globalAlpha = fade * passes[ps].alpha;
      ctx.fillStyle = passes[ps].col;
      buildCrescent(passes[ps].scale);
      ctx.fill();
    }

    // 刀尖与腹背双层柔光晕染（外晕 + 内核心光斑）
    var headGlow = getGlowSprite(ef.glowColor);
    var headCore = getGlowSprite(ef.coreColor);
    var midA = lerp(curStart, curEnd, 0.72);
    var midX = Math.cos(midA) * ef.radius;
    var midY = Math.sin(midA) * ef.radius;
    var grOuter = ef.thickness * 1.95;
    var grInner = ef.thickness * 1.15;
    ctx.globalAlpha = fade * 0.54;
    ctx.drawImage(headGlow, midX - grOuter, midY - grOuter, grOuter * 2, grOuter * 2);
    ctx.globalAlpha = fade * 0.72;
    ctx.drawImage(headCore, midX - grInner, midY - grInner, grInner * 2, grInner * 2);

    ctx.restore();
  }

  // 2. 羽化极速穿刺光束与柔雾音爆环 slashLine（废除四点硬菱形与线框圆环）
  function slashLine(opts) {
    opts = opts || {};
    return addEffect({
      kind: 'slashLine',
      layer: opts.layer || 'sky',
      blend: opts.blend || 'lighter',
      delay: opts.delay || 0,
      duration: opts.duration || 260,
      x1: opts.x1,
      y1: opts.y1,
      x2: opts.x2,
      y2: opts.y2,
      width: opts.width || 16,
      color: opts.color || '#fcd34d',
      coreColor: opts.coreColor || '#ffffff',
      pierce: opts.pierce !== undefined ? opts.pierce : 0.16,
      ringCount: opts.ringCount || 2,
      sparks: opts.sparks !== false,
    });
  }

  function drawSlashLine(ctx, p, ef) {
    var dx = ef.x2 - ef.x1;
    var dy = ef.y2 - ef.y1;
    var dist = Math.hypot(dx, dy) || 1;
    var ux = dx / dist;
    var uy = dy / dist;
    var ang = Math.atan2(dy, dx);

    var ext = dist * ef.pierce;
    var tx2 = ef.x2 + ux * ext;
    var ty2 = ef.y2 + uy * ext;
    var reach = clamp(easeOutQuart(Math.min(1, p * 2.4)), 0, 1);
    var curX2 = lerp(ef.x1, tx2, reach);
    var curY2 = lerp(ef.y1, ty2, reach);
    var curDist = Math.hypot(curX2 - ef.x1, curY2 - ef.y1) || 1;
    var fade = p < 0.18 ? p / 0.18 : Math.pow(1 - (p - 0.18) / 0.82, 1.45);

    if (ef.sparks && p < 0.7 && Math.random() < 0.72) {
      var uSp = Math.random();
      spawnSingleParticle({
        x: lerp(ef.x1, curX2, uSp),
        y: lerp(ef.y1, curY2, uSp),
        shape: Math.random() < 0.5 ? 'spark' : 'wisp',
        color: Math.random() < 0.4 ? '#ffffff' : ef.color,
        size: [1.4, 3.0],
        baseAngle: ang,
        spread: 0.7,
        speed: [45, 145],
        life: [180, 340],
        trail: 3,
      });
    }

    ctx.save();

    // 柔雾音爆环：使用 softRingSprite
    var ringSp = getSoftRingSprite(ef.color);
    for (var r = 0; r < ef.ringCount; r++) {
      var rp = clamp((p - r * 0.08) * 1.4, 0, 1);
      if (rp > 0 && rp < 1) {
        var frac = 0.35 + r * 0.32;
        var rx = lerp(ef.x1, tx2, frac);
        var ry = lerp(ef.y1, ty2, frac);
        var rad = lerp(10, ef.width * 2.4, easeOutCubic(rp));
        ctx.save();
        ctx.translate(rx, ry);
        ctx.rotate(ang);
        ctx.scale(0.36, 1.08);
        ctx.globalAlpha = (1 - rp) * 0.72;
        ctx.drawImage(ringSp, -rad, -rad, rad * 2, rad * 2);
        ctx.restore();
      }
    }

    // 拉伸 plumeSprite 外围体积气鞘
    var plm = getPlumeSprite(ef.color);
    var midX = (ef.x1 + curX2) * 0.5;
    var midY = (ef.y1 + curY2) * 0.5;
    ctx.save();
    ctx.translate(midX, midY);
    ctx.rotate(ang);
    ctx.globalAlpha = fade * 0.52;
    ctx.drawImage(plm, -curDist * 0.62, -ef.width * 1.9, curDist * 1.24, ef.width * 3.8);

    // 5 层渐隐羽化锐刃（两端随渐变自然消散为 0）
    var beamPass = [
      { w: 2.3, al: 0.1, col: ef.color },
      { w: 1.5, al: 0.24, col: ef.color },
      { w: 0.92, al: 0.55, col: ef.color },
      { w: 0.45, al: 0.84, col: ef.coreColor },
      { w: 0.2, al: 0.96, col: '#ffffff' },
    ];
    var lenHalf = curDist * 0.52;
    for (var b = 0; b < beamPass.length; b++) {
      var bp = beamPass[b];
      var halfW = ef.width * 0.5 * bp.w * (1 - p * 0.45);
      var grad = ctx.createLinearGradient(-lenHalf, 0, lenHalf, 0);
      grad.addColorStop(0, rgbaStr(bp.col, 0));
      grad.addColorStop(0.24, rgbaStr(bp.col, bp.al * 0.65 * fade));
      grad.addColorStop(0.62, rgbaStr(bp.col, bp.al * fade));
      grad.addColorStop(0.88, rgbaStr(bp.col, bp.al * 0.8 * fade));
      grad.addColorStop(1, rgbaStr(bp.col, 0));
      ctx.fillStyle = grad;
      ctx.beginPath();
      ctx.moveTo(-lenHalf, 0);
      ctx.quadraticCurveTo(curDist * 0.08, -halfW, lenHalf, 0);
      ctx.quadraticCurveTo(curDist * 0.08, halfW, -lenHalf, 0);
      ctx.fill();
    }
    ctx.restore();

    // 穿刺前端耀斑
    var tipSp = getGlowSprite(ef.color);
    var tr = ef.width * 1.8 * fade;
    ctx.globalAlpha = fade * 0.85;
    ctx.drawImage(tipSp, curX2 - tr, curY2 - tr, tr * 2, tr * 2);

    ctx.restore();
  }

  // 3. 六层立体电离光晕分形闪电 lightning（高密分形 + 锥度电弧 + 大气等离子光晕 + 二级电须 + 交缠副弧）
  function buildLightningPaths(x1, y1, x2, y2, jagged, segments, branches, wantSubArc) {
    function displace(ax, ay, bx, by, disp, seg, customIter) {
      var pts = [
        { x: ax, y: ay },
        { x: bx, y: by },
      ];
      var d = disp;
      var iter = customIter !== undefined ? customIter : Math.max(3, Math.round(Math.log2(seg || 12)));
      for (var k = 0; k < iter; k++) {
        var next = [pts[0]];
        for (var i = 0; i < pts.length - 1; i++) {
          var p0 = pts[i];
          var p1 = pts[i + 1];
          var dx = p1.x - p0.x;
          var dy = p1.y - p0.y;
          var len = Math.hypot(dx, dy) || 1;
          var nx = -dy / len;
          var ny = dx / len;
          var tSplit = rand(0.43, 0.57);
          var off = (Math.random() - 0.5) * 2 * d * len;
          next.push({
            x: lerp(p0.x, p1.x, tSplit) + nx * off,
            y: lerp(p0.y, p1.y, tSplit) + ny * off,
          });
          next.push(p1);
        }
        pts = next;
        d *= 0.62;
      }
      return pts;
    }

    var mainLen = Math.hypot(x2 - x1, y2 - y1) || 1;
    var baseAng = Math.atan2(y2 - y1, x2 - x1);
    var baseIter = Math.round(Math.log2(segments || 12));
    var trunkIter = mainLen > 210
      ? Math.max(4, Math.min(5, baseIter + 1))
      : mainLen > 85
        ? Math.max(4, baseIter)
        : Math.max(3, baseIter);

    var trunk = displace(x1, y1, x2, y2, jagged, segments, trunkIter);
    var forks = [];
    var twigs = [];

    for (var b = 0; b < branches; b++) {
      var idx = Math.max(1, Math.min(trunk.length - 2, ((trunk.length - 2) * rand(0.2, 0.82)) | 0));
      var origin = trunk[idx];
      var forkAng = baseAng + (Math.random() < 0.5 ? -1 : 1) * rand(0.32, 0.82);
      var forkLen = mainLen * rand(0.2, 0.42);
      var fx = origin.x + Math.cos(forkAng) * forkLen;
      var fy = origin.y + Math.sin(forkAng) * forkLen;
      var forkPts = displace(origin.x, origin.y, fx, fy, jagged * 0.9, 8, 3);
      forks.push({ pts: forkPts, origin: origin });

      // 二级毛细电须
      if (forkLen > 28 && forkPts.length >= 4 && Math.random() < 0.7) {
        var twigIdx = Math.max(1, Math.min(forkPts.length - 2, ((forkPts.length - 2) * rand(0.35, 0.65)) | 0));
        var tOrig = forkPts[twigIdx];
        var twigAng = forkAng + (Math.random() < 0.5 ? -1 : 1) * rand(0.4, 0.9);
        var twigLen = forkLen * rand(0.32, 0.52);
        var twigPts = displace(
          tOrig.x,
          tOrig.y,
          tOrig.x + Math.cos(twigAng) * twigLen,
          tOrig.y + Math.sin(twigAng) * twigLen,
          jagged * 0.8,
          4,
          2
        );
        twigs.push(twigPts);
      }
    }

    // 伴生交缠副电弧（中长距闪电自动构造反向微偏交织电丝）
    var subTrunk = null;
    if (wantSubArc && mainLen > 58) {
      var ndx = -(y2 - y1) / mainLen;
      var ndy = (x2 - x1) / mainLen;
      var side = Math.random() < 0.5 ? -1 : 1;
      var bulge = mainLen * rand(0.05, 0.11) * side;
      var ctrlX = (x1 + x2) * 0.5 + ndx * bulge;
      var ctrlY = (y1 + y2) * 0.5 + ndy * bulge;
      var subIter = Math.max(3, trunkIter - 1);
      var subA = displace(x1, y1, ctrlX, ctrlY, jagged * 0.82, 8, Math.max(2, subIter - 1));
      var subB = displace(ctrlX, ctrlY, x2, y2, jagged * 0.82, 8, Math.max(2, subIter - 1));
      subTrunk = subA.concat(subB.slice(1));
    }

    return { trunk: trunk, forks: forks, twigs: twigs, subTrunk: subTrunk };
  }

  function lightning(opts) {
    opts = opts || {};
    var x1 = opts.x1 !== undefined ? opts.x1 : opts.from ? opts.from.x : state.width * 0.5;
    var y1 = opts.y1 !== undefined ? opts.y1 : opts.from ? opts.from.y : 20;
    var x2 = opts.x2 !== undefined ? opts.x2 : opts.to ? opts.to.x : state.width * 0.5;
    var y2 = opts.y2 !== undefined ? opts.y2 : opts.to ? opts.to.y : state.height * 0.5;
    var jagged = opts.jagged !== undefined ? opts.jagged : 0.24;
    var segments = opts.segments || 12;
    var branches = opts.branches !== undefined ? opts.branches : 3;
    var color = opts.color || '#60efff';
    var glowColor = opts.glowColor || deriveHaloColor(color);
    var secondaryColor = opts.secondaryColor || tintTowardWhite(color, 0.48);
    var subArc = opts.subArc !== undefined ? !!opts.subArc : true;

    return addEffect({
      kind: 'lightning',
      layer: opts.layer || 'sky',
      blend: opts.blend || 'lighter',
      delay: opts.delay || 0,
      duration: opts.duration || 280,
      x1: x1,
      y1: y1,
      x2: x2,
      y2: y2,
      jagged: jagged,
      segments: segments,
      branches: branches,
      flicker: opts.flicker !== undefined ? opts.flicker : 3,
      lastStep: 0,
      color: color,
      glowColor: glowColor,
      secondaryColor: secondaryColor,
      haloScale: opts.haloScale !== undefined ? opts.haloScale : 1.0,
      subArc: subArc,
      coreColor: opts.coreColor || '#ffffff',
      width: opts.width || 4.2,
      geom: buildLightningPaths(x1, y1, x2, y2, jagged, segments, branches, subArc),
    });
  }

  function drawLightning(ctx, p, ef) {
    if (ef.flicker > 1) {
      var step = Math.floor(p * ef.flicker);
      if (step !== ef.lastStep) {
        ef.lastStep = step;
        ef.geom = buildLightningPaths(ef.x1, ef.y1, ef.x2, ef.y2, ef.jagged, ef.segments, ef.branches, ef.subArc);
      }
    }

    var fade = p < 0.12 ? p / 0.12 : Math.pow(1 - (p - 0.12) / 0.88, 1.3);
    var flickerPulse = 0.76 + 0.24 * Math.sin(p * Math.PI * 13);
    var a = fade * flickerPulse;
    var hs = ef.haloScale || 1.0;

    // 锥度分段电弧描边辅助：按最多 4 段线性递减线宽，并支持可控柔光 shadowBlur
    function strokeTaperedPoly(pts, wStart, wEnd, col, alpha, shadowCol, shadowBlurVal) {
      if (!pts || pts.length < 2 || alpha <= 0.004) return;
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.strokeStyle = col;
      ctx.globalAlpha = alpha;
      if (shadowBlurVal > 0) {
        ctx.shadowColor = shadowCol || col;
        ctx.shadowBlur = Math.min(18, shadowBlurVal);
      } else {
        ctx.shadowBlur = 0;
      }

      var total = pts.length - 1;
      var chunks = Math.min(4, total);
      for (var c = 0; c < chunks; c++) {
        var i0 = Math.floor((c * total) / chunks);
        var i1 = Math.floor(((c + 1) * total) / chunks);
        if (i1 <= i0) continue;
        var midFrac = (i0 + i1) / (2 * total);
        ctx.lineWidth = Math.max(0.35, lerp(wStart, wEnd, midFrac));
        ctx.beginPath();
        ctx.moveTo(pts[i0].x, pts[i0].y);
        for (var i = i0 + 1; i <= i1; i++) {
          ctx.lineTo(pts[i].x, pts[i].y);
        }
        ctx.stroke();
      }

      if (shadowBlurVal > 0) {
        ctx.shadowBlur = 0;
      }
    }

    ctx.save();
    var trunk = ef.geom.trunk;
    var plmHalo = getPlumeSprite(ef.glowColor);
    var mainHaloSp = getGlowSprite(ef.color);
    var outerHaloSp = getGlowSprite(ef.glowColor);
    var secHaloSp = getGlowSprite(ef.secondaryColor);
    var whiteSp = getGlowSprite('#ffffff');

    // 第 1 层：沿主干电离通道铺设连续高斯体积光晕走廊（全柔和零硬边）
    if (trunk && trunk.length >= 2) {
      var stepIdx = Math.max(1, Math.floor(trunk.length / 9));
      var plmW = ef.width * 10.5 * hs;
      var plmH = ef.width * 6.8 * hs;
      var dotR = ef.width * 4.4 * hs;
      for (var n = 0; n < trunk.length; n += stepIdx) {
        var pt = trunk[n];
        var nextPt = trunk[Math.min(trunk.length - 1, n + 1)];
        var prevPt = trunk[Math.max(0, n - 1)];
        var segAng = Math.atan2(nextPt.y - prevPt.y, nextPt.x - prevPt.x);

        ctx.save();
        ctx.translate(pt.x, pt.y);
        ctx.rotate(segAng);
        ctx.globalAlpha = a * 0.2;
        ctx.drawImage(plmHalo, -plmW * 0.5, -plmH * 0.5, plmW, plmH);
        ctx.restore();

        if ((n / stepIdx) % 2 === 0) {
          ctx.globalAlpha = a * 0.22;
          ctx.drawImage(mainHaloSp, pt.x - dotR, pt.y - dotR, dotR * 2, dotR * 2);
        }
      }
    }

    // 第 2 层：起点电离晕、分叉根部耀斑与落点三层爆闪光晕
    var rOriginOut = ef.width * 5.2 * hs;
    var rOriginIn = ef.width * 2.3;
    ctx.globalAlpha = a * 0.46;
    ctx.drawImage(outerHaloSp, ef.x1 - rOriginOut, ef.y1 - rOriginOut, rOriginOut * 2, rOriginOut * 2);
    ctx.globalAlpha = a * 0.68;
    ctx.drawImage(secHaloSp, ef.x1 - rOriginIn, ef.y1 - rOriginIn, rOriginIn * 2, rOriginIn * 2);

    for (var fb = 0; fb < ef.geom.forks.length; fb++) {
      var forkObj = ef.geom.forks[fb];
      var fOrig = forkObj.origin;
      var rNode = ef.width * 3.2 * hs;
      ctx.globalAlpha = a * 0.5;
      ctx.drawImage(secHaloSp, fOrig.x - rNode, fOrig.y - rNode, rNode * 2, rNode * 2);
    }

    var rHitOut = ef.width * 9.0 * hs;
    var rHitMid = ef.width * 5.2 * hs;
    var rHitCore = ef.width * 2.3;
    ctx.globalAlpha = a * 0.46;
    ctx.drawImage(outerHaloSp, ef.x2 - rHitOut, ef.y2 - rHitOut, rHitOut * 2, rHitOut * 2);
    ctx.globalAlpha = a * 0.78;
    ctx.drawImage(mainHaloSp, ef.x2 - rHitMid, ef.y2 - rHitMid, rHitMid * 2, rHitMid * 2);
    ctx.globalAlpha = a * 0.92;
    ctx.drawImage(whiteSp, ef.x2 - rHitCore, ef.y2 - rHitCore, rHitCore * 2, rHitCore * 2);

    // 第 3 层：伴生交缠副电弧
    if (ef.geom.subTrunk) {
      strokeTaperedPoly(ef.geom.subTrunk, ef.width * 1.6, ef.width * 0.9, ef.glowColor, a * 0.22);
      strokeTaperedPoly(ef.geom.subTrunk, ef.width * 0.68, ef.width * 0.36, ef.secondaryColor, a * 0.58);
      strokeTaperedPoly(ef.geom.subTrunk, ef.width * 0.26, ef.width * 0.14, '#ffffff', a * 0.72);
    }

    // 第 4 层：主干四重色温锥度电弧（仅核心/外鞘启用适度 shadowBlur 防重影卡顿）
    strokeTaperedPoly(trunk, ef.width * 3.2, ef.width * 2.1, ef.glowColor, a * 0.22);
    strokeTaperedPoly(trunk, ef.width * 1.85, ef.width * 1.2, ef.color, a * 0.56, ef.color, ef.width * 2.0);
    strokeTaperedPoly(trunk, ef.width * 1.02, ef.width * 0.68, ef.secondaryColor, a * 0.85);
    strokeTaperedPoly(trunk, ef.width * 0.46, ef.width * 0.28, ef.coreColor, Math.min(1, a * 1.0), '#ffffff', ef.width * 1.1);

    // 第 5 层：主分叉三重尖刺收梢 + 二级毛细电须二重微丝
    for (var b = 0; b < ef.geom.forks.length; b++) {
      var fkPts = ef.geom.forks[b].pts;
      strokeTaperedPoly(fkPts, ef.width * 1.75, ef.width * 0.42, ef.glowColor, a * 0.18);
      strokeTaperedPoly(fkPts, ef.width * 0.78, ef.width * 0.16, ef.color, a * 0.62);
      strokeTaperedPoly(fkPts, ef.width * 0.32, ef.width * 0.08, ef.coreColor, a * 0.88);
    }

    for (var tw = 0; tw < ef.geom.twigs.length; tw++) {
      var twPts = ef.geom.twigs[tw];
      strokeTaperedPoly(twPts, ef.width * 0.42, ef.width * 0.1, ef.secondaryColor, a * 0.48);
      strokeTaperedPoly(twPts, ef.width * 0.18, ef.width * 0.06, ef.coreColor, a * 0.72);
    }

    // 第 6 层：沿电离路径游离电珠与落点飞溅电星
    if (p < 0.62 && trunk && trunk.length > 3 && Math.random() < 0.48) {
      var randIdx = 1 + ((Math.random() * (trunk.length - 2)) | 0);
      var orbPt = trunk[randIdx];
      spawnSingleParticle({
        x: orbPt.x,
        y: orbPt.y,
        shape: Math.random() < 0.6 ? 'orb' : 'flare',
        colors: [ef.secondaryColor, ef.color, '#ffffff'],
        size: [1.1, 2.3],
        speed: [14, 48],
        life: [180, 340],
      });
    }

    if (p < 0.65 && Math.random() < 0.65) {
      spawnSingleParticle({
        x: ef.x2,
        y: ef.y2,
        shape: 'spark',
        colors: [ef.color, ef.secondaryColor, '#ffffff'],
        size: [1.4, 2.8],
        speed: [75, 190],
        life: [160, 320],
        trail: 3,
      });
    }
    ctx.restore();
  }

  // 4. 5层柔边流光弹道 streamer
  function streamer(opts) {
    opts = opts || {};
    var x1 = opts.x1 !== undefined ? opts.x1 : opts.from ? opts.from.x : state.width * 0.25;
    var y1 = opts.y1 !== undefined ? opts.y1 : opts.from ? opts.from.y : state.height * 0.5;
    var x2 = opts.x2 !== undefined ? opts.x2 : opts.to ? opts.to.x : state.width * 0.75;
    var y2 = opts.y2 !== undefined ? opts.y2 : opts.to ? opts.to.y : state.height * 0.5;
    var dx = x2 - x1;
    var dy = y2 - y1;
    var dist = Math.hypot(dx, dy) || 1;
    var nx = -dy / dist;
    var ny = dx / dist;
    var arcAmt = opts.arc !== undefined ? opts.arc : dist * 0.22 * (opts.curveSide || -1);
    arcAmt = clamp(arcAmt, -dist * 0.32, dist * 0.32);

    return addEffect({
      kind: 'streamer',
      layer: opts.layer || 'sky',
      blend: opts.blend || 'lighter',
      delay: opts.delay || 0,
      duration: opts.duration || 420,
      x1: x1,
      y1: y1,
      cx: (x1 + x2) * 0.5 + nx * arcAmt,
      cy: (y1 + y2) * 0.5 + ny * arcAmt,
      x2: x2,
      y2: y2,
      nx: nx,
      ny: ny,
      width: opts.width || 11,
      tailLength: opts.tailLength || 0.38,
      wobble: opts.wobble || 0,
      headOrb: opts.headOrb !== false,
      sparks: opts.sparks !== false,
      sparkColors: opts.sparkColors || [opts.color || '#67e8f9', '#ffffff'],
      color: opts.color || '#67e8f9',
      coreColor: opts.coreColor || '#ffffff',
      onArrive: opts.onArrive || null,
      arrived: false,
    });
  }

  function evalQuadBezier(p0x, p0y, cx, cy, p1x, p1y, t) {
    var inv = 1 - t;
    return {
      x: inv * inv * p0x + 2 * inv * t * cx + t * t * p1x,
      y: inv * inv * p0y + 2 * inv * t * cy + t * t * p1y,
    };
  }

  function drawStreamer(ctx, p, ef) {
    var travelSpan = 0.78;
    var headT = clamp(easeInOutQuad(Math.min(1, p / travelSpan)), 0, 1);
    var tailT = clamp(headT - ef.tailLength * (1 - Math.max(0, p - travelSpan) / (1 - travelSpan)), 0, 1);
    if (p >= travelSpan) {
      tailT = lerp(Math.max(0, 1 - ef.tailLength), 1, (p - travelSpan) / (1 - travelSpan));
    }

    if (!ef.arrived && p >= travelSpan) {
      ef.arrived = true;
      if (typeof ef.onArrive === 'function') ef.onArrive({ x: ef.x2, y: ef.y2 });
    }

    var samples = 20;
    var pts = [];
    for (var i = 0; i <= samples; i++) {
      var u = lerp(tailT, headT, i / samples);
      var pt = evalQuadBezier(ef.x1, ef.y1, ef.cx, ef.cy, ef.x2, ef.y2, u);
      if (ef.wobble) {
        var wb = Math.sin(u * 14 - p * 16) * ef.wobble * (1 - u * 0.4);
        pt.x += ef.nx * wb;
        pt.y += ef.ny * wb;
      }
      pts.push(pt);
    }

    var headPt = pts[pts.length - 1];
    if (ef.sparks && p < travelSpan && Math.random() < 0.85) {
      spawnSingleParticle({
        x: headPt.x,
        y: headPt.y,
        shape: Math.random() < 0.5 ? 'wisp' : 'orb',
        colors: ef.sparkColors,
        size: [1.8, 4.0],
        speed: [18, 72],
        life: [240, 460],
        trail: 3,
      });
    }

    ctx.save();
    var fadeEnv = p > 0.85 ? (1 - p) / 0.15 : 1;

    // 丝带体积尾晕床（沿切向拉伸外色温气鞘）
    var tailHaloPlm = getPlumeSprite(deriveHaloColor(ef.color));
    var haloFracPts = [0.45, 0.8];
    for (var hf = 0; hf < haloFracPts.length; hf++) {
      var hIdx = Math.max(1, Math.min(pts.length - 2, Math.floor((pts.length - 1) * haloFracPts[hf])));
      var hp = pts[hIdx];
      var hpNext = pts[hIdx + 1];
      var hpPrev = pts[hIdx - 1];
      var sAng = Math.atan2(hpNext.y - hpPrev.y, hpNext.x - hpPrev.x);
      ctx.save();
      ctx.translate(hp.x, hp.y);
      ctx.rotate(sAng);
      ctx.globalAlpha = 0.26 * fadeEnv;
      ctx.drawImage(tailHaloPlm, -ef.width * 3.4, -ef.width * 1.9, ef.width * 6.8, ef.width * 3.8);
      ctx.restore();
    }

    var ribbonPasses = [
      { wMul: 2.8, al: 0.06, col: ef.color },
      { wMul: 1.9, al: 0.16, col: ef.color },
      { wMul: 1.25, al: 0.36, col: ef.color },
      { wMul: 0.72, al: 0.75, col: ef.color },
      { wMul: 0.3, al: 0.95, col: ef.coreColor },
    ];

    for (var pass = 0; pass < ribbonPasses.length; pass++) {
      var rp = ribbonPasses[pass];
      ctx.fillStyle = rp.col;
      ctx.globalAlpha = rp.al * fadeEnv;

      ctx.beginPath();
      for (var k = 0; k < pts.length; k++) {
        var frac = k / (pts.length - 1);
        var prev = pts[Math.max(0, k - 1)];
        var next = pts[Math.min(pts.length - 1, k + 1)];
        var tdx = next.x - prev.x;
        var tdy = next.y - prev.y;
        var tl = Math.hypot(tdx, tdy) || 1;
        var pnx = -tdy / tl;
        var pny = tdx / tl;
        var taper = Math.sin(frac * Math.PI * 0.92);
        var hw = ef.width * 0.5 * rp.wMul * Math.pow(frac, 0.75) * (0.35 + 0.65 * taper);
        var lx = pts[k].x + pnx * hw;
        var ly = pts[k].y + pny * hw;
        if (k === 0) ctx.moveTo(lx, ly);
        else ctx.lineTo(lx, ly);
      }
      for (var k2 = pts.length - 1; k2 >= 0; k2--) {
        var frac2 = k2 / (pts.length - 1);
        var prev2 = pts[Math.max(0, k2 - 1)];
        var next2 = pts[Math.min(pts.length - 1, k2 + 1)];
        var tdx2 = next2.x - prev2.x;
        var tdy2 = next2.y - prev2.y;
        var tl2 = Math.hypot(tdx2, tdy2) || 1;
        var pnx2 = -tdy2 / tl2;
        var pny2 = tdx2 / tl2;
        var taper2 = Math.sin(frac2 * Math.PI * 0.92);
        var hw2 = ef.width * 0.5 * rp.wMul * Math.pow(frac2, 0.75) * (0.35 + 0.65 * taper2);
        ctx.lineTo(pts[k2].x - pnx2 * hw2, pts[k2].y - pny2 * hw2);
      }
      ctx.closePath();
      ctx.fill();
    }

    if (ef.headOrb && p < travelSpan + 0.08) {
      var sp = getGlowSprite(ef.color);
      var coreSp = getGlowSprite(ef.coreColor);
      var orbOuter = ef.width * 3.0;
      var orbInner = ef.width * 1.6;
      ctx.globalAlpha = 0.45 * fadeEnv;
      ctx.drawImage(sp, headPt.x - orbOuter, headPt.y - orbOuter, orbOuter * 2, orbOuter * 2);
      ctx.globalAlpha = 0.92 * fadeEnv;
      ctx.drawImage(coreSp, headPt.x - orbInner, headPt.y - orbInner, orbInner * 2, orbInner * 2);
    }
    ctx.restore();
  }

  // 5. 3D 透视旋转道法法阵 magicCircle（柔光双层描边 + 废除 strokeRect + 常驻呼吸灵尘）
  function drawMagicCircleGeometry(ctx, mc, tSec, alpha) {
    var R = mc.radius * (mc.scaleNow || 1);
    var col = mc.pulseColor && mc.pulseAmt > 0.05 ? mc.pulseColor : mc.color;
    var sec = mc.secondaryColor || '#ffffff';
    var style = mc.style || 'bagua';
    var rot = tSec * mc.rotationSpeed;
    var boost = 1 + (mc.pulseAmt || 0) * 0.55;

    ctx.save();
    ctx.globalCompositeOperation = mc.blend || 'lighter';
    ctx.translate(mc.x, mc.y);
    ctx.scale(1, mc.tilt);

    var bgGrad = ctx.createRadialGradient(0, 0, R * 0.08, 0, 0, R * 1.18);
    bgGrad.addColorStop(0, rgbaStr(col, 0.24 * alpha * boost));
    bgGrad.addColorStop(0.55, rgbaStr(col, 0.09 * alpha * boost));
    bgGrad.addColorStop(1, rgbaStr(col, 0));
    ctx.fillStyle = bgGrad;
    ctx.beginPath();
    ctx.arc(0, 0, R * 1.18, 0, TWO_PI);
    ctx.fill();

    // 柔雾阵环光晕床：赋予法阵边缘氤氲灵雾
    var bedRing = getSoftRingSprite(col);
    ctx.globalAlpha = 0.34 * alpha * boost;
    ctx.drawImage(bedRing, -R * 1.08, -R * 1.08, R * 2.16, R * 2.16);

    // 三层柔光描边辅助：外扩雾晕 + 中层辉光 + 核心亮纹
    function glowStroke(width, color, aScale) {
      ctx.strokeStyle = color;
      ctx.lineWidth = width * 4.6;
      ctx.globalAlpha = alpha * 0.09 * aScale;
      ctx.stroke();
      ctx.lineWidth = width * 2.1;
      ctx.globalAlpha = alpha * 0.28 * aScale;
      ctx.stroke();
      ctx.lineWidth = width;
      ctx.globalAlpha = alpha * 0.88 * aScale;
      ctx.stroke();
    }

    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';

    ctx.save();
    ctx.rotate(rot);
    var baseLw = (mc.lineWidth || 2.1) * boost;
    ctx.beginPath();
    ctx.arc(0, 0, R, 0, TWO_PI);
    glowStroke(baseLw, col, 0.95);

    ctx.beginPath();
    ctx.arc(0, 0, R * 0.87, 0, TWO_PI);
    glowStroke(1.2, col, 0.58);

    var tickCount = style === 'thunder' ? 12 : style === 'blood' ? 16 : 8;
    for (var i = 0; i < tickCount; i++) {
      var a = (i / tickCount) * TWO_PI;
      ctx.save();
      ctx.rotate(a);
      var tickCol = i % 2 === 0 ? col : sec;
      if (style === 'bagua' || style === 'spirit') {
        for (var row = 0; row < 3; row++) {
          var ry = -R * (0.91 + row * 0.025);
          ctx.beginPath();
          if ((i + row) % 2 === 0) {
            ctx.moveTo(-R * 0.09, ry);
            ctx.lineTo(R * 0.09, ry);
          } else {
            ctx.moveTo(-R * 0.09, ry);
            ctx.lineTo(-R * 0.02, ry);
            ctx.moveTo(R * 0.02, ry);
            ctx.lineTo(R * 0.09, ry);
          }
          glowStroke(1.5, tickCol, 0.82);
        }
      } else if (style === 'blood') {
        ctx.beginPath();
        ctx.moveTo(-R * 0.06, -R * 0.88);
        ctx.lineTo(0, -R * 1.07);
        ctx.lineTo(R * 0.06, -R * 0.88);
        glowStroke(1.7, tickCol, 0.82);
      } else {
        ctx.beginPath();
        ctx.moveTo(0, -R * 0.88);
        ctx.lineTo(0, -R * 0.99);
        glowStroke(1.8, tickCol, 0.82);
      }
      ctx.restore();
    }
    ctx.restore();

    ctx.save();
    ctx.rotate(-rot * 1.35);

    if (style === 'bagua' || style === 'spirit') {
      var sides = style === 'bagua' ? 8 : 6;
      ctx.beginPath();
      for (var s = 0; s <= sides; s++) {
        var sa = (s / sides) * TWO_PI;
        var px = Math.cos(sa) * R * 0.74;
        var py = Math.sin(sa) * R * 0.74;
        if (s === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      }
      glowStroke(1.5 * boost, sec, 0.78);

      // 使用旋转路径绘制内接菱方阵并施加柔光描边
      for (var q = 0; q < 2; q++) {
        ctx.save();
        ctx.rotate((q * Math.PI) / 4);
        var hr = R * 0.48;
        ctx.beginPath();
        ctx.moveTo(-hr, -hr);
        ctx.lineTo(hr, -hr);
        ctx.lineTo(hr, hr);
        ctx.lineTo(-hr, hr);
        ctx.closePath();
        glowStroke(1.35 * boost, col, 0.72);
        ctx.restore();
      }
    } else if (style === 'thunder') {
      for (var tri = 0; tri < 2; tri++) {
        ctx.beginPath();
        for (var v = 0; v <= 3; v++) {
          var va = (v / 3) * TWO_PI + tri * Math.PI;
          var vx = Math.cos(va) * R * 0.76;
          var vy = Math.sin(va) * R * 0.76;
          if (v === 0) ctx.moveTo(vx, vy);
          else ctx.lineTo(vx, vy);
        }
        glowStroke(1.55 * boost, sec, 0.8);
      }
    } else if (style === 'blood') {
      ctx.beginPath();
      for (var st = 0; st <= 5; st++) {
        var starA = ((st * 2) % 5) * (TWO_PI / 5) - HALF_PI;
        var sx = Math.cos(starA) * R * 0.78;
        var sy = Math.sin(starA) * R * 0.78;
        if (st === 0) ctx.moveTo(sx, sy);
        else ctx.lineTo(sx, sy);
      }
      glowStroke(1.6 * boost, sec, 0.82);
    } else if (style === 'illusion') {
      for (var m = 0; m < 3; m++) {
        var ma = (m * TWO_PI) / 3;
        ctx.beginPath();
        ctx.arc(Math.cos(ma) * R * 0.22, Math.sin(ma) * R * 0.22, R * 0.56, 0, TWO_PI);
        glowStroke(1.45 * boost, sec, 0.75);
      }
    } else if (style === 'sword') {
      for (var sw = 0; sw < 4; sw++) {
        ctx.save();
        ctx.rotate(sw * HALF_PI);
        ctx.beginPath();
        ctx.moveTo(0, -R * 0.82);
        ctx.lineTo(R * 0.07, -R * 0.42);
        ctx.lineTo(0, -R * 0.22);
        ctx.lineTo(-R * 0.07, -R * 0.42);
        ctx.closePath();
        glowStroke(1.5 * boost, sec, 0.8);
        ctx.restore();
      }
    } else if (style === 'water') {
      var waveR = R * (0.45 + 0.14 * Math.sin(tSec * 3.6));
      ctx.beginPath();
      ctx.arc(0, 0, waveR, 0, TWO_PI);
      glowStroke(1.4 * boost, sec, 0.72);
      ctx.beginPath();
      ctx.arc(0, 0, R * 0.68, 0, TWO_PI);
      glowStroke(1.5 * boost, col, 0.78);
    } else {
      ctx.beginPath();
      for (var fl = 0; fl < 12; fl++) {
        var fa = (fl / 12) * TWO_PI;
        var fr = fl % 2 === 0 ? R * 0.8 : R * 0.5;
        var fx = Math.cos(fa) * fr;
        var fy = Math.sin(fa) * fr;
        if (fl === 0) ctx.moveTo(fx, fy);
        else ctx.lineTo(fx, fy);
      }
      ctx.closePath();
      glowStroke(1.55 * boost, sec, 0.78);
    }

    // 阵眼双层柔和灵核光晕
    var outerCoreSp = getGlowSprite(col);
    var outerCoreR = R * 0.42;
    ctx.globalAlpha = 0.36 * alpha * boost;
    ctx.drawImage(outerCoreSp, -outerCoreR, -outerCoreR, outerCoreR * 2, outerCoreR * 2);

    var coreSp = getGlowSprite(sec);
    var coreR = R * 0.24;
    ctx.globalAlpha = 0.78 * alpha * boost;
    ctx.drawImage(coreSp, -coreR, -coreR, coreR * 2, coreR * 2);

    ctx.restore();
    ctx.restore();
  }

  function syncPersistentCircleAnchor(mc) {
    if (!mc) return;
    mc.radius = (mc.baseRadius || mc.radius || 76) * (mc.rawScale ? 1 : scale());
    if (mc.anchorEl) {
      var pt = foot(mc.anchorEl);
      mc.x = pt.x + (mc.anchorOffsetX || 0);
      mc.y = pt.y + (mc.anchorOffsetY || 0);
    } else if (mc.anchorSide) {
      var spt = sideFootCenter(mc.anchorSide);
      mc.x = spt.x + (mc.anchorOffsetX || 0);
      mc.y = spt.y + (mc.anchorOffsetY || 0);
    }
  }

  function magicCircle(opts) {
    opts = opts || {};
    var tilt = opts.tilt !== undefined ? opts.tilt : 0.44;
    var defaultLayer = tilt < 0.85 ? 'ground' : 'sky';
    var baseRad = opts.radius || 76;
    var sMul = opts.rawScale ? 1 : scale();
    var obj = {
      kind: 'magicCircle',
      layer: opts.layer || defaultLayer,
      blend: opts.blend || 'lighter',
      delay: opts.delay || 0,
      duration: opts.duration || 950,
      x: opts.x !== undefined ? opts.x : state.width * 0.5,
      y: opts.y !== undefined ? opts.y : state.height * 0.6,
      baseRadius: baseRad,
      rawScale: !!opts.rawScale,
      radius: baseRad * sMul,
      anchorEl: opts.anchorEl || null,
      anchorSide: opts.anchorSide || null,
      anchorOffsetX: opts.anchorOffsetX || 0,
      anchorOffsetY: opts.anchorOffsetY || 0,
      tilt: tilt,
      color: opts.color || '#f5c451',
      secondaryColor: opts.secondaryColor || '#fff5d6',
      style: opts.style || 'bagua',
      rotationSpeed: opts.rotationSpeed !== undefined ? opts.rotationSpeed : 0.85,
      pulseSpeed: opts.pulseSpeed || 2.4,
      alphaMax: opts.alpha !== undefined ? opts.alpha : 0.92,
      lineWidth: opts.lineWidth || 2.0,
      elapsed: 0,
      ageSec: 0,
      stopping: false,
      stopElapsed: 0,
      stopDur: 400,
      pulseAmt: 0,
      pulseColor: null,
      scaleNow: 1,
    };
    if (obj.anchorEl || obj.anchorSide) {
      syncPersistentCircleAnchor(obj);
    }

    if (opts.persistent) {
      var spawnPersist = function () {
        if (obj.anchorEl || obj.anchorSide) syncPersistentCircleAnchor(obj);
        state.persistentCircles.push(obj);
        ensureLoop();
      };
      if (obj.delay > 0) after(obj.delay, spawnPersist);
      else spawnPersist();

      return {
        stop: function (fadeMs) {
          obj.stopping = true;
          obj.stopElapsed = 0;
          obj.stopDur = fadeMs || 380;
        },
        pulse: function (color, intensity) {
          obj.pulseColor = color || '#ffffff';
          obj.pulseAmt = clamp(intensity || 1.2, 0.4, 2.0);
          ensureLoop();
        },
        moveTo: function (x, y) {
          obj.x = x;
          obj.y = y;
          obj.radius = obj.baseRadius * (obj.rawScale ? 1 : scale());
        },
      };
    }

    return addEffect(obj);
  }

  function drawTimedMagicCircle(ctx, p, ef) {
    var fadeIn = p < 0.16 ? easeOutCubic(p / 0.16) : 1;
    var fadeOut = p > 0.72 ? 1 - (p - 0.72) / 0.28 : 1;
    ef.scaleNow = p < 0.18 ? lerp(0.55, 1.0, easeOutCubic(p / 0.18)) : 1 + Math.sin(p * Math.PI) * 0.04;
    drawMagicCircleGeometry(ctx, ef, ef.elapsed * 0.001, ef.alphaMax * fadeIn * fadeOut);
  }

  // 6. 像素高斯柔光冲击波 shockwave（完全取代硬描边圆环）
  function shockwave(opts) {
    opts = opts || {};
    var sMul = opts.rawScale ? 1 : scale();
    return addEffect({
      kind: 'shockwave',
      layer: opts.layer || (opts.tilt && opts.tilt < 0.8 ? 'ground' : 'sky'),
      blend: opts.blend || 'lighter',
      delay: opts.delay || 0,
      duration: opts.duration || 380,
      x: opts.x !== undefined ? opts.x : state.width * 0.5,
      y: opts.y !== undefined ? opts.y : state.height * 0.5,
      fromRadius: (opts.fromRadius || 12) * sMul,
      toRadius: (opts.toRadius || 110) * sMul,
      tilt: opts.tilt !== undefined ? opts.tilt : 1,
      color: opts.color || '#ffffff',
      coreColor: opts.coreColor || '#ffffff',
      lineWidth: (opts.lineWidth || 4.5) * sMul,
      double: opts.double !== undefined ? opts.double : true,
    });
  }

  function drawShockwave(ctx, p, ef) {
    var t = easeOutCubic(p);
    var r = lerp(ef.fromRadius, ef.toRadius, t);
    var alpha = Math.pow(1 - p, 1.35);

    var mainRing = getSoftRingSprite(ef.color);
    var coreRing = getSoftRingSprite(ef.coreColor);
    var drawRad = r * 1.38;

    ctx.save();
    ctx.translate(ef.x, ef.y);
    ctx.scale(1, ef.tilt);

    ctx.globalAlpha = alpha * 0.85;
    ctx.drawImage(mainRing, -drawRad, -drawRad, drawRad * 2, drawRad * 2);

    ctx.globalAlpha = alpha * 0.56;
    ctx.drawImage(coreRing, -drawRad * 0.96, -drawRad * 0.96, drawRad * 1.92, drawRad * 1.92);

    if (ef.double && p > 0.14) {
      var p2 = easeOutCubic((p - 0.14) / 0.86);
      var r2 = lerp(ef.fromRadius * 0.6, ef.toRadius * 0.72, p2);
      var dr2 = r2 * 1.36;
      ctx.globalAlpha = Math.pow(1 - p2, 1.5) * 0.68;
      ctx.drawImage(mainRing, -dr2, -dr2, dr2 * 2, dr2 * 2);
    }

    ctx.restore();
  }

  // 7. 彻底重写为双向羽化气团与粒子构成的气柱 pillar（零 fillRect！零硬边！）
  function pillar(opts) {
    opts = opts || {};
    var sMul = opts.rawScale ? 1 : scale();
    return addEffect({
      kind: 'pillar',
      layer: opts.layer || 'sky',
      blend: opts.blend || 'lighter',
      delay: opts.delay || 0,
      duration: opts.duration || 680,
      x: opts.x !== undefined ? opts.x : state.width * 0.5,
      y: opts.y !== undefined ? opts.y : state.height * 0.68,
      width: (opts.width || 62) * sMul,
      height: (opts.height || 240) * sMul,
      color: opts.color || '#60a5fa',
      coreColor: opts.coreColor || '#ffffff',
      helixCount: opts.helixCount !== undefined ? opts.helixCount : 2,
      helixTurns: opts.helixTurns || 2.5,
      sparks: opts.sparks !== false,
    });
  }

  function drawPillar(ctx, p, ef) {
    var openP = p < 0.2 ? easeOutCubic(p / 0.2) : 1;
    var fade = p < 0.52 ? 1 : 1 - Math.pow((p - 0.52) / 0.48, 1.28);
    var curW = ef.width * openP * (0.9 + 0.1 * Math.sin(p * 12));
    var curH = ef.height * (p < 0.22 ? lerp(0.32, 1, p / 0.22) : 1);

    // 真实上升粒子流支撑气柱动态体积
    if (ef.sparks && p < 0.72 && Math.random() < 0.75) {
      var shapePick = Math.random() < 0.55 ? 'wisp' : Math.random() < 0.5 ? 'orb' : 'ember';
      spawnSingleParticle({
        x: ef.x + rand(-curW * 0.34, curW * 0.34),
        y: ef.y - rand(0, curH * 0.24),
        shape: shapePick,
        colors: [ef.color, ef.coreColor, '#ffffff'],
        size: [2.2, 5.2],
        speed: [75, 195],
        angle: [-HALF_PI - 0.28, -HALF_PI + 0.28],
        vortex: 95 * (Math.random() < 0.5 ? 1 : -1),
        vortexCenter: { x: ef.x, y: ef.y - curH * 0.5 },
        gravity: -120,
        drag: 0.96,
        life: [360, 680],
        trail: shapePick === 'orb' ? 4 : 0,
      });
    }

    var plume = getPlumeSprite(ef.color);
    var corePlume = getPlumeSprite(ef.coreColor);
    var glowBase = getGlowSprite(ef.color);

    ctx.save();

    // 底部柔和落地光晕（无硬线椭圆）
    ctx.save();
    ctx.translate(ef.x, ef.y);
    ctx.scale(1, 0.34);
    ctx.globalAlpha = fade * 0.48;
    var baseR = curW * 1.1;
    ctx.drawImage(glowBase, -baseR, -baseR, baseR * 2, baseR * 2);
    ctx.restore();

    // 沿上升轨迹堆叠 9 团双向高斯羽化雾团，随高度正弦摇曳且首尾消散
    var cloudCount = 9;
    for (var c = 0; c < cloudCount; c++) {
      var u = (c + 0.5) / cloudCount;
      var flow = (u + p * 0.45) % 1;
      var env = Math.sin(flow * Math.PI);
      var yy = ef.y - flow * curH;
      var swayX = ef.x + Math.sin(flow * 5.2 - p * 10) * curW * 0.14;
      var rx = curW * lerp(0.55, 0.92, 1 - flow * 0.5);
      var ry = (curH / cloudCount) * 1.85;
      ctx.globalAlpha = fade * env * 0.42;
      ctx.drawImage(plume, swayX - rx, yy - ry, rx * 2, ry * 2);

      if (c < 6) {
        ctx.globalAlpha = fade * env * 0.28;
        ctx.drawImage(corePlume, swayX - rx * 0.52, yy - ry * 0.85, rx * 1.04, ry * 1.7);
      }
    }

    // 羽化螺旋能量光带（逐段渐隐不突兀）
    var segs = 26;
    for (var h = 0; h < ef.helixCount; h++) {
      var phase = (h * TWO_PI) / ef.helixCount - p * 9.0;
      var headPt = null;
      for (var i = 0; i < segs; i++) {
        var f0 = i / segs;
        var f1 = (i + 1) / segs;
        var envSeg = Math.sin(((f0 + f1) * 0.5) * Math.PI);
        if (envSeg <= 0.04) continue;
        var y0 = ef.y - f0 * curH;
        var y1 = ef.y - f1 * curH;
        var x0 = ef.x + Math.sin(f0 * TWO_PI * ef.helixTurns + phase) * curW * 0.48 * (1 - f0 * 0.25);
        var x1 = ef.x + Math.sin(f1 * TWO_PI * ef.helixTurns + phase) * curW * 0.48 * (1 - f1 * 0.25);

        ctx.beginPath();
        ctx.moveTo(x0, y0);
        ctx.lineTo(x1, y1);
        ctx.lineCap = 'round';
        ctx.strokeStyle = h % 2 === 0 ? ef.coreColor : ef.color;
        ctx.lineWidth = Math.max(1.0, 5.2 * envSeg * (1 - p * 0.4));
        ctx.globalAlpha = fade * envSeg * 0.28;
        ctx.stroke();

        ctx.lineWidth = Math.max(0.8, 2.2 * envSeg * (1 - p * 0.4));
        ctx.globalAlpha = fade * envSeg * 0.78;
        ctx.stroke();

        if (i === ((p * 0.85 * segs) | 0)) {
          headPt = { x: x1, y: y1 };
        }
      }
      if (headPt) {
        var hr = 11 * fade;
        ctx.globalAlpha = fade * 0.8;
        ctx.drawImage(glowBase, headPt.x - hr, headPt.y - hr, hr * 2, hr * 2);
      }
    }

    ctx.restore();
  }

  // 8. 流体化海浪 / 炎流推进 wave（完全删除上下直线截断框，改用羽化雾团群与柔弧）
  function wave(opts) {
    opts = opts || {};
    var sMul = opts.rawScale ? 1 : scale();
    return addEffect({
      kind: 'wave',
      layer: opts.layer || 'sky',
      blend: opts.blend || (opts.style === 'dark' ? 'source-over' : 'lighter'),
      delay: opts.delay || 0,
      duration: opts.duration || 680,
      x1: opts.x1 !== undefined ? opts.x1 : state.width * 0.22,
      y1: opts.y1 !== undefined ? opts.y1 : state.height * 0.52,
      x2: opts.x2 !== undefined ? opts.x2 : state.width * 0.85,
      y2: opts.y2 !== undefined ? opts.y2 : state.height * 0.52,
      width: opts.rawScale ? (opts.width || 165) : (opts.width || 165) * sMul,
      thickness: (opts.thickness || 76) * sMul,
      color: opts.color || '#38bdf8',
      secondaryColor: opts.secondaryColor || '#0284c7',
      foamColor: opts.foamColor || '#e0f2fe',
      style: opts.style || 'water',
      crests: opts.crests || 3,
      sparks: opts.sparks !== false,
    });
  }

  function drawWave(ctx, p, ef) {
    var prog = easeOutCubic(p);
    var fade = p < 0.14 ? p / 0.14 : p > 0.72 ? (1 - p) / 0.28 : 1;
    var dx = ef.x2 - ef.x1;
    var dy = ef.y2 - ef.y1;
    var ang = Math.atan2(dy, dx);
    var dist = Math.hypot(dx, dy);
    var pos = lerp(0, dist, prog);

    var mainPlume = getPlumeSprite(ef.color);
    var secPlume = getPlumeSprite(ef.secondaryColor);
    var foamPlume = getPlumeSprite(ef.foamColor);

    if (ef.sparks && p < 0.78 && Math.random() < 0.55) {
      var ux = Math.cos(ang);
      var uy = Math.sin(ang);
      var spanRand = rand(-ef.width * 0.36, ef.width * 0.36);
      var sx = ef.x1 + ux * pos - uy * spanRand;
      var sy = ef.y1 + uy * pos + ux * spanRand;
      spawnSingleParticle({
        x: sx,
        y: sy,
        shape: ef.style === 'fire' ? 'ember' : 'wisp',
        colors: [ef.foamColor, ef.color, '#ffffff'],
        size: [2.8, 5.4],
        baseAngle: ang,
        spread: 0.9,
        speed: [55, 165],
        life: [260, 520],
        trail: 3,
      });
    }

    ctx.save();
    ctx.translate(ef.x1, ef.y1);
    ctx.rotate(ang);

    var segs = 18;
    for (var c = 0; c < ef.crests; c++) {
      var offX = pos - c * ef.thickness * 0.4;
      var halfSpan = ef.width * (0.5 + c * 0.1);
      var crestAlpha = fade * (0.76 - c * 0.16);

      // 沿浪头曲线分布 18 团交叠羽化流体气团，两端按 sin(u*PI) 自然收尖无直边截断
      for (var i = 0; i <= segs; i++) {
        var frac = i / segs;
        var env = Math.sin(frac * Math.PI);
        if (env <= 0.05) continue;
        var yy = lerp(-halfSpan, halfSpan, frac);
        var ripple = Math.sin(frac * Math.PI * 3 + p * 12 + c * 1.4) * 14 + env * 24;
        var xx = offX + ripple;

        var rx = ef.thickness * (0.58 + 0.3 * env);
        var ry = (halfSpan * 2 / segs) * (1.7 + 0.8 * env);

        ctx.globalAlpha = crestAlpha * env * 0.34;
        ctx.drawImage(secPlume, xx - rx * 1.25, yy - ry, rx * 1.65, ry * 2);

        ctx.globalAlpha = crestAlpha * env * 0.42;
        ctx.drawImage(mainPlume, xx - rx * 0.72, yy - ry * 0.92, rx * 1.15, ry * 1.84);

        if (i % 2 === 0) {
          var fr = ry * 0.72;
          ctx.globalAlpha = crestAlpha * env * 0.48;
          ctx.drawImage(foamPlume, xx - fr * 0.45, yy - fr, fr * 1.2, fr * 2);
        }
      }

      // 分段渐隐的波脊泡沫柔弧（两端渐变至透明）
      ctx.lineCap = 'round';
      for (var j = 0; j < segs; j++) {
        var u0 = j / segs;
        var u1 = (j + 1) / segs;
        var eMid = Math.sin(((u0 + u1) * 0.5) * Math.PI);
        if (eMid <= 0.06) continue;
        var y0 = lerp(-halfSpan, halfSpan, u0);
        var y1 = lerp(-halfSpan, halfSpan, u1);
        var x0 = offX + Math.sin(u0 * Math.PI * 3 + p * 12 + c * 1.4) * 14 + Math.sin(u0 * Math.PI) * 24;
        var x1 = offX + Math.sin(u1 * Math.PI * 3 + p * 12 + c * 1.4) * 14 + Math.sin(u1 * Math.PI) * 24;
        ctx.beginPath();
        ctx.moveTo(x0, y0);
        ctx.lineTo(x1, y1);
        ctx.strokeStyle = ef.foamColor;
        ctx.lineWidth = (c === 0 ? 5.6 : 3.8) * eMid;
        ctx.globalAlpha = crestAlpha * eMid * 0.25;
        ctx.stroke();

        ctx.lineWidth = (c === 0 ? 2.4 : 1.6) * eMid;
        ctx.globalAlpha = crestAlpha * eMid * 0.78;
        ctx.stroke();
      }
    }

    ctx.restore();
  }

  // 9. 地面熔岩/灵能龟裂纹 groundCrack
  function buildCrackPaths(radius, branches) {
    var lines = [];
    for (var i = 0; i < branches; i++) {
      var baseA = (i / branches) * TWO_PI + rand(-0.24, 0.24);
      var pts = [{ x: 0, y: 0 }];
      var steps = 5;
      var curR = 0;
      var curA = baseA;
      for (var s = 1; s <= steps; s++) {
        curR = (s / steps) * radius * rand(0.75, 1.12);
        curA += rand(-0.34, 0.34);
        pts.push({ x: Math.cos(curA) * curR, y: Math.sin(curA) * curR * 0.48 });
      }
      lines.push(pts);
      if (i % 2 === 0 && pts.length > 3) {
        var forkStart = pts[2];
        var fa = curA + (Math.random() < 0.5 ? 0.65 : -0.65);
        lines.push([
          forkStart,
          {
            x: forkStart.x + Math.cos(fa) * radius * 0.36,
            y: forkStart.y + Math.sin(fa) * radius * 0.2,
          },
        ]);
      }
    }
    return lines;
  }

  function groundCrack(opts) {
    opts = opts || {};
    var sMul = opts.rawScale ? 1 : scale();
    var radius = (opts.radius || 84) * sMul;
    var branches = opts.branches || 7;
    return addEffect({
      kind: 'groundCrack',
      layer: opts.layer || 'ground',
      blend: opts.blend || 'source-over',
      delay: opts.delay || 0,
      duration: opts.duration || 1600,
      x: opts.x !== undefined ? opts.x : state.width * 0.5,
      y: opts.y !== undefined ? opts.y : state.height * 0.62,
      radius: radius,
      color: opts.color || '#f97316',
      crackColor: opts.crackColor || '#090b10',
      lineWidth: (opts.lineWidth || 3.4) * sMul,
      paths: buildCrackPaths(radius, branches),
    });
  }

  function drawGroundCrack(ctx, p, ef) {
    var open = clamp(p * 6, 0, 1);
    var fade = p < 0.65 ? 1 : Math.pow(1 - (p - 0.65) / 0.35, 1.3);

    ctx.save();
    ctx.translate(ef.x, ef.y);

    // 地裂核心柔和余温光池
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.scale(1, 0.48);
    var sp = getPlumeSprite(ef.color);
    var gr = ef.radius * 0.85;
    ctx.globalAlpha = fade * 0.26;
    ctx.drawImage(sp, -gr, -gr, gr * 2, gr * 2);
    ctx.restore();

    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = ef.crackColor;
    ctx.globalAlpha = fade * 0.88;
    ctx.lineWidth = ef.lineWidth * 1.45;
    for (var i = 0; i < ef.paths.length; i++) {
      var pts = ef.paths[i];
      var limit = Math.max(2, Math.ceil(pts.length * open));
      ctx.beginPath();
      ctx.moveTo(pts[0].x, pts[0].y);
      for (var j = 1; j < limit; j++) ctx.lineTo(pts[j].x, pts[j].y);
      ctx.stroke();
    }

    ctx.globalCompositeOperation = 'lighter';

    // 裂纹中段与末端渗光晕斑（透视压扁适配地裂视角）
    var crackNodeSp = getGlowSprite(ef.color);
    var nodeR = ef.lineWidth * 3.2;
    ctx.save();
    for (var ni = 0; ni < ef.paths.length; ni++) {
      var nPts = ef.paths[ni];
      var nLimit = Math.max(2, Math.ceil(nPts.length * open));
      var midIdx = Math.max(1, (nLimit * 0.55) | 0);
      var endIdx = nLimit - 1;
      var marks = [nPts[midIdx], nPts[endIdx]];
      for (var mk = 0; mk < marks.length; mk++) {
        var mp = marks[mk];
        ctx.save();
        ctx.translate(mp.x, mp.y);
        ctx.scale(1, 0.52);
        ctx.globalAlpha = fade * 0.28;
        ctx.drawImage(crackNodeSp, -nodeR, -nodeR, nodeR * 2, nodeR * 2);
        ctx.restore();
      }
    }
    ctx.restore();

    function strokeCrackPass(width, col, alphaVal) {
      ctx.strokeStyle = col;
      ctx.globalAlpha = alphaVal;
      ctx.lineWidth = width;
      for (var ci = 0; ci < ef.paths.length; ci++) {
        var cPts = ef.paths[ci];
        var cLim = Math.max(2, Math.ceil(cPts.length * open));
        ctx.beginPath();
        ctx.moveTo(cPts[0].x, cPts[0].y);
        for (var cj = 1; cj < cLim; cj++) ctx.lineTo(cPts[cj].x, cPts[cj].y);
        ctx.stroke();
      }
    }

    // 三层地裂渗光：宽幅渗光 + 主色熔光 + 炽白裂芯
    strokeCrackPass(ef.lineWidth * 3.0, ef.color, fade * 0.14);
    strokeCrackPass(ef.lineWidth * 1.5, ef.color, fade * 0.46);
    strokeCrackPass(ef.lineWidth * 0.46, '#ffffff', fade * (0.88 - p * 0.25));

    ctx.restore();
  }

  // 10. 具象修仙器物飞行特效：丹药 / 符箓 / 阵盘 artifact（柔边纸符与宝珠光晕）
  function artifact(opts) {
    opts = opts || {};
    var x1 = opts.x1 !== undefined ? opts.x1 : opts.x !== undefined ? opts.x : state.width * 0.5;
    var y1 = opts.y1 !== undefined ? opts.y1 : opts.y !== undefined ? opts.y : state.height * 0.5;
    var x2 = opts.x2 !== undefined ? opts.x2 : x1;
    var y2 = opts.y2 !== undefined ? opts.y2 : y1;

    return addEffect({
      kind: 'artifact',
      propKind: opts.kind || 'pill',
      layer: opts.layer || 'sky',
      blend: opts.blend || 'source-over',
      delay: opts.delay || 0,
      duration: opts.duration || 560,
      x1: x1,
      y1: y1,
      x2: x2,
      y2: y2,
      arc: opts.arc !== undefined ? opts.arc : -54,
      size: opts.size || 24,
      scaleFrom: opts.scaleFrom || 0.72,
      scaleTo: opts.scaleTo || 1.15,
      tiltFrom: opts.tiltFrom || 0.92,
      tiltTo: opts.tiltTo || (opts.kind === 'disk' ? 0.44 : 1),
      spin: opts.spin !== undefined ? opts.spin : opts.kind === 'talisman' ? 8.5 : 3.2,
      color: opts.color || '#f5c451',
      accentColor: opts.accentColor || '#ffffff',
      glowColor: opts.glowColor || opts.color || '#f5c451',
      trail: opts.trail !== false,
      onArrive: opts.onArrive || null,
      arrived: false,
    });
  }

  function drawArtifact(ctx, p, ef) {
    var moveT = easeInOutQuad(Math.min(1, p / 0.85));
    var px = lerp(ef.x1, ef.x2, moveT);
    var py = lerp(ef.y1, ef.y2, moveT) + Math.sin(moveT * Math.PI) * ef.arc;

    if (!ef.arrived && p >= 0.85) {
      ef.arrived = true;
      if (typeof ef.onArrive === 'function') ef.onArrive({ x: ef.x2, y: ef.y2 });
    }

    var fade = p < 0.14 ? p / 0.14 : p > 0.82 ? (1 - p) / 0.18 : 1;
    var sc = lerp(ef.scaleFrom, ef.scaleTo, easeOutCubic(p));
    var rot = p * ef.spin;
    var sz = ef.size * sc;

    if (ef.trail && p < 0.84 && Math.random() < 0.85) {
      spawnSingleParticle({
        x: px,
        y: py,
        shape: Math.random() < 0.55 ? 'wisp' : 'orb',
        colors: [ef.glowColor, ef.accentColor],
        size: [1.8, 3.8],
        speed: [14, 48],
        life: [240, 440],
        trail: 3,
      });
    }

    ctx.save();
    ctx.translate(px, py);
    ctx.globalAlpha = fade;

    var outerPlm = getPlumeSprite(deriveHaloColor(ef.glowColor));
    var sp = getGlowSprite(ef.glowColor);
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.globalAlpha = fade * 0.52;
    ctx.drawImage(outerPlm, -sz * 3.1, -sz * 3.1, sz * 6.2, sz * 6.2);
    ctx.globalAlpha = fade * 0.88;
    ctx.drawImage(sp, -sz * 1.9, -sz * 1.9, sz * 3.8, sz * 3.8);
    ctx.restore();

    if (ef.propKind === 'pill') {
      var g = ctx.createRadialGradient(-sz * 0.25, -sz * 0.25, sz * 0.06, 0, 0, sz * 0.65);
      g.addColorStop(0, '#ffffff');
      g.addColorStop(0.28, ef.accentColor);
      g.addColorStop(0.75, ef.color);
      g.addColorStop(1, 'rgba(10,16,24,0.85)');
      ctx.fillStyle = g;
      ctx.beginPath();
      ctx.arc(0, 0, sz * 0.62, 0, TWO_PI);
      ctx.fill();

      ctx.globalCompositeOperation = 'lighter';
      ctx.strokeStyle = ef.accentColor;
      ctx.lineWidth = 1.6;
      ctx.rotate(rot);
      ctx.globalAlpha = fade * 0.72;
      ctx.beginPath();
      ctx.ellipse(0, 0, sz * 1.1, sz * 0.36, 0.35, 0, TWO_PI);
      ctx.stroke();
    } else if (ef.propKind === 'talisman') {
      ctx.rotate(Math.sin(p * 12) * 0.28 + rot * 0.18);
      var pw = sz * 0.56;
      var ph = sz * 1.18;
      var cr = 3.5;
      var tg = ctx.createLinearGradient(0, -ph, 0, ph);
      tg.addColorStop(0, '#fef08a');
      tg.addColorStop(0.5, '#facc15');
      tg.addColorStop(1, '#ca8a04');
      ctx.fillStyle = tg;
      ctx.strokeStyle = 'rgba(255,255,255,0.85)';
      ctx.lineWidth = 1.2;
      ctx.beginPath();
      ctx.moveTo(-pw + cr, -ph);
      ctx.lineTo(pw - cr, -ph);
      ctx.quadraticCurveTo(pw, -ph, pw, -ph + cr);
      ctx.lineTo(pw, ph - cr);
      ctx.quadraticCurveTo(pw, ph, pw - cr, ph);
      ctx.lineTo(-pw + cr, ph);
      ctx.quadraticCurveTo(-pw, ph, -pw, ph - cr);
      ctx.lineTo(-pw, -ph + cr);
      ctx.quadraticCurveTo(-pw, -ph, -pw + cr, -ph);
      ctx.closePath();
      ctx.fill();
      ctx.stroke();

      ctx.save();
      ctx.globalCompositeOperation = 'lighter';
      ctx.strokeStyle = ef.glowColor;
      ctx.lineWidth = 4.2;
      ctx.globalAlpha = fade * 0.36;
      ctx.stroke();
      ctx.restore();

      ctx.strokeStyle = ef.color || '#dc2626';
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.lineWidth = 2.1;
      ctx.beginPath();
      ctx.moveTo(0, -ph * 0.7);
      ctx.lineTo(-pw * 0.5, -ph * 0.32);
      ctx.lineTo(pw * 0.5, -ph * 0.05);
      ctx.lineTo(-pw * 0.42, ph * 0.34);
      ctx.lineTo(pw * 0.38, ph * 0.68);
      ctx.moveTo(-pw * 0.48, -ph * 0.5);
      ctx.lineTo(pw * 0.48, -ph * 0.5);
      ctx.stroke();

      ctx.save();
      ctx.globalCompositeOperation = 'lighter';
      ctx.strokeStyle = ef.accentColor || '#ffffff';
      ctx.lineWidth = 0.9;
      ctx.globalAlpha = fade * 0.65;
      ctx.stroke();
      ctx.restore();
    } else {
      var curTilt = lerp(ef.tiltFrom, ef.tiltTo, moveT);
      ctx.scale(1, curTilt);
      ctx.rotate(rot);

      ctx.fillStyle = rgbaStr('#181f29', 0.92);
      ctx.strokeStyle = ef.color;
      ctx.lineWidth = 2.4;
      ctx.beginPath();
      for (var i = 0; i < 8; i++) {
        var a = (i / 8) * TWO_PI + Math.PI / 8;
        var ox = Math.cos(a) * sz;
        var oy = Math.sin(a) * sz;
        if (i === 0) ctx.moveTo(ox, oy);
        else ctx.lineTo(ox, oy);
      }
      ctx.closePath();
      ctx.fill();
      ctx.stroke();

      ctx.save();
      ctx.globalCompositeOperation = 'lighter';
      ctx.strokeStyle = ef.glowColor;
      ctx.lineWidth = 5.4;
      ctx.globalAlpha = fade * 0.34;
      ctx.stroke();
      ctx.restore();

      ctx.strokeStyle = ef.accentColor;
      ctx.lineWidth = 1.4;
      ctx.beginPath();
      ctx.arc(0, 0, sz * 0.62, 0, TWO_PI);
      ctx.stroke();

      ctx.fillStyle = ef.color;
      ctx.beginPath();
      ctx.arc(0, 0, sz * 0.24, 0, TWO_PI);
      ctx.fill();
    }

    ctx.restore();
  }

  function custom(opts) {
    opts = opts || {};
    return addEffect({
      kind: 'custom',
      layer: opts.layer || 'sky',
      blend: opts.blend || 'lighter',
      delay: opts.delay || 0,
      duration: opts.duration || 500,
      state: opts.state || {},
      drawFn: opts.draw,
    });
  }

  // ---------------------------------------------------------------------------
  // 主动画帧循环
  // ---------------------------------------------------------------------------
  function drawListForLayer(ctx, layer, nowSec) {
    for (var m = 0; m < state.persistentCircles.length; m++) {
      var mc = state.persistentCircles[m];
      if (mc.layer !== layer) continue;
      var baseAlpha = mc.alphaMax * (0.86 + 0.14 * Math.sin(nowSec * mc.pulseSpeed));
      if (mc.stopping) {
        baseAlpha *= Math.max(0, 1 - mc.stopElapsed / mc.stopDur);
      }
      if (mc.ageSec < 0.35) {
        var open = easeOutCubic(mc.ageSec / 0.35);
        mc.scaleNow = lerp(0.45, 1.0, open);
        baseAlpha *= open;
      } else {
        mc.scaleNow = 1;
      }
      drawMagicCircleGeometry(ctx, mc, mc.ageSec, baseAlpha);
    }

    for (var i = 0; i < state.effects.length; i++) {
      var ef = state.effects[i];
      if (ef.layer !== layer) continue;
      var p = clamp(ef.elapsed / ef.duration, 0, 1);
      ctx.save();
      ctx.globalCompositeOperation = ef.blend;
      if (ef.kind === 'slash') drawSlash(ctx, p, ef);
      else if (ef.kind === 'slashLine') drawSlashLine(ctx, p, ef);
      else if (ef.kind === 'lightning') drawLightning(ctx, p, ef);
      else if (ef.kind === 'streamer') drawStreamer(ctx, p, ef);
      else if (ef.kind === 'magicCircle') drawTimedMagicCircle(ctx, p, ef);
      else if (ef.kind === 'shockwave') drawShockwave(ctx, p, ef);
      else if (ef.kind === 'pillar') drawPillar(ctx, p, ef);
      else if (ef.kind === 'wave') drawWave(ctx, p, ef);
      else if (ef.kind === 'groundCrack') drawGroundCrack(ctx, p, ef);
      else if (ef.kind === 'artifact') drawArtifact(ctx, p, ef);
      else if (ef.kind === 'custom' && typeof ef.drawFn === 'function') {
        ef.drawFn(ctx, p, ef, DaohuaVFX);
      }
      ctx.restore();
    }

    for (var k = 0; k < state.particles.length; k++) {
      if (state.particles[k].layer === layer) {
        renderParticle(ctx, state.particles[k]);
      }
    }
  }

  function tick(now) {
    var dtMs = clamp(now - state.lastTime, 1, 50);
    var dtSec = dtMs * 0.001;
    state.lastTime = now;
    var nowSec = now * 0.001;

    var camX = 0;
    var camY = 0;
    for (var s = state.shakes.length - 1; s >= 0; s--) {
      var sh = state.shakes[s];
      sh.elapsed += dtMs;
      if (sh.elapsed >= sh.duration) {
        state.shakes.splice(s, 1);
        continue;
      }
      var decay = Math.pow(1 - sh.elapsed / sh.duration, 1.5);
      camX += Math.sin(sh.elapsed * 0.095 + sh.seed) * sh.amp * decay;
      camY += Math.cos(sh.elapsed * 0.125 + sh.seed * 1.7) * sh.amp * decay * 0.85;
    }
    state.camX = camX;
    state.camY = camY;

    var maxDimAlpha = 0;
    var dimColor = '#04060d';
    for (var d = state.dims.length - 1; d >= 0; d--) {
      var dm = state.dims[d];
      dm.elapsed += dtMs;
      if (dm.elapsed >= dm.duration) {
        state.dims.splice(d, 1);
        continue;
      }
      var dp = dm.elapsed / dm.duration;
      var env = dp < 0.16 ? dp / 0.16 : dp > 0.65 ? (1 - dp) / 0.35 : 1;
      var a = dm.opacity * env;
      if (a > maxDimAlpha) {
        maxDimAlpha = a;
        dimColor = dm.color;
      }
    }

    // 常驻阵法推进与呼吸感微粒飘升
    for (var m = state.persistentCircles.length - 1; m >= 0; m--) {
      var mc = state.persistentCircles[m];
      if (mc.anchorEl || mc.anchorSide) {
        syncPersistentCircleAnchor(mc);
      }
      mc.ageSec += dtSec;
      if (mc.pulseAmt > 0) {
        mc.pulseAmt = Math.max(0, mc.pulseAmt - dtSec * 1.8);
      }
      if (!mc.stopping && Math.random() < 0.24) {
        var rAng = Math.random() * TWO_PI;
        var rr = mc.radius * rand(0.25, 0.92);
        spawnSingleParticle({
          layer: 'sky',
          x: mc.x + Math.cos(rAng) * rr,
          y: mc.y + Math.sin(rAng) * rr * mc.tilt,
          shape: Math.random() < 0.65 ? 'orb' : 'wisp',
          colors: [mc.color, mc.secondaryColor],
          size: [1.5, 3.0],
          speed: [12, 34],
          angle: [-HALF_PI - 0.35, -HALF_PI + 0.35],
          gravity: -28,
          alpha: 0.65,
          life: [520, 920],
        });
      }
      if (mc.stopping) {
        mc.stopElapsed += dtMs;
        if (mc.stopElapsed >= mc.stopDur) {
          state.persistentCircles.splice(m, 1);
        }
      }
    }

    for (var i = state.effects.length - 1; i >= 0; i--) {
      var ef = state.effects[i];
      ef.elapsed += dtMs;
      if (ef.elapsed >= ef.duration) {
        state.effects.splice(i, 1);
      }
    }

    updateParticles(dtSec, dtMs);

    var w = state.width;
    var h = state.height;
    var dpr = state.dpr;

    if (state.fieldEl) {
      state.fieldEl.style.transform =
        Math.abs(camX) > 0.05 || Math.abs(camY) > 0.05
          ? 'translate3d(' + camX.toFixed(2) + 'px,' + camY.toFixed(2) + 'px,0)'
          : '';
    }

    if (state.groundCtx) {
      state.groundCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
      state.groundCtx.clearRect(0, 0, w, h);
      state.groundCtx.save();
      state.groundCtx.translate(camX, camY);
      drawListForLayer(state.groundCtx, 'ground', nowSec);
      state.groundCtx.restore();
    }

    if (state.skyCtx) {
      state.skyCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
      state.skyCtx.clearRect(0, 0, w, h);

      if (maxDimAlpha > 0.005) {
        state.skyCtx.save();
        var vGrad = state.skyCtx.createRadialGradient(w * 0.5, h * 0.5, w * 0.15, w * 0.5, h * 0.5, w * 0.72);
        vGrad.addColorStop(0, rgbaStr(dimColor, maxDimAlpha * 0.68));
        vGrad.addColorStop(1, rgbaStr(dimColor, Math.min(0.82, maxDimAlpha * 1.45)));
        state.skyCtx.fillStyle = vGrad;
        state.skyCtx.fillRect(0, 0, w, h);
        state.skyCtx.restore();
      }

      state.skyCtx.save();
      state.skyCtx.translate(camX, camY);
      drawListForLayer(state.skyCtx, 'sky', nowSec);
      state.skyCtx.restore();
    }

    if (hasActiveVisuals()) {
      state.rafId = requestAnimationFrame(tick);
    } else {
      state.rafId = 0;
    }
  }

  // ---------------------------------------------------------------------------
  // 模块注册与分发
  // ---------------------------------------------------------------------------
  function register(id, handler) {
    if (!id || typeof handler !== 'function') return;
    state.registry[id] = handler;
  }

  function has(id) {
    return !!state.registry[id];
  }

  function listRegistered() {
    return Object.keys(state.registry);
  }

  function play(id, ctx) {
    if (isProductionHidden()) return null;
    var fn = state.registry[id];
    if (!fn) return null;
    ensureLoop();
    return fn(ctx || {}, DaohuaVFX);
  }

  function activeCount() {
    return {
      particles: state.particles.length,
      effects: state.effects.length,
      persistent: state.persistentCircles.length,
      pendingTimers: state.timers.size,
      total: state.particles.length + state.effects.length + state.persistentCircles.length,
    };
  }

  var DaohuaVFX = {
    isCanvasV2: true,
    version: '2.3.0',

    mount: mount,
    unmount: unmount,
    dispose: unmount,
    resize: resize,
    onResize: onResize,
    clear: clear,
    after: after,
    register: register,
    has: has,
    listRegistered: listRegistered,
    play: play,
    activeCount: activeCount,

    clamp: clamp,
    lerp: lerp,
    rand: rand,
    pick: pick,
    dir: dirBetween,
    scale: scale,
    clampStage: clampStage,
    groupBounds: groupBounds,
    combatAxis: combatAxis,
    size: function () {
      return { w: state.width, h: state.height };
    },
    rect: rect,
    center: center,
    cardCenter: cardCenter,
    foot: foot,
    head: head,
    arenaCenter: arenaCenter,
    sideCenter: sideCenter,
    sideFootCenter: sideFootCenter,

    quake: quake,
    cinematicDim: cinematicDim,
    motion: motion,
    impact: impact,
    hitFlash: hitFlash,
    floatText: floatText,
    resourceSlice: resourceSlice,
    defeat: defeat,
    revive: revive,

    particle: burst,
    burst: burst,
    emit: burst,
    slash: slash,
    slashLine: slashLine,
    lightning: lightning,
    streamer: streamer,
    magicCircle: magicCircle,
    shockwave: shockwave,
    pillar: pillar,
    wave: wave,
    groundCrack: groundCrack,
    artifact: artifact,
    custom: custom,
  };

  function bindModuleQueue() {
    var q = global.DaohuaVFXModules;
    if (!Array.isArray(q)) {
      q = [];
      global.DaohuaVFXModules = q;
    }
    var orig = q.slice();
    q.length = 0;
    orig.forEach(function (fn) {
      if (typeof fn === 'function') fn(DaohuaVFX);
    });
    q.push = function () {
      for (var i = 0; i < arguments.length; i++) {
        if (typeof arguments[i] === 'function') {
          arguments[i](DaohuaVFX);
        }
      }
      return q.length;
    };
  }

  DaohuaVFX.flushModules = bindModuleQueue;
  global.DaohuaVFX = DaohuaVFX;
  bindModuleQueue();
})(window);
