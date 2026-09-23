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
    const isInternal = index === 0;
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

  /* ---------------- 右栏功法页 ---------------- */
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
      tile.title = ability.id + '\n' + (ability.description || '') +
        '\n吟唱 ' + (ability.castMilliseconds / 1000).toFixed(1) + 's · 灵力 ' + ability.manaCost +
        ' · ' + targetLabel(ability.targetType);
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
      meta.textContent = '灵 ' + ability.manaCost;
      tile.appendChild(meta);
      tile.addEventListener('click', () => castFromConsole(ability.id));
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
        SANDBOX.log('出手方切换为「' + (btn.dataset.side === 'ally' ? '我方（少侠）' : '敌方（魔猿）') + '」', 'system');
      });
    });

    // 通用反馈按钮
    document.querySelectorAll('[data-feedback]').forEach(btn => {
      btn.addEventListener('click', () => SANDBOX.playFeedback(btn.dataset.feedback));
    });

    // 全量轮播
    const autoBtn = el('autoDemoBtn');
    let autoRunning = false;
    if (autoBtn) {
      autoBtn.addEventListener('click', () => {
        if (autoRunning) {
          SANDBOX.stopAutoDemo();
          autoRunning = false;
          autoBtn.textContent = '▶ 全量功法轮播';
          SANDBOX.log('轮播已停止', 'system');
        } else {
          const total = SANDBOX.autoDemo();
          if (total > 0) {
            autoRunning = true;
            autoBtn.textContent = '⏹ 停止轮播';
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
    if (resetBtn) resetBtn.addEventListener('click', () => SANDBOX.resetAll());

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

    // 2. 注册表覆盖：全部 10 个功法 + 反馈组
    const missing = CATALOG.ABILITIES.filter(a => !VFX.has(a.id)).map(a => a.id);
    check('10 个功法脚本全部注册', missing.length === 0, missing.join(',') || '全部命中');
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
        // 吟唱结束瞬间：演出节点应已生成
        const fxCount = overlay ? overlay.querySelectorAll('.vfx-node').length : 0;
        check('特效节点生成', fxCount > 0, 'overlay 内 ' + fxCount + ' 节点');
      }, 1150);
      setTimeout(() => {
        check('伤害结算落地', enemy && enemy.hp < hpBefore, hpBefore + ' → ' + (enemy ? enemy.hp : '-'));
      }, 1600);
      setTimeout(() => {
        // 粒子演出寿命最长 ~2.4s（吟唱法阵持续发射器 + 长尾余烬），此处检查须晚于全部 retire
        const left = overlay ? overlay.querySelectorAll('.vfx-node').length : 0;
        check('特效节点自动清理', left === 0, '残留 ' + left + ' 节点');
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

  /* ---------------- URL 触发钩子（无头测试/演示直达） ---------------- */
  function wireUrlHooks() {
    const params = new URLSearchParams(location.search);
    if (params.get('side') === 'enemy') {
      SANDBOX.setDemoSide('enemy');
      document.querySelectorAll('.vfx-segment-btn').forEach(b =>
        b.classList.toggle('is-active', b.dataset.side === 'enemy'));
    }
    const fx = params.get('fx');
    if (fx) setTimeout(() => castFromConsole(fx), 600);
    if (params.get('auto') === '1') setTimeout(() => SANDBOX.autoDemo(), 800);
    if (params.get('selftest') === '1') setTimeout(runSelftest, 500);
  }

  /* ---------------- 启动 ---------------- */
  renderLoadout();
  renderRightAbilities();
  wireConsole();
  wireChrome();
  wireUrlHooks();
  SANDBOX.log('演武画廊就绪 —— 10 式功法已挂载，点击装配槽/功法格即演武', 'system');
})();
