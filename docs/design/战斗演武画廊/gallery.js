/**
 * 道化太初 · 演武画廊界面编排、分类图鉴及自动化自检套件
 */
(function (global) {
  'use strict';

  var Catalog = global.DaohuaCatalog;
  var Sandbox = global.DaohuaSandbox;
  var VFX = global.DaohuaVFX;

  var activeTab = 'all-active';
  var selectedPreviewId = 'ability.star-piercer';
  var hotbarSkillIds = Catalog.LOADOUT_IDS.slice();
  var hotbarConsumableIds = Catalog.CONSUMABLE_SLOT_IDS.slice();

  // DOM 索引
  var $ = function (id) {
    return document.getElementById(id);
  };

  var SHOWCASE_CARDS = [
    {
      id: 'ability.zhanyu.tayun',
      action: function () {
        Sandbox.runShowcase('showcase.tayun');
      },
      title: '踏云（移形切斩玉）',
      meta: '移形分身 3~4 道高速流光俯冲全体敌人',
      badge: '移形变招',
      glyph: '踏',
    },
    {
      id: 'item.tianlei-formation-disk.proc',
      action: function () {
        Sandbox.runShowcase('showcase.tianlei-proc');
      },
      title: '天雷阵 · 触发反击',
      meta: '阵眼脉冲闪亮 + 多道真雷轰劈来犯敌人',
      badge: '阵法触发',
      glyph: '雷',
    },
    {
      id: 'item.xuesha-formation-disk.proc',
      action: function () {
        Sandbox.runShowcase('showcase.xuesha-proc');
      },
      title: '血煞阵 · 触发吸血',
      meta: '敌身爆出血刺 + 双血玉流光虹吸回流施法者',
      badge: '100%吸血',
      glyph: '血',
    },
    {
      id: 'showcase.star-kill',
      action: function () {
        Sandbox.runShowcase('showcase.star-kill');
      },
      title: '贯星 · 击杀连环刺',
      meta: '天枢斩杀首目标后折射二次贯星穿刺余敌',
      badge: '击杀连星',
      glyph: '星',
    },
    {
      id: 'feedback.crit',
      action: function () {
        Sandbox.runShowcase('showcase.crit');
      },
      title: '通用暴击爆裂反馈',
      meta: '十字金芒 + 双重冲击环 + 震屏跳字',
      badge: '暴击演示',
      glyph: '暴',
    },
    {
      id: 'feedback.attack',
      action: function () {
        Sandbox.runShowcase('showcase.enemy-attack');
      },
      title: '妖兽敌方反击（触发天雷）',
      meta: '赤乌/巨猿凶猛反扑，测试受击与反伤阵',
      badge: '敌方出招',
      glyph: '敌',
    },
    {
      id: 'showcase.basic-combo',
      action: function () {
        Sandbox.runShowcase('showcase.basic-combo');
      },
      title: '普攻五式连段轮换',
      meta: '连续展示断岳斜劈 / 惊鸿逆撩 / 穿云疾刺 / 残月横扫 / 双绝交错',
      badge: '五式普攻',
      glyph: '攻',
    },
    {
      id: 'feedback.defeat',
      action: function () {
        Sandbox.runShowcase('showcase.defeat-revive');
      },
      title: '败退魂散 & 重生光柱',
      meta: '演练单位阵亡灰化碎裂及 1.1s 后金碧重聚',
      badge: '生死反馈',
      glyph: '生',
    },
  ];

  function createIconEl(item, sizeClass) {
    if (item && item.hasIcon) {
      var img = document.createElement('img');
      img.className = sizeClass || 'slot-icon-img';
      img.src = Catalog.getItemIconUrl(item.id);
      img.alt = item.name;
      img.onerror = function () {
        if (!img.parentNode) return;
        var span = document.createElement('span');
        span.className = 'slot-glyph';
        span.textContent = (item.name || '术').slice(0, 1);
        img.parentNode.replaceChild(span, img);
      };
      return img;
    }
    var glyph = document.createElement('span');
    glyph.className = 'slot-glyph';
    glyph.textContent = (item && (item.glyph || item.name.slice(0, 1))) || '术';
    return glyph;
  }

  function buildArenaUnits() {
    var allyWrap = $('allyUnitsContainer');
    var enemyWrap = $('enemyUnitsContainer');
    allyWrap.innerHTML = '';
    enemyWrap.innerHTML = '';

    Sandbox.state.units.forEach(function (u) {
      var card = document.createElement('div');
      card.className = 'arena-unit is-' + u.side;
      card.dataset.unitId = u.id;

      card.innerHTML =
        '<div class="arena-unit-name"></div>' +
        '<div class="arena-unit-role"></div>' +
        '<div class="arena-unit-avatar"><img alt="" /></div>' +
        '<div class="unit-bars">' +
        '  <div class="unit-bar-row"><div class="unit-bar-fill is-hp"></div><span class="unit-bar-text is-hp-txt"></span></div>' +
        '  <div class="unit-bar-row"><div class="unit-bar-fill is-mp"></div><span class="unit-bar-text is-mp-txt"></span></div>' +
        '</div>' +
        '<div class="unit-buffs"></div>';

      card.querySelector('.arena-unit-name').textContent = u.name;
      card.querySelector('.arena-unit-role').textContent = u.role;
      var avImg = card.querySelector('.arena-unit-avatar img');
      avImg.src = Catalog.getAvatarUrl(u.avatar);
      avImg.alt = u.name;

      card.addEventListener('click', function () {
        if (u.side === 'ally') {
          Sandbox.state.selectedCasterId = u.id;
          renderUnits();
          renderHotbars();
        } else {
          Sandbox.state.selectedTargetId = u.id;
          renderUnits();
        }
      });

      if (u.side === 'ally') allyWrap.appendChild(card);
      else enemyWrap.appendChild(card);
    });
  }

  function renderUnits() {
    Sandbox.state.units.forEach(function (u) {
      var card = document.querySelector('.arena-unit[data-unit-id="' + u.id + '"]');
      if (!card) return;
      card.classList.toggle('is-defeated', u.hp <= 0);
      card.classList.toggle('is-selected', Sandbox.state.selectedCasterId === u.id);
      card.classList.toggle('is-target', Sandbox.state.selectedTargetId === u.id);

      var hpPct = Math.max(0, Math.min(100, (u.hp / u.maxHp) * 100));
      var mpPct = Math.max(0, Math.min(100, (u.mp / u.maxMp) * 100));
      card.querySelector('.unit-bar-fill.is-hp').style.width = hpPct.toFixed(1) + '%';
      card.querySelector('.unit-bar-fill.is-mp').style.width = mpPct.toFixed(1) + '%';
      card.querySelector('.is-hp-txt').textContent = u.hp + '/' + u.maxHp;
      card.querySelector('.is-mp-txt').textContent = u.mp + '/' + u.maxMp;

      var buffBox = card.querySelector('.unit-buffs');
      buffBox.innerHTML = '';
      Object.keys(u.buffs).forEach(function (bk) {
        var b = u.buffs[bk];
        var pill = document.createElement('span');
        pill.className = 'unit-buff-pill';
        if (b.kind === 'yixing' || bk === 'buff.yixing') pill.className += ' is-yixing';
        else if (b.kind === 'formation') pill.className += ' is-formation';
        else if (b.kind === 'ranhun') pill.className += ' is-ranhun';
        pill.textContent = b.name;
        buffBox.appendChild(pill);
      });
    });

    // 阵法 HUD 与 移形状态栏
    var fHud = $('formationHud');
    fHud.innerHTML = '';
    var formParts = [];
    if (Sandbox.state.activeFormations.personal) {
      var pf = Sandbox.state.activeFormations.personal;
      formParts.push(pf.name);
      if (pf.itemId === 'item.tianlei-formation-disk') {
        var tb = document.createElement('button');
        tb.type = 'button';
        tb.className = 'formation-badge-btn';
        tb.textContent = '⚡ 触发天雷反击';
        tb.addEventListener('click', function () {
          Sandbox.triggerFormationProc('tianlei');
        });
        fHud.appendChild(tb);
      } else if (pf.itemId === 'item.xuesha-formation-disk') {
        var xb = document.createElement('button');
        xb.type = 'button';
        xb.className = 'formation-badge-btn';
        xb.textContent = '🩸 触发血煞汲取';
        xb.addEventListener('click', function () {
          Sandbox.triggerFormationProc('xuesha');
        });
        fHud.appendChild(xb);
      }
    }
    if (Sandbox.state.activeFormations.team) {
      formParts.push(Sandbox.state.activeFormations.team.name + '(全队)');
    }
    $('hudFormationState').textContent = '阵盘：' + (formParts.length ? formParts.join(' + ') : '未布阵');

    var caster = Sandbox.getUnit(Sandbox.state.selectedCasterId) || Sandbox.state.units[0];
    var hasYixing = !!(caster && caster.buffs['buff.yixing']);
    var yEl = $('hudYixingState');
    yEl.textContent = hasYixing ? '身法：移形激活（斩玉→踏云）' : '身法：常态';
    yEl.classList.toggle('is-hot', hasYixing);
  }

  function renderCastHud() {
    var hud = $('castHud');
    var c = Sandbox.state.casting;
    if (!c) {
      hud.hidden = true;
      $('hudStageState').textContent = Sandbox.state.autoDemo ? '状态：自动演武' : '状态：待机';
      return;
    }
    hud.hidden = false;
    $('castHudName').textContent = '吟唱 · ' + c.name;
    var pct = Math.round(c.progress * 100);
    $('castHudFill').style.width = pct + '%';
    $('castHudPct').textContent = pct + '%';
    $('hudStageState').textContent = '吟唱：' + c.name;
  }

  function renderHotbars() {
    var caster = Sandbox.getUnit(Sandbox.state.selectedCasterId) || Sandbox.state.units[0];
    var hasYixing = !!(caster && caster.buffs['buff.yixing']);

    var skillWrap = $('skillHotbar');
    skillWrap.innerHTML = '';
    hotbarSkillIds.forEach(function (id, idx) {
      var item = Catalog.byId(id);
      if (!item) return;
      var morphed = id === 'ability.zhanyu' && hasYixing;
      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'hot-slot-btn' + (morphed ? ' is-morphed' : '');
      btn.dataset.skillId = id;

      var keyEl = document.createElement('span');
      keyEl.className = 'slot-key';
      keyEl.textContent = String(idx + 1);
      btn.appendChild(keyEl);
      btn.appendChild(createIconEl(item));

      var labelEl = document.createElement('span');
      labelEl.textContent = morphed ? '踏云' : item.name;
      btn.appendChild(labelEl);

      btn.addEventListener('click', function () {
        selectDetail(morphed ? 'ability.zhanyu.tayun' : id);
        Sandbox.castAbility(id);
      });
      skillWrap.appendChild(btn);
    });

    var conKeys = ['Q', 'W', 'E', 'R', 'T', 'Y'];
    var conWrap = $('consumableHotbar');
    conWrap.innerHTML = '';
    hotbarConsumableIds.forEach(function (id, idx) {
      var item = Catalog.byId(id);
      if (!item) return;
      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'hot-slot-btn';
      btn.dataset.itemId = id;

      var keyEl = document.createElement('span');
      keyEl.className = 'slot-key';
      keyEl.textContent = conKeys[idx];
      btn.appendChild(keyEl);
      btn.appendChild(createIconEl(item));

      var labelEl = document.createElement('span');
      labelEl.textContent = item.name;
      btn.appendChild(labelEl);

      btn.addEventListener('click', function () {
        selectDetail(id);
        Sandbox.useConsumable(id);
      });
      conWrap.appendChild(btn);
    });
  }

  function renderCatalog() {
    var grid = $('catalogGrid');
    grid.innerHTML = '';

    if (activeTab === 'showcase') {
      SHOWCASE_CARDS.forEach(function (sc) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'catalog-card' + (selectedPreviewId === sc.id ? ' is-selected' : '');
        btn.innerHTML =
          '<div class="catalog-card-icon"><span class="slot-glyph">' +
          sc.glyph +
          '</span></div>' +
          '<div class="catalog-card-info">' +
          '  <div class="catalog-card-title"><span>' +
          sc.title +
          '</span><span class="tier-chip is-t2">' +
          sc.badge +
          '</span></div>' +
          '  <div class="catalog-card-meta">' +
          sc.meta +
          '</div>' +
          '</div>';
        btn.addEventListener('click', function () {
          selectedPreviewId = sc.id;
          sc.action();
          renderCatalog();
        });
        grid.appendChild(btn);
      });
      return;
    }

    var items = [];
    if (activeTab === 'all-active') items = Catalog.ACTIVE_ABILITIES;
    else if (activeTab === 'physical') items = Catalog.ACTIVE_ABILITIES.filter(function (a) { return a.kind === 'physical'; });
    else if (activeTab === 'spell') items = Catalog.ACTIVE_ABILITIES.filter(function (a) { return a.kind === 'spell'; });
    else if (activeTab === 'consumable') items = Catalog.CONSUMABLES;
    else if (activeTab === 'internal') items = Catalog.INTERNAL_ABILITIES;

    items.forEach(function (item) {
      var isPassive = item.kind === 'internal';
      var card = document.createElement('button');
      card.type = 'button';
      card.className =
        'catalog-card' +
        (isPassive ? ' is-passive' : '') +
        (selectedPreviewId === item.id ? ' is-selected' : '');
      card.dataset.entryId = item.id;

      var iconBox = document.createElement('div');
      iconBox.className = 'catalog-card-icon';
      iconBox.appendChild(createIconEl(item));
      card.appendChild(iconBox);

      var info = document.createElement('div');
      info.className = 'catalog-card-info';

      var tierLabel = item.tier === 2 ? '二阶' : item.tier === 1 ? '一阶' : '基础';
      var tierCls = item.tier === 2 ? 'tier-chip is-t2' : 'tier-chip';
      var elemName = item.element && Catalog.ELEMENT_META[item.element] ? Catalog.ELEMENT_META[item.element].label : '';
      var metaText = isPassive
        ? item.passiveSummary
        : item.consumableType
          ? item.effectSummary
          : (elemName ? elemName + ' · ' : '') + '吟唱 ' + (item.castTimeMs / 1000).toFixed(1) + 's · ' + item.desc;

      info.innerHTML =
        '<div class="catalog-card-title"><span>' +
        item.name +
        '</span><span class="' +
        tierCls +
        '">' +
        (isPassive ? tierLabel + '·被动' : tierLabel) +
        '</span></div>' +
        '<div class="catalog-card-meta"></div>';
      info.querySelector('.catalog-card-meta').textContent = metaText;
      card.appendChild(info);

      card.addEventListener('click', function () {
        selectDetail(item.id);
        if (isPassive) return;
        if (item.consumableType) Sandbox.useConsumable(item.id);
        else Sandbox.castAbility(item.id);
      });
      grid.appendChild(card);

      // 若是斩玉，额外附加一张「踏云（移形态）」直达卡片方便点播
      if (item.id === 'ability.zhanyu' && (activeTab === 'all-active' || activeTab === 'physical')) {
        var tayunCard = document.createElement('button');
        tayunCard.type = 'button';
        tayunCard.className = 'catalog-card' + (selectedPreviewId === 'ability.zhanyu.tayun' ? ' is-selected' : '');
        tayunCard.dataset.entryId = 'ability.zhanyu.tayun';
        tayunCard.innerHTML =
          '<div class="catalog-card-icon"><span class="slot-glyph">踏</span></div>' +
          '<div class="catalog-card-info">' +
          '  <div class="catalog-card-title"><span>踏云（斩玉·移形）</span><span class="tier-chip is-t2">移形切态</span></div>' +
          '  <div class="catalog-card-meta">分身流光穿梭全体敌人，释放暴风剑影</div>' +
          '</div>';
        tayunCard.addEventListener('click', function () {
          selectDetail('ability.zhanyu.tayun');
          Sandbox.runShowcase('showcase.tayun');
        });
        grid.appendChild(tayunCard);
      }
    });
  }

  function selectDetail(id) {
    selectedPreviewId = id;
    var body = $('detailBody');
    var tag = $('detailTag');
    var realId = id === 'ability.zhanyu.tayun' ? 'ability.zhanyu' : id;
    var item = Catalog.byId(realId);

    if (!item) {
      tag.textContent = '反馈演示';
      body.innerHTML = '<p class="detail-desc">当前正在预览演武场机制触发特效。</p>';
      return;
    }

    var isTayun = id === 'ability.zhanyu.tayun';
    var hasVfx = VFX && VFX.has(id);
    tag.textContent = item.kind === 'internal' ? '心法仅展示' : hasVfx ? 'VFX 已就绪' : '未注册';

    var kvHtml = '';
    if (item.consumableType) {
      kvHtml =
        '<div>类型：<strong>' +
        (item.consumableType === 'pill' ? '丹药' : item.consumableType === 'talisman' ? '符箓' : '阵盘') +
        '</strong></div>' +
        '<div>冷却：<strong>' +
        (item.cooldownMs / 1000).toFixed(0) +
        's</strong></div>' +
        '<div>持续：<strong>' +
        (item.durationMs ? (item.durationMs / 1000).toFixed(0) + 's' : '瞬发') +
        '</strong></div>' +
        '<div>阶位：<strong>' +
        (item.tier === 2 ? '二阶' : item.tier === 1 ? '一阶' : '基础') +
        '</strong></div>';
    } else if (item.active) {
      var em = Catalog.ELEMENT_META[item.element] || Catalog.ELEMENT_META.none;
      kvHtml =
        '<div>属性：<strong>' +
        em.label +
        '</strong></div>' +
        '<div>读条：<strong>' +
        (item.castTimeMs / 1000).toFixed(1) +
        's</strong></div>' +
        '<div>冷却：<strong>' +
        (item.cooldownMs / 1000).toFixed(0) +
        's</strong></div>' +
        '<div>消耗：<strong>' +
        item.mpCost +
        ' 内力</strong></div>';
    } else {
      kvHtml =
        '<div>定位：<strong>被动心法</strong></div>' +
        '<div>阶位：<strong>' +
        (item.tier === 2 ? '二阶' : '一阶') +
        '</strong></div>';
    }

    body.innerHTML =
      '<div style="font-size:15px;font-weight:700;color:var(--gold-bright)">' +
      (isTayun ? '踏云（斩玉·移形变招）' : item.name) +
      ' <span style="font-size:11px;color:var(--text-dim);font-weight:400">(' +
      id +
      ')</span></div>' +
      '<div class="detail-kv">' +
      kvHtml +
      '</div>' +
      '<p class="detail-desc"></p>' +
      (item.active !== false
        ? '<div class="detail-actions"><button type="button" class="btn-ctl is-active" id="btnReplaySelected">立即重放演示</button></div>'
        : '');

    body.querySelector('.detail-desc').textContent = item.desc || item.effectSummary || item.passiveSummary || '';

    var replayBtn = $('btnReplaySelected');
    if (replayBtn) {
      replayBtn.addEventListener('click', function () {
        if (item.consumableType) Sandbox.useConsumable(item.id);
        else if (isTayun) Sandbox.runShowcase('showcase.tayun');
        else Sandbox.castAbility(item.id);
      });
    }
  }

  function renderLogs() {
    var wrap = $('combatLog');
    wrap.innerHTML = '';
    Sandbox.state.logs.forEach(function (row) {
      var div = document.createElement('div');
      div.className = 'log-row' + (row.kind !== 'normal' ? ' is-' + row.kind : '');
      div.textContent = '[' + row.ts + '] ' + row.text;
      wrap.appendChild(div);
    });
  }

  // 顶部 HUD 粒子数轮询显示
  setInterval(function () {
    if (!VFX) return;
    var c = VFX.activeCount();
    $('hudVfxCount').textContent =
      '粒子：' + c.particles + ' | 绘制：' + c.effects + (c.persistent ? ' | 常驻阵：' + c.persistent : '');
  }, 180);

  // ---------------------------------------------------------------------------
  // ?selftest=1 自动化完整门禁验证
  // ---------------------------------------------------------------------------
  function runSelftest() {
    var REQUIRED_IDS = [
      // 8 反馈
      'feedback.attack',
      'feedback.impact',
      'feedback.crit',
      'feedback.castRing',
      'feedback.defeat',
      'feedback.revive',
      'feedback.regen',
      'feedback.burst',
      // 7 外功 + 踏云 = 8
      'ability.heavy-slash',
      'ability.chain-strike',
      'ability.taunt',
      'ability.heavy-clouds',
      'ability.star-piercer',
      'ability.sui-gang',
      'ability.zhanyu',
      'ability.zhanyu.tayun',
      // 9 法术
      'ability.palm-lightning',
      'ability.blazing-blast',
      'ability.qingping-melody',
      'ability.lightning-attraction',
      'ability.pozhen-melody',
      'ability.ganlu',
      'ability.tide',
      'ability.yueqian',
      'ability.huoyan-fengbao',
      // 6 丹药 + 1 符 + 4 阵盘 + 2 反击 = 13
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
    ];

    var missing = REQUIRED_IDS.filter(function (id) {
      return !VFX || !VFX.has(id);
    });

    var groundCanvas = document.querySelector('#vfxGroundOverlay canvas');
    var skyCanvas = document.querySelector('#vfxOverlay canvas');

    // 同步点播几组高亮视觉特效验证 Canvas 实体活动数量
    var allyEl = document.querySelector('.arena-unit[data-unit-id="ally-0"]');
    var enemyEl = document.querySelector('.arena-unit[data-unit-id="enemy-1"]');
    var enemyEls = Array.prototype.slice.call(document.querySelectorAll('.arena-unit.is-enemy'));
    var allyEls = Array.prototype.slice.call(document.querySelectorAll('.arena-unit.is-ally'));

    var sampleIds = [
      'ability.star-piercer',
      'ability.zhanyu.tayun',
      'ability.huoyan-fengbao',
      'ability.tide',
      'item.huangya-pill',
      'item.leiji-talisman',
      'item.tianlei-formation-disk',
      'item.tianlei-formation-disk.proc',
      'item.xuesha-formation-disk.proc',
    ];

    var invoked = 0;
    sampleIds.forEach(function (id, i) {
      VFX.after(i * 60, function () {
        VFX.play(id, {
          casterEl: allyEl,
          targetEl: enemyEl,
          targetEls: enemyEls,
          allyEls: allyEls,
          enemyEls: enemyEls,
          damage: 320,
          lifesteal: 180,
          heal: 150,
          crit: i % 2 === 0,
          kill: true,
        });
      });
      invoked++;
    });

    setTimeout(function () {
      var brokenImages = Array.prototype.slice.call(document.querySelectorAll('img')).filter(function (im) {
        return !im.complete || im.naturalWidth === 0;
      });
      var counts = VFX.activeCount();
      var errors = [];
      if (!VFX || !VFX.isCanvasV2) errors.push('VFX engine v2 not active');
      if (missing.length > 0) errors.push('Missing IDs: ' + missing.join(','));
      if (!groundCanvas || groundCanvas.width <= 0) errors.push('Ground canvas invalid');
      if (!skyCanvas || skyCanvas.width <= 0) errors.push('Sky canvas invalid');
      if (brokenImages.length > 0) errors.push('Broken images: ' + brokenImages.length);
      if (counts.total <= 0) errors.push('No active VFX visuals during selftest sample');

      // 布局与坐标越界断言：全部 6 名角色 center/foot 须严格落在舞台边界内且任意两张卡互不重叠
      var stageSize = VFX && VFX.size ? VFX.size() : { w: 0, h: 0 };
      var allUnits = Array.prototype.slice.call(document.querySelectorAll('.arena-unit'));
      var unitRects = [];
      var outOfBoundsCount = 0;
      var overlapCount = 0;

      if (allUnits.length < 6) {
        errors.push('Expected 6 arena units, got ' + allUnits.length);
      }

      allUnits.forEach(function (el) {
        var uid = el.getAttribute('data-unit-id') || 'unknown';
        var cp = VFX.center(el);
        var fp = VFX.foot(el);
        var ur = VFX.rect(el);
        if (
          cp.x < 8 ||
          cp.x > stageSize.w - 8 ||
          cp.y < 8 ||
          cp.y > stageSize.h - 8
        ) {
          outOfBoundsCount++;
          errors.push('center out of bounds for ' + uid + ': ' + Math.round(cp.x) + ',' + Math.round(cp.y));
        }
        if (
          fp.x < 8 ||
          fp.x > stageSize.w - 8 ||
          fp.y < 8 ||
          fp.y > stageSize.h - 8
        ) {
          outOfBoundsCount++;
          errors.push('foot out of bounds for ' + uid + ': ' + Math.round(fp.x) + ',' + Math.round(fp.y));
        }
        if (!ur) {
          outOfBoundsCount++;
          errors.push('missing rect for ' + uid);
        } else {
          unitRects.push({ id: uid, rect: ur });
        }
      });

      for (var i = 0; i < unitRects.length; i++) {
        for (var j = i + 1; j < unitRects.length; j++) {
          var a = unitRects[i].rect;
          var b = unitRects[j].rect;
          var xOverlap = Math.min(a.right, b.right) - Math.max(a.left, b.left);
          var yOverlap = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
          if (xOverlap > 0.5 && yOverlap > 0.5) {
            overlapCount++;
            errors.push('card overlap: ' + unitRects[i].id + ' vs ' + unitRects[j].id);
          }
        }
      }

      var pass = errors.length === 0;

      var summary = {
        pass: pass,
        passed: REQUIRED_IDS.length - missing.length,
        total: REQUIRED_IDS.length,
        errors: errors,
        engineVersion: VFX && VFX.version,
        isCanvasV2: !!(VFX && VFX.isCanvasV2),
        expectedCount: REQUIRED_IDS.length,
        registeredCount: VFX ? VFX.listRegistered().length : 0,
        missingIds: missing,
        catalogAbilities: Catalog.ABILITIES.length,
        catalogConsumables: Catalog.CONSUMABLES.length,
        groundCanvasSize: groundCanvas ? [groundCanvas.width, groundCanvas.height] : null,
        skyCanvasSize: skyCanvas ? [skyCanvas.width, skyCanvas.height] : null,
        stageSize: [stageSize.w, stageSize.h],
        layoutCheck: {
          unitCount: allUnits.length,
          outOfBoundsCount: outOfBoundsCount,
          overlapCount: overlapCount,
        },
        brokenImagesCount: brokenImages.length,
        activeDuringSample: counts,
        sampleInvoked: invoked,
      };

      global.__SELFTEST__ = summary;
      var banner = $('selftestBanner');
      banner.hidden = false;
      banner.classList.toggle('is-fail', !pass);
      banner.textContent = pass
        ? 'SELFTEST PASS: ' +
          REQUIRED_IDS.length +
          '/' +
          REQUIRED_IDS.length +
          ' 特效已就绪 | 画布 ' +
          stageSize.w +
          'x' +
          stageSize.h +
          ' (' +
          skyCanvas.width +
          'x' +
          skyCanvas.height +
          ') | 活跃元素 ' +
          counts.total +
          ' | 0 越界 0 重叠 0 坏图'
        : 'SELFTEST FAIL: ' + errors.slice(0, 3).join(' ; ');
    }, 680);
  }

  // ---------------------------------------------------------------------------
  // 初始化启动
  // ---------------------------------------------------------------------------
  function init() {
    Sandbox.setUnitElResolver(function (unitId) {
      return document.querySelector('.arena-unit[data-unit-id="' + unitId + '"]');
    });

    buildArenaUnits();
    VFX.mount($('arenaShell'), $('vfxOverlay'), $('vfxGroundOverlay'));

    renderUnits();
    renderHotbars();
    renderCatalog();
    selectDetail('ability.star-piercer');
    renderLogs();

    Sandbox.subscribe(function (reason) {
      if (reason === 'cast') renderCastHud();
      else if (reason === 'log') renderLogs();
      else {
        renderUnits();
        renderHotbars();
        renderCastHud();
        renderLogs();
      }
      if (reason === 'demo') {
        var btn = $('btnAutoDemo');
        btn.classList.toggle('is-active', Sandbox.state.autoDemo);
        btn.textContent = Sandbox.state.autoDemo ? '停止自动演武' : '自动演武';
      }
      if (reason === 'speed') {
        var sbtn = $('btnCastSpeed');
        sbtn.classList.toggle('is-active', Sandbox.state.fastCast);
        sbtn.textContent = Sandbox.state.fastCast ? '极速读条 (0.18x)' : '真实读条 (1.0x)';
      }
    });

    // 顶部控制按钮
    $('btnCastSpeed').addEventListener('click', function () {
      Sandbox.setFastCast(!Sandbox.state.fastCast);
    });
    $('btnAutoDemo').addEventListener('click', function () {
      Sandbox.toggleAutoDemo();
    });
    $('btnReset').addEventListener('click', function () {
      Sandbox.toggleAutoDemo(false);
      Sandbox.reset();
    });
    $('btnClearLog').addEventListener('click', function () {
      Sandbox.state.logs = [];
      renderLogs();
    });

    // 分类 Tab
    $('consoleTabs').addEventListener('click', function (ev) {
      var btn = ev.target.closest('.console-tab');
      if (!btn) return;
      activeTab = btn.dataset.tab;
      var tabs = document.querySelectorAll('#consoleTabs .console-tab');
      for (var i = 0; i < tabs.length; i++) {
        tabs[i].classList.toggle('is-active', tabs[i] === btn);
      }
      renderCatalog();
    });

    // 快捷键 1-6 / Q-Y
    global.addEventListener('keydown', function (ev) {
      if (ev.target && (ev.target.tagName === 'INPUT' || ev.target.tagName === 'TEXTAREA')) return;
      var k = ev.key.toUpperCase();
      if (k >= '1' && k <= '6') {
        var sid = hotbarSkillIds[Number(k) - 1];
        if (sid) Sandbox.castAbility(sid);
      }
      var cidx = ['Q', 'W', 'E', 'R', 'T', 'Y'].indexOf(k);
      if (cidx >= 0 && hotbarConsumableIds[cidx]) {
        Sandbox.useConsumable(hotbarConsumableIds[cidx]);
      }
    });

    // 自检参数检测
    if (global.location && global.location.search.indexOf('selftest=1') !== -1) {
      // 确保所有异步图片加载完成后执行图片和画布体检
      setTimeout(runSelftest, 300);
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})(window);
