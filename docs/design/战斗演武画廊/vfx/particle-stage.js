/* =========================================================================
 * 道化太初 · 共享 Canvas 程序化粒子舞台
 *
 * classic-script 双宿主契约：画廊直接引用，正式前端经 import.meta.glob 引入。
 * 只负责视觉和视觉时钟，不持有权威战斗状态；命中回调由技能时间轴触发。
 * ========================================================================= */
(function () {
  'use strict';

  const TAU = Math.PI * 2;
  const EMPTY = Object.freeze([]);
  const clamp01 = value => Math.max(0, Math.min(1, value));
  const mix = (a, b, t) => a + (b - a) * t;
  const easeOut = t => 1 - Math.pow(1 - clamp01(t), 3);
  const easeIn = t => Math.pow(clamp01(t), 2);
  const range = (random, values, fallback) => {
    if (!values) return fallback;
    return mix(values[0], values[1], random());
  };
  const color = (random, colors) => colors[(random() * colors.length) | 0];

  /** 将字符串折叠为稳定种子，让同一招式的粒子路径可复现。 */
  function hashSeed(value) {
    const text = String(value || 'daohua-vfx');
    let hash = 2166136261;
    for (let i = 0; i < text.length; i++) {
      hash ^= text.charCodeAt(i);
      hash = Math.imul(hash, 16777619);
    }
    return hash >>> 0;
  }

  /** 轻量可播种随机源；只在发射粒子时调用，渲染帧无随机漂移。 */
  function createRandom(seed) {
    let state = typeof seed === 'number' ? seed >>> 0 : hashSeed(seed);
    return function () {
      state += 0x6D2B79F5;
      let t = state;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  /**
   * 技能时间轴：事件、粒子与命中回调共享一个可暂停/可定位的本地时钟。
   * 粒子对象来自宿主固定池，播放期间不会逐帧创建对象或 DOM 节点。
   */
  class ParticleTimeline {
    constructor(stage, options) {
      this.stage = stage;
      this.random = createRandom(options.seed);
      this.clock = options.clock || 'preview';
      this.speed = options.speed || 1;
      this.onComplete = options.onComplete || null;
      this.time = 0;
      this.duration = 1200;
      this.lastTime = 0;
      this.events = [];
      this.particles = [];
      this.sorted = true;
      this.done = false;
      this.updateParticles();
    }

    /** 在指定毫秒执行一次视觉事件；伤害演示也只能从这里进入。 */
    at(milliseconds, callback) {
      this.events.push({ at: Math.max(0, milliseconds), callback });
      this.lastTime = Math.max(this.lastTime, milliseconds);
      this.sorted = false;
      return this;
    }

    /** 声明收尾时刻；发射器自动向后扩展，避免长一帧的粒子被硬切。 */
    finish(milliseconds) {
      this.duration = Math.max(milliseconds, this.lastTime);
      return this.duration;
    }

    /** 清空本次演出，不触发尚未发生的命中事件。 */
    cancel() {
      for (let i = 0; i < this.particles.length; i++) this.stage.release(this.particles[i]);
      this.particles.length = 0;
      this.events.length = 0;
      this.done = true;
    }

    /** 推进到明确的本地时刻；先结算事件再计算粒子位置，保证同帧严格一致。 */
    advanceTo(milliseconds) {
      if (this.done) return;
      if (milliseconds <= this.time) {
        this.updateParticles();
        return;
      }
      const next = Math.min(this.duration, milliseconds);
      if (!this.sorted) {
        this.events.sort((a, b) => a.at - b.at);
        this.sorted = true;
      }
      for (let i = 0; i < this.events.length; i++) {
        const event = this.events[i];
        if (event.at > this.time && event.at <= next) {
          this.time = event.at;
          this.updateParticles();
          event.callback(this);
        }
      }
      this.time = next;
      this.updateParticles();
      if (this.time >= this.duration) {
        for (let i = 0; i < this.particles.length; i++) this.stage.release(this.particles[i]);
        this.particles.length = 0;
        this.done = true;
        if (this.onComplete) this.onComplete();
      }
    }

    /** 限制单次推进时长，防止后台恢复时跳过大量演出；事件本身不跳。 */
    advance(milliseconds) {
      this.advanceTo(Math.min(this.duration, this.time + Math.min(1000 / 120, Math.max(0, milliseconds))));
    }

    /** 从起点重算到指定毫秒；预览自检不依赖机器帧率。 */
    seek(milliseconds) {
      this.time = 0;
      this.done = false;
      this.advanceTo(milliseconds);
    }

    /** 解析式粒子运动：同一本地时间直接求位置，不积累浮点积分误差。 */
    updateParticles() {
      let write = 0;
      for (let i = 0; i < this.particles.length; i++) {
        const p = this.particles[i];
        const age = this.time - p.birth;
        if (age >= p.life) {
          this.stage.release(p);
          continue;
        }
        this.particles[write++] = p;
        if (age < 0) {
          p.alpha = 0;
          continue;
        }
        const u = clamp01(age / p.life);
        const seconds = Math.max(0, age) / 1000;
        if (p.mode === 1) {
          const q = easeOut((age - p.lag) / p.travel);
          const inverse = 1 - q;
          const x = inverse * inverse * p.x0 + 2 * inverse * q * p.cx + q * q * p.x1;
          const y = inverse * inverse * p.y0 + 2 * inverse * q * p.cy + q * q * p.y1;
          const dx = 2 * inverse * (p.cx - p.x0) + 2 * q * (p.x1 - p.cx);
          const dy = 2 * inverse * (p.cy - p.y0) + 2 * q * (p.y1 - p.cy);
          p.rotation = Math.atan2(dy, dx);
          p.x = x + p.ox * q;
          p.y = y + p.oy * q;
        } else if (p.mode === 2) {
          const a = p.angle + p.spin * u;
          const radius = mix(p.r0, p.r1, easeIn(u));
          p.x = p.cx + Math.cos(a) * radius;
          p.y = p.cy + Math.sin(a) * radius * p.squash;
          p.rotation = a + Math.PI / 2;
        } else if (p.mode === 3) {
          const q = easeIn(u);
          p.x = mix(p.x0, p.cx, q);
          p.y = mix(p.y0, p.cy, q);
          p.rotation = Math.atan2(p.cy - p.y0, p.cx - p.x0);
        } else if (p.mode === 4) {
          const radius = mix(p.r0, p.r1, easeOut(u));
          p.x = p.cx + Math.cos(p.angle) * radius;
          p.y = p.cy + Math.sin(p.angle) * radius * p.squash;
          p.rotation = p.angle + Math.PI / 2;
        } else {
          const distance = p.drag > 0
            ? p.velocity * (1 - Math.exp(-p.drag * seconds)) / p.drag
            : p.velocity * seconds;
          p.x = p.x0 + Math.cos(p.angle) * distance;
          p.y = p.y0 + Math.sin(p.angle) * distance + p.gravity * seconds * seconds * 0.5;
        }
        const fadeIn = Math.min(1, age / p.fadeIn);
        const fadeOut = Math.pow(1 - u, p.fadePower);
        p.alpha = p.opacity * fadeIn * fadeOut;
        if (p.spin && p.mode !== 2) p.rotation += p.spin * u;
      }
      this.particles.length = write;
    }
  }

  /**
   * 创建一个舞台。canvas 与 ResizeObserver 各一份；空场时不运行 RAF。
   * @param {HTMLElement} host 已定位的 .vfx-stage-overlay。
   */
  function create(host, options) {
    if (!host) throw new Error('粒子舞台缺少覆盖层宿主。');
    const canvas = document.createElement('canvas');
    canvas.className = 'vfx-particle-canvas';
    canvas.dataset.vfxRenderer = 'canvas-particles';
    canvas.setAttribute('aria-hidden', 'true');
    host.appendChild(canvas);
    const context = canvas.getContext('2d', { alpha: true, desynchronized: true });
    if (!context) throw new Error('当前浏览器不支持 Canvas 2D 粒子舞台。');

    const capacity = (options && options.capacity) || 1800;
    const pool = Array.from({ length: capacity }, () => ({ pooled: false }));
    const timelines = [];
    const stats = {
      frames: 0, activeParticles: 0, peakParticles: 0, activeTimelines: 0,
      completed: 0, droppedParticles: 0, frameMs: 0, maxFrameMs: 0, fps: 0,
    };
    let width = 1;
    let height = 1;
    let pixelRatio = 1;
    let raf = 0;
    let previous = 0;
    let paused = false;
    let hidden = document.hidden;
    let speed = 1;
    let disposed = false;
    let lastTimeline = null;

    function acquire() {
      const particle = pool.pop();
      if (!particle) {
        stats.droppedParticles++;
        return null;
      }
      particle.pooled = false;
      return particle;
    }

    function release(particle) {
      if (particle.pooled) return;
      particle.pooled = true;
      pool.push(particle);
    }

    function resize() {
      const rect = host.getBoundingClientRect();
      width = Math.max(1, rect.width);
      height = Math.max(1, rect.height);
      pixelRatio = Math.min(2, window.devicePixelRatio || 1);
      const nextWidth = Math.round(width * pixelRatio);
      const nextHeight = Math.round(height * pixelRatio);
      if (canvas.width !== nextWidth || canvas.height !== nextHeight) {
        canvas.width = nextWidth;
        canvas.height = nextHeight;
      }
      render();
    }

    /** 写入一个固定池粒子；以下字段全为数值/字符串标量，逐帧读取零分配。 */
    function emit(timeline, p) {
      const particle = acquire();
      if (!particle) return;
      particle.mode = p.mode || 0;
      particle.shape = p.shape || 'spark';
      particle.birth = p.birth || 0;
      particle.life = Math.max(16, p.life || 320);
      particle.travel = Math.max(16, p.travel || particle.life);
      particle.lag = p.lag || 0;
      particle.x0 = p.x0 || 0;
      particle.y0 = p.y0 || 0;
      particle.x1 = p.x1 || 0;
      particle.y1 = p.y1 || 0;
      particle.cx = p.cx || 0;
      particle.cy = p.cy || 0;
      particle.ox = p.ox || 0;
      particle.oy = p.oy || 0;
      particle.x = particle.x0;
      particle.y = particle.y0;
      particle.r0 = p.r0 || 0;
      particle.r1 = p.r1 || 0;
      particle.squash = p.squash == null ? 1 : p.squash;
      particle.angle = p.angle || 0;
      particle.rotation = p.rotation || 0;
      particle.velocity = p.velocity || 0;
      particle.gravity = p.gravity || 0;
      particle.drag = p.drag == null ? 3 : p.drag;
      particle.spin = p.spin || 0;
      particle.size = p.size || 1.5;
      particle.length = p.length || 6;
      particle.color = p.color || '#ffe7a3';
      particle.opacity = p.opacity == null ? 1 : p.opacity;
      particle.alpha = 0;
      particle.fadeIn = p.fadeIn || 16;
      particle.fadePower = p.fadePower || 1.25;
      particle.glow = !!p.glow;
      particle.core = p.core !== false;
      timeline.particles.push(particle);
      timeline.lastTime = Math.max(timeline.lastTime, particle.birth + particle.life);
    }

    function timeline(options) {
      const timeline = new ParticleTimeline(api, options || EMPTY);
      timelines.push(timeline);
      lastTimeline = timeline;
      request();
      return timeline;
    }

    /** 方向性碎光：不长拖尾，速度在约 0.2~0.4 秒内快速衰减。 */
    function burst(tl, o) {
      const random = tl.random;
      const at = o.at || 0;
      for (let i = 0; i < (o.count || 18); i++) {
        const angle = (o.angle || 0) + (random() - 0.5) * (o.spread == null ? TAU : o.spread);
        const origin = range(random, o.origin, 4);
        emit(tl, {
          shape: o.shape || 'spark', birth: at + range(random, o.stagger, 0),
          life: range(random, o.life, 320), x0: o.x + Math.cos(angle) * origin,
          y0: o.y + Math.sin(angle) * origin, angle,
          rotation: angle, velocity: range(random, o.speed, 180),
          gravity: o.gravity || 0, drag: o.drag == null ? 4 : o.drag,
          size: range(random, o.size, 1.5), length: range(random, o.length, 8),
          color: color(random, o.colors || ['#fff1bd', '#ffc95c']),
          opacity: o.opacity, glow: o.glow !== false, core: o.core,
          spin: range(random, o.spin, 0), fadePower: o.fadePower || 1.4,
        });
      }
    }

    /** 向心汇聚：外圈亮子加速收束到技能锚点，用于蓄力、治疗和内功。 */
    function converge(tl, o) {
      const random = tl.random;
      for (let i = 0; i < (o.count || 24); i++) {
        const angle = (i / (o.count || 24)) * TAU + random() * 0.3;
        const radius = range(random, o.radius, 72);
        emit(tl, {
          mode: 3, shape: o.shape || 'diamond', birth: (o.at || 0) + range(random, o.stagger, 40),
          life: range(random, o.life, 260), x0: o.x + Math.cos(angle) * radius,
          y0: o.y + Math.sin(angle) * radius * (o.squash == null ? 0.72 : o.squash),
          cx: o.x, cy: o.y, size: range(random, o.size, 2),
          length: range(random, o.length, 7), color: color(random, o.colors || ['#c4b5fd']),
          opacity: o.opacity, glow: o.glow !== false, fadePower: 0.35,
        });
      }
    }

    /** 螺旋光点：参数化圆周/收束路径，不使用贴图或旋转 DOM 法阵。 */
    function orbit(tl, o) {
      const random = tl.random;
      for (let i = 0; i < (o.count || 28); i++) {
        emit(tl, {
          mode: 2, shape: o.shape || 'diamond', birth: (o.at || 0) + range(random, o.stagger, 60),
          life: range(random, o.life, 600), cx: o.x, cy: o.y,
          angle: (i / (o.count || 28)) * TAU + (o.angle || 0),
          r0: range(random, o.radius, 48), r1: o.endRadius == null ? 14 : o.endRadius,
          squash: o.squash == null ? 0.58 : o.squash, spin: o.spin == null ? Math.PI * 1.7 : o.spin,
          size: range(random, o.size, 2), length: range(random, o.length, 7),
          color: color(random, o.colors || ['#a7f3d0']), glow: o.glow !== false,
          opacity: o.opacity, fadePower: 1.2,
        });
      }
    }

    /** 由碎光组成的扩散环；ellipse 让冲击贴住角色脚下而非盖住全身。 */
    function ring(tl, o) {
      const random = tl.random;
      for (let i = 0; i < (o.count || 32); i++) {
        emit(tl, {
          mode: 4, shape: 'spark', birth: (o.at || 0) + range(random, o.stagger, 12),
          life: range(random, o.life, 300), cx: o.x, cy: o.y,
          angle: (i / (o.count || 32)) * TAU, r0: o.radius || 8,
          r1: o.endRadius || 60, squash: o.squash == null ? 0.45 : o.squash,
          size: range(random, o.size, 1.25), length: range(random, o.length, 5),
          color: color(random, o.colors || ['#ffe3a1']), glow: o.glow !== false,
          opacity: o.opacity == null ? 0.9 : o.opacity, fadePower: 1.6,
        });
      }
    }

    /** 短寿命路径光点：斩线、弧刃、电弧都由细亮粒子构成。 */
    function path(tl, points, o) {
      const random = tl.random;
      const count = o.count || 32;
      const at = o.at || 0;
      const travel = o.travel || 100;
      for (let i = 0; i < count; i++) {
        const q = count === 1 ? 0 : i / (count - 1);
        const position = q * (points.length - 1);
        const segment = Math.min(points.length - 2, Math.floor(position));
        const local = position - segment;
        const a = points[segment];
        const b = points[segment + 1];
        const x = mix(a[0], b[0], local);
        const y = mix(a[1], b[1], local);
        emit(tl, {
          shape: o.shape || 'spark', birth: at + q * travel,
          life: range(random, o.life, 220) + (o.hold || 0), x0: x, y0: y,
          rotation: Math.atan2(b[1] - a[1], b[0] - a[0]),
          size: range(random, o.size, 1.35), length: range(random, o.length, 8),
          color: color(random, o.colors || ['#fff4c7']), glow: o.glow !== false,
          opacity: o.opacity, fadePower: o.fadePower || 1.2,
          spin: range(random, o.spin, 0),
        });
      }
    }

    /** 有明确到达时刻的流束；首颗粒子抵达时就是技能命中帧。 */
    function stream(tl, o) {
      const random = tl.random;
      const dx = o.x1 - o.x0;
      const dy = o.y1 - o.y0;
      const distance = Math.hypot(dx, dy) || 1;
      const bend = o.bend || 0;
      const cx = (o.x0 + o.x1) / 2 - dy / distance * bend;
      const cy = (o.y0 + o.y1) / 2 + dx / distance * bend;
      for (let i = 0; i < (o.count || 18); i++) {
        const offset = (random() - 0.5) * (o.spread || 0);
        emit(tl, {
          mode: 1, shape: i === 0 ? (o.head || 'diamond') : (o.shape || 'spark'),
          birth: (o.at || 0) + i * (o.stagger == null ? 7 : o.stagger),
          life: (o.flight || 150) + (o.life || 150),
          travel: o.flight || 150, x0: o.x0, y0: o.y0, x1: o.x1, y1: o.y1,
          cx, cy, ox: -dy / distance * offset, oy: dx / distance * offset,
          size: i === 0 ? (o.headSize || 3.5) : range(random, o.size, 1.4),
          length: i === 0 ? (o.headLength || 10) : range(random, o.length, 9),
          color: color(random, o.colors || ['#fff4c7']), glow: o.glow !== false,
          opacity: o.opacity, fadePower: o.fadePower || 0.7,
        });
      }
    }

    /** 生成斜弧斩的采样点；真正绘制的是逐粒光刃而非整张贴图。 */
    function slash(tl, o) {
      const points = [];
      const angle = o.angle || 0;
      const cos = Math.cos(angle);
      const sin = Math.sin(angle);
      for (let i = 0; i <= 16; i++) {
        const q = i / 16;
        const x = (q - 0.5) * (o.length || 120);
        const y = -(o.bend || 28) * Math.sin(q * Math.PI);
        points.push([o.x + x * cos - y * sin, o.y + x * sin + y * cos]);
      }
      path(tl, points, o);
    }

    /** 锯齿闪电：有限分叉与硬亮折线，避免整片紫光遮住角色。 */
    function bolt(tl, o) {
      const random = tl.random;
      const points = [];
      const count = o.segments || 7;
      for (let i = 0; i <= count; i++) {
        const q = i / count;
        const jitter = i === 0 || i === count ? 0 : (random() - 0.5) * (o.jitter || 16);
        points.push([mix(o.x0, o.x1, q) + jitter, mix(o.y0, o.y1, q) + (random() - 0.5) * 4]);
      }
      path(tl, points, o);
    }

    /** 描出几何符阵的颗粒棱线，保持空心，不能变成不透明的遮挡盘。 */
    function polygon(tl, o) {
      const points = [];
      const sides = o.sides || 6;
      for (let i = 0; i <= sides; i++) {
        const angle = (i % sides) / sides * TAU + (o.angle || 0);
        points.push([o.x + Math.cos(angle) * o.radius, o.y + Math.sin(angle) * o.radius * (o.squash || 1)]);
      }
      path(tl, points, o);
    }

    function drawParticle(p) {
      if (p.alpha <= 0.005) return;
      context.globalAlpha = Math.min(1, p.alpha);
      const cos = Math.cos(p.rotation);
      const sin = Math.sin(p.rotation);
      if (p.shape === 'spark') {
        const dx = cos * p.length * 0.5;
        const dy = sin * p.length * 0.5;
        context.strokeStyle = p.color;
        context.lineCap = 'round';
        if (p.glow) {
          context.globalAlpha = p.alpha * 0.16;
          context.lineWidth = p.size * 3.4;
          context.beginPath();
          context.moveTo(p.x - dx, p.y - dy);
          context.lineTo(p.x + dx, p.y + dy);
          context.stroke();
          context.globalAlpha = p.alpha;
        }
        context.lineWidth = p.size;
        context.beginPath();
        context.moveTo(p.x - dx, p.y - dy);
        context.lineTo(p.x + dx, p.y + dy);
        context.stroke();
        if (p.core && p.size >= 1.1) {
          context.globalAlpha = p.alpha * 0.55;
          context.strokeStyle = '#fff9e9';
          context.lineWidth = Math.max(0.45, p.size * 0.35);
          context.beginPath();
          context.moveTo(p.x - dx * 0.7, p.y - dy * 0.7);
          context.lineTo(p.x + dx * 0.7, p.y + dy * 0.7);
          context.stroke();
        }
        return;
      }
      context.fillStyle = p.color;
      const size = p.size;
      if (p.shape === 'shard') {
        context.beginPath();
        context.moveTo(p.x + cos * p.length, p.y + sin * p.length);
        context.lineTo(p.x - sin * size - cos * size, p.y + cos * size - sin * size);
        context.lineTo(p.x + sin * size - cos * size, p.y - cos * size - sin * size);
        context.closePath();
        context.fill();
      } else if (p.shape === 'leaf') {
        context.beginPath();
        context.moveTo(p.x + cos * p.length, p.y + sin * p.length);
        context.lineTo(p.x - sin * size, p.y + cos * size);
        context.lineTo(p.x - cos * p.length, p.y - sin * p.length);
        context.lineTo(p.x + sin * size, p.y - cos * size);
        context.closePath();
        context.fill();
      } else if (p.shape === 'diamond') {
        const length = p.length * 0.5;
        context.beginPath();
        context.moveTo(p.x + cos * length, p.y + sin * length);
        context.lineTo(p.x - sin * size, p.y + cos * size);
        context.lineTo(p.x - cos * length, p.y - sin * length);
        context.lineTo(p.x + sin * size, p.y - cos * size);
        context.closePath();
        context.fill();
        if (p.glow) {
          context.globalAlpha = p.alpha * 0.3;
          context.fillRect(p.x - size * 1.8, p.y - 0.5, size * 3.6, 1);
        }
      } else {
        context.fillRect(p.x - size * 0.5, p.y - size * 0.5, size, size);
      }
    }

    function render() {
      context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);
      context.clearRect(0, 0, width, height);
      context.globalCompositeOperation = 'lighter';
      let particles = 0;
      for (let i = 0; i < timelines.length; i++) {
        const timeline = timelines[i];
        if (timeline.done) continue;
        for (let j = 0; j < timeline.particles.length; j++) drawParticle(timeline.particles[j]);
        particles += timeline.particles.length;
      }
      context.globalAlpha = 1;
      context.globalCompositeOperation = 'source-over';
      stats.activeParticles = particles;
      stats.activeTimelines = timelines.length;
      stats.peakParticles = Math.max(stats.peakParticles, particles);
    }

    function request() {
      if (!raf && !paused && !hidden && !disposed && timelines.some(t => !t.done)) {
        raf = requestAnimationFrame(tick);
      }
    }

    function tick(now) {
      raf = 0;
      if (paused || hidden || disposed) return;
      const started = performance.now();
      const elapsed = previous ? Math.min(50, now - previous) : 1000 / 60;
      previous = now;
      let completed = 0;
      for (let i = 0; i < timelines.length; i++) {
        const timeline = timelines[i];
        if (timeline.done) continue;
        timeline.advanceTo(timeline.time + elapsed * (timeline.clock === 'preview' ? speed : 1) * timeline.speed);
        if (timeline.done) completed++;
      }
      for (let i = timelines.length - 1; i >= 0; i--) {
        if (timelines[i].done) timelines.splice(i, 1);
      }
      stats.completed += completed;
      stats.frames++;
      const cost = performance.now() - started;
      stats.frameMs = stats.frameMs ? mix(stats.frameMs, cost, 0.08) : cost;
      stats.maxFrameMs = Math.max(stats.maxFrameMs, cost);
      const fps = 1000 / Math.max(1, elapsed);
      stats.fps = stats.fps ? mix(stats.fps, fps, 0.06) : fps;
      render();
      request();
    }

    function setPaused(value) {
      paused = !!value;
      previous = 0;
      if (paused && raf) {
        cancelAnimationFrame(raf);
        raf = 0;
      } else request();
    }

    function visibility() {
      hidden = document.hidden;
      previous = 0;
      if (hidden && raf) {
        cancelAnimationFrame(raf);
        raf = 0;
      } else request();
    }

    function cancel(clock) {
      for (let i = timelines.length - 1; i >= 0; i--) {
        if (!clock || timelines[i].clock === clock) timelines[i].cancel();
        if (!clock || timelines[i].clock === clock) timelines.splice(i, 1);
      }
      stats.activeParticles = 0;
      stats.activeTimelines = 0;
      render();
    }

    const observer = new ResizeObserver(resize);
    observer.observe(host);
    document.addEventListener('visibilitychange', visibility);

    const api = {
      canvas,
      context,
      timeline,
      burst,
      converge,
      orbit,
      ring,
      path,
      stream,
      slash,
      bolt,
      polygon,
      release,
      render,
      resize,
      setPaused,
      cancel,
      seek(milliseconds) {
        for (let i = 0; i < timelines.length; i++) {
          if (timelines[i].clock === 'preview') timelines[i].seek(milliseconds);
        }
        render();
      },
      getTimelineSnapshot() {
        return timelines.filter(t => t.clock === 'preview').map(t => ({
          time: t.time, duration: t.duration, done: t.done,
          particles: t.particles.length,
          visibleParticles: t.particles.reduce((count, p) => count + (p.alpha > 0.005 ? 1 : 0), 0),
          eventCount: t.events.length,
        }));
      },
      setSpeed(value) { speed = Math.max(0.25, Math.min(4, Number(value) || 1)); },
      getSpeed() { return speed; },
      isPaused() { return paused; },
      isIdle() { return !timelines.some(t => !t.done && t.clock === 'preview'); },
      getLastTimeline() { return lastTimeline; },
      getStats() { return { ...stats, speed, paused, hidden, capacity, freeParticles: pool.length, width, height, pixelRatio }; },
      sampleAlpha() {
        render();
        const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
        let count = 0;
        for (let i = 3; i < pixels.length; i += 64) if (pixels[i] > 10) count++;
        return count;
      },
      dispose() {
        if (disposed) return;
        disposed = true;
        if (raf) cancelAnimationFrame(raf);
        observer.disconnect();
        document.removeEventListener('visibilitychange', visibility);
        cancel();
        canvas.remove();
      },
    };
    resize();
    return api;
  }

  window.DaohuaParticleStage = { create, createRandom, hashSeed };
})();
