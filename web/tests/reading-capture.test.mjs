import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {webcrypto} from 'node:crypto';
import * as data from '../learning-data.js';
import {describeNote,resolveWiki} from '../library.js';
const source=await readFile(new URL('../learning.js',import.meta.url),'utf8');
const progressSource=await readFile(new URL('../reading-state.js',import.meta.url),'utf8');
const fixture=JSON.parse(await readFile(new URL('./fixtures/legacy-capture.json',import.meta.url),'utf8'));
const deferred=()=>{let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};};
const until=async predicate=>{for(let i=0;i<100;i++){if(predicate())return;await new Promise(resolve=>setImmediate(resolve));}assert.fail('reading capture did not reach expected state');};
function page({ai=false,reduced=true,fetcher,save,settling,uncachedBody=false,width=390}={}){
  let document,scope='account-a',onCollect,progressPaused;
  const writes=[],navigations=[],notices=[],animations=[],scrolls=[],cache=new Map(),notes=[],progressMounts=[];
  class Events{
    constructor(){this.listeners={};}
    addEventListener(type,fn){(this.listeners[type]??=new Set()).add(fn);}
    removeEventListener(type,fn){this.listeners[type]?.delete(fn);}
    fire(type,event={}){for(const fn of [...(this.listeners[type]||[])])fn({...event,type,preventDefault(){this.defaultPrevented=true;}});}
  }
  class Element extends Events{
    constructor(tag='div',className=''){
      super();this.tagName=tag.toUpperCase();this.className=className;this.children=[];this.parentNode=null;this.attributes={};this.style={position:'',top:'',left:'',right:'',width:'',overflow:'',scrollBehavior:'',setProperty(k,v){this[k]=v;}};this.value='';this.textContent='';this.tabIndex=/^(BUTTON|INPUT|SELECT|TEXTAREA|SUMMARY)$/.test(this.tagName)?0:-1;this.hidden=false;this.disabled=false;this.inert=false;
    }
    append(...nodes){for(const node of nodes){node.remove();node.parentNode=this;this.children.push(node);}}
    remove(){if(!this.parentNode)return;const list=this.parentNode.children;list.splice(list.indexOf(this),1);this.parentNode=null;}
    replaceChildren(...nodes){for(const child of [...this.children])child.remove();this.append(...nodes);}
    get isConnected(){let node=this;while(node){if(node===document.body)return true;node=node.parentNode;}return false;}
    setAttribute(k,v){this.attributes[k]=String(v);}
    getAttribute(k){return this.attributes[k]??null;}
    contains(node){return node===this || this.children.some(child=>child.contains(node));}
    querySelectorAll(selector){
      const matches=node=>selector.split(',').some(part=>part[0]==='.'?node.className.split(' ').includes(part.slice(1)):part==='[tabindex]'?node.attributes.tabindex!==undefined:node.tagName.toLowerCase()===part);
      return this.children.flatMap(child=>[...(matches(child)?[child]:[]),...child.querySelectorAll(selector)]);
    }
    querySelector(selector){return this.querySelectorAll(selector)[0]??null;}
    focus(){document.activeElement=this;}
    blur(){if(document.activeElement===this)document.activeElement=document.body;}
    getClientRects(){return this.hidden?[]:[{}];}
    animate(frames,options){const pending=deferred(),record={element:this,frames,options,pending,canceled:false};animations.push(record);return {finished:pending.promise,cancel(){record.canceled=true;pending.reject(new DOMException('canceled','AbortError'));}};}
  }
  const body=new Element('body'),root=new Element('main'),tabs=new Element('nav'),masthead=new Element('header');body.append(masthead,root,tabs);
  document=Object.assign(new Events(),{body,documentElement:new Element('html'),activeElement:body,visibilityState:'visible',createElement:tag=>new Element(tag),createElementNS:(namespace,tag)=>Object.assign(new Element(tag),{namespaceURI:namespace})});
  const window=Object.assign(new Events(),{scrollX:0,scrollY:640,innerWidth:width,innerHeight:844,visualViewport:Object.assign(new Events(),{offsetTop:0,height:844}),matchMedia:()=>({matches:reduced}),scrollTo(x,y){this.scrollX=x;this.scrollY=y;scrolls.push([x,y]);}});
  const storage={getItem:key=>cache.get(key)??null,setItem:(key,value)=>cache.set(key,String(value))};
  const day=new Date(),key=[day.getFullYear(),String(day.getMonth()+1).padStart(2,'0'),String(day.getDate()).padStart(2,'0')].join('-'),story={id:'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',title:'A little story',summary:'A story.',source:'Test',category:'general',url:'https://www.bbc.com/news/story'};
  storage.setItem('tuci:reading:edition-v1',JSON.stringify({day:key,articles:[story]}));if(!uncachedBody)storage.setItem('tuci:reading:bodies-v1',JSON.stringify([{id:story.id,paragraphs:['We remain resilient. Reading helps every day.']} ]));storage.setItem('tuci:reading:open:account-a',JSON.stringify(story.id));storage.setItem('tuci:capture:account-a',JSON.stringify({word:'sentinel',context:'Full-page draft'}));
  const ctx={root,scope:()=>scope,notes:()=>notes,settings:()=>({classify:true,...(ai?{aiKey:'fixture-key',aiEndpoint:'https://fixture.invalid/v1',aiModel:'fixture'}:{})}),navigate:(...args)=>navigations.push(args),toast:message=>notices.push(message),saveNote:async(...args)=>{writes.push({scope,args});await save?.(...args);notes.push({path:args[0],markdown:args[1]});}};
  const realm={...data,document,window,localStorage:storage,sessionStorage:storage,setTimeout,clearTimeout,AbortController,DOMException,TextDecoder,URL,Response,console,fetch:fetcher||(()=>{throw Error('Unexpected request');}),settleLoginViewport:async options=>{document.activeElement?.blur();if(settling)await settling(options);},mountReadingWords:options=>{onCollect=options.onCollect;return ()=>{};},mountReadingProgress:(_ctx,_story,content,options)=>{progressMounts.push({content,options});progressPaused=options.paused;const cleanup=()=>{};cleanup.flush=()=>{};return cleanup;}};
  Object.assign(realm,{describeNote,resolveWiki,noteBody(note){const body=new Element('article','nb-markdown');body.textContent=note.body;return body;}});
  const script=source.replace(/^import .*;\r?$/gm,'').replaceAll('export ','')+'\nglobalThis.api={renderReading,openReadingCapture};';
  vm.runInNewContext(script,realm,{filename:'web/learning.js'});
  const dispose=realm.api.renderReading(ctx);window.scrollY=640;scrolls.length=0;
  const collect=()=>onCollect('resilient','We remain resilient.');
  const sheet=()=>body.querySelector('.learn-capture-sheet');
  const form=()=>sheet()?.querySelector('form');
  const submit=()=>form().fire('submit');
  const findButton=text=>sheet()?.querySelectorAll('button').find(node=>node.textContent===text);
  return {document,window,root,tabs,masthead,writes,navigations,notices,animations,scrolls,cache,progressMounts,collect,sheet,form,submit,findButton,dispose,switchAccount:()=>scope='account-b',paused:()=>progressPaused()};
}

test('reading capture saves in a temporary sheet then returns to the same article and exact scroll position',async()=>{
  const p=page(),article=p.root.querySelector('article'),children=[...p.root.children],draft=p.cache.get('tuci:capture:account-a');
  p.collect();assert.ok(p.sheet());assert.equal(p.document.body.style.position,'fixed');assert.equal(p.document.body.style.top,'-640px');assert.equal(p.paused(),true);assert.equal(p.root.inert,true);assert.equal(p.tabs.inert,true);
  assert.equal(p.form().querySelector('input').value,'resilient');assert.equal(p.form().querySelector('.learn-sentence').value,'We remain resilient.');
  p.submit();await until(()=>!p.sheet());
  assert.equal(p.writes.length,1);assert.equal(p.writes[0].scope,'account-a');assert.match(p.writes[0].args[1],/We remain resilient/);assert.equal(p.root.querySelector('article'),article);assert.deepEqual(p.root.children,children);assert.deepEqual(p.navigations,[]);assert.equal(p.window.scrollY,640);assert.deepEqual(p.scrolls.at(-1),[0,640]);assert.equal(p.root.inert,false);assert.equal(p.tabs.inert,false);assert.equal(p.paused(),false);assert.equal(p.document.body.style.position,'');assert.equal(p.cache.get('tuci:capture:account-a'),draft);assert.equal(p.document.activeElement,article);p.dispose();
});

test('wide article capture keeps the reader mounted and offers a saved or existing note preview',async()=>{
  const p=page({width:1280}),article=p.root.querySelector('article'),draft=p.cache.get('tuci:capture:account-a');
  p.collect();assert.ok(p.root.querySelector('.learn-capture-drawer'));
  assert.equal(p.document.body.style.position,'');assert.equal(p.root.inert,false);
  p.submit();await until(()=>p.findButton('打开笔记 →'));
  assert.equal(p.writes.length,1);assert.equal(p.root.querySelector('article'),article);
  assert.equal(p.sheet().querySelector('.learn-note-preview').querySelector('h2').textContent,'resilient');
  p.submit();assert.equal(p.writes.length,1);assert.match(p.sheet().querySelector('.learn-feedback').children[0].textContent,/已收录/);
  p.findButton('打开笔记 →').fire('click');assert.deepEqual(p.navigations,[['library','其他/resilient.md']]);
  p.root.querySelectorAll('button').find(node=>node.textContent==='← 每日阅读').fire('click');
  assert.equal(p.sheet(),null);assert.equal(p.document.listeners.keydown.size,0);assert.equal(p.window.scrollY,640);
  assert.equal(p.cache.get('tuci:capture:account-a'),draft);p.dispose();
});

test('an unavailable article leaves progress untouched and lets the reader retry the same full text',async()=>{
  let attempts=0;
  const p=page({uncachedBody:true,fetcher:async()=>{
    if(++attempts===1)throw Error('Network unavailable');
    return Response.json({paragraphs:['The full article is available.']});
  }});
  const find=text=>p.root.querySelectorAll('button').find(node=>node.textContent===text);
  await until(()=>find('重试全文'));assert.equal(p.paused(),true);
  assert.match(p.root.querySelector('article').querySelector('p').textContent,/暂时无法载入全文/);
  find('重试全文').fire('click');await until(()=>!p.paused());
  assert.equal(attempts,2);assert.equal(p.root.querySelector('article').querySelector('p').textContent,'The full article is available.');
  find('← 每日阅读').fire('click');assert.ok(p.root.querySelector('.learn-stories'));assert.equal(p.window.scrollY,640);p.dispose();
});

test('article source preserves its real external link and renders a monochrome SVG instead of a text arrow',()=>{
  const p=page(),footer=p.root.querySelector('.learn-source'),link=footer.querySelector('a'),action=link.querySelector('.learn-source-action'),icon=action.querySelector('svg');
  assert.equal(p.root.children.at(-1),footer);assert.equal(link.href,'https://www.bbc.com/news/story');assert.equal(link.target,'_blank');assert.equal(link.rel,'noopener noreferrer');assert.equal(link.getAttribute('aria-label'),'阅读 Test 原文（新窗口打开）');assert.equal(link.querySelector('.learn-source-name').textContent,'Test');assert.equal(action.textContent,'阅读原文');assert.equal(icon.namespaceURI,'http://www.w3.org/2000/svg');assert.equal(icon.getAttribute('aria-hidden'),'true');assert.equal(icon.getAttribute('focusable'),'false');assert.ok(icon.querySelector('path').getAttribute('d'));p.dispose();
});

test('save failure stays in the sheet with input intact and retry closes only after a successful save',async()=>{
  let tries=0;const p=page({save:async()=>{if(tries++===0)throw Error('词库暂时不可写。');}});p.collect();const sheet=p.sheet(),input=p.form().querySelector('input');p.submit();await until(()=>p.findButton('重试保存'));
  assert.equal(p.sheet(),sheet);assert.equal(input.value,'resilient');assert.match(sheet.querySelector('.learn-feedback').children[0].textContent,/暂时不可写/);assert.equal(p.window.scrollY,640);assert.deepEqual(p.navigations,[]);
  p.findButton('重试保存').fire('click');await until(()=>!p.sheet());assert.equal(tries,2);assert.equal(p.window.scrollY,640);p.dispose();
});

test('cancel dismisses without saving, preserving full-page draft and restoring previously inert backgrounds',async()=>{
  const p=page();p.tabs.inert=true;const draft=p.cache.get('tuci:capture:account-a');p.collect();p.findButton('×').fire('click');await until(()=>!p.sheet());assert.equal(p.writes.length,0);assert.equal(p.tabs.inert,true);assert.equal(p.root.inert,false);assert.equal(p.window.scrollY,640);assert.equal(p.cache.get('tuci:capture:account-a'),draft);p.dispose();
});

test('account switch rejects a late AI result before reading or writing the next account notes',async()=>{
  const waiting=deferred(),p=page({ai:true,fetcher:()=>waiting.promise});p.collect();p.submit();p.switchAccount();waiting.resolve(Response.json({choices:[{message:{content:JSON.stringify(fixture)},finish_reason:'stop'}]}));await new Promise(resolve=>setImmediate(resolve));await new Promise(resolve=>setImmediate(resolve));assert.equal(p.writes.length,0);assert.deepEqual(p.notices,[]);p.dispose();assert.equal(p.sheet(),null);assert.equal(p.root.inert,false);
});

test('cancel during AI generation aborts and ignores a provider that responds after the sheet closed',async()=>{
  const waiting=deferred();let signal;const p=page({ai:true,fetcher:(_url,options)=>{signal=options.signal;return waiting.promise;}});p.collect();p.submit();p.findButton('×').fire('click');await until(()=>!p.sheet());assert.equal(signal.aborted,true);waiting.resolve(Response.json({choices:[{message:{content:JSON.stringify(fixture)},finish_reason:'stop'}]}));await new Promise(resolve=>setImmediate(resolve));assert.equal(p.writes.length,0);assert.equal(p.window.scrollY,640);p.dispose();
});

test('sheet enters from below, leaves downward, tracks the keyboard viewport and cleans up during navigation',async()=>{
  const p=page({reduced:false});p.collect();assert.equal(p.animations.length,2);assert.equal(p.animations[0].frames[0].transform,'translateY(100%)');assert.equal(p.animations[0].frames[1].transform,'translateY(0)');
  p.window.visualViewport.height=440;p.window.visualViewport.offsetTop=26;p.window.visualViewport.fire('resize');assert.equal(p.document.body.querySelector('.learn-capture-layer').style.height,'440px');assert.equal(p.document.body.querySelector('.learn-capture-layer').style.top,'26px');
  p.findButton('×').fire('click');assert.equal(p.animations.length,4);assert.equal(p.animations[2].frames[1].transform,'translateY(100%)');assert.ok(p.sheet());p.dispose();assert.equal(p.sheet(),null);assert.equal(p.root.inert,false);assert.equal(p.document.body.style.position,'');assert.equal(p.window.visualViewport.listeners.resize.size,0);assert.deepEqual(p.scrolls.at(-1),[0,640]);
});

test('closing a focused field keeps scroll locked until the keyboard settles',async()=>{
  const keyboard=deferred(),p=page({settling:()=>keyboard.promise});p.collect();p.form().querySelector('input').focus();p.findButton('×').fire('click');await new Promise(resolve=>setImmediate(resolve));assert.ok(p.sheet());assert.equal(p.document.body.style.position,'fixed');assert.equal(p.paused(),true);keyboard.resolve();await until(()=>!p.sheet());assert.equal(p.window.scrollY,640);assert.equal(p.paused(),false);p.dispose();
});

test('finished exit animations retain their end state while a slower keyboard is settling',async()=>{
  const keyboard=deferred(),p=page({reduced:false,settling:()=>keyboard.promise});p.collect();p.form().querySelector('input').focus();p.findButton('×').fire('click');const exiting=p.animations.slice(2);assert.equal(exiting.length,2);
  for(const animation of exiting)animation.pending.resolve();await new Promise(resolve=>setImmediate(resolve));
  assert.ok(p.sheet());assert.equal(p.document.body.style.position,'fixed');assert.ok(exiting.every(animation=>animation.canceled===false),'finished exit lost fill:forwards before keyboard settled');
  keyboard.resolve();await until(()=>!p.sheet());assert.ok(exiting.every(animation=>animation.canceled));assert.equal(p.window.scrollY,640);p.dispose();
});

test('reading progress never persists the zero scroll offset used by an open fixed-body sheet',{timeout:5000},async()=>{
  const ready=deferred();
  let paused=false;const writes=[],listeners=new Map(),window={scrollY:640,innerHeight:844,addEventListener:(name,fn)=>{listeners.set(name,fn);if(name==='scroll')ready.resolve();},removeEventListener:name=>listeners.delete(name),scrollTo(){assert.fail('no saved position to restore');}},document={documentElement:{scrollHeight:3000},visibilityState:'visible',addEventListener:(name,fn)=>listeners.set(name,fn),removeEventListener:name=>listeners.delete(name)};
  const ctx={scope:()=> 'account-a',stateEntries:()=>[],setState:async(key,value)=>writes.push({key,value})},content={querySelectorAll:()=>[{getBoundingClientRect:()=>({top:100-window.scrollY})}]};
  const realm={crypto:webcrypto,TextEncoder,URL,window,document,setTimeout,clearTimeout,requestAnimationFrame:fn=>queueMicrotask(fn)};
  vm.runInNewContext(progressSource.replaceAll('export ','')+'\nglobalThis.mount=mountReadingProgress;',realm);
  const savedKey=await realm.readingKey('https://www.bbc.com/news/story');
  ctx.stateEntries=()=>[{key:savedKey,value:{progress:.9,anchor:'p:0'}}];
  // A queued article body keeps the current pixels instead of jumping to the old
  // stored anchor, while later scrolling still records progress normally.
  const cleanup=realm.mount(ctx,{url:'https://www.bbc.com/news/story',title:'Story'},content,{paused:()=>paused,restore:false});await ready.promise;
  cleanup.flush();assert.equal(writes.length,1);assert.equal(writes[0].value.progress,640/2156);
  paused=true;window.scrollY=0;document.visibilityState='hidden';listeners.get('visibilitychange')();cleanup.flush();assert.equal(writes.length,1);
  window.scrollY=640;paused=false;cleanup.flush();assert.equal(writes.length,2);assert.equal(writes[1].value.progress,640/2156);cleanup();assert.equal(listeners.size,0);
});

for(const outcome of ['success','failure'])test('late article '+outcome+' waits for capture dismissal and preserves its reading position',async()=>{
  const network=deferred(),p=page({uncachedBody:true,fetcher:()=>network.promise}),article=p.root.querySelector('article'),children=[...p.root.children];
  p.collect();const sheet=p.sheet();
  if(outcome==='success')network.resolve(Response.json({paragraphs:['The full article has arrived.','More text follows.']}));else network.reject(Error('Network unavailable'));
  await new Promise(resolve=>setImmediate(resolve));await new Promise(resolve=>setImmediate(resolve));
  assert.equal(p.sheet(),sheet);assert.equal(p.root.querySelector('article'),article);assert.deepEqual(p.root.children,children);assert.equal(p.progressMounts.length,1);
  p.findButton('×').fire('click');await until(()=>!p.sheet());
  assert.notEqual(p.root.querySelector('article'),article);assert.equal(p.progressMounts.length,2);assert.equal(p.progressMounts.at(-1).options.restore,false);assert.equal(p.window.scrollY,640);assert.deepEqual(p.scrolls.at(-1),[0,640]);assert.deepEqual(p.navigations,[]);
  assert.match(p.root.querySelector('article').querySelector('p').textContent,outcome==='success'?/full article/:/暂时无法/);p.dispose();
});

test('leaving reading or switching accounts discards a queued article update instead of applying it during sheet cleanup',async()=>{
  const network=deferred(),p=page({uncachedBody:true,fetcher:()=>network.promise}),article=p.root.querySelector('article');p.collect();network.resolve(Response.json({paragraphs:['Late body.']}));await new Promise(resolve=>setImmediate(resolve));await new Promise(resolve=>setImmediate(resolve));p.switchAccount();p.dispose();assert.equal(p.sheet(),null);assert.equal(p.root.querySelector('article'),article);assert.equal(p.progressMounts.length,1);assert.equal(p.root.inert,false);assert.equal(p.document.body.style.position,'');
});
