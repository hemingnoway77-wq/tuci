import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {createLoginLifecycle,settleLoginViewport,playLoginTransition} from '../login-transition.js';
const original=await readFile(new URL('../app.js',import.meta.url),'utf8');
const deferred=()=>{let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};};
const until=async predicate=>{for(let i=0;i<100;i++){if(predicate())return;await new Promise(resolve=>setImmediate(resolve));}assert.fail('app did not reach the expected phase');};
function app({authentication,opening,meResponse,reducedMotion=false,savedPreferences}={}){
  let document,clock=0,openCalls=0;const animations=[],stores=[];
  class Element{
    constructor(tag='div',className=''){this.tagName=tag.toUpperCase();this.className=className;this.children=[];this.parentNode=null;this.dataset={};this.attributes={};this.style={setProperty(name,value){this[name]=value;}};this.textContent='';this.value='';this.hidden=false;this.inert=false;this.disabled=false;this.listeners={};const classes=new Set();this.classList={add:name=>classes.add(name),remove:name=>classes.delete(name),contains:name=>classes.has(name)};}
    get isConnected(){let node=this;while(node){if(node===document.body)return true;node=node.parentNode;}return false;}
    append(...nodes){for(const node of nodes){node.remove();node.parentNode=this;this.children.push(node);}}
    remove(){if(this.parentNode){const siblings=this.parentNode.children,index=siblings.indexOf(this);if(index>=0)siblings.splice(index,1);this.parentNode=null;}}
    replaceChildren(...nodes){for(const child of [...this.children])child.remove();this.append(...nodes);}
    setAttribute(name,value){this.attributes[name]=String(value);}removeAttribute(name){delete this.attributes[name];}
    addEventListener(type,fn){this.listeners[type]=fn;}removeEventListener(type){delete this.listeners[type];}
    focus(){document.activeElement=this;}blur(){if(document.activeElement===this)document.activeElement=document.body;}
    getBoundingClientRect(){return this.className==='auth-login-word'?{top:560,width:55,height:32}:{top:0,width:390,height:844};}
    querySelectorAll(selector){
      const matches=node=>selector[0]==='.'?node.className.split(' ').includes(selector.slice(1)):selector[0]==='#'?node.id===selector.slice(1):selector==='[role=alert]'?node.attributes.role==='alert':selector==='[type=submit]'?node.type==='submit':node.tagName.toLowerCase()===selector;
      const result=[];for(const child of this.children){if(matches(child))result.push(child);result.push(...child.querySelectorAll(selector));}return result;
    }
    querySelector(selector){return this.querySelectorAll(selector)[0]||null;}
    set innerHTML(markup){
      this.markup=markup;this.replaceChildren();if(!markup.includes('class="auth-screen"'))return;
      const surface=new Element('section','auth-screen'),form=new Element('form','auth-form'),title=new Element('h1'),username=new Element('input'),password=new Element('input'),submit=new Element('button','auth-submit'),word=new Element('span','auth-login-word'),error=new Element('p','account-error'),line=new Element('span','auth-shutter-line');
      username.value='alice';password.value='fixture-password';submit.type='submit';word.textContent='登录';title.textContent='兔词';error.setAttribute('role','alert');form.elements={username,password};submit.append(word);form.append(title,username,password,submit,error);
      if(markup.includes('class="auth-reveal"')){const reveal=new Element('button','auth-reveal');reveal.dataset.for='password';form.append(reveal);}
      if(markup.includes('id="auth-swap"')){const swap=new Element('button','auth-swap-link');swap.id='auth-swap';form.append(swap);}
      surface.append(form,line);this.append(surface);
    }
    animate(frames,options){const pending=deferred(),record={element:this,frames,options,pending,canceled:false};animations.push(record);return {finished:pending.promise,cancel(){record.canceled=true;pending.reject(new DOMException('canceled','AbortError'));}};}
  }
  const body=new Element('body'),root=new Element('main'),tabs=new Element('nav'),header=new Element('header','masthead'),status=new Element('span'),toast=new Element('div'),settings=new Element('a'),meta=new Element('meta'),html=new Element('html');
  root.id='content';tabs.id='tabs';status.id='sync-status';toast.id='toast';settings.id='settings-link';header.append(status,settings);body.append(header,root,tabs,toast);
  const window=Object.assign(new EventTarget(),{innerWidth:390,innerHeight:844,visualViewport:{width:390,height:844,offsetTop:0},scrollTo(){}});
  document=Object.assign(new EventTarget(),{body,documentElement:html,activeElement:body,visibilityState:'visible',title:'',createElement:tag=>new Element(tag),querySelector:selector=>selector.startsWith('meta[')?meta:selector==='.masthead'?header:body.querySelector(selector)});
  const storage=new Map(savedPreferences?[['tuci.web.preferences.v1:user-alice',JSON.stringify(savedPreferences)]]:[]),localStorage={getItem:key=>storage.get(key)||null,setItem:(key,value)=>storage.set(key,String(value)),removeItem:key=>storage.delete(key)};
  const profile={userId:'user-alice',vaultId:'vault-alice',username:'alice',deviceId:'device-alice'};
  const makeStore=()=>{const value={closed:false,notes:()=>[],stateEntries:()=>[],status:()=>({bound:true,pending:0,syncing:false,needsBinding:false}),subscribe(){return ()=>{};},sync:async()=>{},close(){this.closed=true;},importLegacy:async()=>false};stores.push(value);return value;};
  const context={document,window,localStorage,sessionStorage:localStorage,location:{hash:'#capture'},navigator:{onLine:true},getComputedStyle:()=>({getPropertyValue:name=>({'--paper':'#f8f6ef','--ink':'#26251f','--line':'#d9d6c9','--muted':'#68675b','--accent':'#6f5943'}[name]||'')}),matchMedia:query=>({matches:query.includes('reduced-motion') && reducedMotion}),performance:{now:()=>clock},requestAnimationFrame:callback=>queueMicrotask(()=>{clock+=16;callback();}),setInterval:()=>1,clearInterval(){},setTimeout,clearTimeout,Event,DOMException,AbortController,Promise,console,createLoginLifecycle,settleLoginViewport:options=>settleLoginViewport({...options,view:window,document,now:()=>clock,frame:callback=>queueMicrotask(()=>{clock+=16;callback();})}),playLoginTransition,
    openStore:async()=>{openCalls++;return opening?await opening.promise:makeStore();},readLegacyStore:async()=>null,checkedIdentity:value=>value,accountRequest:async route=>route==='me' && meResponse?await meResponse.promise:authentication?await authentication.promise:profile,themeNames:{warm:'warmNeutral'},localThemes:{warmNeutral:'warm'},renderCapture:ctx=>ctx.root.append(new Element('section','main-ready')),renderLibrary(){},renderReading(){},renderRoam(){},dictionaryStatus:async()=>({}),downloadDictionary:async()=>({})};
  context.createTabIndicator=()=>({select(){},dispose(){}});
  const source=original.replace(/^import .*;\r?$/gm,'').replace('await bootstrapAccount();','globalThis.appTest={renderAuth,closeAccount,loginLifecycle,getStore:()=>store,getSurface:()=>loginSurface,getIdentity:()=>identity,bootstrapAccount};');
  vm.runInNewContext(source,context,{filename:'web/app.js'});context.appTest.renderAuth();
  return {context,document,root,header,tabs,animations,stores,profile,makeStore,getOpenCalls:()=>openCalls,surface:()=>root.querySelector('.auth-screen'),submit:()=>root.querySelector('form').onsubmit({preventDefault(){}})};
}
test('actual app keeps the complete login surface connected and main inert through every animation phase',async()=>{
  const page=app(),surface=page.surface(),submitted=page.submit();
  await until(()=>page.animations.length===1);
  assert.equal(surface.parentNode,page.document.body);assert.equal(surface.isConnected,true);assert.ok(page.root.querySelector('.main-ready'));assert.equal(page.root.inert,true);assert.equal(page.header.inert,true);assert.equal(page.tabs.inert,true);
  page.animations[0].pending.resolve();await until(()=>page.animations.length===3);assert.equal(surface.isConnected,true);
  page.animations[1].pending.resolve();page.animations[2].pending.resolve();await until(()=>page.animations.length===4);assert.equal(page.root.inert,true);
  page.animations[3].pending.resolve();await until(()=>page.animations.length===5);assert.equal(surface.isConnected,true);assert.equal(page.root.inert,true);
  page.animations[4].pending.resolve();await submitted;
  assert.equal(surface.isConnected,false);assert.equal(page.root.inert,false);assert.equal(page.header.inert,false);assert.equal(page.tabs.inert,false);assert.ok(page.root.querySelector('.main-ready'));assert.equal(page.context.appTest.getStore(),page.stores[0]);assert.equal(page.stores[0].closed,false);
});
test('actual app preserves failed credentials and error text without mounting or animating a main page',async()=>{
  const auth=deferred(),page=app({authentication:auth}),surface=page.surface(),form=surface.querySelector('form'),submitted=page.submit();auth.reject(Error('用户名或密码不正确。'));await submitted;
  assert.equal(surface.isConnected,true);assert.equal(form.elements.username.value,'alice');assert.equal(form.elements.password.value,'fixture-password');assert.equal(form.querySelector('[role=alert]').textContent,'用户名或密码不正确。');assert.equal(page.animations.length,0);assert.equal(page.root.querySelector('.main-ready'),null);assert.equal(page.stores.length,0);
});
test('actual account invalidation removes its moving curtain and cannot reveal a stale prepared main',async()=>{
  const page=app(),oldSurface=page.surface(),submitted=page.submit();await until(()=>page.animations.length===1);
  page.context.appTest.closeAccount();page.context.appTest.renderAuth();const current=page.surface();await submitted;
  assert.notEqual(current,oldSurface);assert.equal(current.isConnected,true);assert.equal(oldSurface.isConnected,false);assert.equal(page.context.appTest.getStore(),null);assert.equal(page.root.querySelector('.main-ready'),null);assert.equal(page.stores[0].closed,true);assert.equal(page.root.inert,false);
});
test('actual app cancels a late private-store open without replacing a newer login form',async()=>{
  const opening=deferred(),page=app({opening}),submitted=page.submit();await until(()=>page.getOpenCalls()===1);
  page.context.appTest.renderAuth();const current=page.surface(),candidate=page.makeStore();opening.resolve(candidate);await submitted;
  assert.equal(page.surface(),current);assert.equal(current.isConnected,true);assert.equal(candidate.closed,true);assert.equal(page.animations.length,0);assert.equal(page.context.appTest.getStore(),null);
});
test('actual reduced-motion login prepares the main first, then reveals it with no animation handles',async()=>{
  const page=app({reducedMotion:true}),surface=page.surface();await page.submit();assert.equal(page.animations.length,0);assert.equal(surface.isConnected,false);assert.ok(page.root.querySelector('.main-ready'));assert.equal(page.root.inert,false);
});

test('fresh login and an account without a saved theme use ink by default',async()=>{
  const page=app({reducedMotion:true});
  assert.equal(page.document.documentElement.dataset.theme,'ink');
  await page.submit();assert.equal(page.document.documentElement.dataset.theme,'ink');
  page.context.appTest.renderAuth();assert.equal(page.document.documentElement.dataset.theme,'ink');
});

test('login starts in ink while an existing account retains its selected theme after authentication',async()=>{
  const page=app({reducedMotion:true,savedPreferences:{theme:'rose'}});
  assert.equal(page.document.documentElement.dataset.theme,'ink');
  await page.submit();assert.equal(page.document.documentElement.dataset.theme,'rose');
  page.context.appTest.renderAuth();assert.equal(page.document.documentElement.dataset.theme,'ink');
});

test('an automatic session check arriving late cannot replace an explicit login attempt',async()=>{
  const auth=deferred(),me=deferred(),page=app({authentication:auth,meResponse:me,reducedMotion:true}),startup=page.context.appTest.bootstrapAccount();
  await new Promise(resolve=>setImmediate(resolve));const submitted=page.submit();me.resolve({...page.profile,userId:'stale-user',vaultId:'stale-vault'});await startup;
  auth.resolve(page.profile);await submitted;assert.equal(page.context.appTest.getIdentity().userId,page.profile.userId);assert.equal(page.stores.length,1);assert.ok(page.root.querySelector('.main-ready'));
});
