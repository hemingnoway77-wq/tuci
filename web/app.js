import { openStore } from './storage.js';
import { renderLibrary, renderRoam } from './library.js';
import { renderCapture, renderReading } from './learning.js';

const root = document.querySelector('#content');
const tabs = document.querySelector('#tabs');
const statusLabel = document.querySelector('#sync-status');
const toastBox = document.querySelector('#toast');
const sections = { capture: '收录', reading: '阅读', library: '词库', roam: '漫游', settings: '设置' };
const symbols = {
  capture: '<path d="m14.5 5.5 4 4M4 20l4.5-1 12-12a2.8 2.8 0 0 0-4-4l-12 12L4 20Z"/><path d="M13 20h7"/>',
  reading: '<path d="M3 4.5C6 4 9 4.5 12 6.5 15 4.5 18 4 21 4.5v14c-3-.5-6 0-9 2-3-2-6-2.5-9-2zM12 6.5v14"/>',
  library: '<rect x="3" y="4" width="4" height="16" rx=".8"/><path d="M4 8h2m3-4h4v16H9zM10 16h2m2.5-11 3.6-1 4 15-3.6 1z"/>',
  roam: '<path d="M3 6h3c3.8 0 7.5 12 12 12h3m-4-4 4 4-4 4M3 18h3c1.6 0 3-1.6 4.2-3.5M14 9.5c1.4-2.2 2.6-3.5 4-3.5h3m-4-4 4 4-4 4"/>',
  settings: '<path d="M3 7h6m4 0h8M3 17h11m4 0h3"/><circle cx="11" cy="7" r="2.4"/><circle cx="16" cy="17" r="2.4"/>'
};
function icon(name) { return '<svg class="icon" aria-hidden="true" viewBox="0 0 24 24">'+symbols[name]+'</svg>'; }
for (const name of ['capture', 'reading', 'library', 'roam']) {
  const a = document.createElement('a');
  a.className = 'tab'; a.href = '#'+name; a.dataset.route = name;
  a.innerHTML = icon(name)+'<span>'+sections[name]+'</span>';
  tabs.append(a);
}
document.querySelector('#settings-link').innerHTML = icon('settings');
const prefsKey = 'tuci.web.preferences.v1';
const defaults = { theme: 'warm', classify: true, vaultPrefix: '英语单词', aiEndpoint: '', aiModel: '', aiKey: '' };
let prefs = { ...defaults };
try { Object.assign(prefs, JSON.parse(localStorage.getItem(prefsKey) || '{}')); } catch { /* Defaults work in a fresh browser. */ }
function applyTheme() {
  document.documentElement.dataset.theme = prefs.theme;
  document.querySelector('meta[name="theme-color"]').content = getComputedStyle(document.documentElement).getPropertyValue('--paper').trim();
}
applyTheme();
function savePrefs(patch) {
  const changed = { ...prefs, ...patch };
  localStorage.setItem(prefsKey, JSON.stringify(changed));
  prefs = changed; applyTheme();
}
let store, cleanup, routeKey, toastTimer;
function toast(text) {
  clearTimeout(toastTimer); toastBox.textContent = text; toastBox.classList.add('show');
  toastTimer = setTimeout(() => toastBox.classList.remove('show'), 3600);
}
function currentRoute() {
  const [name, ...rest] = location.hash.slice(1).split('/');
  let detail;
  try { detail = rest.length ? decodeURIComponent(rest.join('/')) : undefined; } catch { /* Discard invalid links. */ }
  return { name: Object.hasOwn(sections,name) ? name : 'capture', detail };
}
function navigate(name, detail) {
  if (!Object.hasOwn(sections, name)) return;
  const next = '#'+name+(detail == null ? '' : '/'+encodeURIComponent(detail));
  if (location.hash === next) render(); else location.hash = next;
}
function updateStatus() {
  const state = store.status();
  let label = '本机词库', readiness = 'local';
  if (!navigator.onLine) label = '离线';
  else if (state.syncing) label = '同步中';
  else if (state.bound && (state.error || state.pending)) { label = '待同步'; readiness = 'pending'; }
  else if (state.bound) { label = '已连接'; readiness = 'online'; }
  statusLabel.textContent = label; statusLabel.dataset.state = readiness;
  if (currentRoute().name === 'settings') refreshSyncPanel();
}
const ctx = {
  root, toast, navigate,
  notes: () => store.notes(),
  saveNote: (...args) => store.saveNote(...args),
  deleteNote: (...args) => store.deleteNote(...args),
  settings: () => ({ ...prefs }),
  scope: () => store.scope()
};
function render() {
  if (typeof cleanup === 'function') cleanup();
  cleanup = undefined;
  const {name,detail} = currentRoute();
  const different = routeKey !== location.hash;
  routeKey = location.hash;
  document.body.dataset.page = name;
  root.replaceChildren();
  for (const tab of tabs.children) {
    if (tab.dataset.route === name) tab.setAttribute('aria-current','page');
    else tab.removeAttribute('aria-current');
  }
  document.title = sections[name]+' · 兔词';
  const view = {capture:renderCapture,reading:renderReading,library:renderLibrary,roam:renderRoam,settings:renderSettings}[name];
  try { cleanup = view(ctx,detail); }
  catch (e) { showFailure(e); }
  if (different && name !== 'library' && name !== 'reading') window.scrollTo(0,0);
}
function showFailure(error) {
  const p = document.createElement('p'); p.className = 'panel';
  p.textContent = '暂时无法打开此页。'+(error?.message || '');
  const retry = document.createElement('button'); retry.className='secondary'; retry.textContent='重新打开';
  retry.onclick = () => location.reload(); root.replaceChildren(p,retry);
}
function refreshSyncPanel() {
  const panel = root.querySelector('#sync-panel');
  if (!panel) return;
  const s = store.status();
  panel.querySelector('#connected').hidden = !s.bound || s.needsBinding;
  panel.querySelector('#sync-now').disabled = !!s.needsRecovery || !!s.syncing;
  panel.querySelector('#unbound').hidden = s.bound && !s.needsBinding;
  panel.querySelector('#recover').hidden = !s.needsRecovery;
  const line = panel.querySelector('#sync-description');
  if (s.needsBinding) line.textContent = '当前设备的授权已失效。请在已连接的设备上生成同一词库的新配对码。';
  else if (s.needsRecovery) line.textContent = '服务器上的词库记录已恢复到较早版本，请重新核对本机笔记。';
  else if (!s.bound) line.textContent = '在已连接的兔词里生成配对码，即可取回原来的词库。首次使用也可以输入激活码。';
  else if (s.error) line.textContent = s.error;
  else if (s.syncing) line.textContent = '正在同步词库…';
  else if (s.pending) line.textContent = '有 '+s.pending+' 项改动待上传。';
  else if (s.lastSync) line.textContent = '最近同步于 '+new Date(s.lastSync).toLocaleString('zh-CN',{month:'numeric',day:'numeric',hour:'2-digit',minute:'2-digit'})+'。';
  else line.textContent = '已连接到你的词库。';
}
function renderSettings() {
  root.innerHTML = `<div class="page-head"><h1>设置</h1></div><div class="settings-grid">
    <section class="panel" id="sync-panel"><h2>词库同步</h2><p class="hint" id="sync-description"></p>
      <form id="unbound"><label class="field">激活码或配对码<input id="code" autocomplete="off" autocapitalize="none" spellcheck="false" required></label><div class="actions" style="margin-top:14px"><button class="button">连接我的词库</button></div></form>
      <div class="actions" id="connected" hidden><button class="button" id="sync-now">立即同步</button><button class="secondary" id="pair">生成配对码</button><button class="secondary" id="recover" hidden>重新核对词库</button></div>
      <output id="pair-output" class="pair-code" hidden></output>
    </section>
    <form class="panel" id="ai-settings"><h2>AI 词卡</h2>
      <p class="hint">生成词卡时，浏览器会将单词和原句发给你填写的服务商。API Key 保存在这台设备的浏览器中。</p>
      <label class="field">API 地址<input type="url" id="ai-address" placeholder="https://…/v1" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <label class="field">模型名<input id="ai-model" placeholder="填写服务商提供的模型名" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <label class="field">API Key<input type="password" id="ai-key" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <label class="check"><input type="checkbox" id="classify">按主要词性分目录</label>
      <div><button class="button">保存 AI 设置</button></div>
    </form>
    <section class="panel"><h2>纸页主题</h2><div class="theme-options"></div></section>
    <form class="panel" id="links"><h2>笔记链接</h2><label class="field">Obsidian 中的词库相对目录<input id="prefix" autocapitalize="none" spellcheck="false"></label><div class="actions"><button class="secondary">保存目录</button><button class="secondary" type="button" id="refresh-links">刷新词条链接</button></div></form>
    </div>`;
  const $ = key => root.querySelector(key);
  const tryTask = async (button, action) => {
    if (button.disabled) return;
    button.disabled=true;
    try { await action(); }
    catch (e) { toast(e?.message || '操作失败，请重试。'); }
    finally { button.disabled=false; refreshSyncPanel(); }
  };
  $('#unbound').onsubmit = event => {
    event.preventDefault();
    const code = $('#code').value.trim();
    if (code) tryTask(event.currentTarget.querySelector('button'), async () => {
      await store.bind(code); toast('已连接词库'); $('#code') && ($('#code').value='');
    });
  };
  $('#sync-now').onclick = event => tryTask(event.currentTarget,async () => { await store.sync(); toast(store.status().error || '同步完成'); });
  $('#pair').onclick = event => tryTask(event.currentTarget,async () => {
    const code = await store.pair();
    const output = $('#pair-output'); if (output) {output.textContent=code; output.hidden=false;}
  });
  $('#recover').onclick = event => {
    if (confirm('保留本机笔记，重新与服务器逐篇核对？')) tryTask(event.currentTarget, () => store.recover());
  };
  $('#ai-address').value = prefs.aiEndpoint; $('#ai-model').value=prefs.aiModel; $('#ai-key').value=prefs.aiKey;
  $('#classify').checked=prefs.classify;
  $('#ai-settings').onsubmit = event => {
    event.preventDefault();
    const address = $('#ai-address').value.trim();
    try {
      if (address) { const u = new URL(address); if (u.protocol !== 'https:' || u.username || u.password || u.search || u.hash) throw new Error('请填写服务商的 HTTPS API 地址。'); }
      savePrefs({aiEndpoint:address,aiModel:$('#ai-model').value.trim(),aiKey:$('#ai-key').value.trim(),classify:$('#classify').checked}); toast('AI 设置已保存');
    } catch(e) { toast(e.message); }
  };
  const options = $('.theme-options');
  for (const [theme,label] of Object.entries({warm:'暖灰',ink:'墨白',rose:'玫红'})) {
    const b = document.createElement('button'); b.className='secondary'; b.type='button'; b.textContent=label;
    b.setAttribute('aria-pressed',String(theme===prefs.theme));
    b.onclick=()=>{ try {savePrefs({theme}); for(const item of options.children) item.setAttribute('aria-pressed',String(item===b));} catch(e){toast(e.message);} };
    options.append(b);
  }
  $('#prefix').value = prefs.vaultPrefix;
  $('#links').onsubmit=event=>{event.preventDefault();try{savePrefs({vaultPrefix:$('#prefix').value.trim()});toast('目录已保存');}catch(e){toast(e.message);}};
  $('#refresh-links').onclick=event=>tryTask(event.currentTarget,async()=>{const count=await store.refreshLinks();toast(count?'已更新 '+count+' 篇笔记的链接':'词条链接已是最新');});
  refreshSyncPanel();
}

if ('serviceWorker' in navigator) navigator.serviceWorker.register('./sw.js',{scope:'./',updateViaCache:'none'}).catch(() => toast('离线缓存暂未就绪，请联网重新打开。'));
try {
  store = await openStore();
  let previous = '';
  store.subscribe(() => {
    updateStatus();
    const revision = JSON.stringify(store.notes().map(n=>[n.id,n.path,n.markdown]));
    if (revision !== previous) { previous=revision; window.dispatchEvent(new Event('tuci-notes')); }
  });
  window.addEventListener('online', updateStatus); window.addEventListener('offline', updateStatus);
  window.addEventListener('hashchange', render);
  updateStatus(); render();
} catch (error) { showFailure(error); }
