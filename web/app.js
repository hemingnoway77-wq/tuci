import {createLoginLifecycle,settleLoginViewport,playLoginTransition} from './login-transition.js';
import { dictionaryStatus,downloadDictionary } from './dictionary.js';
import { openStore, readLegacyStore } from './storage.js';
import { accountRequest,checkedIdentity,themeNames,localThemes } from './account.js';
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
let store, cleanup, routeKey, toastTimer;
let dictionaryTransfer=null,dictionaryInfo=null,dictionaryMessage='';
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
  const line=root.querySelector('#sync-description'), button=root.querySelector('#sync-now');
  if (!line || !store) return;
  const s=store.status();
  button.disabled=s.syncing || s.needsBinding || s.needsRecovery;
  line.textContent=s.needsBinding?'请重新登录以继续同步。本机数据已保留。':s.needsRecovery?'云端历史需要重新核对，请先保留本机数据。':s.error || (s.syncing?'正在同步词库和学习记录…':s.pending?'有 '+s.pending+' 项改动待同步。':s.lastSync?'最近同步于 '+new Date(s.lastSync).toLocaleString('zh-CN'):'已连接账号词库。');
  const recover=root.querySelector('#recover');if(recover)recover.hidden=!s.needsRecovery;
}
function renderSettings() {
  root.innerHTML=`<div class="page-head"><h1>设置</h1><p class="hint">把日常留在自己的词库里。</p></div><div class="settings-grid">
    <section class="panel"><h2>我的账号</h2><p id="account-name"></p><p class="hint">词库、漫游记录、阅读位置和主题会在 Windows 与手机间同步。API Key 仅保存在这台设备。</p><div class="actions"><button class="secondary" id="logout">退出账号</button></div></section>
    <section class="panel" id="sync-panel"><h2>词库与学习记录</h2><p class="hint" id="sync-description"></p><div class="actions"><button class="button" id="sync-now">立即同步</button><button class="secondary" id="recover" hidden>重新核对词库</button></div><p class="hint">Safari 与添加到主屏幕的兔词可能使用各自的存储；请在两个入口登录同一账号。离线改动会在下次联网时同步。</p></section>
    <form class="panel" id="ai-settings"><h2>AI 词卡</h2><p class="hint">单词与原句会发送给你填写的 AI 服务商。API Key 只在当前设备使用，不上传到兔词云端。</p>
      <label class="field">API 地址<input type="url" id="ai-address" placeholder="https://…/v1" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <label class="field">模型名<input id="ai-model" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <label class="field">API Key<input type="password" id="ai-key" autocapitalize="none" autocomplete="off" spellcheck="false"></label>
      <label class="check"><input type="checkbox" id="classify">按主要词性分目录</label><div><button class="button">保存 AI 设置</button></div></form>
    <section class="panel" id="offline-dictionary"><h2>离线查词</h2><p class="hint">与 Windows 使用同一份英汉词典，约 40 万词。查词时按字母下载需要的部分；也可一次下载完整词典（约 27 MB），之后离线使用。</p><p id="dictionary-status" class="hint" role="status">正在检查下载状态…</p><progress id="dictionary-progress" value="0" max="26" aria-label="词典下载进度" hidden></progress><div class="actions"><button class="secondary" id="dictionary-download">下载完整离线词典（约 27 MB）</button><button class="secondary" id="dictionary-cancel" hidden>暂停下载</button></div><p class="hint">已下载部分会保留，支持继续下载。未收录的词可联网翻译；发音使用设备系统朗读。词典是公开资源，不包含账号笔记或 API Key。</p></section>
    <section class="panel"><h2>纸页主题</h2><div class="theme-options"></div></section>
    <form class="panel" id="password-settings"><h2>修改密码</h2><label class="field">当前密码<input id="current-password" type="password" autocomplete="current-password" required></label><label class="field">新密码<input id="new-password" type="password" autocomplete="new-password" minlength="8" maxlength="128" required></label><p class="hint">至少 8 个字符，建议使用一段容易记住的长密码。</p><div><button class="secondary">更新密码</button></div></form>
    <form class="panel" id="links"><h2>笔记链接</h2><label class="field">Obsidian 中的词库相对目录<input id="prefix" autocapitalize="none" spellcheck="false"></label><div class="actions"><button class="secondary">保存目录</button><button class="secondary" type="button" id="refresh-links">刷新词条链接</button></div></form></div>`;
  const $=key=>root.querySelector(key);
  const task=async(button,action)=>{if(button.disabled)return;button.disabled=true;try{await action();}catch(e){toast(e.message);}finally{button.disabled=false;refreshSyncPanel();}};
  $('#account-name').textContent=identity.username;
  $('#sync-now').onclick=e=>task(e.currentTarget,async()=>{await store.sync();toast('词库和学习记录已同步');});
  $('#recover').onclick=e=>{if(confirm('保留本机笔记，重新与服务器逐篇核对？'))task(e.currentTarget,()=>store.recover());};
  $('#logout').onclick=e=>task(e.currentTarget,async()=>{
    const owned=store,vault=identity.vaultId;localStorage.setItem(loggedOutKey,'1');localStorage.removeItem(identityKey);accountChannel?.postMessage({logout:true});
    try{await accountRequest('logout',{},null,vault);}finally{if(store===owned){closeAccount();renderAuth('login');}}
  });
  $('#ai-address').value=prefs.aiEndpoint;$('#ai-model').value=prefs.aiModel;$('#ai-key').value=prefs.aiKey;$('#classify').checked=prefs.classify;
  $('#ai-settings').onsubmit=e=>{e.preventDefault();try{
    const address=$('#ai-address').value.trim();if(address){const u=new URL(address);if(u.protocol!=='https:'||u.username||u.password||u.search||u.hash)throw Error('请填写服务商的 HTTPS API 地址。');}
    savePrefs({aiEndpoint:address,aiModel:$('#ai-model').value.trim(),aiKey:$('#ai-key').value.trim(),classify:$('#classify').checked});toast('AI 设置已保存在此设备');
  }catch(error){toast(error.message);}};
  for(const [theme,label] of Object.entries({warm:'暖灰',ink:'墨白',rose:'玫红'})){
    const b=document.createElement('button');b.type='button';b.className='secondary';b.textContent=label;b.setAttribute('aria-pressed',String(theme===prefs.theme));
    b.onclick=()=>{savePrefs({theme});for(const item of $('.theme-options').children)item.setAttribute('aria-pressed',String(item===b));};$('.theme-options').append(b);
  }
  $('#password-settings').onsubmit=e=>{e.preventDefault();task(e.currentTarget.querySelector('button'),async()=>{
    await accountRequest('password',{currentPassword:$('#current-password').value,newPassword:$('#new-password').value},null,identity.vaultId);$('#password-settings').reset();toast('密码已更新');
  });};
  $('#prefix').value=prefs.vaultPrefix;
  $('#links').onsubmit=e=>{e.preventDefault();savePrefs({vaultPrefix:$('#prefix').value.trim()});toast('目录已保存');};
  $('#refresh-links').onclick=e=>task(e.currentTarget,async()=>{const count=await store.refreshLinks();toast(count?'已更新 '+count+' 篇笔记的链接':'词条链接已是最新');});
  $('#dictionary-download').onclick=startDictionaryDownload;
  $('#dictionary-cancel').onclick=()=>dictionaryTransfer?.controller.abort();
  refreshDictionaryStatus();refreshSyncPanel();
}
function showDictionaryStatus(){
  const panel=root.querySelector('#offline-dictionary');if(!panel)return;
  const info=dictionaryTransfer?.progress || dictionaryInfo,button=panel.querySelector('#dictionary-download'),cancel=panel.querySelector('#dictionary-cancel'),progress=panel.querySelector('progress');
  button.disabled=!!dictionaryTransfer || !!info?.complete;cancel.hidden=!dictionaryTransfer;progress.hidden=!dictionaryTransfer;
  if(info){progress.max=info.total;progress.value=info.completed??info.downloaded??0;}
  const completed=info?.completed??info?.downloaded??0,bytes=info?.bytes??info?.downloadedBytes??0;
  button.textContent=info?.complete?'完整离线词典已就绪':completed?'继续下载离线词典':'下载完整离线词典（约 27 MB）';
  panel.querySelector('#dictionary-status').textContent=dictionaryMessage || (info?'已下载 '+completed+' / '+info.total+' 个字母分片 · '+(bytes/1000000).toFixed(1)+' / '+(info.totalBytes/1000000).toFixed(1)+' MB':'正在检查下载状态…');
}
async function refreshDictionaryStatus(){
  showDictionaryStatus();
  try{dictionaryInfo=await dictionaryStatus();}catch(error){if(!dictionaryTransfer)dictionaryMessage=error.message;}
  showDictionaryStatus();
}
async function startDictionaryDownload(){
  if(dictionaryTransfer)return;
  dictionaryMessage='';const transfer={controller:new AbortController(),progress:dictionaryInfo};dictionaryTransfer=transfer;showDictionaryStatus();
  try{
    dictionaryInfo=await downloadDictionary({signal:transfer.controller.signal,onProgress:value=>{transfer.progress=value;showDictionaryStatus();}});
    dictionaryMessage='完整离线词典已就绪。清理网站数据会移除这些下载。';
  }catch(error){dictionaryMessage=error.name==='AbortError'?'下载已暂停，已完成的分片会保留。':error.message;}
  finally{dictionaryTransfer=null;await refreshDictionaryStatus();}
}
function mainInteractivity(enabled){
  for(const element of [root,tabs,document.querySelector('.masthead')])if(element){element.inert=!enabled;if(enabled)element.removeAttribute('aria-hidden');else element.setAttribute('aria-hidden','true');}
}
function closeAccount({cancelLogin=true}={}){
  if(cancelLogin){viewEpoch++;loginLifecycle.cancel();loginSurface?.remove();loginSurface=null;document.body.classList.remove('login-transitioning');mainInteractivity(true);}
  if(typeof cleanup==='function')cleanup();cleanup=undefined;unsubscribe?.();unsubscribe=null;store?.close();store=null;identity=null;
  prefs={...defaults};applyTheme();tabs.hidden=true;statusLabel.textContent='登录后同步';
}
function renderAuth(_mode='login',message='') {
  closeAccount();document.body.dataset.page='account';document.title='兔词';root.removeAttribute('aria-busy');
  root.innerHTML=`<section class="auth-screen"><form class="auth-form"><div class="auth-brand"><div class="auth-emblem"><span class="auth-emblem-ring" aria-hidden="true"></span><span class="auth-logo" role="img" aria-label="兔词 Logo"></span></div><h1>兔词</h1></div><label class="auth-field"><span class="sr-only">账号</span><input name="username" aria-label="账号" placeholder="账号" autocomplete="username" autocapitalize="none" spellcheck="false" minlength="2" maxlength="40" required><svg class="auth-field-icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="8" r="3.4"/><path d="M5.5 20v-2.5a6.5 6.5 0 0 1 13 0V20"/></svg></label><label class="auth-field"><span class="sr-only">密码</span><input name="password" aria-label="密码" placeholder="密码" type="password" autocomplete="current-password" maxlength="128" required><svg class="auth-field-icon" viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="10" width="14" height="11" rx="1.5"/><path d="M8 10V6a4 4 0 0 1 8 0v4m-4 5v2"/></svg></label><button class="auth-submit" type="submit"><span class="auth-login-word">登录</span></button><p class="account-error" role="alert"></p></form><span class="auth-shutter-line" aria-hidden="true"></span></section>`;
  const surface=root.querySelector('.auth-screen'),form=surface.querySelector('form'),error=form.querySelector('[role=alert]');error.textContent=message;
  form.onsubmit=async event=>{
    event.preventDefault();viewEpoch++;const button=form.querySelector('[type=submit]');button.disabled=true;error.textContent='';let failed=false,mounted=false;
    await loginLifecycle.run({
      authenticate:async signal=>checkedIdentity(await accountRequest('login',{username:form.elements.username.value.trim(),password:form.elements.password.value,deviceName:matchMedia('(display-mode: standalone)').matches?'兔词主屏幕网页':'兔词 Safari 网页',browser:true},null,null,{signal})),
      prepare:prepareAccount,
      transition:async(prepared,signal)=>{
        await settleLoginViewport({signal});if(signal.aborted)throw new DOMException('登录已取消。','AbortError');
        const style=getComputedStyle(surface);for(const name of ['--paper','--ink','--line','--muted','--accent'])surface.style.setProperty(name,style.getPropertyValue(name));
        form.inert=true;loginSurface=surface;document.body.append(surface);document.body.classList.add('login-transitioning');mounted=true;
        commitAccount(prepared);mainInteractivity(false);
        return await playLoginTransition({surface,word:form.querySelector('.auth-login-word'),line:surface.querySelector('.auth-shutter-line'),signal,reducedMotion:matchMedia('(prefers-reduced-motion: reduce)').matches,offline:!navigator.onLine});
      },
      reveal:prepared=>{
        if(store!==prepared.store)throw new DOMException('登录已取消。','AbortError');
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
if ('serviceWorker' in navigator) navigator.serviceWorker.register('./sw.js',{scope:'./',updateViaCache:'none'}).catch(() => toast('离线缓存暂未就绪，请联网重新打开。'));
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
