(function () {
  'use strict';

  if (window.top !== window) return;
  if (!chrome.runtime || !chrome.runtime.id) return;

  // 全量扫描只发生在 Ctrl+Alt+K 触发时；MutationObserver 引导的自动重扫
  // 仅在用户于扩展弹窗里开启“自动跟随新内容”后生效，且只发送可视区域
  // 文字，两次之间至少间隔 RESCAN_COOLDOWN，避免聊天页面烧掉大量模型调用。
  const FULL_SCAN_LIMIT = 30000;
  const VIEWPORT_SCAN_LIMIT = 12000;
  const RESCAN_DEBOUNCE = 900;
  const RESCAN_COOLDOWN = 8000;

  const state = {
    client: (crypto.randomUUID ? crypto.randomUUID() : String(Math.random()).slice(2)),
    generation: 0,
    active: false,
    autoFollow: false,
    difficulty: 'standard',
    ignoredTerms: new Set(),
    items: [],
    popup: null,
    popupSeq: 0,
    currentView: null,
    anchor: null,
    history: [],
    refreshTimer: 0,
    lastScanAt: 0,
    scanSeq: 0,
  };

  chrome.storage.local.get({ autoFollow: false, difficulty: 'standard', ignoredTerms: [] }, (result) => {
    state.autoFollow = !!result.autoFollow;
    state.difficulty = ['concise', 'standard', 'detailed'].includes(result.difficulty)
      ? result.difficulty : 'standard';
    state.ignoredTerms = new Set((result.ignoredTerms || []).map((term) => String(term).toLocaleLowerCase()));
  });
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== 'local') return;
    if (Object.prototype.hasOwnProperty.call(changes, 'autoFollow')) {
      state.autoFollow = !!changes.autoFollow.newValue;
      if (state.autoFollow && state.active) scheduleRescan();
    }
    if (Object.prototype.hasOwnProperty.call(changes, 'difficulty')) {
      const value = changes.difficulty.newValue;
      state.difficulty = ['concise', 'standard', 'detailed'].includes(value) ? value : 'standard';
      if (state.active) scan({ viewportOnly: false });
    }
    if (Object.prototype.hasOwnProperty.call(changes, 'ignoredTerms')) {
      const previousSize = state.ignoredTerms.size;
      state.ignoredTerms = new Set(
        (changes.ignoredTerms.newValue || []).map((term) => String(term).toLocaleLowerCase()));
      if (state.active && state.ignoredTerms.size < previousSize)
        scan({ viewportOnly: false });
      else
        removeIgnoredHighlights();
    }
  });

  const root = document.createElement('div');
  root.id = 'semantic-overlay-browser-root';
  (document.documentElement || document.body).appendChild(root);

  // 所有 HTTP 请求都经 background service worker 代理（令牌由它持有）。
  function request(path, body) {
    const options = body === undefined ? {} : { method: 'POST', body };
    return new Promise((resolve, reject) => {
      try {
        chrome.runtime.sendMessage({ type: 'api', path, options }, (response) => {
          if (chrome.runtime.lastError) { reject(new Error(chrome.runtime.lastError.message)); return; }
          if (!response) { reject(new Error('no response from background')); return; }
          if (!response.ok) { reject(new Error(response.error || 'request failed')); return; }
          resolve(response.data);
        });
      } catch (error) {
        reject(error);
      }
    });
  }

  function textNodes() {
    const result = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode())) {
      const parent = node.parentElement;
      if (!parent || root.contains(parent)) continue;
      const tag = parent.tagName;
      if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || !node.nodeValue.trim()) continue;
      result.push(node);
    }
    return result;
  }

  function visibleTextNodes() {
    const margin = 160;
    const limitTop = -margin;
    const limitBottom = innerHeight + margin;
    const result = [];
    for (const node of textNodes()) {
      const range = document.createRange();
      range.selectNodeContents(node);
      const rects = Array.from(range.getClientRects());
      const visible = rects.some((rect) => rect.width && rect.height && rect.bottom > limitTop && rect.top < limitBottom);
      if (visible) result.push(node);
    }
    return result;
  }

  function viewportText(limit) {
    const parts = [];
    let total = 0;
    for (const node of visibleTextNodes()) {
      const value = node.nodeValue.trim();
      if (!value) continue;
      parts.push(value);
      total += value.length + 1;
      if (total >= limit) break;
    }
    return parts.join('\n').slice(0, limit);
  }

  function rangeForTerm(term, ordinal) {
    if (!term) return null;
    let seen = 0;
    for (const node of textNodes()) {
      let from = 0;
      while (from < node.nodeValue.length) {
        const at = node.nodeValue.indexOf(term, from);
        if (at < 0) break;
        if (seen++ === ordinal) {
          const range = document.createRange();
          range.setStart(node, at);
          range.setEnd(node, at + term.length);
          return range;
        }
        from = at + Math.max(1, term.length);
      }
    }
    return null;
  }

  function rangeRect(range) {
    const rects = Array.from(range.getClientRects()).filter((rect) => rect.width && rect.height);
    if (!rects.length) return null;
    let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
    for (const rect of rects) {
      left = Math.min(left, rect.left);
      top = Math.min(top, rect.top);
      right = Math.max(right, rect.right);
      bottom = Math.max(bottom, rect.bottom);
    }
    return { left, top, right, bottom, width: right - left, height: bottom - top };
  }

  // 弹窗锚点持有活 Range：滚动/布局变化后仍能取到当前最新位置，
  // Range 失效（节点被删除）时退回上一次已知位置。
  function anchorRect() {
    if (!state.anchor) return null;
    if (state.anchor.range) {
      try {
        const rect = rangeRect(state.anchor.range);
        if (rect) {
          state.anchor.lastRect = rect;
          return rect;
        }
      } catch (_) { /* Range 失效，使用旧位置 */ }
    }
    return state.anchor.lastRect || null;
  }

  function removeTerms() {
    root.querySelectorAll('.semantic-overlay-browser-term').forEach((node) => node.remove());
    state.items = [];
  }

  function removeIgnoredHighlights() {
    const kept = [];
    for (const item of state.items) {
      if (state.ignoredTerms.has(item.term.toLocaleLowerCase())) {
        item.nodes.forEach((node) => node.remove());
      } else {
        kept.push(item);
      }
    }
    state.items = kept;
  }

  function ignoreTerm(term) {
    const value = String(term || '').trim();
    if (!value) return;
    const key = value.toLocaleLowerCase();
    if (!state.ignoredTerms.has(key)) state.ignoredTerms.add(key);
    chrome.storage.local.set({ ignoredTerms: Array.from(state.ignoredTerms) });
    removeIgnoredHighlights();
    clearPopup();
  }

  function clearPopup() {
    state.popupSeq++;
    if (state.popup) state.popup.remove();
    state.popup = null;
    state.currentView = null;
    state.anchor = null;
    state.history = [];
  }

  function clearAll() {
    removeTerms();
    clearPopup();
    state.active = false;
  }

  function positionPopup(popup, rect) {
    if (!rect) return;
    const gap = 9;
    const width = popup.offsetWidth;
    const height = popup.offsetHeight;
    let x = Math.min(Math.max(8, rect.left), Math.max(8, innerWidth - width - 8));
    let y = rect.bottom + gap;
    if (y + height > innerHeight - 8) y = rect.top - height - gap;
    if (y < 8) y = 8;
    popup.style.left = x + 'px';
    popup.style.top = y + 'px';
  }

  function renderView(view) {
    if (!state.popup || !view) return;
    state.currentView = view;
    const title = state.popup.querySelector('.semantic-overlay-browser-title');
    const body = state.popup.querySelector('.semantic-overlay-browser-body');
    const source = state.popup.querySelector('[data-action="source"]');
    const retry = state.popup.querySelector('[data-action="retry"]');
    const copy = state.popup.querySelector('[data-action="copy"]');
    title.querySelector('[data-action="back"]').style.visibility = state.history.length ? 'visible' : 'hidden';
    title.querySelector('[data-action="term"]').textContent = view.term;
    body.textContent = '';
    const text = view.explanation || '暂时无法获取可靠解释。';
    const entities = (view.entities || []).filter((entity) => entity && Number.isInteger(entity.start) && Number.isInteger(entity.end));
    let cursor = 0;
    for (const entity of entities.sort((a, b) => a.start - b.start)) {
      if (entity.start < cursor || entity.end > text.length || text.slice(entity.start, entity.end) !== entity.text) continue;
      body.append(document.createTextNode(text.slice(cursor, entity.start)));
      const link = document.createElement('a');
      link.href = '#';
      link.textContent = entity.text;
      link.addEventListener('click', (event) => {
        event.preventDefault();
        if (state.history.length >= 3) return;
        state.history.push(state.currentView);
        showPopup(entity.text, state.currentView.explanation);
      });
      body.append(link);
      cursor = entity.end;
    }
    body.append(document.createTextNode(text.slice(cursor)));
    const sourceUrl = (view.sources || []).find((value) => /^https?:\/\//i.test(value || ''));
    source.hidden = !sourceUrl;
    if (sourceUrl) source.href = sourceUrl;
    retry.hidden = !view.can_refresh;
    retry.disabled = false;
    retry.textContent = '换个解释';
    copy.textContent = '复制解释';
    positionPopup(state.popup, anchorRect());
  }

  async function loadPopupDefinition(popup, term, context, refresh, previousExplanation) {
    if (!state.anchor || state.popup !== popup) return;
    const anchor = state.anchor;
    const previousView = state.currentView;
    const sequence = ++state.popupSeq;
    const body = popup.querySelector('.semantic-overlay-browser-body');
    const retry = popup.querySelector('[data-action="retry"]');
    popup.querySelector('[data-action="term"]').textContent = term;
    body.textContent = refresh ? '正在换一种解释…' : '正在查询…';
    retry.disabled = true;
    if (refresh) retry.textContent = '正在重写…';
    try {
      const response = await request('/lookup', {
        term,
        context: String(context || '').slice(0, 500),
        refresh: !!refresh,
        previous_explanation: refresh ? String(previousExplanation || '').slice(0, 500) : '',
      });
      if (sequence !== state.popupSeq || state.popup !== popup || state.anchor !== anchor) return;
      renderView({
        term,
        context,
        explanation: response.explanation,
        entities: response.entities,
        sources: response.sources,
        can_refresh: !!response.can_refresh,
      });
    } catch (error) {
      if (sequence !== state.popupSeq || state.popup !== popup || state.anchor !== anchor) return;
      if (refresh && previousView) {
        renderView(previousView);
        retry.textContent = '重试失败';
      } else {
        renderView({ term, context, explanation: '暂时无法获取可靠解释。', entities: [], can_refresh: false });
      }
    }
  }

  async function showPopup(term, context) {
    if (!state.anchor) return;
    const savedHistory = state.history.slice();
    const savedAnchor = state.anchor;
    clearPopup();
    state.history = savedHistory;
    state.anchor = savedAnchor;
    const popup = document.createElement('div');
    popup.className = 'semantic-overlay-browser-popup';
    popup.innerHTML = '<div class="semantic-overlay-browser-title"><button data-action="back" aria-label="返回">‹</button><span data-action="term"></span><button data-action="close" aria-label="关闭">×</button></div><div class="semantic-overlay-browser-body">正在查询…</div><div class="semantic-overlay-browser-footer"><a data-action="source" target="_blank" rel="noopener noreferrer" hidden>查看来源</a><button data-action="ignore" type="button">不再标注</button><button data-action="retry" type="button" hidden>换个解释</button><button data-action="copy" type="button">复制解释</button></div>';
    root.appendChild(popup);
    state.popup = popup;
    popup.querySelector('[data-action="close"]').addEventListener('click', clearPopup);
    popup.querySelector('[data-action="back"]').addEventListener('click', () => {
      if (!state.history.length) return;
      renderView(state.history.pop());
    });
    popup.querySelector('[data-action="copy"]').addEventListener('click', async (event) => {
      const text = state.currentView && state.currentView.explanation;
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
        event.currentTarget.textContent = '已复制';
      } catch (_) {
        event.currentTarget.textContent = '复制失败';
      }
    });
    popup.querySelector('[data-action="ignore"]').addEventListener('click', () => {
      if (state.currentView) ignoreTerm(state.currentView.term);
    });
    popup.querySelector('[data-action="retry"]').addEventListener('click', () => {
      const view = state.currentView;
      if (!view || !view.can_refresh) return;
      loadPopupDefinition(popup, view.term, view.context, true, view.explanation);
    });
    positionPopup(popup, anchorRect());
    loadPopupDefinition(popup, term, context, false, '');
  }

  function showTaskPopup(item) {
    if (!state.anchor || !item) return;
    const savedAnchor = state.anchor;
    clearPopup();
    state.anchor = savedAnchor;
    const popup = document.createElement('div');
    popup.className = 'semantic-overlay-browser-popup';
    popup.innerHTML = '<div class="semantic-overlay-browser-title"><button data-action="back" aria-label="返回">‹</button><span data-action="term"></span><button data-action="close" aria-label="关闭">×</button></div><div class="semantic-overlay-browser-body"></div><div class="semantic-overlay-browser-footer"><a data-action="source" target="_blank" rel="noopener noreferrer" hidden>查看来源</a><button data-action="ignore" type="button" hidden>不再标注</button><button data-action="retry" type="button" hidden>换个解释</button><button data-action="copy" type="button">复制日程</button></div>';
    root.appendChild(popup);
    state.popup = popup;
    popup.querySelector('[data-action="close"]').addEventListener('click', clearPopup);
    popup.querySelector('[data-action="copy"]').addEventListener('click', async (event) => {
      const text = state.currentView && state.currentView.explanation;
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
        event.currentTarget.textContent = '已复制';
      } catch (_) {
        event.currentTarget.textContent = '复制失败';
      }
    });
    const timeText = item.time_text || item.term;
    const title = item.title || '待确认事项';
    renderView({
      term: '待确认日程',
      explanation: `时间：${timeText}\n事项：${title}\n状态：等待确认，尚未写入任何日历。`,
      entities: [],
      sources: [],
      can_refresh: false,
    });
    popup.querySelector('[data-action="copy"]').textContent = '复制日程';
    const edit = document.createElement('button');
    edit.type = 'button';
    edit.textContent = '编辑日程…';
    popup.querySelector('.semantic-overlay-browser-footer').prepend(edit);
    edit.addEventListener('click', () => editTaskPopup(popup, item));
  }

  function editTaskPopup(popup, item) {
    if (state.popup !== popup) return;
    popup.querySelector('.semantic-overlay-browser-footer').hidden = true;
    const body = popup.querySelector('.semantic-overlay-browser-body');
    body.textContent = '';
    const form = document.createElement('form');
    form.className = 'semantic-overlay-calendar-form';
    form.innerHTML = '<label>事项<input name="title" required maxlength="120"></label>' +
      '<label>开始日期和时间（含年份）<input name="start" type="datetime-local" required></label>' +
      '<label>结束日期和时间<input name="end" type="datetime-local" required></label>' +
      '<label>事件当日 UTC 时差<input name="utc_offset" required placeholder="中国填 +08:00"></label>' +
      '<p>导出后请在日历软件中导入。夏令时地区需填写事件当日的时差。</p>' +
      '<button type="submit">确认并导出日历文件</button><p role="status"></p>';
    form.elements.title.value = item.title || '';
    body.append(form);
    const submit = form.querySelector('[type="submit"]');
    const status = form.querySelector('[role="status"]');
    let busy = false;
    form.addEventListener('input', () => { if (!busy) { submit.disabled = false; status.textContent = ''; } });
    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      if (busy) return;
      const payload = { confirmed: true };
      for (const name of ['title', 'start', 'end', 'utc_offset']) payload[name] = form.elements[name].value.trim();
      busy = true;
      submit.disabled = true;
      form.querySelectorAll('input').forEach((input) => { input.disabled = true; });
      status.textContent = '正在校验日程…';
      let saved = false;
      try {
        const response = await request('/calendar/export', payload);
        if (state.popup !== popup || !form.isConnected) return;
        if (!response.ok) { status.textContent = response.error || '导出失败'; return; }
        const url = URL.createObjectURL(new Blob([response.ics], { type: 'text/calendar;charset=utf-8' }));
        const link = document.createElement('a');
        link.href = url;
        link.download = response.filename;
        form.append(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        saved = true;
        status.textContent = '已请求下载日历文件，请检查浏览器下载列表后导入。重复导入的处理取决于日历软件。';
      } catch (_) {
        status.textContent = '连接失败，请确认实时字典正在运行后重试。';
      } finally {
        busy = false;
        submit.disabled = saved;
        form.querySelectorAll('input').forEach((input) => { input.disabled = false; });
      }
    });
    positionPopup(popup, anchorRect());
  }

  function analysisHighlights(result) {
    const concepts = Array.isArray(result && result.entities) ? result.entities : [];
    const tasks = Array.isArray(result && result.actions)
      ? result.actions.map((action) => ({ ...action, kind: 'task' })) : [];
    return concepts.concat(tasks);
  }

  function termColor(term) {
    let hash = 2166136261;
    const key = String(term || '').normalize('NFKC').toLowerCase().replace(/\s/g, '');
    for (let i = 0; i < key.length; i++) hash = Math.imul(hash ^ key.charCodeAt(i), 16777619) >>> 0;
    let hue = hash % 240;
    if (hue >= 150) hue += 90;
    const mid = 85 + Math.round(150 * (1 - Math.abs((hue / 60) % 2 - 1)));
    if (hue < 60) return [235, mid, 85];
    if (hue < 120) return [mid, 235, 85];
    if (hue < 180) return [85, 235, mid];
    if (hue < 240) return [85, mid, 235];
    if (hue < 300) return [mid, 85, 235];
    return [235, 85, mid];
  }

  function renderHighlights(entities) {
    removeTerms();
    const occurrenceByTerm = Object.create(null);
    for (const entity of entities || []) {
      if (!entity || !entity.text) continue;
      const isTask = entity.kind === 'task' || entity.type === 'calendar_event';
      if (!isTask && state.ignoredTerms.has(entity.text.toLocaleLowerCase())) continue;
      const key = entity.text;
      const ordinal = occurrenceByTerm[key] || 0;
      occurrenceByTerm[key] = ordinal + 1;
      const range = rangeForTerm(entity.text, ordinal);
      if (!range) continue;
      const parent = range.startContainer.parentElement;
      const surrounding = (parent && parent.innerText) || range.startContainer.nodeValue || '';
      const at = surrounding.indexOf(entity.text);
      const contextStart = Math.max(0, (at >= 0 ? at : 0) - 220);
      const item = {
        range,
        term: entity.text,
        context: surrounding.slice(contextStart, contextStart + 500),
        kind: isTask ? 'task' : 'concept',
        title: entity.title || '',
        time_text: entity.time_text || entity.text,
        nodes: []
      };
      for (const rect of range.getClientRects()) {
        if (!rect.width || !rect.height) continue;
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'semantic-overlay-browser-term';
        if (isTask) button.classList.add('semantic-overlay-browser-task');
        else {
          const rgb = termColor(entity.text);
          button.style.backgroundColor = `rgba(${rgb.join(',')}, .34)`;
          button.style.borderColor = `rgba(${rgb.map((v) => Math.round(v * .7)).join(',')}, .82)`;
        }
        button.title = isTask ? '点击查看待确认日程' : '点击查看解释：' + entity.text;
        button.style.left = rect.left + 'px';
        button.style.top = rect.top + 'px';
        button.style.width = rect.width + 'px';
        button.style.height = rect.height + 'px';
        button.addEventListener('click', () => {
          state.history = [];
          state.anchor = { range: item.range, lastRect: { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height } };
          if (item.kind === 'task') showTaskPopup(item);
          else showPopup(item.term, item.context);
        });
        root.appendChild(button);
        item.nodes.push(button);
      }
      if (item.nodes.length) state.items.push(item);
    }
  }

  function updatePositions() {
    for (const item of state.items) {
      const rects = Array.from(item.range.getClientRects()).filter((rect) => rect.width && rect.height);
      item.nodes.forEach((node, index) => {
        const rect = rects[index];
        if (!rect) { node.style.display = 'none'; return; }
        node.style.display = '';
        node.style.left = rect.left + 'px';
        node.style.top = rect.top + 'px';
        node.style.width = rect.width + 'px';
        node.style.height = rect.height + 'px';
      });
    }
    if (state.popup && state.currentView) positionPopup(state.popup, anchorRect());
  }

  async function scan(options) {
    if (!document.body) return;
    const sequence = ++state.scanSeq;
    state.lastScanAt = Date.now();
    const text = options && options.viewportOnly
      ? viewportText(VIEWPORT_SCAN_LIMIT)
      : document.body.innerText.slice(0, FULL_SCAN_LIMIT);
    if (!text.trim()) return;
    try {
      const preview = await request('/analyze', { text, mode: 'local', difficulty: state.difficulty });
      if (state.active && sequence === state.scanSeq) renderHighlights(analysisHighlights(preview));
    } catch (error) {
      // 请求失败时保留现有高亮：服务短暂不可用不应清空整页标注。
      return;
    }

    try {
      const refined = await request('/analyze', { text, mode: 'model', difficulty: state.difficulty });
      if (state.active && sequence === state.scanSeq && refined.analysis_mode === 'llm') {
        renderHighlights(analysisHighlights(refined));
      }
    } catch (error) {
      // 模型不可用时保留已经显示的本地结果。
    }
  }

  function scheduleRescan() {
    if (!state.active || !state.autoFollow || state.refreshTimer) return;
    const wait = Math.max(RESCAN_DEBOUNCE, RESCAN_COOLDOWN - (Date.now() - state.lastScanAt));
    state.refreshTimer = setTimeout(() => {
      state.refreshTimer = 0;
      if (!state.active || !state.autoFollow) return;
      scan({ viewportOnly: true });
    }, wait);
  }

  async function acknowledge(generation) {
    try { await request('/browser/ack', { generation, client: state.client, focused: document.hasFocus() }); } catch (_) { }
  }

  async function poll() {
    try {
      const command = await request('/browser/poll?client=' + encodeURIComponent(state.client));
      if (command.generation > state.generation && command.client === state.client) {
        state.generation = command.generation;
        await acknowledge(command.generation);
        if (command.kind === 'clear') clearAll();
        if (command.kind === 'scan' && document.hasFocus()) {
          state.active = true;
          clearPopup();
          await scan();
        }
      }
    } catch (_) { }
    setTimeout(poll, 300);
  }

  document.addEventListener('pointerdown', (event) => {
    if (state.popup && !state.popup.contains(event.target) && !event.target.closest('.semantic-overlay-browser-term')) clearPopup();
  }, true);
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      if (state.refreshTimer) { clearTimeout(state.refreshTimer); state.refreshTimer = 0; }
      removeTerms();
      clearPopup();
      state.active = false;
    }
  });
  addEventListener('scroll', updatePositions, true);
  addEventListener('resize', updatePositions);
  new MutationObserver((mutations) => {
    if (mutations.some((mutation) => !root.contains(mutation.target))) scheduleRescan();
  }).observe(document.body, { childList: true, subtree: true, characterData: true });
  poll();
})();
