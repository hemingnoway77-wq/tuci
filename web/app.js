import {createLoginLifecycle,settleLoginViewport,playLoginTransition} from './login-transition.js';
import { dictionaryStatus,downloadDictionary } from './dictionary.js';
import { openStore, readLegacyStore } from './storage.js';
import { accountRequest,checkedIdentity,invitesRequest,themeNames,localThemes } from './account.js';
import { renderLibrary, renderRoam } from './library.js';
import { renderCapture, renderReading } from './learning.js';
import { createTabIndicator } from './tab-indicator.js';

const root = document.querySelector('#content');
const tabs = document.querySelector('#tabs');
const statusLabel = document.querySelector('#sync-status');
const toastBox = document.querySelector('#toast');
const sections = { capture: '收录', reading: '每日阅读', library: '词库', roam: '漫游', settings: '设置' };
const symbols = {
  capture: '<path d="m14.5 5.5 4 4M4 20l4.5-1 12-12a2.8 2.8 0 0 0-4-4l-12 12L4 20Z"/><path d="M13 20h7"/>',
  reading: '<path d="M3 4.5C6 4 9 4.5 12 6.5 15 4.5 18 4 21 4.5v14c-3-.5-6 0-9 2-3-2-6-2.5-9-2zM12 6.5v14"/>',
  library: '<rect x="3" y="4" width="4" height="16" rx=".8"/><path d="M4 8h2m3-4h4v16H9zM10 16h2m2.5-11 3.6-1 4 15-3.6 1z"/>',
  roam: '<path d="M3 6h3c3.8 0 7.5 12 12 12h3m-4-4 4 4-4 4M3 18h3c1.6 0 3-1.6 4.2-3.5M14 9.5c1.4-2.2 2.6-3.5 4-3.5h3m-4-4 4 4-4 4"/>',
  settings: '<path d="M3 7h6m4 0h8M3 17h11m4 0h3"/><circle cx="11" cy="7" r="2.4"/><circle cx="16" cy="17" r="2.4"/>'
};
function icon(name) { return '<svg class="icon" aria-hidden="true" viewBox="0 0 24 24">'+symbols[name]+'</svg>'; }
for (const name of ['capture', 'library', 'roam', 'reading']) {
  const a = document.createElement('a');
  a.className = 'tab'; a.href = '#'+name; a.dataset.route = name;
  a.innerHTML = icon(name)+'<span>'+sections[name]+'</span>';
  tabs.append(a);
}
const moduleIndicator = createTabIndicator(tabs);
const settingsLink = document.querySelector('#settings-link');
settingsLink.innerHTML = icon('settings');
let prefsKey = 'tuci.web.preferences.v1';
const identityKey='tuci.web.account.identity.v1', loggedOutKey='tuci.web.account.logged-out.v1';
let identity=null, legacy=null, unsubscribe=null,legacyLoad=null;
function loadLegacy(){return legacyLoad ||= readLegacyStore().then(value=>{legacy=value;return value;});}
const loginLifecycle=createLoginLifecycle();let loginSurface=null,viewEpoch=0;
const accountChannel=typeof BroadcastChannel==='function'?new BroadcastChannel('tuci-web-account-session-v1'):null;
const defaults = { theme: 'ink', classify: true, vaultPrefix: '英语单词', aiEndpoint: '', aiModel: '', aiKey: '' };
let prefs = { ...defaults };

function applyTheme() {
  document.documentElement.dataset.theme = prefs.theme;
  document.querySelector('meta[name="theme-color"]').content = getComputedStyle(document.documentElement).getPropertyValue('--paper').trim();
}
applyTheme();
function savePrefs(patch) {
  const changed = { ...prefs, ...patch };
  localStorage.setItem(prefsKey, JSON.stringify(changed));
  prefs = changed; applyTheme();
  if (patch.theme && store) store.setState('preference/theme',{name:themeNames[patch.theme]}).catch(e=>toast(e.message));
}
let store, cleanup, routeKey, activePage, pageMotion, toastTimer;
let webRegistration, webUpdateReady=false;
const dictionaryDownload=offlineDownloadPanel({
  panelId:'offline-dictionary', title:'离线词典', initialLabel:'下载完整离线词典（约 27 MB）',
  status:dictionaryStatus, download:downloadDictionary
});
const translatorDownload=offlineDownloadPanel({
  panelId:'offline-translator', title:'离线翻译模型', initialLabel:'下载离线翻译模型（约 55 MB）',
  status:async()=>(await import('./offline-translation.js')).offlineTranslationStatus(),
  download:async(options)=>(await import('./offline-translation.js')).downloadOfflineTranslation(options)
});
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
  if (!store) {statusLabel.textContent='登录后同步';return;}
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
  scope: () => store?.scope(),
  practice: () => store?.practice,
  stateEntries: () => store.stateEntries(),
  setState: (...args) => store.setState(...args),
  setStates: (...args) => store.setStates(...args),
  subscribeState: fn => store.subscribe(fn),
  moveNote: (...args) => store.moveNote(...args),
  trashNote: (...args) => store.trashNote(...args),
  trashItems: () => store.trashItems(),
  restoreNote: (...args) => store.restoreNote(...args)
};
function render() {
  if (!store) return;
  if (typeof cleanup === 'function') cleanup();
  cleanup = undefined;
  const {name,detail} = currentRoute();
  const different = routeKey !== location.hash;
  const changedPage = activePage && activePage !== name;
  activePage = name;
  routeKey = location.hash;
  document.body.dataset.page = name;
  root.replaceChildren();
  let selectedTab;
  for (const tab of tabs.children) {
    if (tab.dataset.route === name) { tab.setAttribute('aria-current','page'); selectedTab=tab; }
    else tab.removeAttribute('aria-current');
  }
  moduleIndicator.select(selectedTab);
  document.title = sections[name]+' · 兔词';
  const view = {capture:renderCapture,reading:renderReading,library:renderLibrary,roam:renderRoam,settings:renderSettings}[name];
  try { cleanup = view(ctx,detail); }
  catch (e) { showFailure(e); }
  if (different && name !== 'library' && name !== 'reading') window.scrollTo(0,0);
  if (changedPage && !matchMedia('(prefers-reduced-motion: reduce)').matches) {
    pageMotion?.cancel();
    pageMotion=root.animate?.([{opacity:.8,transform:'translateY(5px)'},{opacity:1,transform:'none'}],
      {duration:165,easing:'cubic-bezier(.2,.7,.25,1)'});
  }
}
function showFailure(error) {
  const p = document.createElement('p'); p.className = 'panel';
  p.textContent = '暂时无法打开此页。'+(error?.message || '');
  const retry = document.createElement('button'); retry.className='secondary'; retry.textContent='重新打开';
  retry.onclick = () => location.reload(); root.replaceChildren(p,retry);
}
function refreshSyncPanel() {
  const line=root.querySelector('#sync-description'), button=root.querySelector('#sync-now');
  if (!line || !store) return;
  const s=store.status();
  button.disabled=s.syncing || s.needsBinding || s.needsRecovery;
  line.textContent=s.needsBinding?'请重新登录以继续同步。本机数据已保留。':s.needsRecovery?'云端历史需要重新核对，请先保留本机数据。':s.error || (s.syncing?'正在同步词库和学习记录…':s.pending?'有 '+s.pending+' 项改动待同步。':s.lastSync?'最近同步于 '+new Date(s.lastSync).toLocaleString('zh-CN'):'已连接账号词库。');
  const recover=root.querySelector('#recover');if(recover)recover.hidden=!s.needsRecovery;
}
function renderSettings(_ctx,detail) {
  const profile=identity,ownedStore=store;
  const labels={account:'账号与同步',library:'本地词库',ai:'AI 接口',app:'应用'};
  root.innerHTML=`<div class="page-head"><h1>设置</h1></div><div class="settings-layout">
    <div class="settings-nav" role="tablist" aria-label="设置分类">
      ${Object.entries(labels).map(([key,label])=>`<button type="button" role="tab" id="settings-tab-${key}" data-settings-tab="${key}" aria-controls="settings-${key}">${label}${key==='app'?'<i class="update-dot" id="update-tab-dot" aria-hidden="true" hidden></i>':''}</button>`).join('')}
    </div>
    <div class="settings-grid" role="tabpanel" id="settings-account" aria-labelledby="settings-tab-account">
    <section class="panel" id="sync-panel"><h2>账号与同步</h2><p id="account-name"></p><p class="hint" id="sync-description" role="status"></p><div class="actions"><button class="button" id="sync-now">立即同步</button><button class="secondary" id="logout">退出账号</button><button class="secondary" id="recover" hidden>重新核对词库</button></div></section>
    <details class="panel settings-disclosure"><summary>修改密码</summary><form id="password-settings"><label class="field">当前密码<input id="current-password" type="password" autocomplete="current-password" required></label><label class="field">新密码<input id="new-password" type="password" autocomplete="new-password" minlength="8" maxlength="128" required></label><div><button class="secondary">保存新密码</button></div></form></details>
    ${profile.canInvite?`<details class="panel settings-disclosure" id="invite-panel"><summary>邀请码</summary><p class="hint">邀请码用于注册账号，7 天内有效，仅可使用一次。</p><div class="actions"><button class="button" id="make-invite">生成邀请码</button><button class="secondary" id="refresh-invites">刷新列表</button></div><div class="invite-new" id="invite-code" hidden><code id="invite-text"></code><button class="secondary" type="button" id="copy-invite">复制</button></div><p class="hint" id="invite-status" role="status"></p><ul class="invite-list" id="invite-list"></ul></details>`:''}
    </div>
    <div class="settings-grid" role="tabpanel" id="settings-ai" aria-labelledby="settings-tab-ai" hidden>
    <form class="panel" id="ai-settings"><h2>AI 接口</h2>
      <label class="field">API 地址<input type="url" id="ai-address" placeholder="https://…/v1" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <label class="field">模型名<input id="ai-model" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <label class="field">API Key<input type="password" id="ai-key" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <p class="hint">API Key 保存在此设备。收录与生成练习使用你填写的服务商。</p><div><button class="button">保存 AI 设置</button></div></form>
    </div>
    <div class="settings-grid" role="tabpanel" id="settings-library" aria-labelledby="settings-tab-library" hidden>
    <form class="panel" id="links"><h2>本地词库</h2><label class="field">词库相对目录<input id="prefix" autocapitalize="none" spellcheck="false"></label><label class="check"><input type="checkbox" id="classify">按主要词性分目录</label><div class="actions"><button class="button">保存词库设置</button><button class="secondary" type="button" id="refresh-links">刷新词条链接</button></div></form>
    <section class="panel" id="offline-dictionary"><h2>离线查词</h2><p class="hint">与 Windows 使用同一份英汉词典，约 40 万词。查词会按需下载字母分片，也可提前下载完整词典。</p><p id="dictionary-status" class="hint" role="status">正在检查下载状态…</p><progress id="dictionary-progress" value="0" max="26" aria-label="词典下载进度" hidden></progress><div class="actions"><button class="secondary" id="dictionary-download" data-download>下载完整离线词典（约 27 MB）</button><button class="secondary" id="dictionary-cancel" data-cancel hidden>暂停下载</button></div></section>
    <section class="panel" id="offline-translator"><h2>离线翻译</h2><p class="hint">下载后，查词遇到词典未收录的词时可在此设备上翻译。</p><p class="hint" role="status">正在检查下载状态…</p><progress value="0" max="1" aria-label="翻译模型下载进度" hidden></progress><div class="actions"><button class="secondary" data-download>下载离线翻译模型（约 55 MB）</button><button class="secondary" data-cancel hidden>暂停下载</button></div></section>
    </div>
    <div class="settings-grid" role="tabpanel" id="settings-app" aria-labelledby="settings-tab-app" hidden>
      <section class="panel"><h2>应用更新</h2><p class="hint" id="web-update-status" role="status"></p><div class="actions"><button class="secondary" id="check-web-update">检查更新</button><a class="secondary" href="./update.html">修复离线缓存</a></div></section>
      <section class="panel"><h2>主题</h2><div class="theme-options"></div></section>
    </div></div>`;
  const layout=root.querySelector('.settings-layout');
  const $=key=>layout.querySelector(key);
  const nav=$('.settings-nav'), buttons=[...nav.querySelectorAll('[data-settings-tab]')];
  const indicator=createTabIndicator(nav,{vertical:()=>matchMedia('(min-width: 680px)').matches});
  let inviteLoad=null, activeSection=null;
  const stopInviteLoad=()=>{
    if(!inviteLoad)return;
    const request=inviteLoad;inviteLoad=null;request.abort();
    $('#refresh-invites').disabled=false;
    $('#invite-status').textContent='';
  };
  function selectCategory(button,animate=true) {
    activeSection=button.dataset.settingsTab;
    for(const item of buttons){
      const selected=item===button;
      item.setAttribute('aria-selected',String(selected));
      item.tabIndex=selected?0:-1;
      $('#settings-'+item.dataset.settingsTab).hidden=!selected;
    }
    indicator.select(button,animate);
    if(activeSection==='library'){void dictionaryDownload.refresh();void translatorDownload.refresh();}
    if(activeSection==='account' && $('#invite-panel')?.open) $('#refresh-invites').click();
    else stopInviteLoad();
  }
  for(const button of buttons)button.onclick=()=>selectCategory(button);
  nav.onkeydown=event=>{
    const index=buttons.indexOf(event.target);
    const direction={ArrowRight:1,ArrowDown:1,ArrowLeft:-1,ArrowUp:-1}[event.key];
    if(index<0 || (direction===undefined && event.key!=='Home' && event.key!=='End'))return;
    const next=event.key==='Home'?0:event.key==='End'?buttons.length-1:(index+direction+buttons.length)%buttons.length;
    event.preventDefault();buttons[next].focus();selectCategory(buttons[next]);
  };
  const task=async(button,action)=>{if(button.disabled)return;button.disabled=true;try{await action();}catch(e){toast(e.message);}finally{button.disabled=false;refreshSyncPanel();}};
  $('#account-name').textContent=profile.username;
  if(profile.canInvite){
    const details=$('#invite-panel'), refresh=$('#refresh-invites');
    const codeBox=$('#invite-code'),codeText=$('#invite-text'),status=$('#invite-status'),list=$('#invite-list');
    const when=seconds=>new Date(seconds*1000).toLocaleString('zh-CN',{month:'numeric',day:'numeric',hour:'2-digit',minute:'2-digit'});
    const loadInvites=async()=>{
      if(inviteLoad || !details.open || activeSection!=='account')return;
      const request=new AbortController();
      inviteLoad=request;refresh.disabled=true;status.textContent='正在加载邀请码…';
      const timeout=setTimeout(()=>request.abort(),6000);
      try{
        const data=await invitesRequest(null,profile.vaultId,{signal:request.signal});
        if(inviteLoad!==request)return;
        const rows=(data.invites||[]).map(item=>{
          const row=document.createElement('li'),state=document.createElement('span'),meta=document.createElement('span');
          state.textContent=item.used?'已使用':item.expires*1000<=Date.now()?'已过期':'未使用';
          meta.textContent=when(item.created)+' 创建'+(item.used?'':' · '+when(item.expires)+' 过期');
          row.append(state,meta);return row;
        });
        list.replaceChildren(...(rows.length?rows:[Object.assign(document.createElement('li'),{textContent:'还没有生成过邀请码。'})]));
        status.textContent=codeBox.hidden?'':'7 天内有效，只能使用一次。';
      }catch(error){if(inviteLoad===request)status.textContent=request.signal.aborted?'加载超时，可以刷新列表重试。':error.message;}
      finally{
        clearTimeout(timeout);
        if(inviteLoad===request){inviteLoad=null;refresh.disabled=false;}
      }
    };
    details.ontoggle=()=>{
      if(details.open)loadInvites();
      else {stopInviteLoad();refresh.disabled=false;status.textContent='';}
    };
    refresh.onclick=loadInvites;
    $('#make-invite').onclick=event=>task(event.currentTarget,async()=>{
      stopInviteLoad();refresh.disabled=false;
      const made=await invitesRequest({},profile.vaultId);
      codeBox.hidden=false;codeText.textContent=made.code;status.textContent='7 天内有效，只能使用一次。';
      void loadInvites();
    });
    $('#copy-invite').onclick=async()=>{
      try{await navigator.clipboard.writeText(codeText.textContent);status.textContent='邀请码已复制。';}
      catch{status.textContent='请长按邀请码手动复制。';}
    };
  }
  $('#sync-now').onclick=e=>task(e.currentTarget,async()=>{await ownedStore.sync();toast('词库和学习记录已同步');});
  $('#recover').onclick=e=>{if(confirm('保留本机笔记，重新与服务器逐篇核对？'))task(e.currentTarget,()=>ownedStore.recover());};
  $('#logout').onclick=e=>task(e.currentTarget,async()=>{
    const owned=store,vault=identity.vaultId;localStorage.setItem(loggedOutKey,'1');localStorage.removeItem(identityKey);accountChannel?.postMessage({logout:true});
    try{await accountRequest('logout',{},null,vault);}finally{if(store===owned){closeAccount();renderAuth('login');}}
  });
  $('#ai-address').value=prefs.aiEndpoint;$('#ai-model').value=prefs.aiModel;$('#ai-key').value=prefs.aiKey;$('#classify').checked=prefs.classify;
  $('#ai-settings').onsubmit=e=>{e.preventDefault();try{
    const address=$('#ai-address').value.trim();if(address){const u=new URL(address);if(u.protocol!=='https:'||u.username||u.password||u.search||u.hash)throw Error('请填写服务商的 HTTPS API 地址。');}
    savePrefs({aiEndpoint:address,aiModel:$('#ai-model').value.trim(),aiKey:$('#ai-key').value.trim()});toast('AI 设置已保存在此设备');
  }catch(error){toast(error.message);}};
  for(const [theme,label] of Object.entries({warm:'暖灰',ink:'墨白',rose:'玫红'})){
    const b=document.createElement('button');b.type='button';b.className='secondary';b.textContent=label;b.setAttribute('aria-pressed',String(theme===prefs.theme));
    b.onclick=()=>{savePrefs({theme});for(const item of $('.theme-options').children)item.setAttribute('aria-pressed',String(item===b));};$('.theme-options').append(b);
  }
  $('#password-settings').onsubmit=e=>{e.preventDefault();task(e.currentTarget.querySelector('button'),async()=>{
    await accountRequest('password',{currentPassword:$('#current-password').value,newPassword:$('#new-password').value},null,profile.vaultId);$('#password-settings').reset();toast('密码已更新');
  });};
  $('#prefix').value=prefs.vaultPrefix;
  $('#links').onsubmit=e=>{e.preventDefault();savePrefs({vaultPrefix:$('#prefix').value.trim(),classify:$('#classify').checked});toast('词库设置已保存');};
  $('#refresh-links').onclick=e=>task(e.currentTarget,async()=>{const count=await ownedStore.refreshLinks();toast(count?'已更新 '+count+' 篇笔记的链接':'词条链接已是最新');});
  for(const [panel,download] of [['#offline-dictionary',dictionaryDownload],['#offline-translator',translatorDownload]]){
    $(panel).querySelector('[data-download]').onclick=download.start;
    $(panel).querySelector('[data-cancel]').onclick=download.cancel;
  }
  const hasSettingsDraft=()=>$('#ai-address').value.trim()!==prefs.aiEndpoint ||
    $('#ai-model').value.trim()!==prefs.aiModel || $('#ai-key').value.trim()!==prefs.aiKey ||
    $('#prefix').value.trim()!==prefs.vaultPrefix || $('#classify').checked!==prefs.classify ||
    !!($('#current-password').value || $('#new-password').value);
  $('#check-web-update').onclick=event=>checkWebUpdate(event,hasSettingsDraft);
  selectCategory(buttons.find(button=>button.dataset.settingsTab===detail) || buttons[0],false);
  refreshSyncPanel();showWebUpdate();
  return ()=>{stopInviteLoad();indicator.dispose();};
}
function offlineDownloadPanel({panelId,title,initialLabel,status,download}){
  let transfer=null, info=null, message='';
  function draw(){
    const panel=root.querySelector('#'+panelId);
    if(!panel)return;
    const data=transfer?.progress || info;
    const button=panel.querySelector('[data-download]');
    const cancel=panel.querySelector('[data-cancel]');
    const progress=panel.querySelector('progress');
    const completed=data?.completed??data?.downloaded??0, bytes=data?.bytes??data?.downloadedBytes??0;
    button.disabled=!!transfer || !!data?.complete;
    button.textContent=data?.complete?title+'已就绪':completed?'继续下载'+title:initialLabel;
    cancel.hidden=progress.hidden=!transfer;
    if(data){progress.max=Math.max(1,data.total);progress.value=completed;}
    panel.querySelector('[role=status]').textContent=message || (data
      ? '已下载 '+(bytes/1000000).toFixed(1)+' / '+(data.totalBytes/1000000).toFixed(1)+' MB'
      : '正在检查下载状态…');
  }
  async function refresh(){
    draw();
    if(transfer)return;
    try{info=await status();}catch(error){if(!transfer)message=error.message;}
    draw();
  }
  async function start(){
    if(transfer)return;
    message='';
    const current={controller:new AbortController(),progress:info};
    transfer=current;draw();
    try{
      info=await download({signal:current.controller.signal,onProgress:value=>{current.progress=value;draw();}});
      message=title+'已就绪。';
    }catch(error){message=error.name==='AbortError'?'下载已暂停，已完成的文件会保留。':error.message;}
    finally{transfer=null;await refresh();}
  }
  return {refresh,start,cancel:()=>transfer?.controller.abort()};
}
function mainInteractivity(enabled){
  for(const element of [root,tabs,document.querySelector('.masthead')])if(element){element.inert=!enabled;if(enabled)element.removeAttribute('aria-hidden');else element.setAttribute('aria-hidden','true');}
}
function closeAccount({cancelLogin=true}={}){
  if(cancelLogin){viewEpoch++;loginLifecycle.cancel();loginSurface?.remove();loginSurface=null;document.body.classList.remove('login-transitioning');mainInteractivity(true);}
  if(typeof cleanup==='function')cleanup();cleanup=undefined;unsubscribe?.();unsubscribe=null;store?.close();store=null;identity=null;
  pageMotion?.cancel();pageMotion=null;activePage=null;moduleIndicator.select(null,false);
  prefs={...defaults};applyTheme();tabs.hidden=true;statusLabel.textContent='登录后同步';
}
function renderAuth(mode='login',message='') {
  closeAccount();document.body.dataset.page='account';document.title='兔词';root.removeAttribute('aria-busy');
  const registering=mode==='register',verb=registering?'注册':'登录';
  const ICON_USER='<svg class="auth-field-icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="8" r="3.4"/><path d="M5.5 20v-2.5a6.5 6.5 0 0 1 13 0V20"/></svg>';
  const ICON_LOCK='<svg class="auth-field-icon" viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="10" width="14" height="11" rx="1.5"/><path d="M8 10V6a4 4 0 0 1 8 0v4m-4 5v2"/></svg>';
  const ICON_KEY='<svg class="auth-field-icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="8" cy="8" r="3.6"/><path d="M10.6 10.6 19.5 19.5m-2.7-2.7 1.9-1.9m-4.2-.4 1.7-1.7"/></svg>';
  const ICON_EYE='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2.6 12S6.2 6.2 12 6.2 21.4 12 21.4 12 17.8 17.8 12 17.8 2.6 12 2.6 12Z"/><circle cx="12" cy="12" r="2.6"/><path class="eye-slash" d="M4.2 19.8 19.8 4.2"/></svg>';
  const reveal=name=>'<button class="auth-reveal" type="button" data-for="'+name+'" aria-label="显示密码" aria-pressed="false">'+ICON_EYE+'</button>';
  const passwordField=(name,label,placeholder)=>'<label class="auth-field"><span class="auth-label" aria-hidden="true">'+label+'</span><span class="auth-box">'+ICON_LOCK+
    '<input name="'+name+'" aria-label="'+label+'" placeholder="'+placeholder+'" type="password" autocomplete="'+(registering?'new-password':'current-password')+'" maxlength="128" required>'+reveal(name)+'</span></label>';
  const inviteHtml=registering?'<label class="auth-field"><span class="auth-label" aria-hidden="true">邀请码</span><span class="auth-box">'+ICON_KEY+
    '<input name="invite" aria-label="邀请码" placeholder="XXXX-XXXX-XXXX" autocomplete="off" autocapitalize="characters" spellcheck="false" minlength="12" maxlength="32" required></span></label>':'';
  const usernameHtml='<label class="auth-field"><span class="auth-label" aria-hidden="true">账号</span><span class="auth-box">'+ICON_USER+
    '<input name="username" aria-label="账号" placeholder="请输入账号" autocomplete="username" autocapitalize="none" spellcheck="false" minlength="2" maxlength="40" required></span></label>';
  const swapHtml='<p class="auth-swap">'+(registering?'已有账号？':'还没有账号？')+
    '<button class="auth-swap-link" type="button" id="auth-swap">'+(registering?'返回登录':'用邀请码注册')+'</button></p>';
  root.innerHTML='<section class="auth-screen"><form class="auth-form"><div class="auth-brand"><div class="auth-emblem"><span class="auth-emblem-ring" aria-hidden="true"></span><span class="auth-logo" role="img" aria-label="兔词 Logo"></span></div><h1>兔词</h1><p class="auth-tagline">收录 · 阅读 · 记忆</p></div>'+
    '<div class="auth-card">'+inviteHtml+usernameHtml+passwordField('password','密码','请输入密码')+
    (registering?passwordField('confirm','确认密码','请再次输入密码'):'')+
    '<button class="auth-submit" type="submit"><span class="auth-login-word">'+verb+'</span></button><p class="account-error" role="alert"></p></div>'+swapHtml+
    '</form><span class="auth-shutter-line" aria-hidden="true"></span></section>';
  const surface=root.querySelector('.auth-screen'),form=surface.querySelector('form'),error=form.querySelector('[role=alert]');error.textContent=message;
  surface.querySelector('#auth-swap').onclick=()=>renderAuth(registering?'login':'register');
  for(const toggle of form.querySelectorAll('.auth-reveal'))toggle.addEventListener('click',event=>{
    event.preventDefault();event.stopPropagation();
    const field=form.elements[toggle.dataset.for],shown=field.type==='text';
    field.type=shown?'password':'text';
    toggle.setAttribute('aria-pressed',String(!shown));
    toggle.setAttribute('aria-label',shown?'显示密码':'隐藏密码');
    field.focus({preventScroll:true});
    try{const end=field.value.length;field.setSelectionRange(end,end);}catch{}
  });
  form.onsubmit=async event=>{
    event.preventDefault();
    // Check the repeated password before spending the invite.
    if(registering && form.elements.password.value!==form.elements.confirm.value){
      error.textContent='两次输入的密码不一致。';form.elements.confirm.focus();return;
    }
    viewEpoch++;const button=form.querySelector('[type=submit]');button.disabled=true;error.textContent='';let failed=false,mounted=false;
    await loginLifecycle.run({
      authenticate:async signal=>{
        const payload={username:form.elements.username.value.trim(),password:form.elements.password.value,
          deviceName:matchMedia('(display-mode: standalone)').matches?'兔词主屏幕网页':'兔词 Safari 网页',browser:true};
        if(registering)payload.inviteCode=form.elements.invite.value;
        return checkedIdentity(await accountRequest(registering?'register':'login',payload,null,null,{signal}));
      },
      prepare:prepareAccount,
      transition:async(prepared,signal)=>{
        await settleLoginViewport({signal});if(signal.aborted)throw new DOMException(verb+'已取消。','AbortError');
        const style=getComputedStyle(surface);for(const name of ['--paper','--ink','--line','--muted','--accent'])surface.style.setProperty(name,style.getPropertyValue(name));
        form.inert=true;loginSurface=surface;document.body.append(surface);document.body.classList.add('login-transitioning');mounted=true;
        commitAccount(prepared);mainInteractivity(false);
        return await playLoginTransition({surface,word:form.querySelector('.auth-login-word'),line:surface.querySelector('.auth-shutter-line'),signal,reducedMotion:matchMedia('(prefers-reduced-motion: reduce)').matches,offline:!navigator.onLine});
      },
      reveal:prepared=>{
        if(store!==prepared.store)throw new DOMException(verb+'已取消。','AbortError');
        localStorage.removeItem(loggedOutKey);surface.remove();loginSurface=null;document.body.classList.remove('login-transitioning');mainInteractivity(true);root.focus({preventScroll:true});
        prepared.store.sync().catch(()=>{});
      },
      onError:e=>{failed=true;error.textContent=e.message;},
      dispose:prepared=>{
        if(store===prepared.store)closeAccount({cancelLogin:false});else prepared.store.close();
        if(failed && mounted){root.replaceChildren(surface);surface.removeAttribute('style');form.inert=false;document.body.dataset.page='account';document.title='兔词';mainInteractivity(true);}
        else if(mounted)surface.remove();
        if(loginSurface===surface){loginSurface=null;document.body.classList.remove('login-transitioning');}
      }
    });
    button.disabled=false;
  };
}
async function prepareAccount(account,signal,allowGuest=false){
  const check=()=>{if(signal?.aborted)throw new DOMException('登录已取消。','AbortError');};check();
  const key='tuci.web.preferences.v1:'+account.userId;let settings={...defaults},candidate;
  try{Object.assign(settings,JSON.parse(localStorage.getItem(key)||'{}'));}catch{}
  try{
    await loadLegacy();check();candidate=await openStore({identity:account});check();
    const legacyOwner=localStorage.getItem('tuci.web.legacy-owner.v1');
    const claimable=legacy && (legacy.profile.vaultId===account.vaultId || (!legacy.profile.vaultId && (allowGuest || legacyOwner===account.userId) && (!legacyOwner || legacyOwner===account.userId)));
    if(claimable){
      let roaming=null;try{roaming=localStorage.getItem('tuci.web.roaming.v1:'+legacy.profile.scope);}catch{}
      await candidate.importLegacy(legacy,{roaming});check();localStorage.setItem('tuci.web.legacy-owner.v1',account.userId);
      if(!localStorage.getItem(key))try{Object.assign(settings,JSON.parse(localStorage.getItem('tuci.web.preferences.v1')||'{}'));localStorage.setItem(key,JSON.stringify(settings));}catch{}
    }
    check();if(candidate.status().needsBinding)throw Error('登录已过期，请重新登录。本机数据已保留。');
    return {account,store:candidate,prefs:settings,prefsKey:key};
  }catch(error){candidate?.close();throw error;}
}
function commitAccount(prepared){
  closeAccount({cancelLogin:false});identity=prepared.account;store=prepared.store;prefs=prepared.prefs;prefsKey=prepared.prefsKey;
  localStorage.setItem(identityKey,JSON.stringify(identity));accountChannel?.postMessage({userId:identity.userId});
  tabs.hidden=false;let previous='';const owned=store;
  unsubscribe=owned.subscribe(()=>{
    if(store!==owned)return;updateStatus();const theme=owned.stateEntries().find(row=>row.key==='preference/theme')?.value?.name;
    if(localThemes[theme] && prefs.theme!==localThemes[theme]){prefs={...prefs,theme:localThemes[theme]};localStorage.setItem(prefsKey,JSON.stringify(prefs));applyTheme();}
    const revision=JSON.stringify(owned.notes().map(n=>[n.id,n.path,n.markdown]));if(revision!==previous){previous=revision;window.dispatchEvent(new Event('tuci-notes'));}
    if(owned.status().needsBinding){closeAccount();renderAuth('login','登录已过期，请重新登录。本机数据已保留。');}
  });
  applyTheme();updateStatus();root.removeAttribute('aria-busy');render();
}
async function enterAccount(account,allowGuest=false){
  let failure;
  const entered=await loginLifecycle.run({authenticate:async()=>account,prepare:(value,signal)=>prepareAccount(value,signal,allowGuest),
    transition:async prepared=>{commitAccount(prepared);return true;},
    reveal:prepared=>{mainInteractivity(true);prepared.store.sync().catch(()=>{});},
    onError:error=>{failure=error;},dispose:prepared=>{if(store===prepared.store)closeAccount({cancelLogin:false});else prepared.store.close();}});
  if(failure)throw failure;return entered;
}
function checkOtherAccount(value){
  if(loginLifecycle.active || !store || !value || value.userId!==identity.userId){closeAccount();renderAuth('login','账号已在另一个窗口切换，请重新登录。离线改动已保留。');}
}
if(accountChannel)accountChannel.onmessage=event=>checkOtherAccount(event.data.logout?null:event.data);
window.addEventListener('storage',event=>{if(event.key===identityKey){try{checkOtherAccount(JSON.parse(event.newValue||'null'));}catch{checkOtherAccount(null);}}});
window.addEventListener('online',()=>{updateStatus();store?.sync().catch(()=>{});});window.addEventListener('offline',updateStatus);
window.addEventListener('hashchange',()=>{if(loginLifecycle.active){closeAccount();renderAuth();}else if(store)render();else renderAuth();});
document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible')store?.sync().catch(()=>{});});
setInterval(()=>{if(document.visibilityState==='visible')store?.sync().catch(()=>{});},60000);
function showWebUpdate(message) {
  settingsLink.dataset.update=webUpdateReady?'ready':'';
  settingsLink.setAttribute('aria-label',webUpdateReady?'设置，有可用更新':'设置');
  const dot=root.querySelector('#update-tab-dot');
  if(dot) {
    dot.hidden=!webUpdateReady;
    root.querySelector('#settings-tab-app').setAttribute('aria-label',webUpdateReady?'应用，有可用更新':'应用');
  }
  const button=root.querySelector('#check-web-update');
  if(!button)return;
  button.dataset.update=webUpdateReady?'ready':'';
  button.textContent=webUpdateReady?'重新打开以更新':'检查更新';
  const status=root.querySelector('#web-update-status');
  status.textContent=message || (webUpdateReady?'新版本已下载。':'联网后会自动检查新版。');
}
async function checkWebUpdate(event,hasSettingsDraft) {
  const button=event.currentTarget;
  if(webUpdateReady){
    if(hasSettingsDraft())showWebUpdate('设置里还有未保存的内容，请先处理再更新。');
    else location.reload();
    return;
  }
  button.disabled=true;
  showWebUpdate('正在检查更新…');
  try {
    const registration=await webRegistration;
    if(!registration){showWebUpdate('此浏览器暂未启用离线更新。');return;}
    await registration.update();
    showWebUpdate(webUpdateReady?'':registration.installing || registration.waiting?'正在准备新版…':'当前已是最新版本。');
  } catch { showWebUpdate(webUpdateReady?'':'检查更新失败，请检查网络后重试。'); }
  finally {button.disabled=false;}
}
if ('serviceWorker' in navigator) {
  let controlled=navigator.serviceWorker.controller;
  navigator.serviceWorker.addEventListener('controllerchange',()=>{
    const next=navigator.serviceWorker.controller;
    if(controlled && next && next!==controlled){webUpdateReady=true;showWebUpdate();}
    controlled=next;
  });
  webRegistration=navigator.serviceWorker.register('./sw.js',{scope:'./',updateViaCache:'none'})
    .catch(()=>{toast('离线缓存暂未就绪，请联网重新打开。');return null;});
}
async function bootstrapAccount(){
  const epoch=viewEpoch;
  try{
    await loadLegacy();if(epoch!==viewEpoch)return;
    if(localStorage.getItem(loggedOutKey))return;
    try{
      const profile=checkedIdentity(await accountRequest('me'));if(epoch!==viewEpoch)return;await enterAccount(profile);
    }catch(error){
      if(epoch!==viewEpoch)return;
      const cached=JSON.parse(localStorage.getItem(identityKey)||'null');
      if(!navigator.onLine && cached)await enterAccount(checkedIdentity(cached));
    }
  }catch{ /* The interactive login remains available; explicit login reports its own errors. */ }
}
renderAuth();
await bootstrapAccount();
