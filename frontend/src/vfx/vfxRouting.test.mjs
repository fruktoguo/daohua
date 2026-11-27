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
  resolveZeroDamageSupportExpansion,
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

test('vfxRouting：消耗品自动使用日志解析与 18 项消耗品字典完备性', () => {
  assert.equal(parseAutoUseActivatedItemId('自动使用 雷击符，造成 182.4 点伤害。'), 'item.leiji-talisman')
  assert.equal(parseAutoUseActivatedItemId('自动使用 剑符，造成 350 点伤害（暴击）。'), 'item.jian-talisman')
  assert.equal(parseAutoUseActivatedItemId('自动使用 怒焰符，施加 25 层燃烧。'), 'item.nuyan-talisman')
  assert.equal(parseAutoUseActivatedItemId('自动使用 定神符。'), 'item.dingshen-talisman')
  assert.equal(parseAutoUseActivatedItemId('自动使用 神行符。'), 'item.shenxing-talisman')
  assert.equal(parseAutoUseActivatedItemId('自动使用 隐身符。'), 'item.yinshen-talisman')
  assert.equal(parseAutoUseActivatedItemId('自动使用 止血散。'), 'item.zhixue-powder')
  assert.equal(parseAutoUseActivatedItemId('自动使用 {{item:item.heqi-pill|合气丹}}。'), 'item.heqi-pill')
  assert.equal(parseAutoUseActivatedItemId('自动使用 聚气香，可生效 300 秒。'), null)
  assert.equal(parseAutoUseActivatedItemId('{{ability:ability.zhanyu|斩玉}}造成 100 点伤害。'), null)

  assert.equal(Object.keys(FORMATION_BUFF_TO_VFX).length, 4)
  assert.equal(FORMATION_BUFF_TO_VFX['formation.mizong'].teamWide, true)
  assert.equal(Object.keys(FORMATION_ITEM_TO_BUFF).length, 4)
  assert.equal(Object.keys(FORMATION_PROC_MAP).length, 2)
  assert.equal(Object.keys(PILL_BUFF_TO_ITEM).length, 6)
  assert.equal(Object.keys(INSTANT_CONSUMABLE_NAME_TO_ITEM).length, 12)
  assert.equal(INSTANT_CONSUMABLE_ITEM_IDS.size, 12)
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
      visibilityState: 'visible',
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
    sandbox,
    rafQueue,
    timerQueue,
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

test('vfxRouting：吸血自疗镜像不污染 targetIds 且零伤群辅/特质/心法路由准确', () => {
  // 1. 同一技能同时伴随自身零伤吸血回声（无论自身回血在前或在后），都不应把施法者混入 targetIds
  const withEchoAfter = groupCombatEventBatches([
    {
      key: 'hit1',
      delay: 10,
      comboIndex: 0,
      event: { at: 8.4, attackerId: 'player', targetId: 'enemy:0', abilityId: 'ability.shunshan', damage: 240, hit: true },
    },
    {
      key: 'drain1',
      delay: 10,
      comboIndex: 0,
      event: { at: 8.4, attackerId: 'ally:0', targetId: 'player', abilityId: 'ability.shunshan', damage: 0, hit: true },
    },
  ], 0)
  assert.equal(withEchoAfter.length, 1)
  assert.deepEqual(withEchoAfter[0].targetIds, ['enemy:0'])
  assert.deepEqual(withEchoAfter[0].consumeKeys, ['hit1', 'drain1'])
  assert.equal(withEchoAfter[0].totalDamage, 240)

  const withEchoBefore = groupCombatEventBatches([
    {
      key: 'drain0',
      delay: 10,
      comboIndex: 0,
      event: { at: 9.2, attackerId: 'player', targetId: 'player', abilityId: 'ability.pojun', damage: 0, hit: true },
    },
    {
      key: 'hit2a',
      delay: 10,
      comboIndex: 0,
      event: { at: 9.2, attackerId: 'player', targetId: 'enemy:0', abilityId: 'ability.pojun', damage: 180, hit: true },
    },
    {
      key: 'hit2b',
      delay: 10,
      comboIndex: 0,
      event: { at: 9.2, attackerId: 'player', targetId: 'enemy:1', abilityId: 'ability.pojun', damage: 160, hit: true },
    },
  ], 0)
  assert.equal(withEchoBefore.length, 1)
  assert.deepEqual(withEchoBefore[0].targetIds, ['enemy:0', 'enemy:1'])
  assert.deepEqual(withEchoBefore[0].consumeKeys, ['drain0', 'hit2a', 'hit2b'])
  assert.equal(withEchoBefore[0].totalDamage, 340)

  // 2. 纯零伤群辅/自增益（如山鬼谣/碎罡只发一条 targetId === attackerId）应保留自身占位并正确识别展开模式
  const shanguiBatch = groupCombatEventBatches([
    {
      key: 'sg1',
      delay: 15,
      comboIndex: 0,
      event: { at: 10.5, attackerId: 'ally:1', targetId: 'ally:1', abilityId: 'ability.shangui-yao', damage: 0, hit: true },
    },
  ], 0)
  assert.equal(shanguiBatch.length, 1)
  assert.deepEqual(shanguiBatch[0].targetIds, ['ally:1'])
  assert.equal(resolveZeroDamageSupportExpansion(shanguiBatch[0], 'all-allies', 0), 'all-allies')
  assert.equal(resolveZeroDamageSupportExpansion(shanguiBatch[0], 'allAllies', 0), 'all-allies')
  assert.equal(resolveZeroDamageSupportExpansion(shanguiBatch[0], 'lowest-hp-ally', 0), 'lowest-hp-ally')
  assert.equal(resolveZeroDamageSupportExpansion(shanguiBatch[0], 'lowestHitpointsAlly', 0), 'lowest-hp-ally')
  assert.equal(resolveZeroDamageSupportExpansion(withEchoAfter[0], 'all-allies', 0), null)

  // 3. 显式跨队友单疗不应再改写目标
  const explicitAllyHeal = groupCombatEventBatches([
    {
      key: 'gl1',
      delay: 12,
      comboIndex: 0,
      event: { at: 11.0, attackerId: 'player', targetId: 'ally:2', abilityId: 'ability.ganlu', damage: 0, hit: true },
    },
  ], 0)
  assert.equal(resolveZeroDamageSupportExpansion(explicitAllyHeal[0], 'lowest-hp-ally', 0), null)

  // 4. 特质反应与内功心法 ID 直通路由
  const knownIds = new Set(['trait.xuyi', 'trait.ranhun', 'ability.xuanyuan-yiqi-gong', 'feedback.burst'])
  const hasFn = id => knownIds.has(id)
  assert.equal(resolveRegisteredVfxId('trait.xuyi', false, hasFn), 'trait.xuyi')
  assert.equal(resolveRegisteredVfxId('trait.ranhun', false, hasFn), 'trait.ranhun')
  assert.equal(resolveRegisteredVfxId('ability.xuanyuan-yiqi-gong', false, hasFn), 'ability.xuanyuan-yiqi-gong')
})

test('全量 55 项共享 VFX：完整注册覆盖与横竖屏无头渲染门禁', () => {
  const __dirname = path.dirname(fileURLToPath(import.meta.url))
  const vfxDir = path.resolve(__dirname, '../../../docs/design/战斗演武画廊/vfx')
  const moduleFiles = [
    ...fs.readdirSync(path.join(vfxDir, 'abilities')).filter(f => f.endsWith('.js')).sort().map(f => `abilities/${f}`),
    ...fs.readdirSync(path.join(vfxDir, 'consumables')).filter(f => f.endsWith('.js')).sort().map(f => `consumables/${f}`),
  ]

  const { VFX, allyEl, enemyEl, topEnemyEl, bottomAllyEl, stepTimers, stepRaf } = createHeadlessVfxSandbox(moduleFiles)

  const ALL_55_VFX_IDS = [
    // 8 通用反馈
    'feedback.attack',
    'feedback.impact',
    'feedback.crit',
    'feedback.castRing',
    'feedback.defeat',
    'feedback.revive',
    'feedback.regen',
    'feedback.burst',
    // 9 外功 + 1 踏云变招 = 10
    'ability.heavy-slash',
    'ability.chain-strike',
    'ability.taunt',
    'ability.heavy-clouds',
    'ability.star-piercer',
    'ability.sui-gang',
    'ability.zhanyu',
    'ability.zhanyu.tayun',
    'ability.shunshan',
    'ability.pojun',
    // 12 内功法术
    'ability.palm-lightning',
    'ability.blazing-blast',
    'ability.qingping-melody',
    'ability.lightning-attraction',
    'ability.pozhen-melody',
    'ability.ganlu',
    'ability.tide',
    'ability.yueqian',
    'ability.huoyan-fengbao',
    'ability.tianlei-po',
    'ability.zhuoxin',
    'ability.shangui-yao',
    // 10 内功心法
    'ability.tuna-gong',
    'ability.manniu-jin',
    'ability.tiebu-shan',
    'ability.feiyan-gong',
    'ability.qingxin-jue',
    'ability.fenxue-gong',
    'ability.xiaozhoutian-gong',
    'ability.cunshen-jue',
    'ability.lianjing-huaqi-jue',
    'ability.xuanyuan-yiqi-gong',
    // 2 特质反应
    'trait.xuyi',
    'trait.ranhun',
    // 13 消耗品与阵盘反击
    'item.zhixue-powder',
    'item.shunqi-pill',
    'item.shengji-balm',
    'item.ningqi-powder',
    'item.huangya-pill',
    'item.heqi-pill',
    'item.leiji-talisman',
    'item.juling-formation-disk',
    'item.mizong-formation-disk',
    'item.tianlei-formation-disk',
    'item.xuesha-formation-disk',
    'item.tianlei-formation-disk.proc',
    'item.xuesha-formation-disk.proc',
  ]

  assert.equal(VFX.listRegistered().length, 55, 'Canvas V2 应准确注册全部 55 项 VFX')
  for (const id of ALL_55_VFX_IDS) {
    assert.equal(VFX.has(id), true, `Canvas V2 缺失注册 ID: ${id}`)
  }

  const setups = [
    {
      label: '横屏',
      casterEl: allyEl,
      targetEl: enemyEl,
      enemyEls: [enemyEl, topEnemyEl],
      allyEls: [allyEl, bottomAllyEl],
    },
    {
      label: '竖屏',
      casterEl: bottomAllyEl,
      targetEl: topEnemyEl,
      enemyEls: [topEnemyEl, enemyEl],
      allyEls: [bottomAllyEl, allyEl],
    },
  ]

  for (const setup of setups) {
    for (const id of ALL_55_VFX_IDS) {
      VFX.clear()
      const isFriendlyTarget =
        id === 'ability.ganlu' ||
        id === 'ability.shangui-yao' ||
        id === 'ability.yueqian' ||
        id === 'ability.pozhen-melody' ||
        id === 'ability.sui-gang' ||
        id === 'feedback.castRing' ||
        id === 'feedback.revive' ||
        id === 'feedback.regen' ||
        id.endsWith('-pill') ||
        id.endsWith('-powder') ||
        id.endsWith('-balm') ||
        id.endsWith('-formation-disk')

      const res = VFX.play(id, {
        side: 'ally',
        casterEl: setup.casterEl,
        targetEl: isFriendlyTarget ? setup.casterEl : setup.targetEl,
        targetEls: isFriendlyTarget ? setup.allyEls : setup.enemyEls,
        allyEls: setup.allyEls,
        enemyEls: setup.enemyEls,
        damage: 240,
        perTargetDamage: 180,
        heal: 150,
        crit: true,
        hit: true,
        castMs: 1200,
        durationMs: 1200,
      })

      assert.ok(
        VFX.activeCount().total > 0,
        `${setup.label} 下播放 ${id} 应激活至少 1 个特效或粒子节点`,
      )
      stepTimers(350)
      stepRaf(32)
      if (res && typeof res.stop === 'function') {
        res.stop(100)
      }
      stepTimers(1500)
      stepRaf(48)
    }
  }

  VFX.dispose()
})

test('core.js：生产模式页面不可见时阻止新特效与定时器累积，clear 同步取消挂起 rAF', () => {
  const { VFX, sandbox, rafQueue, timerQueue, allyEl, enemyEl } = createHeadlessVfxSandbox()

  VFX.play('feedback.attack', {
    side: 'ally',
    casterEl: allyEl,
    targetEl: enemyEl,
    targetEls: [enemyEl],
    allyEls: [allyEl],
    enemyEls: [enemyEl],
    damage: 100,
    hit: true,
  })
  assert.ok(rafQueue.size > 0, '可见时 play 应注册动画帧')
  assert.ok(VFX.activeCount().total > 0)

  // clear() 必须同时清空在途定时器与挂起的 requestAnimationFrame，防止切回首帧集中爆播
  VFX.clear()
  assert.equal(rafQueue.size, 0, 'clear 后不应残留挂起的 rAF')
  assert.equal(timerQueue.length, 0, 'clear 后不应残留定时器')
  assert.equal(VFX.activeCount().total, 0)

  // 切到后台标签页后，生产模式 play() 与 after() 不再新增粒子、动画帧或定时器
  sandbox.document.visibilityState = 'hidden'
  const hiddenPlay = VFX.play('feedback.attack', {
    side: 'ally',
    casterEl: allyEl,
    targetEl: enemyEl,
    targetEls: [enemyEl],
    allyEls: [allyEl],
    enemyEls: [enemyEl],
    damage: 100,
    hit: true,
  })
  let afterFired = false
  VFX.after(120, () => {
    afterFired = true
  })
  assert.equal(hiddenPlay, null)
  assert.equal(rafQueue.size, 0, '后台状态不应排队新的 rAF')
  assert.equal(timerQueue.length, 0, '后台状态不应排队新的定时器')
  assert.equal(VFX.activeCount().total, 0)
  assert.equal(afterFired, false)

  sandbox.document.visibilityState = 'visible'
  VFX.dispose()
})

test('combatVfxStore：特效开关读写持久化与默认值兜底', async () => {
  const {
    COMBAT_VFX_STORAGE_KEY,
    readStoredCombatVfxEnabled,
    persistCombatVfxEnabled,
  } = await import('../game/combatVfxStore.ts')

  const backing = new Map()
  const mockStorage = {
    getItem: key => backing.get(key) ?? null,
    setItem: (key, value) => backing.set(key, value),
  }

  assert.equal(readStoredCombatVfxEnabled(mockStorage), true, '未配置时默认开启特效')
  persistCombatVfxEnabled(false, mockStorage)
  assert.equal(backing.get(COMBAT_VFX_STORAGE_KEY), 'false')
  assert.equal(readStoredCombatVfxEnabled(mockStorage), false)

  persistCombatVfxEnabled(true, mockStorage)
  assert.equal(backing.get(COMBAT_VFX_STORAGE_KEY), 'true')
  assert.equal(readStoredCombatVfxEnabled(mockStorage), true)
})
