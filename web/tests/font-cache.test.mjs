import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {createHash,webcrypto} from 'node:crypto';
const source=await readFile(new URL('../sw.template.js',import.meta.url),'utf8');
const bytes=Buffer.from('wOF2-font-cache-fixture'),url='https://example.test/tuci/fonts/test.woff2';
const info={bytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')};
function worker({storage=new Map(),fetcher=async()=>new Response(bytes),version='one'}={}){
  const handlers={},precached=[];
  const cache={async match(key){return storage.get(key)?.clone();},async put(key,value){storage.set(key,value.clone());},async delete(key){storage.delete(key);},async addAll(requests){precached.push(...requests.map(request=>request.url));}};
  const self={registration:{scope:'https://example.test/tuci/'},location:{origin:'https://example.test'},addEventListener:(name,fn)=>handlers[name]=fn,skipWaiting:async()=>{},clients:{claim:async()=>{}}};
  const opened=[];const caches={async open(name){opened.push(name);return cache;},async keys(){return[];},async delete(){}};
  vm.runInNewContext(source.replace('"__VERSION__"',JSON.stringify(version)).replace('"__ASSETS__"',JSON.stringify({'index.html':'sha256-test'}))
    .replace('"__FONT_VERSION__"','"same-font-assets"').replace('"__FONT_ASSETS__"',JSON.stringify({'fonts/test.woff2':info})),{self,caches,URL,Request,Response,crypto:webcrypto,fetch:fetcher});
  function request(target=url){let response;handlers.fetch({request:{method:'GET',url:target,mode:'cors'},respondWith:value=>response=value});return response;}
  function install(){let result;handlers.install({waitUntil:value=>result=value});return result;}
  return {request,install,storage,opened,precached};
}
test('font shards load only when used, verify before caching and survive offline shell upgrades',async()=>{
  let calls=0;const storage=new Map(),first=worker({storage,fetcher:async(_url,options)=>{calls++;assert.equal(_url,url);assert.equal(options.credentials,'omit');return new Response(bytes);}});
  await first.install();assert.equal(calls,0);assert.equal(first.precached.includes(url),false);
  assert.deepEqual(Buffer.from(await (await first.request()).arrayBuffer()),bytes);assert.equal(calls,1);
  const offline=worker({storage,version:'two',fetcher:async()=>assert.fail('verified font must remain available offline')});
  assert.deepEqual(Buffer.from(await (await offline.request()).arrayBuffer()),bytes);
  assert.deepEqual(first.opened.filter(name=>name.startsWith('tuci:fonts:')),offline.opened);
});
test('a wrong-length or corrupt network font is never cached and can be retried',async()=>{
  for(const bad of [Buffer.alloc(bytes.length-1),Buffer.alloc(bytes.length)]){
    let correct=false;const page=worker({fetcher:async()=>new Response(correct?bytes:bad)});
    await assert.rejects(page.request(),/Invalid font/);assert.equal(page.storage.size,0);
    correct=true;assert.deepEqual(Buffer.from(await(await page.request()).arrayBuffer()),bytes);assert.equal(page.storage.size,1);
  }
});
test('corrupt cached glyph bytes are removed and replaced only by a verified response',async()=>{
  const storage=new Map([[url,new Response(Buffer.alloc(bytes.length))]]);let calls=0;
  const page=worker({storage,fetcher:async()=>{calls++;return new Response(bytes);}});
  assert.deepEqual(Buffer.from(await(await page.request()).arrayBuffer()),bytes);assert.equal(calls,1);
  assert.deepEqual(Buffer.from(await storage.get(url).arrayBuffer()),bytes);
});
test('the font handler deduplicates simultaneous loads and ignores unlisted or external fonts',async()=>{
  let finish,calls=0;const gate=new Promise(resolve=>finish=resolve),page=worker({fetcher:async()=>{calls++;await gate;return new Response(bytes);}});
  const first=page.request(),second=page.request();await new Promise(resolve=>setImmediate(resolve));assert.equal(calls,1);finish();
  assert.deepEqual(Buffer.from(await(await first).arrayBuffer()),bytes);assert.deepEqual(Buffer.from(await(await second).arrayBuffer()),bytes);
  assert.equal(page.request('https://elsewhere.example/font.woff2'),undefined);assert.equal(page.request('https://example.test/tuci/fonts/unlisted.woff2'),undefined);
});
