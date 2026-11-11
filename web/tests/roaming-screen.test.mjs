import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {webcrypto} from 'node:crypto';
import {readFile} from 'node:fs/promises';
import * as practiceFunctions from '../practice.js';
import {createTabIndicator} from '../tab-indicator.js';
const source=await readFile(new URL('../library.js',import.meta.url),'utf8');

function screen({save,practice=false,meaning=false}={}) {
  let document,scope='roam-screen-a',subscriber;const writes=[],observers=[];
  let ledger, bank, fetches=0, elapsed=0;
  const intervals=new Set();
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
    get ownerDocument() {return document;}
    append(...nodes) {for(const node of nodes) {if(typeof node!=='string'){node.remove();node.parentNode=this;}this.children.push(node);}}
    remove() {if(!this.parentNode)return;const list=this.parentNode.children;list.splice(list.indexOf(this),1);this.parentNode=null;}
    replaceChildren(...nodes) {for(const child of this.children)if(typeof child!=='string')child.parentNode=null;this.children=[];this.append(...nodes);}
    setAttribute(name,value) {this.attrs[name]=String(value);}
    getAttribute(name) {return this.attrs[name]??null;}
    matches(selector) {if(selector.includes(','))return selector.split(',').some(part=>this.matches(part.trim()));if(selector===' .nb-review-controls button'.trim())return this.tagName==='BUTTON'&&this.parentNode?.className==='nb-review-controls';if(selector.startsWith('.'))return this.className.split(' ').includes(selector.slice(1));if(selector.startsWith('#'))return this.id===selector.slice(1);if(selector==='dialog[open]')return this.tagName==='DIALOG'&&this.open;return this.tagName.toLowerCase()===selector;}
    querySelectorAll(selector) {return this.children.flatMap(child=>typeof child==='string'?[]:[...(child.matches(selector)?[child]:[]),...child.querySelectorAll(selector)]);}
    querySelector(selector) {return this.querySelectorAll(selector)[0]??null;}
    getBoundingClientRect() {return this.id==='tabs'?{top:748,height:96}:this.className==='masthead'?{height:111}:this.className==='nb-meaning-mask'?{height:180}:{top:100,height:44,width:100,left:0,right:390,bottom:144};}
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
  const ctx={root,scope:()=>scope,notes:()=>notes,stateEntries:()=>rows,subscribeState:fn=>{subscriber=fn;return ()=>{subscriber=null;};},setStates:async value=>{
    writes.push(value);await save?.(value);
    if(practice) for(const row of value){const at=rows.findIndex(prior=>prior.key===row.key);if(at<0)rows.push(row);else rows[at]=row;}
  },setState:async(...value)=>writes.push(value),navigate(){},toast(){}};
  if (practice) {
    const store={readBank:async()=>bank,saveBank:async(_key,value)=>{bank=value;},updateLedger:async update=>{const result=update(ledger);ledger=result.ledger;return result;},
      fetchBanks:async()=>{
        fetches++;
        const [sense]=await practiceFunctions.buildPracticeSenses(notes[0]);
        if(meaning && !ledger)ledger={version:1,stats:{[JSON.stringify(['a',sense.id,'spelling'])]:{count:1,correct:1}},receipts:[],pending:null};
        return {banks:[{noteId:'a',word,contexts:[{senseId:sense.id,english:'She learned '+word+' after the class.',chinese:'她在下课后学了这个单词。'}]}]};
      }};
    ctx.practice=()=>store;
  }
  document.defaultView=window;
  const realm={window,document,Date:Clock,crypto:webcrypto,getComputedStyle:()=>({fontSize:'17.04px'}),requestAnimationFrame:()=>1,cancelAnimationFrame(){},setTimeout,clearTimeout,queueMicrotask,console,
    ...practiceFunctions,createTabIndicator,performance:{now:()=>elapsed},setInterval(fn){intervals.add(fn);return fn;},clearInterval(fn){intervals.delete(fn);}};
  vm.runInNewContext(source.replace(/^import [^\n]+\n/gm,'').replaceAll('export ','')+'\nglobalThis.render=renderRoam;',realm);
  const dispose=realm.render(ctx);
  return {root,window,document,writes,rows,observers,dispose,refresh:()=>subscriber?.(),switchAccount:()=>scope='roam-screen-b',
    fetches:()=>fetches,ledger:()=>ledger,advanceClock(ms){elapsed+=ms;for(const tick of [...intervals])tick();},word};
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

test('answer mode keeps the same exposure, hides the answer and pauses the timer and draft across modes',async()=>{
  const page=screen({practice:true});
  const mode=index=>page.root.querySelectorAll('.nb-practice-mode')[index];
  assert.equal(page.fetches(),0);
  mode(1).click();
  for(let i=0;i<40&&!page.root.querySelector('.nb-practice-input');i++) await new Promise(resolve=>setTimeout(resolve,2));
  assert.equal(page.fetches(),1);
  assert.doesNotMatch(page.root.textContent,new RegExp(page.word));
  let field=page.root.querySelector('.nb-practice-input');assert.ok(field);field.value='my draft';field.fire('input');
  page.advanceClock(4_000);
  assert.equal(page.root.querySelector('.nb-practice-timer').textContent,'26 秒');
  mode(0).click();assert.ok(page.root.querySelector('.nb-word'));
  page.advanceClock(60_000);
  mode(1).click();field=page.root.querySelector('.nb-practice-input');assert.equal(field.value,'my draft');
  assert.equal(page.root.querySelector('.nb-practice-timer').textContent,'26 秒');
  page.document.visibilityState='hidden';page.document.fire('visibilitychange');page.advanceClock(60_000);
  page.document.visibilityState='visible';page.document.fire('visibilitychange');page.advanceClock(26_000);
  assert.match(page.root.querySelector('.nb-practice-result').textContent,/时间到/);
  assert.match(page.root.textContent,new RegExp(page.word));
  assert.equal(page.writes.length,0);assert.equal(page.ledger().receipts.length,0);
  page.dispose();
});

test('a Chinese answer stays self-graded and a failed sync queue write retries the same completed exposure',async()=>{
  let saves=0;
  const page=screen({practice:true,meaning:true,save:()=>{if(++saves===1)throw Error('offline write failed');}});
  const settle=()=>new Promise(resolve=>setTimeout(resolve,2));
  page.root.querySelectorAll('.nb-practice-mode')[1].click();
  for(let i=0;i<40&&!page.root.querySelector('.nb-practice-input');i++)await settle();
  assert.equal(page.root.querySelector('.nb-practice-word').textContent,page.word);
  assert.doesNotMatch(page.root.textContent,/她在下课后/);
  const field=page.root.querySelector('.nb-practice-input');assert.equal(field.tagName,'TEXTAREA');
  field.value='写下自己理解的意思';field.fire('input');page.root.querySelector('.nb-practice-form').fire('submit');
  assert.match(page.root.querySelector('.nb-practice-result').textContent,/对照参考释义.*她在下课后/);
  assert.equal(page.writes.length,0);
  page.root.querySelector('.nb-review-controls').children[1].click();await settle();
  assert.equal(page.ledger().receipts[0].outcome,'selfConfirmed');
  assert.equal(page.rows.find(row=>row.key==='roaming/current').value.exposureId,'today');
  page.root.querySelector('.nb-roam-failure').querySelector('button').click();await settle();
  assert.equal(page.ledger().receipts.length,1);
  assert.deepEqual(page.writes[0],page.writes[1]);
  assert.equal(page.writes[1][0].value.remembered,true);
  assert.equal(page.rows.find(row=>row.key==='roaming/current').value.noteId,'b');
  page.dispose();
});
