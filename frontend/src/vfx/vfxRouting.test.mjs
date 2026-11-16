import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import vm from 'node:vm'
import { fileURLToPath } from 'node:url'
import {
  FORMATION_BUFF_TO_VFX,
  FORMATION_ITEM_TO_BUFF,
  FORMATION_PROC_MAP,
  INSTANT_CONSUMABLE_ITEM_IDS,
  INSTANT_CONSUMABLE_NAME_TO_ITEM,
  PILL_BUFF_TO_ITEM,
  detectDuelZhanyuMorph,
  detectZhanyuTayunMorph,
  groupCombatEventBatches,
  isActorIdMatchingDomKey,
  normalizeActorCanonicalKey,
  parseAutoUseActivatedItemId,
  resolveRegisteredVfxId,
} from './vfxRouting.ts'

test('vfxRouting：实体 ID 归一化与 DOM actorKey 全形态精确匹配', () => {
  assert.equal(normalizeActorCanonicalKey('player', 1), 'player')
  assert.equal(normalizeActorCanonicalKey('ally:1', 1), 'player')
  assert.equal(normalizeActorCanonicalKey('ally:0', null), 'player')
  assert.equal(normalizeActorCanonicalKey('ally:2:林师兄', 1), 'ally:2')
  assert.equal(normalizeActorCanonicalKey('enemy:3:1:enemy.wolf', 0), 'enemy:1')
  assert.equal(normalizeActorCanonicalKey('enemy:duel:青云道友', 0), 'enemy:0')

  // 本机与组队换位匹配
  assert.equal(isActorIdMatchingDomKey('player', 'player', 0), true)
  assert.equal(isActorIdMatchingDomKey('ally:0', 'player', 0), true)
  assert.equal(isActorIdMatchingDomKey('ally:2', 'player', 2), true)
  assert.equal(isActorIdMatchingDomKey('ally:1', 'player', 2), false)
  assert.equal(isActorIdMatchingDomKey('ally:1', 'ally:1:苏师妹', 2), true)
  assert.equal(isActorIdMatchingDomKey('player', 'ally:2:本机', 2), true)

  // 远征多波敌怪（enemy:wave:idx:enemyId）与斗法（enemy:duel:name）
  assert.equal(isActorIdMatchingDomKey('enemy:0', 'enemy:4:0:enemy.wood-spirit'), true)
  assert.equal(isActorIdMatchingDomKey('enemy:1', 'enemy:4:0:enemy.wood-spirit'), false)
  assert.equal(isActorIdMatchingDomKey('enemy:2', 'enemy:4:2:enemy.iron-puppet'), true)
  assert.equal(isActorIdMatchingDomKey('enemy:0', 'enemy:duel:玄冥子'), true)
  assert.equal(isActorIdMatchingDomKey('enemy', 'enemy:duel:玄冥子'), true)
  assert.equal(isActorIdMatchingDomKey('enemy:1', 'enemy:duel:玄冥子'), false)
})

test('vfxRouting：groupCombatEventBatches 合并同帧多目标 AoE 并保留同目标连击拆分', () => {
  const scheduled = [
    {
      key: 'k1',
      delay: 10,
      comboIndex: 0,
      event: { at: 5.2, attackerId: 'ally:1', targetId: 'enemy:0', abilityId: 'ability.huoyan-fengbao', damage: 120, hit: true, critical: false },
    },
    {
      key: 'k2',
      delay: 25,
      comboIndex: 0,
      event: { at: 5.2, attackerId: 'player', targetId: 'enemy:1', abilityId: 'ability.huoyan-fengbao', damage: 160, hit: true, critical: true },
    },
    // 同目标同秒第二击：不得挤进同一 AoE 批，需拆为连击第二段
    {
      key: 'k3',
      delay: 120,
      comboIndex: 1,
      event: { at: 5.2, attackerId: 'player', targetId: 'enemy:0', abilityId: 'ability.huoyan-fengbao', damage: 80, hit: true },
    },
    // 普攻分别出手不合并
    {
      key: 'k4',
      delay: 200,
      comboIndex: 0,
      event: { at: 5.5, attackerId: 'enemy:0', targetId: 'player', abilityId: null, damage: 45, hit: true, parried: true },
    },
  ]

  const batches = groupCombatEventBatches(scheduled, 1)
  assert.equal(batches.length, 3)

  assert.equal(batches[0].canonicalAttackerKey, 'player')
  assert.deepEqual(batches[0].targetIds, ['enemy:0', 'enemy:1'])
  assert.equal(batches[0].totalDamage, 280)
  assert.equal(batches[0].perTargetDamage, 140)
  assert.equal(batches[0].anyCrit, true)
  assert.equal(batches[0].delayMs, 10)

  assert.deepEqual(batches[1].targetIds, ['enemy:0'])
  assert.equal(batches[1].delayMs, 120)

  assert.equal(batches[2].abilityId, null)
  assert.equal(batches[2].anyParried, true)
})

test('vfxRouting：斩玉→踏云化形（实时战与斗法剧本）与阵盘反击路由', () => {
  assert.equal(detectZhanyuTayunMorph({
    targetCount: 3,
    hasYixingBuff: false,
    yixingWasConsumed: false,
    hasRecentYueqian: false,
    hasRecentTayunLog: false,
  }), true)

  assert.equal(detectZhanyuTayunMorph({
    targetCount: 1,
    hasYixingBuff: false,
    yixingWasConsumed: true,
    hasRecentYueqian: false,
    hasRecentTayunLog: false,
  }), true)

  assert.equal(detectZhanyuTayunMorph({
    targetCount: 1,
    hasYixingBuff: false,
    yixingWasConsumed: false,
    hasRecentYueqian: false,
    hasRecentTayunLog: false,
  }), false)

  const duelEvents = [
    { atSeconds: 2.0, attackerIsInitiator: true, targetIsInitiator: true, abilityId: 'ability.yueqian', attackerManapointsAfter: 50, targetManapointsAfter: 50, hit: true },
    { atSeconds: 4.5, attackerIsInitiator: true, targetIsInitiator: false, abilityId: 'ability.zhanyu', attackerManapointsAfter: 40, targetManapointsAfter: 50, hit: true, damage: 90 },
    { atSeconds: 6.2, attackerIsInitiator: true, targetIsInitiator: false, abilityId: 'ability.zhanyu', attackerManapointsAfter: 30, targetManapointsAfter: 50, hit: true, damage: 45 },
  ]
  assert.equal(detectDuelZhanyuMorph(duelEvents, 1), true)
  assert.equal(detectDuelZhanyuMorph(duelEvents, 2), false)

  const known = new Set(['ability.zhanyu', 'ability.zhanyu.tayun', 'item.tianlei-formation-disk.proc', 'feedback.burst', 'feedback.attack'])
  const hasVfx = id => known.has(id)
  assert.equal(resolveRegisteredVfxId(null, false, hasVfx), 'feedback.attack')
  assert.equal(resolveRegisteredVfxId('ability.zhanyu', false, hasVfx), 'ability.zhanyu')
  assert.equal(resolveRegisteredVfxId('ability.zhanyu', true, hasVfx), 'ability.zhanyu.tayun')
  assert.equal(resolveRegisteredVfxId('item.tianlei-formation-disk', false, hasVfx), 'item.tianlei-formation-disk.proc')
  assert.equal(resolveRegisteredVfxId('ability.unknown-future', false, hasVfx), 'feedback.burst')
})

test('vfxRouting：消耗品自动使用日志解析与 13 项消耗品字典完备性', () => {
  assert.equal(parseAutoUseActivatedItemId('自动使用 雷击符，造成 182.4 点伤害。'), 'item.leiji-talisman')
  assert.equal(parseAutoUseActivatedItemId('自动使用 止血散。'), 'item.zhixue-powder')
  assert.equal(parseAutoUseActivatedItemId('自动使用 {{item:item.heqi-pill|合气丹}}。'), 'item.heqi-pill')
  assert.equal(parseAutoUseActivatedItemId('自动使用 聚气香，可生效 300 秒。'), null)
  assert.equal(parseAutoUseActivatedItemId('{{ability:ability.zhanyu|斩玉}}造成 100 点伤害。'), null)

  assert.equal(Object.keys(FORMATION_BUFF_TO_VFX).length, 4)
  assert.equal(FORMATION_BUFF_TO_VFX['formation.mizong'].teamWide, true)
  assert.equal(Object.keys(FORMATION_ITEM_TO_BUFF).length, 4)
  assert.equal(Object.keys(FORMATION_PROC_MAP).length, 2)
  assert.equal(Object.keys(PILL_BUFF_TO_ITEM).length, 6)
  assert.equal(Object.keys(INSTANT_CONSUMABLE_NAME_TO_ITEM).length, 7)
  assert.equal(INSTANT_CONSUMABLE_ITEM_IDS.size, 7)
})

function createHeadlessVfxSandbox(extraRelativePaths = []) {
  const __dirname = path.dirname(fileURLToPath(import.meta.url))
  const vfxDir = path.resolve(__dirname, '../../../docs/design/战斗演武画廊/vfx')
  const coreSrc = fs.readFileSync(path.join(vfxDir, 'core.js'), 'utf8')
  const feedbackSrc = fs.readFileSync(path.join(vfxDir, 'feedback.js'), 'utf8')
  const extraModules = extraRelativePaths.map(rel => ({
    name: rel,
    src: fs.readFileSync(path.join(vfxDir, rel), 'utf8'),
  }))

  let nowMs = 1000
  let nextTimerId = 1
  const timerQueue = []
  let nextRafId = 1
  const rafQueue = new Map()

  function make2dContext() {
    const grad = { addColorStop() {} }
    return {
      globalCompositeOperation: 'source-over',
      globalAlpha: 1,
      fillStyle: '#000',
      strokeStyle: '#000',
      lineWidth: 1,
      lineCap: 'butt',
      lineJoin: 'miter',
      shadowBlur: 0,
      shadowColor: '#000',
      save() {},
      restore() {},
      setTransform() {},
      clearRect() {},
      translate() {},
      rotate() {},
      scale() {},
      beginPath() {},
      closePath() {},
      moveTo() {},
      lineTo() {},
      quadraticCurveTo() {},
      bezierCurveTo() {},
      arc() {},
      ellipse() {},
      fill() {},
      stroke() {},
      fillRect() {},
      drawImage() {},
      createRadialGradient() { return grad },
      createLinearGradient() { return grad },
      createImageData(w, h) { return { width: w, height: h, data: new Uint8ClampedArray(w * h * 4) } },
      putImageData() {},
    }
  }

  class FakeElement {
    constructor(tag, classes = [], rect = { left: 0, top: 0, width: 100, height: 100 }) {
      this.nodeType = 1
      this.tagName = tag.toUpperCase()
      this._classes = new Set(classes)
      this._rect = rect
      this._children = []
      this.parentNode = null
      this.style = {}
      this.width = rect.width
      this.height = rect.height
      this.clientWidth = rect.width
      this.clientHeight = rect.height
      this.offsetLeft = rect.left
      this.offsetTop = rect.top
      this.offsetWidth = rect.width
      this.offsetHeight = rect.height
      this.offsetParent = null
      this.animations = []
      this.classList = {
        add: c => this._classes.add(c),
        remove: c => this._classes.delete(c),
        contains: c => this._classes.has(c),
        toggle: (c, force) => (force === undefined ? (this._classes.has(c) ? (this._classes.delete(c), false) : (this._classes.add(c), true)) : (force ? (this._classes.add(c), true) : (this._classes.delete(c), false))),
      }
    }
    set innerHTML(_v) {
      this._children = []
    }
    get innerHTML() { return '' }
    appendChild(child) {
      child.parentNode = this
      child.offsetParent = this
      this._children.push(child)
      return child
    }
    removeChild(child) {
      this._children = this._children.filter(c => c !== child)
      child.parentNode = null
      return child
    }
    contains(el) {
      if (el === this) return true
      return this._children.some(c => c.contains(el))
    }
    closest(selector) {
      const parts = selector.split(',').map(s => s.trim().replace(/^\./, ''))
      let cur = this
      while (cur) {
        if (parts.some(cls => cur.classList.contains(cls))) return cur
        cur = cur.parentNode
      }
      return null
    }
    querySelector(selector) {
      const parts = selector.split(',').map(s => s.trim().replace(/^\./, ''))
      const visit = node => {
        for (const c of node._children) {
          if (parts.some(cls => c.classList.contains(cls))) return c
          const hit = visit(c)
          if (hit) return hit
        }
        return null
      }
      return visit(this)
    }
    querySelectorAll(selector) {
      const parts = selector.split(',').map(s => s.trim())
      const out = []
      const match = (node, sel) => {
        const clsList = sel.split('.').filter(Boolean)
        return clsList.length > 0 && clsList.every(c => node.classList.contains(c))
      }
      const visit = node => {
        for (const c of node._children) {
          if (parts.some(sel => match(c, sel))) out.push(c)
          visit(c)
        }
      }
      visit(this)
      return out
    }
    getBoundingClientRect() {
      const { left, top, width, height } = this._rect
      return { left, top, width, height, right: left + width, bottom: top + height }
    }
    getContext() {
      return make2dContext()
    }
    animate(frames, opts) {
      this.animations.push({ frames, opts })
      return { onfinish: null, cancel() {} }
    }
  }

  const sandbox = {
    Math,
    Number,
    String,
    Array,
    Object,
    Set,
    Map,
    WeakMap,
    parseInt,
    isNaN,
    console,
    performance: { now: () => nowMs },
    devicePixelRatio: 1,
    getComputedStyle: () => ({ getPropertyValue: () => '' }),
    requestAnimationFrame: fn => {
      const id = nextRafId++
      rafQueue.set(id, fn)
      return id
    },
    cancelAnimationFrame: id => {
      rafQueue.delete(id)
    },
    setTimeout: (fn, ms = 0) => {
      const id = nextTimerId++
      timerQueue.push({ id, at: nowMs + Math.max(0, ms), fn })
      timerQueue.sort((a, b) => a.at - b.at || a.id - b.id)
      return id
    },
    clearTimeout: id => {
      const idx = timerQueue.findIndex(t => t.id === id)
      if (idx >= 0) timerQueue.splice(idx, 1)
    },
    document: {
      createElement: tag => new FakeElement(tag),
    },
  }
  sandbox.window = sandbox

  vm.runInNewContext(coreSrc, sandbox, { filename: 'core.js' })
  vm.runInNewContext(feedbackSrc, sandbox, { filename: 'feedback.js' })
  for (const mod of extraModules) {
    vm.runInNewContext(mod.src, sandbox, { filename: mod.name })
  }

  const stage = new FakeElement('div', ['combat-arena-shell'], { left: 0, top: 0, width: 960, height: 460 })
  const sky = new FakeElement('div', ['vfx-stage-overlay'], { left: 0, top: 0, width: 960, height: 460 })
  const ground = new FakeElement('div', ['vfx-ground-overlay'], { left: 0, top: 0, width: 960, height: 460 })
  stage.appendChild(ground)
  stage.appendChild(sky)

  const allyEl = new FakeElement('div', ['arena-actor', 'arena-actor--player', 'is-ally'], { left: 120, top: 180, width: 140, height: 110 })
  const enemyEl = new FakeElement('div', ['arena-actor', 'arena-actor--enemy', 'is-enemy'], { left: 680, top: 180, width: 140, height: 110 })
  const topEnemyEl = new FakeElement('div', ['arena-actor', 'arena-actor--enemy', 'is-enemy'], { left: 120, top: 40, width: 140, height: 110 })
  const bottomAllyEl = new FakeElement('div', ['arena-actor', 'arena-actor--player', 'is-ally'], { left: 120, top: 300, width: 140, height: 110 })
  stage.appendChild(allyEl)
  stage.appendChild(enemyEl)
  stage.appendChild(topEnemyEl)
  stage.appendChild(bottomAllyEl)

  sandbox.DaohuaVFX.mount(stage, sky, ground, { mode: 'production' })

  const stepTimers = (advanceMs = 200) => {
    const limit = nowMs + advanceMs
    while (timerQueue.length > 0 && timerQueue[0].at <= limit) {
      const next = timerQueue.shift()
      nowMs = next.at
      next.fn()
    }
    nowMs = limit
  }

  const stepRaf = (dtMs = 48) => {
    nowMs += dtMs
    const callbacks = [...rafQueue.values()]
    rafQueue.clear()
    for (const cb of callbacks) cb(nowMs)
  }

  return {
    VFX: sandbox.DaohuaVFX,
    allyEl,
    enemyEl,
    topEnemyEl,
    bottomAllyEl,
    stepTimers,
    stepRaf,
  }
}

test('feedback.attack：五式通用普攻轮换（显式序号/别名/横竖屏/多单位独立/闪避免红闪）', () => {
  const { VFX, allyEl, enemyEl, topEnemyEl, bottomAllyEl, stepTimers, stepRaf } = createHeadlessVfxSandbox()
  assert.equal(VFX.has('feedback.attack'), true)

  const slashCalls = []
  const slashLineCalls = []
  const impactCalls = []
  const origSlash = VFX.slash
  const origSlashLine = VFX.slashLine
  const origImpact = VFX.impact
  VFX.slash = opts => {
    slashCalls.push(opts)
    return origSlash(opts)
  }
  VFX.slashLine = opts => {
    slashLineCalls.push(opts)
    return origSlashLine(opts)
  }
  VFX.impact = (el, opts) => {
    impactCalls.push({ el, opts })
    return origImpact(el, opts)
  }

  // 1. 显式播放第 0..4 式，核对每种招式几何特征均不同（含横扫 rotation/tilt、疾刺 slashLine、十字双斜交叉）
  const aliases = ['slash-down', 'slash-up', 'thrust', 'sweep', 'cross']
  for (let i = 0; i < 5; i++) {
    const prevSlash = slashCalls.length
    const prevLine = slashLineCalls.length
    const prevImpact = impactCalls.length
    VFX.play('feedback.attack', {
      side: 'ally',
      attackVariant: i % 2 === 0 ? i : aliases[i],
      casterEl: allyEl,
      targetEl: enemyEl,
      element: 'none',
      damage: 100 + i * 15,
      hit: true,
    })
    stepTimers(220)
    stepRaf(45)

    const deltaSlash = slashCalls.slice(prevSlash)
    const deltaLine = slashLineCalls.slice(prevLine)
    assert.equal(impactCalls.length - prevImpact, 1, `第 ${i} 式命中后应触发 1 次 impact`)

    if (i === 2) {
      // 穿云疾刺：使用 compact slashLine 而非宽弧 slash
      assert.equal(deltaLine.length, 1)
      assert.equal(deltaSlash.length, 0)
    } else if (i === 3) {
      // 残月横扫：使用 3D 透视 tilt + rotation
      assert.equal(deltaSlash.length, 2)
      assert.ok(deltaSlash[0].tilt < 0.7)
      assert.equal(typeof deltaSlash[0].rotation, 'number')
    } else if (i === 4) {
      // 双绝交错：正逆交叉各一刀
      assert.equal(deltaSlash.length, 2)
      assert.ok(deltaSlash[0].startAngle < deltaSlash[0].endAngle)
      assert.ok(deltaSlash[1].startAngle > deltaSlash[1].endAngle)
    } else {
      assert.equal(deltaSlash.length, 2)
    }
  }

  // 2. 竖屏上下对阵验证（tilt 横扫 rotation 旋转后渲染粒子无 NaN / 异常）
  VFX.clear()
  for (let i = 0; i < 5; i++) {
    VFX.play('feedback.attack', {
      side: 'ally',
      attackVariant: i,
      casterEl: bottomAllyEl,
      targetEl: topEnemyEl,
      element: i === 1 ? 'fire' : i === 3 ? 'wood' : 'none',
      damage: 88,
      hit: true,
    })
    stepTimers(220)
    stepRaf(60)
    assert.ok(VFX.activeCount().total > 0, `竖屏第 ${i} 式应生成活跃画布粒子/刀光`)
  }

  // 3. 多出手单位按 attackerKey 各自独立递增连段，互不串扰（连打第 3 次恰好触发第 2 式穿云疾刺）
  VFX.clear()
  const lineStart = slashLineCalls.length
  for (let hit = 0; hit < 2; hit++) {
    VFX.play('feedback.attack', {
      side: 'ally',
      attackerKey: 'player',
      casterEl: allyEl,
      targetEl: enemyEl,
      hit: true,
    })
    VFX.play('feedback.attack', {
      side: 'enemy',
      attackerKey: 'enemy:0',
      casterEl: enemyEl,
      targetEl: allyEl,
      hit: true,
    })
  }
  stepTimers(220)
  assert.equal(slashLineCalls.length, lineStart, '双方各自仅打到第 0/1 式，尚不应进入第 2 式疾刺')

  VFX.play('feedback.attack', {
    side: 'ally',
    attackerKey: 'player',
    casterEl: allyEl,
    targetEl: enemyEl,
    hit: true,
  })
  stepTimers(220)
  assert.equal(slashLineCalls.length, lineStart + 1, 'player 第 3 次普攻应独立轮到第 2 式穿云疾刺')

  // 4. 闪避（hit: false）不触发红光震颤 impact，仅保留掠风颗粒
  const impactBeforeDodge = impactCalls.length
  VFX.play('feedback.attack', {
    side: 'ally',
    attackVariant: 0,
    casterEl: allyEl,
    targetEl: enemyEl,
    damage: 0,
    hit: false,
  })
  stepTimers(220)
  assert.equal(impactCalls.length, impactBeforeDodge)

  VFX.dispose()
})

test('雾梦泽 20 级五新技能：Canvas V2 特效注册与无头渲染零抛错及斗法破军/瞬闪赋能踏云推断', () => {
  // 1. 斗法推断：破军及首次无先导移形的瞬闪均应赋能紧接的斩玉化态为踏云；再连一次瞬闪会消耗移形，不误判踏云
  const eventsPojun = [
    { atSeconds: 1.5, attackerIsInitiator: true, abilityId: 'ability.pojun' },
    { atSeconds: 2.0, attackerIsInitiator: true, abilityId: 'ability.zhanyu' },
  ]
  assert.equal(detectDuelZhanyuMorph(eventsPojun, 1), true)

  const eventsShunshanGrant = [
    { atSeconds: 0.5, attackerIsInitiator: true, abilityId: 'ability.shunshan' },
    { atSeconds: 1.0, attackerIsInitiator: true, abilityId: 'ability.zhanyu' },
  ]
  assert.equal(detectDuelZhanyuMorph(eventsShunshanGrant, 1), true)

  const eventsShunshanConsumed = [
    { atSeconds: 0.5, attackerIsInitiator: true, abilityId: 'ability.shunshan' },
    { atSeconds: 1.0, attackerIsInitiator: true, abilityId: 'ability.shunshan' },
    { atSeconds: 1.5, attackerIsInitiator: true, abilityId: 'ability.zhanyu' },
  ]
  assert.equal(detectDuelZhanyuMorph(eventsShunshanConsumed, 2), false)

  // 2. Canvas V2 无头沙盒加载并播放五门新技能特效（横屏下帧与定时器推进零异常）
  const files = [
    'abilities/tianlei-po.js',
    'abilities/zhuoxin.js',
    'abilities/shunshan.js',
    'abilities/shangui-yao.js',
    'abilities/pojun.js',
  ]
  const { VFX, allyEl, enemyEl, topEnemyEl, bottomAllyEl, stepTimers, stepRaf } = createHeadlessVfxSandbox(files)
  const expected = [
    'ability.tianlei-po',
    'ability.zhuoxin',
    'ability.shunshan',
    'ability.shangui-yao',
    'ability.pojun',
  ]
  for (const id of expected) {
    assert.equal(VFX.has(id), true, `Canvas V2 应已注册 ${id}`)
  }

  for (const id of expected) {
    const duration = VFX.play(id, {
      casterEl: allyEl,
      targetEl: enemyEl,
      targetEls: id === 'ability.shangui-yao' ? [allyEl, bottomAllyEl] : [enemyEl, topEnemyEl],
      enemyEls: [enemyEl, topEnemyEl],
      allyEls: [allyEl, bottomAllyEl],
      damage: 180,
      crit: false,
    })
    assert.ok(duration > 0, `${id} 应返回正数演出时长`)
    stepTimers(1500)
    stepRaf(64)
  }

  VFX.dispose()
})
