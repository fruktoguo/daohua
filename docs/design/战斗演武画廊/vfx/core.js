/* =========================================================================
 * 道化太初 · 演武特效引擎 (DaohuaVFX)
 *
 * 职责：
 * - 维护特效覆盖层坐标系（以 .vfx-stage-overlay 为原点，全部节点绝对定位，零回流）；
 * - 提供粒子/弹道/光束/冲击环等通用原语；
 * - 注册表 DaohuaVFX.register(abilityId, handler)：每个功法一个脚本文件，
 *   handler(ctx) 内仅负责演出，结算由 sandbox 经 ctx.impact(target, atMs) 调度；
 * - injectStyles(id, css)：技能脚本自含专属关键帧，按 id 去重注入。
 * ========================================================================= */
(function () {
  'use strict';

  const injectedStyles = new Set();
  const registry = new Map();
  const liveNodes = new Set();

  /**
   * 特效模块注册队列（双宿主共享契约）：
   * 技能/反馈脚本统一写作 `(window.DaohuaVFXModules ||= []).push(function (VFX) { ... })`，
   * 文件本身不含 import/export —— 画廊以 classic script 加载、正式前端经
   * `import.meta.glob` eager 引入同一批文件后各自 drain 本队列完成注册。
   * 画廊侧 push 即消费：DaohuaVFX 就绪的脚本加载序中直接执行注册函数。
   */
  const moduleQueue = [];
  const nativePush = Array.prototype.push;
  moduleQueue.push = function (fn) {
    nativePush.call(this, fn);
    if (typeof fn === 'function' && window.DaohuaVFX) fn(window.DaohuaVFX);
    return this.length;
  };
  window.DaohuaVFXModules = moduleQueue;

  /** 统一时钟：所有延时走同一集合，reset 时可整体清理 */
  const timers = new Set();

  function overlay() {
    return document.getElementById('vfxOverlay');
  }

  function shell() {
    return document.getElementById('arenaShell');
  }

  /** 元素中心点在覆盖层坐标系中的位置 */
  function center(el) {
    const ov = overlay();
    if (!el || !ov) return { x: 0, y: 0 };
    const o = ov.getBoundingClientRect();
    const r = el.getBoundingClientRect();
    return { x: r.left - o.left + r.width / 2, y: r.top - o.top + r.height / 2 };
  }

  /** 元素矩形在覆盖层坐标系中的位置 */
  function rect(el) {
    const ov = overlay();
    if (!el || !ov) return { left: 0, top: 0, width: 0, height: 0 };
    const o = ov.getBoundingClientRect();
    const r = el.getBoundingClientRect();
    return { left: r.left - o.left, top: r.top - o.top, width: r.width, height: r.height };
  }

  /** 在覆盖层 (x, y) 处生成特效节点（宽高位 0 锚点，子元素自行展开） */
  function node(cls, x, y) {
    const el = document.createElement('div');
    el.className = 'vfx-node' + (cls ? ' ' + cls : '');
    el.style.left = x + 'px';
    el.style.top = y + 'px';
    overlay().appendChild(el);
    liveNodes.add(el);
    return el;
  }

  /** 生成子元素 */
  function child(parent, cls, tag) {
    const el = document.createElement(tag || 'div');
    el.className = cls;
    parent.appendChild(el);
    return el;
  }

  /** 注册延时句柄（可被 resetAll 清理） */
  function after(ms, fn) {
    const t = setTimeout(() => {
      timers.delete(t);
      fn();
    }, ms);
    timers.add(t);
    return t;
  }

  /** 节点生命周期：ms 后移除 */
  function retire(el, ms) {
    after(ms, () => {
      el.remove();
      liveNodes.delete(el);
    });
  }

  /**
   * 通用粒子喷发：以 (x, y) 为源点生成 count 个粒子。
   * opts: { cls, count, spread(散落半径px), size:[min,max], dur:[min,max]ms,
   *         angle(弧度主方向, 默认全向), arc(扇形弧度), dist:[min,max], rise(向上偏置px),
   *         inward(向心收束：粒子在外圈 spawnRadius 半径取点，沿径向飞向圆心——聚气/汇聚演出),
   *         spawnRadius(外圈半径), squash(垂直压扁比，贴地椭圆), c/c2(粒子色相) }
   * 粒子 CSS 须消费 --dx --dy --dur 三个变量（translate 位移 + 时长）。
   */
  function particles(x, y, opts) {
    const host = node(null, x, y);
    if (opts.c) host.style.setProperty('--c', opts.c);
    if (opts.c2) host.style.setProperty('--c2', opts.c2);
    const count = opts.count || 12;
    const spread = opts.spread || 0;
    const sizeMin = (opts.size && opts.size[0]) || 3;
    const sizeMax = (opts.size && opts.size[1]) || 6;
    const durMin = (opts.dur && opts.dur[0]) || 500;
    const durMax = (opts.dur && opts.dur[1]) || 900;
    const distMin = (opts.dist && opts.dist[0]) || 30;
    const distMax = (opts.dist && opts.dist[1]) || 80;
    const hasDir = typeof opts.angle === 'number';
    const arc = opts.arc == null ? Math.PI * 2 : opts.arc;
    const squash = opts.squash == null ? 1 : opts.squash;
    for (let i = 0; i < count; i++) {
      const p = child(host, opts.cls || 'vfx-particle');
      const sz = sizeMin + Math.random() * (sizeMax - sizeMin);
      p.style.width = sz + 'px';
      p.style.height = sz + 'px';
      if (opts.inward) {
        // 向心收束：外圈取点 → 径向飞向圆心（ squash<1 时轨迹呈贴地椭圆）
        const aIn = Math.random() * Math.PI * 2;
        const r0 = (opts.spawnRadius || 80) + (Math.random() - 0.5) * spread;
        p.style.left = (Math.cos(aIn) * r0) + 'px';
        p.style.top = (Math.sin(aIn) * r0 * squash) + 'px';
        const dIn = r0 * (0.82 + Math.random() * 0.18);
        if (opts.directional) {
          p.style.setProperty('--rot', (Math.atan2(-Math.sin(aIn) * squash, -Math.cos(aIn)) * 180 / Math.PI) + 'deg');
          p.style.setProperty('--dx', dIn + 'px');
          p.style.setProperty('--dy', ((Math.random() - 0.5) * dIn * 0.12 - (opts.rise || 0)) + 'px');
        } else {
          p.style.setProperty('--dx', (-Math.cos(aIn) * dIn) + 'px');
          p.style.setProperty('--dy', (-Math.sin(aIn) * dIn * squash - (opts.rise || 0)) + 'px');
        }
      } else {
        p.style.left = (Math.random() - 0.5) * spread + 'px';
        p.style.top = (Math.random() - 0.5) * spread + 'px';
        const a = hasDir ? opts.angle + (Math.random() - 0.5) * arc : Math.random() * Math.PI * 2;
        const d = distMin + Math.random() * (distMax - distMin);
        if (opts.directional) {
          // 方向性粒子（火花条/光针）：--rot 对齐飞行方向，沿自身长轴飞出
          p.style.setProperty('--rot', (a * 180 / Math.PI) + 'deg');
          p.style.setProperty('--dx', d + 'px');
          p.style.setProperty('--dy', ((Math.random() - 0.5) * d * 0.22 - (opts.rise || 0)) + 'px');
        } else {
          p.style.setProperty('--dx', (Math.cos(a) * d) + 'px');
          p.style.setProperty('--dy', (Math.sin(a) * d - (opts.rise || 0)) + 'px');
        }
      }
      p.style.setProperty('--dur', (durMin + Math.random() * (durMax - durMin)) + 'ms');
      if (opts.spin) p.style.setProperty('--spin', (Math.random() * 360) + 'deg');
    }
    retire(host, durMax + 200);
    return host;
  }

  /**
   * 持续发射器：在 (x, y) 每 intervalMs 喷出一小簇粒子，durationMs 后自动停止。
   * 用于火焰拖尾、灵气萦绕、雷云翻涌等「持续型」粒子场面。
   * 返回停止句柄（同时登记到 timers，resetAll 可整体清理）。
   */
  function emit(x, y, opts) {
    const interval = (opts && opts.interval) || 90;
    const duration = (opts && opts.duration) || 600;
    const perBurst = (opts && opts.perBurst) || 3;
    const timer = setInterval(() => {
      particles(x, y, {
        cls: opts.cls,
        count: perBurst,
        spread: opts.spread,
        size: opts.size,
        dur: opts.dur,
        dist: opts.dist,
        angle: opts.angle,
        arc: opts.arc,
        rise: opts.rise,
        directional: opts.directional,
        inward: opts.inward,
        spawnRadius: opts.spawnRadius,
        squash: opts.squash,
        c: opts.c,
        c2: opts.c2,
      });
    }, interval);
    timers.add(timer);
    const stop = after(duration, () => {
      clearInterval(timer);
      timers.delete(timer);
    });
    return { stop };
  }

  /**
   * 路径拖尾：沿 (x1,y1)→(x2,y2) 线段均匀播撒 count 个粒子，
   * 每个粒子再带随机散落——用于刀光尾迹、光束填充、雷电通道。
   * opts: { cls, size, dur, jitter(垂直抖动px), scatter(飞散距离), angle(粒子飞散主方向,默认沿线垂直) }
   */
  function trail(x1, y1, x2, y2, opts) {
    const count = (opts && opts.count) || 24;
    const jitter = (opts && opts.jitter) || 6;
    const scatter = (opts && opts.scatter) || 30;
    const dx = x2 - x1;
    const dy = y2 - y1;
    const len = Math.hypot(dx, dy) || 1;
    // 法线方向（垂直于路径），粒子向两侧飘散
    const nx = -dy / len;
    const ny = dx / len;
    const host = node(null, x1, y1);
    for (let i = 0; i < count; i++) {
      const t = count === 1 ? 0 : i / (count - 1);
      const p = child(host, (opts && opts.cls) || 'vfx-p-glow');
      const sz = ((opts.size && opts.size[0]) || 3) + Math.random() * (((opts.size && opts.size[1]) || 6) - ((opts.size && opts.size[0]) || 3));
      p.style.width = sz + 'px';
      p.style.height = sz + 'px';
      const jx = (Math.random() - 0.5) * jitter * 2;
      const jy = (Math.random() - 0.5) * jitter * 2;
      p.style.left = (dx * t + jx) + 'px';
      p.style.top = (dy * t + jy) + 'px';
      const side = Math.random() < 0.5 ? -1 : 1;
      const s = Math.random() * scatter;
      p.style.setProperty('--dx', (nx * s * side + jx * 0.6) + 'px');
      p.style.setProperty('--dy', (ny * s * side + jy * 0.6) + 'px');
      const dMin = (opts.dur && opts.dur[0]) || 400;
      const dMax = (opts.dur && opts.dur[1]) || 900;
      p.style.setProperty('--dur', (dMin + Math.random() * (dMax - dMin)) + 'ms');
    }
    retire(host, ((opts.dur && opts.dur[1]) || 900) + 200);
    return host;
  }

  /** 两点间光束：source→target 的旋转条带，cls 决定外观（渐隐由 keyframe 控制） */
  function beam(sourceEl, targetEl, cls, width, dur) {
    const s = center(sourceEl);
    const t = center(targetEl);
    const dx = t.x - s.x;
    const dy = t.y - s.y;
    const dist = Math.hypot(dx, dy);
    const angle = Math.atan2(dy, dx) * 180 / Math.PI;
    const host = node(null, s.x, s.y);
    const b = child(host, cls || 'vfx-beam');
    b.style.width = dist + 'px';
    b.style.height = (width || 4) + 'px';
    b.style.transform = 'rotate(' + angle + 'deg)';
    retire(host, dur || 400);
    return host;
  }

  /**
   * 弹道投射物：source→target 飞行 dur 毫秒，命中回调 onArrive。
   * cls 决定外观；opts.spin 控制自旋，opts.arc 上抛弧度偏移。
   */
  function projectile(sourceEl, targetEl, cls, dur, opts, onArrive) {
    const s = center(sourceEl);
    const t = center(targetEl);
    const host = node(null, s.x, s.y);
    const p = child(host, cls || 'vfx-projectile');
    const lift = (opts && opts.lift) || 0;
    const anim = p.animate([
      { transform: 'translate(0,0) scale(1)', offset: 0 },
      { transform: 'translate(' + (t.x - s.x) * 0.5 + 'px,' + (t.y - s.y) * 0.5 - lift + 'px) scale(1.15)', offset: 0.55 },
      { transform: 'translate(' + (t.x - s.x) + 'px,' + (t.y - s.y) + 'px) scale(1)', offset: 1 },
    ], { duration: dur, easing: (opts && opts.easing) || 'cubic-bezier(0.3,0.7,0.4,1)', fill: 'forwards' });
    anim.onfinish = () => { if (onArrive) onArrive(); };
    retire(host, dur + 400);
    return host;
  }

  /** 目标点冲击环（扩散圆环），cls 决定配色 */
  function ring(targetEl, cls, dur, scale) {
    const t = center(targetEl);
    const host = node(null, t.x, t.y);
    const r = child(host, cls || 'vfx-ring');
    if (scale) r.style.setProperty('--ring-scale', String(scale));
    retire(host, dur || 600);
    return host;
  }

  /** 屏幕震动：挂 .combat-arena-shell，仅 transform 不影响布局 */
  function quake(heavy) {
    const s = shell();
    if (!s) return;
    const cls = heavy ? 'is-quake-heavy' : 'is-quake';
    s.classList.remove('is-quake', 'is-quake-heavy');
    void s.offsetWidth;
    s.classList.add(cls);
    after(heavy ? 480 : 360, () => s.classList.remove(cls));
  }

  /**
   * 真实跳字：写入目标 .arena-resource--hp 的 .arena-resource-floats，
   * DOM 与样式完全同构正式前端（em + is-positive/is-negative + --float-drift + left%）。
   * opts: { kind:'hp'|'mp', crit:boolean, stamp:'暴击'|'恢复'|自定义 }
   */
  function floatText(actorEl, text, opts) {
    if (!actorEl) return;
    const kind = (opts && opts.kind) || 'hp';
    const resource = actorEl.querySelector('.arena-resource--' + kind);
    if (!resource) return;
    const floats = resource.querySelector('.arena-resource-floats');
    if (!floats) return;
    const em = document.createElement('em');
    if (opts && opts.positive) em.className = 'is-positive';
    if (opts && opts.crit) em.classList.add('is-crit');
    em.style.setProperty('--float-drift', ((Math.random() - 0.5) * 16) + 'px');
    em.style.left = (35 + Math.random() * 30) + '%';
    if (opts && opts.stamp) {
      const stamp = document.createElement('span');
      stamp.className = 'dmg-float-stamp';
      stamp.textContent = opts.stamp;
      em.appendChild(stamp);
    }
    const val = document.createElement('span');
    val.textContent = text;
    em.appendChild(val);
    floats.appendChild(em);
    after(1150, () => em.remove());
  }

  /**
   * 真实血条碎裂：在目标 hp 资源条的 .arena-resource-shards 生成 .arena-resource-slice，
   * CSS 变量与正式前端 ArenaResource 组件完全同式。
   */
  function resourceSlice(actorEl, kind, leftRatio, widthRatio) {
    if (!actorEl) return;
    const resource = actorEl.querySelector('.arena-resource--' + (kind || 'hp'));
    if (!resource) return;
    const layer = resource.querySelector('.arena-resource-shards');
    if (!layer) return;
    const width = Math.max(0.01, Math.min(1, widthRatio));
    const left = Math.max(0, Math.min(1 - width, leftRatio));
    const scale = 1 + Math.min(0.5, width * 1.6);
    const isHp = kind !== 'mp';
    const b = document.createElement('b');
    b.className = 'arena-resource-slice';
    b.style.left = (left * 100) + '%';
    b.style.width = (width * 100) + '%';
    b.style.setProperty('--slice-background-position', (left / Math.max(0.0001, 1 - width) * 100) + '% 50%');
    b.style.setProperty('--slice-background-size', (100 / width) + '% 100%');
    b.style.setProperty('--slice-burst-y', ((isHp ? -52 : 52) * scale * 0.22) + 'px');
    b.style.setProperty('--slice-drift', (Math.random() * 8 - 4) + 'px');
    b.style.setProperty('--slice-rotate', ((isHp ? -1 : 1) * (16 + Math.random() * 6)) + 'deg');
    b.style.setProperty('--slice-travel', ((isHp ? -52 : 52) * scale) + 'px');
    layer.appendChild(b);
    after(1100, () => b.remove());
  }

  /**
   * 受击反馈（同构 ArenaActor：is-hit 类震颤 + arena-impact-fx 三道刀痕）。
   * strength 0~1 控制震颤幅度（--impact-distance 等变量与正式组件同式）。
   */
  function impact(actorEl, strength) {
    if (!actorEl) return;
    const s = Math.max(0.25, Math.min(1, strength || 0.4));
    const stage = actorEl.querySelector('.arena-avatar-stage');
    if (!stage) return;
    actorEl.classList.remove('is-hit');
    actorEl.querySelectorAll('.arena-impact-fx').forEach(e => e.remove());
    void actorEl.offsetWidth;
    actorEl.classList.add('is-hit');
    const fx = document.createElement('span');
    fx.className = 'arena-impact-fx';
    fx.setAttribute('aria-hidden', 'true');
    fx.style.setProperty('--impact-distance', (3 + s * 6) + 'px');
    fx.style.setProperty('--impact-distance-far', (3 + s * 6) * 1.8 + 'px');
    fx.style.setProperty('--impact-opacity', String(0.75 + s * 0.25));
    fx.append(document.createElement('i'), document.createElement('i'), document.createElement('i'));
    stage.appendChild(fx);
    after(560, () => {
      actorEl.classList.remove('is-hit');
      fx.remove();
    });
  }

  /**
   * 出手动作（同构 ArenaActor 的 actionMotion：is-action-attack/cast + arena-action-fx--kind）。
   * 与正式前端一致：动作挂在 .arena-avatar-stage，绝不影响血条与行动条布局。
   */
  function motion(actorEl, kind) {
    if (!actorEl) return;
    const stage = actorEl.querySelector('.arena-avatar-stage');
    if (!stage) return;
    const dur = kind === 'cast' ? 720 : 520;
    actorEl.classList.remove('is-action-attack', 'is-action-cast');
    actorEl.querySelectorAll('.arena-action-fx').forEach(e => e.remove());
    void actorEl.offsetWidth;
    actorEl.classList.add('is-action-' + kind);
    const fx = document.createElement('span');
    fx.className = 'arena-action-fx arena-action-fx--' + kind;
    fx.setAttribute('aria-hidden', 'true');
    fx.append(document.createElement('i'), document.createElement('i'), document.createElement('i'));
    stage.appendChild(fx);
    after(dur + 40, () => {
      actorEl.classList.remove('is-action-' + kind);
      fx.remove();
    });
  }

  /** 阵亡消散：真实 UI 使用 is-dead（置灰透明），画廊补一层残魂粒子 */
  function defeat(actorEl) {
    if (!actorEl) return;
    actorEl.classList.add('is-dead');
    const avatar = actorEl.querySelector('.character-avatar');
    if (avatar) avatar.classList.add('is-dead');
    const t = center(actorEl.querySelector('.arena-avatar-stage') || actorEl);
    particles(t.x, t.y, {
      cls: 'vfx-soul-particle',
      count: 22,
      spread: 46,
      size: [2, 5],
      dur: [700, 1300],
      dist: [18, 55],
      rise: 55,
    });
  }

  /** 复活/复活态恢复 */
  function revive(actorEl) {
    if (!actorEl) return;
    actorEl.classList.remove('is-dead');
    const avatar = actorEl.querySelector('.character-avatar');
    if (avatar) avatar.classList.remove('is-dead');
    const t = center(actorEl.querySelector('.arena-avatar-stage') || actorEl);
    ring(actorEl, 'vfx-revive-ring', 700);
    particles(t.x, t.y + 30, {
      cls: 'vfx-revive-mote',
      count: 14,
      spread: 30,
      size: [2, 4],
      dur: [500, 900],
      dist: [20, 45],
      rise: 70,
    });
  }

  /** 技能脚本专属样式注入（按 id 去重） */
  function injectStyles(id, cssText) {
    if (injectedStyles.has(id)) return;
    injectedStyles.add(id);
    const style = document.createElement('style');
    style.dataset.vfx = id;
    style.textContent = cssText;
    document.head.appendChild(style);
  }

  /** 一次性特效：创建宿主节点 + 一个子元素 + 到期自动清理（杜绝匿名 host 泄漏） */
  function spawn(cls, x, y, dur) {
    const host = node(null, x, y);
    const c = child(host, cls);
    retire(host, (dur || 600) + 200);
    return c;
  }

  /** 注册功法演出：id 即真实 abilityId（如 ability.heavy-slash）或通用反馈 id */
  function register(id, handler) {
    registry.set(id, handler);
  }

  function has(id) {
    return registry.has(id);
  }

  /** 播放已注册演出，返回演出总时长（ms）用于轮播排程 */
  function play(id, ctx) {
    const handler = registry.get(id);
    if (!handler) return 0;
    return handler(ctx) || 0;
  }

  /** 清理全部特效节点与延时（重置沙盘用） */
  function resetAll() {
    timers.forEach(t => clearTimeout(t));
    timers.clear();
    liveNodes.forEach(el => el.remove());
    liveNodes.clear();
    const s = shell();
    if (s) s.classList.remove('is-quake', 'is-quake-heavy');
    document.querySelectorAll('.arena-actor').forEach(a => {
      a.classList.remove('is-hit', 'is-action-attack', 'is-action-cast');
    });
    document.querySelectorAll('.arena-action-fx, .arena-impact-fx, .arena-resource-slice, .arena-resource-floats em').forEach(e => e.remove());
  }

  window.DaohuaVFX = {
    after,
    beam,
    center,
    child,
    defeat,
    emit,
    floatText,
    has,
    impact,
    injectStyles,
    motion,
    node,
    overlay,
    particles,
    play,
    projectile,
    quake,
    rect,
    register,
    resetAll,
    resourceSlice,
    retire,
    revive,
    ring,
    shell,
    spawn,
    trail,
  };
})();
