/* =========================================================================
 * 演武画廊 · 页面装配层
 *
 * 职责：
 * - 以真实 CombatLoadoutPanel / AbilitiesPanel 的 DOM 结构渲染装配槽与功法目录；
 * - 接线演武控制台：出手方切换 / 全量轮播 / 环境普攻 / 通用反馈 / 重置；
 * - 右栏 Tab 与队列下拉的真实交互同构；
 * - ?selftest=1 自检模式：逐项验证特效管线并输出报告（供无头浏览器断言）。
 * ========================================================================= */
(function () {
  'use strict';
  const VFX = window.DaohuaVFX;
  const CATALOG = window.DaohuaCatalog;
  const SANDBOX = window.DaohuaSandbox;

  const ICON_BASE = '../../../frontend/src/assets/items/';
  function el(id) { return document.getElementById(id); }

  /* ---------------- 功法槽位（同构 AbilitiesPanel.renderSlot 真实结构） ---------------- */
  function renderAbilitySlot(ability, index, opts) {
    const isInternal = ability.discipline === 'internal';
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'combat-ability-slot' + (isInternal ? ' is-internal-slot' : ' is-spell-slot');
    btn.setAttribute('aria-label', '演武 ' + ability.name);
    btn.dataset.abilityId = ability.id;
    btn.title = ability.name + ' · ' + (ability.description || '') +
      '\n吟唱 ' + (ability.castMilliseconds / 1000).toFixed(1) + 's · 灵力 ' + ability.manaCost +
      ' · 冷却 ' + (ability.cooldownMilliseconds / 1000).toFixed(0) + 's';

    const tag = document.createElement('span');
    tag.className = 'abilities-slot-tag-badge' + (isInternal ? ' is-internal' : '');
    tag.textContent = isInternal ? '内功' : '术法·' + index;
    btn.dataset.discipline = ability.discipline;
    btn.appendChild(tag);

    const cell = document.createElement('span');
    cell.className = 'item-tile combat-ability-cell';
    cell.dataset.rarity = isInternal ? 'emperor' : 'mystic';
    const ribbon = document.createElement('span');
    ribbon.className = 'item-type-ribbon';
    ribbon.setAttribute('aria-hidden', 'true');
    ribbon.innerHTML = '<span>' + (isInternal ? '内功' : '术法') + '</span>';
    cell.appendChild(ribbon);
    const img = document.createElement('img');
    img.alt = ability.name;
    img.className = 'item-tile-icon';
    img.decoding = 'async';
    img.draggable = false;
    img.src = ICON_BASE + ability.icon + '.webp';
    img.onerror = () => {
      img.remove();
      const name = document.createElement('strong');
      name.className = 'item-tile-name';
      name.textContent = ability.name;
      cell.insertBefore(name, cell.firstChild);
    };
    cell.appendChild(img);
    const mana = document.createElement('span');
    mana.className = 'item-corner item-corner--bottom-right';
    mana.textContent = '灵 ' + ability.manaCost;
    cell.appendChild(mana);
    const glow = document.createElement('span');
    glow.className = 'item-tile-glow';
    glow.setAttribute('aria-hidden', 'true');
    cell.appendChild(glow);
    btn.appendChild(cell);

    if (opts && opts.actions !== false) {
      const actions = document.createElement('div');
      actions.className = 'combat-ability-slot-actions';
      actions.innerHTML = '<span class="combat-ability-level-control"><span aria-hidden="true">Lv.</span><span class="combat-ability-level-value">3</span></span>' +
        '<button aria-label="演示 ' + ability.name + '" class="item-slot-inline-action item-slot-inline-action--condition" type="button">演</button>';
      btn.appendChild(actions);
      actions.querySelector('button').addEventListener('click', (e) => {
        e.stopPropagation();
        castFromConsole(ability.id);
      });
    }
    btn.addEventListener('click', () => castFromConsole(ability.id));
    return { btn, cell };
  }

  /** 出手方决定施法者：我方=少侠，敌方=魔猿（前排中位 4 号槽，配表带重斩） */
  function casterId() {
    return SANDBOX.demoSide() === 'ally' ? 'player' : 'enemy2';
  }

  function castFromConsole(abilityId) {
    selectedAbilityId = abilityId;
    updateTransport();
    SANDBOX.castAbility(casterId(), abilityId, { manual: true });
  }

  /* ---------------- 装配区渲染 ---------------- */
  function renderLoadout() {
    const grid = el('loadoutGrid');
    if (!grid) return;
    CATALOG.LOADOUT_IDS.forEach((id, index) => {
      const ability = CATALOG.byId(id);
      if (!ability) return;
      const { btn, cell } = renderAbilitySlot(ability, index, { actions: false });
      grid.appendChild(btn);
      SANDBOX.bindSlot(id, cell);
    });
  }

  /* ---------------- 全功法粒子目录（13 主动 + 10 内功运功预览） ---------------- */
  let selectedAbilityId = null;
  let previewTimer = 0;
  let previewToken = 0;
  let previewRunning = false;

  function selectedAbility() {
    return CATALOG.byId(selectedAbilityId);
  }

  function updateTransport() {
    const ability = selectedAbility();
    const replay = el('vfxReplay');
    if (replay) replay.disabled = !ability;
    const pause = el('vfxPause');
    if (pause) {
      pause.disabled = !ability;
      pause.textContent = VFX.particleStage().isPaused() ? '继续' : '暂停';
      pause.setAttribute('aria-pressed', String(VFX.particleStage().isPaused()));
    }
    const name = el('vfxCurrentName');
    const tag = el('vfxCurrentKind');
    const description = el('vfxCurrentDescription');
    if (name) name.textContent = ability ? ability.name : '选择一门功法';
    if (tag) tag.textContent = ability ? (ability.discipline === 'internal' ? '常驻内功 · 运功预览' : (ability.targetType === 'allEnemies' ? '全体敌方' : ability.targetType === 'allAllies' ? '全体友方' : ability.targetType === 'self' ? '自身增益' : ability.effectType === 'heal' || ability.buff && !ability.damageRatio ? '友方支援' : '单体敌方')) : '全量粒子目录';
    if (description) description.textContent = ability ? ability.description : '点击技能图标预览；内功仅展示自身运功。';
    const phase = el('vfxPlaybackState');
    if (phase) phase.textContent = !ability ? '待选择' : VFX.particleStage().isPaused() ? '已暂停' : previewRunning ? '播放中' : '已收束';
    const castActual = el('vfxCastActual');
    if (castActual) castActual.disabled = !ability || ability.discipline === 'internal';
    document.querySelectorAll('.vfx-ability-entry').forEach(button => {
      const selected = button.dataset.abilityId === selectedAbilityId;
      button.classList.toggle('is-selected', selected);
      button.setAttribute('aria-pressed', String(selected));
    });
  }

  function stopPreview() {
    previewToken++;
    previewRunning = false;
    if (previewTimer) clearTimeout(previewTimer);
    previewTimer = 0;
    SANDBOX.stopAutoDemo();
    VFX.particleStage().cancel('preview');
  }

  function playSelected(options) {
    const ability = selectedAbility();
    if (!ability) return 0;
    const o = options || {};
    stopPreview();
    VFX.particleStage().setPaused(false);
    const token = previewToken;
    previewRunning = true;
    const duration = SANDBOX.previewAbility(ability.id, o.casterId, { visualOnly: o.visualOnly !== false, restart: true });
    const stage = VFX.particleStage();
    previewTimer = setTimeout(() => {
      if (token !== previewToken) return;
      previewRunning = false;
      updateTransport();
    }, (duration + 120) / stage.getSpeed());
    updateTransport();
    return duration;
  }

  function selectAbility(abilityId, options) {
    selectedAbilityId = abilityId;
    document.querySelectorAll('.vfx-segment-btn').forEach(button => {
      button.classList.toggle('is-active', button.dataset.side === SANDBOX.demoSide());
    });
    playSelected(options);
  }

  function setCatalogFilter(filter) {
    document.querySelectorAll('[data-vfx-filter]').forEach(button => {
      const active = button.dataset.vfxFilter === filter;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-pressed', String(active));
    });
    document.querySelectorAll('.vfx-ability-entry').forEach(button => {
      const disc = button.dataset.discipline;
      const hide = filter === 'internal' ? disc !== 'internal' : filter === 'active' ? disc === 'internal' : false;
      button.hidden = hide;
      button.classList.toggle('is-hidden', hide);
    });
  }

  function renderVfxCatalog() {
    const container = el('vfxAbilityCatalog');
    if (!container) return;
    CATALOG.ABILITIES.forEach(ability => {
      const button = document.createElement('button');
      const passive = ability.discipline === 'internal';
      button.type = 'button';
      button.className = 'vfx-ability-entry vfx-element--' + ability.element;
      button.dataset.abilityId = ability.id;
      button.dataset.discipline = ability.discipline;
      button.setAttribute('aria-pressed', 'false');
      button.setAttribute('aria-label', (passive ? '运功预览 ' : '演武 ') + ability.name);
      button.title = ability.name + ' · ' + ability.description + (passive ? '\n常驻内功，仅展示自身运功粒子' : '\n立即重播技能粒子演出');
      const frame = document.createElement('span');
      frame.className = 'item-tile vfx-ability-icon';
      frame.dataset.rarity = ability.tier >= 3 ? 'emperor' : ability.tier === 2 ? 'mystic' : 'spirit';
      const img = document.createElement('img');
      img.src = ICON_BASE + ability.icon + '.webp';
      img.alt = '';
      img.className = 'item-tile-icon';
      img.draggable = false;
      frame.appendChild(img);
      const tier = document.createElement('span');
      tier.className = 'item-corner item-corner--bottom-right';
      tier.textContent = 'T' + ability.tier;
      frame.appendChild(tier);
      const name = document.createElement('strong');
      name.textContent = ability.name;
      const kind = document.createElement('small');
      kind.textContent = passive ? '内功' : ability.category === 'physical' ? '武技' : '术法';
      button.append(frame, name, kind);
      button.addEventListener('click', () => selectAbility(ability.id));
      container.appendChild(button);
    });
    el('vfxCatalogTotal').textContent = String(CATALOG.ABILITIES.length);
    el('vfxActiveTotal').textContent = String(CATALOG.activeAbilities().length);
    el('vfxInternalTotal').textContent = String(CATALOG.internalAbilities().length);
  }

  function wirePreviewTransport() {
    el('vfxReplay').addEventListener('click', () => playSelected());
    el('vfxPause').addEventListener('click', () => {
      const stage = VFX.particleStage();
      stage.setPaused(!stage.isPaused());
      updateTransport();
    });
    document.querySelectorAll('[data-vfx-speed]').forEach(button => {
      button.addEventListener('click', () => {
        const speed = Number(button.dataset.vfxSpeed);
        VFX.particleStage().setSpeed(speed);
        document.querySelectorAll('[data-vfx-speed]').forEach(item => {
          item.classList.toggle('is-active', item === button);
          item.setAttribute('aria-pressed', String(item === button));
        });
        if (previewRunning) playSelected();
      });
    });
    document.querySelectorAll('[data-vfx-filter]').forEach(button => {
      button.addEventListener('click', () => setCatalogFilter(button.dataset.vfxFilter));
    });
    el('vfxCastActual').addEventListener('click', () => {
      const ability = selectedAbility();
      if (!ability || ability.discipline === 'internal') return;
      stopPreview();
      castFromConsole(ability.id);
    });
  }

  /* ---------------- 右栏功法页（真实装配目录） ---------------- */
  function renderRightAbilities() {
    const internal = CATALOG.ABILITIES.filter(a => a.discipline === 'internal');
    const spells = CATALOG.ABILITIES.filter(a => a.discipline === 'spell');

    // 斗法装配槽：内功位 1 + 术法位 3（与装配区 LOADOUT_IDS 同步）
    const slotInternal = el('rightSlotInternal');
    const slotSpells = el('rightSlotSpells');
    if (slotInternal && slotSpells) {
      CATALOG.LOADOUT_IDS.forEach((id, index) => {
        const ability = CATALOG.byId(id);
        if (!ability) return;
        const { btn } = renderAbilitySlot(ability, index, { actions: false });
        (index === 0 ? slotInternal : slotSpells).appendChild(btn);
      });
    }

    // 已学功法网格（点击即演武）
    const catInternal = el('catalogInternal');
    const catSpells = el('catalogSpells');
    const renderTile = (ability) => {
      const tile = document.createElement('button');
      tile.type = 'button';
      tile.className = 'item-tile catalog-ability-tile';
      tile.dataset.rarity = ability.discipline === 'internal' ? 'emperor' : 'mystic';
      tile.dataset.abilityId = ability.id;
      tile.title = ability.id + '\n' + (ability.description || '') + (ability.discipline === 'internal'
        ? '\n常驻内功 · 点击查看自身运功预览'
        : '\n吟唱 ' + (ability.castMilliseconds / 1000).toFixed(1) + 's · 灵力 ' + ability.manaCost + ' · ' + targetLabel(ability.targetType));
      const ribbon = document.createElement('span');
      ribbon.className = 'item-type-ribbon';
      ribbon.setAttribute('aria-hidden', 'true');
      ribbon.innerHTML = '<span>' + (ability.discipline === 'internal' ? '内功' : '术法') + '</span>';
      tile.appendChild(ribbon);
      const img = document.createElement('img');
      img.alt = ability.name;
      img.className = 'item-tile-icon';
      img.decoding = 'async';
      img.draggable = false;
      img.src = ICON_BASE + ability.icon + '.webp';
      img.onerror = () => img.remove();
      tile.appendChild(img);
      const name = document.createElement('strong');
      name.className = 'item-tile-name';
      name.textContent = ability.name;
      tile.appendChild(name);
      const meta = document.createElement('span');
      meta.className = 'item-corner item-corner--bottom-right';
      meta.textContent = ability.discipline === 'internal' ? '常驻' : '灵 ' + ability.manaCost;
      tile.appendChild(meta);
      tile.addEventListener('click', () => {
        if (ability.discipline === 'internal') selectAbility(ability.id);
        else castFromConsole(ability.id);
      });
      return tile;
    };
    internal.forEach(a => catInternal && catInternal.appendChild(renderTile(a)));
    spells.forEach(a => catSpells && catSpells.appendChild(renderTile(a)));

    const cc = el('catalogCount');
    if (cc) cc.textContent = String(CATALOG.ABILITIES.length);
    const ci = el('catalogInternalCount');
    if (ci) ci.textContent = String(internal.length);
    const cs = el('catalogSpellCount');
    if (cs) cs.textContent = String(spells.length);
  }

  function targetLabel(targetType) {
    return {
      single: '单体敌', allEnemies: '敌方全体', allAllies: '我方全体',
      lowestHitpointsAlly: '气血最低友方', self: '自身',
    }[targetType] || targetType;
  }

  /* ---------------- 控制台接线 ---------------- */
  function wireConsole() {
    // 出手方切换
    document.querySelectorAll('.vfx-segment-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        document.querySelectorAll('.vfx-segment-btn').forEach(b => b.classList.remove('is-active'));
        btn.classList.add('is-active');
        SANDBOX.setDemoSide(btn.dataset.side);
        if (selectedAbilityId) playSelected();
        SANDBOX.log('出手方切换为「' + (btn.dataset.side === 'ally' ? '我方（少侠）' : '敌方（魔猿）') + '」', 'system');
      });
    });

    // 通用反馈按钮
    document.querySelectorAll('[data-feedback]').forEach(btn => {
      btn.addEventListener('click', () => SANDBOX.playFeedback(btn.dataset.feedback, btn.dataset.variant));
    });

    // 全量轮播
    const autoBtn = el('autoDemoBtn');
    let autoRunning = false;
    if (autoBtn) {
      autoBtn.addEventListener('click', () => {
        VFX.particleStage().setPaused(false);
        if (autoRunning) {
          SANDBOX.stopAutoDemo();
          autoRunning = false;
          autoBtn.textContent = '▶ 全量功法轮播';
          SANDBOX.log('轮播已停止', 'system');
        } else {
          const total = SANDBOX.autoDemo();
          if (total > 0) {
            stopPreview();
            autoRunning = true;
            autoBtn.textContent = '■ 停止轮播';
            setTimeout(() => {
              autoRunning = false;
              autoBtn.textContent = '▶ 全量功法轮播';
            }, total + 600);
          }
        }
      });
    }

    // 环境普攻开关
    const ambientBtn = el('ambientBtn');
    if (ambientBtn) {
      ambientBtn.addEventListener('click', () => {
        const on = SANDBOX.setAmbient(!SANDBOX.ambient());
        ambientBtn.textContent = on ? '⏸ 环境普攻已开启' : '▶ 环境普攻已关闭';
        SANDBOX.log('环境普攻底噪 ' + (on ? '开启' : '关闭'), 'system');
      });
    }

    // 重置
    const resetBtn = el('resetBtn');
    if (resetBtn) resetBtn.addEventListener('click', () => {
      stopPreview();
      SANDBOX.resetAll();
      updateTransport();
    });

    // 终止当前行动 = 重置（沙盘语义）
    const stopBtn = el('stopActionBtn');
    if (stopBtn) stopBtn.addEventListener('click', () => SANDBOX.resetAll());
  }

  /* ---------------- 右栏 Tab / 队列下拉（真实交互同构） ---------------- */
  function wireChrome() {
    document.querySelectorAll('.right-sidebar-tab-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        document.querySelectorAll('.right-sidebar-tab-btn').forEach(b => {
          b.classList.remove('is-active');
          b.setAttribute('aria-selected', 'false');
        });
        btn.classList.add('is-active');
        btn.setAttribute('aria-selected', 'true');
        document.querySelectorAll('[data-right-view]').forEach(v => {
          v.hidden = v.dataset.rightView !== btn.dataset.rightTab;
        });
      });
    });

    const expand = el('queueExpand');
    const dropdown = el('queueDropdown');
    const backdrop = el('queueBackdrop');
    if (expand && dropdown && backdrop) {
      const toggle = (open) => {
        dropdown.hidden = !open;
        backdrop.hidden = !open;
        expand.setAttribute('aria-expanded', String(open));
        expand.textContent = '队列 3 ' + (open ? '▴' : '▾');
      };
      expand.addEventListener('click', () => toggle(dropdown.hidden));
      backdrop.addEventListener('click', () => toggle(false));
    }

    // 点击敌方/我方单位 → 演示受击（便于单独验证 impact 管线）
    document.querySelectorAll('.arena-actor').forEach(actor => {
      actor.addEventListener('click', () => {
        const id = actor.dataset.actor;
        const a = SANDBOX.actors[id];
        if (a && a.alive) {
          VFX.impact(a.el, 0.5);
          VFX.floatText(a.el, '受击', { kind: 'hp', stamp: '演示' });
        }
      });
    });
  }

  /* ---------------- 自检模式（?selftest=1）：无头浏览器自动断言 ---------------- */
  function runSelftest() {
    const block = el('selftestBlock');
    const report = el('selftestReport');
    if (block) block.hidden = false;
    const results = [];
    const check = (name, ok, detail) => {
      results.push({ name, ok, detail: detail || '' });
    };

    // 1. 结构完整性
    check('vfxOverlay 覆盖层存在', !!el('vfxOverlay'));
    check('arena-shell 存在', !!el('arenaShell'));
    check('双方九宫格各 9 槽', document.querySelectorAll('.arena-formation--allies .arena-slot').length === 9 &&
      document.querySelectorAll('.arena-formation--enemies .arena-slot').length === 9);
    check('占位空槽保留（己方 6 空 / 敌方 6 空）',
      document.querySelectorAll('.arena-formation--allies .arena-slot--empty').length === 6 &&
      document.querySelectorAll('.arena-formation--enemies .arena-slot--empty').length === 6);

    // 2. 全量主动技能注册，内功只允许运功预览，不得伪造战斗技能。
    const active = CATALOG.activeAbilities();
    const missing = active.filter(a => !VFX.has(a.id)).map(a => a.id);
    check('13 门主动技能脚本全部注册', missing.length === 0, missing.join(',') || '全部命中');
    check('十门内功不伪造施法注册', CATALOG.internalAbilities().every(a => !VFX.has(a.id)));
    check('全量 23 门目录完整', document.querySelectorAll('.vfx-ability-entry').length === CATALOG.ABILITIES.length);
    check('Canvas 粒子舞台唯一', document.querySelectorAll('.vfx-particle-canvas').length === 1);
    ['feedback.attack', 'feedback.impact', 'feedback.crit', 'feedback.defeat', 'feedback.regen', 'feedback.revive', 'feedback.castRing']
      .forEach(id => check('反馈注册 ' + id, VFX.has(id)));

    // 3. 功能链路：异步施法 → 特效节点生成 → 结算 → 自动清理
    const overlay = el('vfxOverlay');
    const enemy = SANDBOX.actors.enemy1;
    const hpBefore = enemy ? enemy.hp : 0;
    SANDBOX.setAmbient(false);
    setTimeout(() => {
      const ok = SANDBOX.castAbility('player', 'ability.heavy-slash');
      check('castAbility 受理（重斩）', ok);
      setTimeout(() => {
        const stage = VFX.particleStage();
        check('命中帧生成可见粒子', stage.sampleAlpha() > 0, JSON.stringify(stage.getTimelineSnapshot()));
      }, 1150);
      setTimeout(() => {
        check('伤害结算落地', enemy && enemy.hp < hpBefore, hpBefore + ' → ' + (enemy ? enemy.hp : '-'));
      }, 1600);
      setTimeout(() => {
        const stage = VFX.particleStage();
        const left = stage.getStats().activeParticles;
        check('技能粒子自动清理', left === 0 && stage.sampleAlpha() === 0, '残留 ' + left + ' 粒子');
        check('旧烟雾样式未加载', !document.querySelector('style[data-vfx="feedback"]'));
        check('行动条已复位', !document.querySelector('.arena-actor[data-actor="player"]').classList.contains('is-action-cast'));
        // 汇总输出
        const pass = results.filter(r => r.ok).length;
        const summary = pass + '/' + results.length + ' 通过';
        if (report) {
          report.innerHTML = results.map(r =>
            '<div class="vfx-selftest-row ' + (r.ok ? 'ok' : 'fail') + '">' +
            (r.ok ? '✓' : '✗') + ' ' + r.name + (r.detail ? ' <i>' + r.detail + '</i>' : '') + '</div>'
          ).join('') + '<div class="vfx-selftest-summary">' + summary + '</div>';
        }
        window.__SELFTEST__ = { pass, total: results.length, results };
        SANDBOX.log('自检完成：' + summary, pass === results.length ? 'heal' : 'damage');
        SANDBOX.setAmbient(true); // 自检用完后恢复环境普攻底噪
      }, 3600);
    }, 400);
  }

  /* ---------------- 全技能巡检：23 门逐项实际播放并检查收束 ---------------- */
  async function runCoverage() {
    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
    const rows = [];
    SANDBOX.setAmbient(false);
    stopPreview();
    for (const ability of CATALOG.ABILITIES) {
      selectAbility(ability.id);
      await sleep(ability.discipline === 'internal' ? 420 : 390);
      const stage = VFX.particleStage();
      const alpha = stage.sampleAlpha();
      const snapshot = stage.getTimelineSnapshot();
      await sleep(ability.discipline === 'internal' ? 800 : 850);
      const stats = stage.getStats();
      rows.push({
        id: ability.id,
        name: ability.name,
        discipline: ability.discipline,
        visible: alpha > 0 && snapshot.some(t => t.visibleParticles > 0),
        peakParticles: stats.peakParticles,
        cleaned: stats.activeParticles === 0 && stage.sampleAlpha() === 0,
        droppedParticles: stats.droppedParticles,
      });
    }
    const report = { pass: rows.filter(row => row.visible && row.cleaned && row.droppedParticles === 0).length, total: rows.length, rows };
    window.__VFX_COVERAGE__ = report;
    return report;
  }

  /* ---------------- URL 触发钩子（无头测试/演示直达） ---------------- */
  function wireUrlHooks() {
    const params = new URLSearchParams(location.search);
    if (params.get('side') === 'enemy') {
      SANDBOX.setDemoSide('enemy');
      document.querySelectorAll('.vfx-segment-btn').forEach(b =>
        b.classList.toggle('is-active', b.dataset.side === 'enemy'));
    }
    const fx = params.get('fx');
    if (fx) setTimeout(() => selectAbility(fx), 600);
    if (params.get('freeze') === '1') setTimeout(() => VFX.particleStage().setPaused(true), 160);
    if (params.get('auto') === '1') setTimeout(() => SANDBOX.autoDemo(), 800);
    if (params.get('selftest') === '1') setTimeout(runSelftest, 500);
    if (params.get('coverage') === '1') setTimeout(runCoverage, 500);
  }

  /* ---------------- 启动 ---------------- */
  renderLoadout();
  renderRightAbilities();
  renderVfxCatalog();
  wireConsole();
  wireChrome();
  wirePreviewTransport();
  wireUrlHooks();
  selectAbility('ability.star-piercer', { visualOnly: true });
  SANDBOX.log('粒子演武就绪：13 门主动技能 + 10 门内功运功预览', 'system');
})();
