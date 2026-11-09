import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {webcrypto} from 'node:crypto';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../library.js',import.meta.url),'utf8');

function screen({save}={}) {
  let document,scope='roam-screen-a',subscriber;const writes=[],observers=[];
  class Events {
    listeners=new Map();
    addEventListener(name,fn,options={}) { if(!fn)return; const set=this.listeners.get(name)||new Map();set.set(fn,options);this.listeners.set(name,set); }
    removeEventListener(name,fn) { this.listeners.get(name)?.delete(fn); }
    fire(name,event={}) { const data={target:this,preventDefault(){},...event};for(const [fn,options] of [...(this.listeners.get(name)||[])]) { if(options.once)this.removeEventListener(name,fn);fn(data); } }
  }
  class Element extends Events {
    constructor(tag='div') {super();this.tagName=tag.toUpperCase();this.children=[];this.parentNode=null;this.dataset={};this.attrs={};this.className='';this._text='';this.style={setProperty(k,v){this[k]=v;},removeProperty(k){delete this[k];}};this.classList={add:name=>this.className+=' '+name,remove:name=>this.className=this.className.split(' ').filter(x=>x!==name).join(' ')};}
    get textContent() {return this._text+this.children.map(x=>typeof x==='string'?x:x.textContent).join('');}
    set textContent(value) {this._text=String(value);this.replaceChildren();}
    get isConnected() {let p=this;while(p){if(p===document.body)return true;p=p.parentNode;}return false;}
    append(...nodes) {for(const node of nodes) {if(typeof node!=='string'){node.remove();node.parentNode=this;}this.children.push(node);}}
    remove() {if(!this.parentNode)return;const list=this.parentNode.children;list.splice(list.indexOf(this),1);this.parentNode=null;}
    replaceChildren(...nodes) {for(const child of this.children)if(typeof child!=='string')child.parentNode=null;this.children=[];this.append(...nodes);}
    setAttribute(name,value) {this.attrs[name]=String(value);}
    getAttribute(name) {return this.attrs[name]??null;}
    matches(selector) {if(selector.includes(','))return selector.split(',').some(part=>this.matches(part.trim()));if(selector===' .nb-review-controls button'.trim())return this.tagName==='BUTTON'&&this.parentNode?.className==='nb-review-controls';if(selector.startsWith('.'))return this.className.split(' ').includes(selector.slice(1));if(selector.startsWith('#'))return this.id===selector.slice(1);if(selector==='dialog[open]')return this.tagName==='DIALOG'&&this.open;return this.tagName.toLowerCase()===selector;}
    querySelectorAll(selector) {return this.children.flatMap(child=>typeof child==='string'?[]:[...(child.matches(selector)?[child]:[]),...child.querySelectorAll(selector)]);}
    querySelector(selector) {return this.querySelectorAll(selector)[0]??null;}
    getBoundingClientRect() {return this.id==='tabs'?{top:748,height:96}:this.className==='masthead'?{height:111}:this.className==='nb-meaning-mask'?{height:180}:{top:100,height:44,left:0,right:390,bottom:144};}
    showModal() {this.open=true;}
    close() {this.open=false;this.fire('close');}
    focus() {document.activeElement=this;}
    click() {this.fire('click');}
  }
  const body=new Element('body'),root=new Element('main'),masthead=new Element('header'),tabs=new Element('nav');masthead.className='masthead';tabs.id='tabs';body.append(masthead,root,tabs);
  document=Object.assign(new Events(),{body,activeElement:body,visibilityState:'visible',documentElement:{scrollHeight:844},createElement:tag=>new Element(tag),createElementNS:(ns,tag)=>Object.assign(new Element(tag),{namespaceURI:ns}),querySelector:selector=>body.querySelector(selector)});
  const window=Object.assign(new Events(),{innerHeight:844,scrollY:0,scrollTo(){},visualViewport:Object.assign(new Events(),{height:844}),ResizeObserver:class{constructor(callback){this.callback=callback;this.connected=true;observers.push(this);}observe(){}disconnect(){this.connected=false;}}});
  const now=new Date(2026,10,8,12).getTime();
  class Clock extends Date {constructor(...args){super(...(args.length?args:[now]));}static now(){return now;}}
  const rows=[{key:'event/today/shown',value:{noteId:'a',day:'2026-11-08',at:new Date(now).toISOString()}},{key:'event/last-week/shown',value:{noteId:'b',day:'2026-11-01',at:'2026-11-01T12:00:00Z'}},{key:'roaming/current',value:{noteId:'a',exposureId:'today',at:new Date(now).toISOString(),weightLevel:.75}}];
  const word='antidisestablishmentarianism',notes=[{id:'a',path:'动词/'+word+'.md',markdown:'# '+word+'\n\n**释义**：\n> v. '+('这是保持原有位置的长释义。'.repeat(50))},{id:'b',path:'名词/another.md',markdown:'# another'}];
  const ctx={root,scope:()=>scope,notes:()=>notes,stateEntries:()=>rows,subscribeState:fn=>{subscriber=fn;return ()=>{subscriber=null;};},setStates:async value=>{writes.push(value);await save?.(value);},setState:async(...value)=>writes.push(value),navigate(){},toast(){}};
  const realm={window,document,Date:Clock,crypto:webcrypto,getComputedStyle:()=>({fontSize:'17.04px'}),requestAnimationFrame:()=>1,cancelAnimationFrame(){},setTimeout,clearTimeout,queueMicrotask,console};
  vm.runInNewContext(source.replaceAll('export ','')+'\nglobalThis.render=renderRoam;',realm);
  const dispose=realm.render(ctx);
  return {root,window,document,writes,rows,observers,dispose,refresh:()=>subscriber?.(),switchAccount:()=>scope='roam-screen-b'};
}

test('the single-screen roam places both real coverage periods beside its title and only a tiny weight on the card',()=>{
  const page=screen(),head=page.root.querySelector('.nb-roam-heading'),card=page.root.querySelector('.nb-review');
  assert.equal(page.root.dataset.roamMode,'cards');
  assert.equal(page.root.style['--roam-header-height'],'111px');assert.equal(page.root.style['--roam-tabs-height'],'96px');
  assert.equal(head.querySelector('h1').textContent,'漫游复习');
  const periods=head.querySelectorAll('.nb-mini-coverage');assert.equal(periods.length,2);
  assert.equal(periods[0].textContent,'本周1/2');assert.equal(periods[1].textContent,'本月2/2');
  assert.equal(periods[0].querySelector('progress').value,1);assert.equal(periods[1].querySelector('progress').value,2);
  assert.equal(card.querySelector('.nb-roam-weight').value,.75);
  assert.equal(card.querySelector('.nb-roam-weight').getAttribute('aria-label'),'本次相对抽取倾向 75%');
  assert.equal(page.root.querySelector('.nb-statistics'),null);assert.equal(page.root.querySelector('.nb-heat-grid'),null);
  assert.equal(page.writes.length,0,'opening and displaying both periods does not add an exposure');
  assert.ok(card.querySelector('.nb-word').className.includes('is-long'));assert.ok(card.querySelector('.nb-review-controls'));
  page.dispose();
});

test('daily activity opens and closes a scrollable dialog without changing exposure; account cleanup closes it',()=>{
  const page=screen();page.root.querySelector('.nb-activity-entry').click();
  const popup=page.root.querySelector('dialog[open]');assert.ok(popup);assert.equal(popup.getAttribute('aria-label'),'每日漫游');
  assert.equal(popup.querySelector('.nb-heat-grid').children.length,84);
  const today=popup.querySelector('.nb-heat-grid').children.at(-1);today.click();
  assert.match(popup.querySelector('.nb-day-detail').textContent,/2026-11-08 · 展示 1 次 · 1 个不同单词/);
  popup.querySelector('.nb-text-btn').click();assert.equal(page.root.querySelector('dialog[open]'),null);
  page.root.querySelector('.nb-activity-entry').click();const opened=page.root.querySelector('dialog[open]');
  page.switchAccount();page.dispose();assert.equal(opened.open,false);assert.equal(opened.isConnected,false);
  assert.equal(page.root.dataset.roamMode,undefined);assert.equal(page.root.style['--roam-header-height'],undefined);
  assert.equal(page.writes.length,0);assert.ok(page.observers.every(observer=>!observer.connected));
  assert.equal(page.window.listeners.get('resize').size,0);assert.equal(page.window.visualViewport.listeners.get('resize').size,0);
});

test('opening a full note releases the one-screen constraint and returning restores it without extra exposure',()=>{
  const page=screen();page.root.querySelector('.nb-read-note').click();
  assert.equal(page.root.dataset.roamMode,'note');assert.ok(page.root.querySelector('.nb-markdown'));
  assert.equal(page.root.querySelector('.nb-roam-layout'),null);
  page.root.querySelector('.nb-icon-btn').click();assert.equal(page.root.dataset.roamMode,'cards');
  assert.ok(page.root.querySelector('.nb-review'));assert.equal(page.writes.length,0);page.dispose();
});

test('a queued state refresh waits for the activity dialog to close instead of dismissing it',async()=>{
  const page=screen(),oldCard=page.root.querySelector('.nb-review');
  page.rows.push({key:'event/new/shown',value:{noteId:'b',day:'2026-11-08',at:'2026-11-08T12:00:00Z'}});page.refresh();
  page.root.querySelector('.nb-activity-entry').click();const popup=page.root.querySelector('dialog[open]');
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(popup.open,true);assert.equal(page.root.querySelector('.nb-review'),oldCard);assert.equal(page.writes.length,0);
  popup.querySelector('.nb-text-btn').click();await new Promise(resolve=>setImmediate(resolve));
  assert.equal(page.root.querySelector('dialog[open]'),null);assert.notEqual(page.root.querySelector('.nb-review'),oldCard);
  assert.equal(page.root.querySelector('.nb-mini-coverage').textContent,'本周2/2');page.dispose();
});

test('a pending review write disables the activity action and cannot open a dialog that completion would replace',async()=>{
  let release;const waiting=new Promise(resolve=>release=resolve),page=screen({save:()=>waiting});
  page.root.querySelector('.nb-review-controls').children[0].click();
  const activity=page.root.querySelector('.nb-activity-entry');assert.equal(activity.disabled,true);
  activity.click();assert.equal(page.root.querySelector('dialog[open]'),null);assert.equal(page.writes.length,1);
  page.dispose();release();await new Promise(resolve=>setImmediate(resolve));
});
