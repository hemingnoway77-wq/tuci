import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {createOfflineDictionary,parseDictionaryShard,dictionaryEntry,lookupOffline,downloadDictionary,dictionaryStatus} from '../dictionary.js';

const letters='abcdefghijklmnopqrstuvwxyz';
const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
const baseURL='https://tuci.test/learn/dictionary/';
const encode=rows=>Buffer.from(rows.map(row=>JSON.stringify(row)).join('\n')+'\n');
const abort=()=>new DOMException('cancelled','AbortError');

class CacheStorage {
  stores=new Map(); reads=[]; writes=[]; deleted=[]; deny=false; quota=false;
  async open(name){
    if(this.deny)throw Error('storage denied');
    if(!this.stores.has(name))this.stores.set(name,new Map());
    const rows=this.stores.get(name),owner=this;
    return {
      async match(key){key=String(key);owner.reads.push({name,key});const row=rows.get(key);return row?new Response(row.bytes.slice(),{headers:row.headers}):undefined;},
      async put(key,response){
        if(owner.quota && !name.includes(':manifest:'))throw new DOMException('quota','QuotaExceededError');
        key=String(key);const bytes=new Uint8Array(await response.arrayBuffer()),headers=new Headers(response.headers);
        rows.set(key,{bytes,headers});owner.writes.push({name,key});
      },
      async delete(key){key=String(key);owner.deleted.push({name,key});return rows.delete(key);},
      async keys(){return [...rows.keys()].map(url=>({url}));}
    };
  }
  row(path){
    for(const [name,rows] of this.stores)for(const [key,row] of rows)if(key.endsWith('/'+path))return {name,key,row};
  }
}
function fixture(overrides={}){
  const assets=new Map([...letters].map(letter=>[letter+'.jsonl',encode([[letter,'/'+letter+'/','n. 中文释义','']]) ]));
  for(const [path,value] of Object.entries(overrides))assets.set(path,typeof value==='string'?Buffer.from(value):encode(value));
  const files=[...assets].map(([path,bytes])=>({role:'entries',path,bytes:bytes.length,sha256:sha(bytes)}));
  const manifest={formatVersion:1,statistics:{entryCount:26},files};
  const manifestBytes=Buffer.from(JSON.stringify(manifest));
  assets.set('dictionary.manifest.json',manifestBytes);
  const cacheStorage=new CacheStorage(),requests=[],responses=new Map();let offline=false;
  const fetcher=async (address,options)=>{
    const url=new URL(address),path=url.pathname.split('/').pop();
    assert.equal(url.origin,'https://tuci.test');
    assert.equal(options.credentials,'omit');assert.equal(options.mode,'same-origin');
    assert.equal(options.redirect,'error');assert.equal(options.referrerPolicy,'no-referrer');
    assert.equal(options.cache,'no-store');assert.ok(options.signal instanceof AbortSignal);
    assert.ok(!options.body && !options.headers?.Authorization);
    requests.push(path);
    if(offline)throw new TypeError('offline');
    if(options.signal.aborted)throw abort();
    if(responses.has(path))return responses.get(path)(options);
    const bytes=assets.get(path);assert.ok(bytes,'unexpected dictionary path '+path);
    return new Response(bytes,{headers:{'Content-Length':String(bytes.length)}});
  };
  return {assets,manifest,manifestBytes,cacheStorage,requests,responses,fetcher,setOffline(value){offline=value;},
    dictionary(options={}){return createOfflineDictionary({baseURL,fetcher,cacheStorage,...options});}};
}

test('module exposes the shared lookup, download and status interfaces',()=>{
  assert.equal(typeof lookupOffline,'function');assert.equal(typeof downloadDictionary,'function');assert.equal(typeof dictionaryStatus,'function');
});

test('lazy same-origin lookup normalizes words and returns independent phonetic, meanings and lemma objects',async()=>{
  const f=fixture({'i.jsonl':[['introduced','ɪntrəˈdjuːst','v. 介绍\nvt. 引入','0:introduce/p:introduced']]});
  const dictionary=f.dictionary();
  const value=await dictionary.lookup('  INTRODUCED ');
  assert.deepEqual(f.requests,['dictionary.manifest.json','i.jsonl']);
  assert.deepEqual(value,{word:'introduced',translation:'v. 介绍\nvt. 引入',phonetic:'ɪntrəˈdjuːst',
    meanings:[{pos:'v.',definition:'介绍'},{pos:'vt.',definition:'引入'}],lemma:'introduce',source:'offline'});
  value.meanings[0].definition='caller changed this';value.phonetic='changed';
  assert.equal((await dictionary.lookup('introduced')).meanings[0].definition,'介绍');
  assert.equal(await dictionary.lookup('imaginary'),null);
  assert.equal(f.requests.filter(path=>path==='i.jsonl').length,1);
  await assert.rejects(dictionary.lookup('../private'),/英文单词/);
  assert.equal(f.requests.length,2);
});

test('only two parsed shards survive in LRU memory, including when an invalid larger limit is supplied',async()=>{
  const f=fixture(),dictionary=f.dictionary({maxMemoryShards:99});
  await dictionary.lookup('a');await dictionary.lookup('b');await dictionary.lookup('a');await dictionary.lookup('c');
  assert.deepEqual(dictionary.memoryLetters(),['a','c']);
  await dictionary.lookup('b');
  assert.deepEqual(dictionary.memoryLetters(),['c','b']);
  assert.equal(f.requests.filter(path=>path==='b.jsonl').length,1,'evicted parsed data reloads from public disk cache');
  assert.equal(f.cacheStorage.reads.filter(row=>row.key.endsWith('/b.jsonl')).length,2);
});

test('a fresh module instance reads verified cached manifest and shards while entirely offline',async()=>{
  const f=fixture();await f.dictionary().lookup('a');
  f.setOffline(true);const dictionary=f.dictionary();
  assert.equal((await dictionary.lookup('a')).translation,'n. 中文释义');
  const status=await dictionary.status();
  assert.equal(status.completed,1);assert.equal(status.downloaded,1);assert.equal(status.complete,false);
  assert.equal(status.bytes,status.downloadedBytes);assert.equal(status.storageAvailable,true);
  const names=[...f.cacheStorage.stores.keys()];
  assert.ok(names.some(name=>name.endsWith(sha(f.manifestBytes))));
  assert.ok(names.every(name=>!name.startsWith('tuci:web:')));
});

test('a wrong-length or wrong-hash response never becomes a successful cached shard and is retryable',async()=>{
  for(const changed of ['length','hash']){
    const f=fixture(),dictionary=f.dictionary(),good=f.assets.get('a.jsonl');
    f.responses.set('a.jsonl',()=>{
      const bytes=Buffer.from(good);if(changed==='hash')bytes[4]^=1;
      return new Response(changed==='length'?bytes.subarray(0,-1):bytes);
    });
    await assert.rejects(dictionary.lookup('a'),/校验失败/);
    assert.equal(f.cacheStorage.row('a.jsonl'),undefined);
    assert.deepEqual(dictionary.memoryLetters(),[]);
    f.responses.delete('a.jsonl');assert.equal((await dictionary.lookup('a')).word,'a');
    assert.equal(f.requests.filter(path=>path==='a.jsonl').length,2);
  }
});

test('corrupted disk content is rejected even when its cached metadata claims the correct hash',async()=>{
  const f=fixture();await f.dictionary().lookup('a');
  f.cacheStorage.row('a.jsonl').row.bytes[4]^=1;
  const dictionary=f.dictionary();
  assert.equal((await dictionary.status()).completed,0);
  assert.equal((await dictionary.lookup('a')).word,'a');
  assert.equal(f.requests.filter(path=>path==='a.jsonl').length,2);
  assert.ok(f.cacheStorage.deleted.some(row=>row.key.endsWith('/a.jsonl')));
});

test('cached manifest corruption cannot bootstrap offline lookup',async()=>{
  const f=fixture();await f.dictionary().lookup('a');
  f.cacheStorage.row('dictionary.manifest.json').row.bytes[4]^=1;f.setOffline(true);
  await assert.rejects(f.dictionary().lookup('a'),/校验失败/);
});

test('manifest rejects duplicate shards, missing shards, path tricks and malformed records',async()=>{
  for(const mutate of [
    manifest=>manifest.files.push(manifest.files[0]),
    manifest=>manifest.files.pop(),
    manifest=>manifest.files[0].path='../a.jsonl',
    manifest=>manifest.files[0].sha256='not a hash',
    manifest=>manifest.files[0]=null
  ]){
    const f=fixture();mutate(f.manifest);
    f.responses.set('dictionary.manifest.json',()=>Response.json(f.manifest));
    await assert.rejects(f.dictionary().lookup('a'),/校验失败/);
    assert.equal(f.requests.length,1);
  }
});

test('download caches every shard without parsing and exposes monotonic progress, bytes and completion',async()=>{
  const f=fixture({'z.jsonl':'hash-valid data is not parsed during a bulk download\n'});
  let yielded=0;const progress=[],dictionary=f.dictionary({yieldControl:async()=>{yielded++;}});
  const result=await dictionary.downloadAll({onProgress:value=>progress.push(value)});
  assert.equal(result.complete,true);assert.equal(result.completed,26);assert.equal(result.downloaded,26);
  assert.equal(result.bytes,result.totalBytes);assert.equal(progress.length,27);
  assert.deepEqual(progress.map(value=>value.completed),Array.from({length:27},(_,index)=>index));
  assert.ok(progress.every((value,index)=>!index || value.bytes>=progress[index-1].bytes));
  assert.equal(yielded,0);assert.deepEqual(dictionary.memoryLetters(),[]);
  assert.equal((await dictionary.status()).complete,true);
  await assert.rejects(dictionary.lookup('z'),/校验失败/);
});

test('cancelled bulk download preserves completed shards and a restart resumes without refetching them',async()=>{
  const f=fixture(),controller=new AbortController();
  await assert.rejects(f.dictionary().downloadAll({signal:controller.signal,onProgress:progress=>{
    if(progress.completed===2)controller.abort();
  }}),{name:'AbortError'});
  assert.equal((await f.dictionary().status()).completed,2);
  assert.ok(!f.requests.includes('c.jsonl'));
  const resumed=await f.dictionary().downloadAll();assert.equal(resumed.complete,true);
  assert.equal(f.requests.filter(path=>path==='a.jsonl').length,1);
  assert.equal(f.requests.filter(path=>path==='b.jsonl').length,1);
});

test('cancelling an in-flight response stops its body and never installs incomplete bytes',async()=>{
  const f=fixture(),controller=new AbortController();let startBody,bodyCancelled=false;
  const started=new Promise(resolve=>{startBody=resolve;});
  f.responses.set('a.jsonl',()=>new Response(new ReadableStream({
    start(){startBody();},cancel(){bodyCancelled=true;}
  })));
  const pending=f.dictionary().lookup('a',{signal:controller.signal});await started;controller.abort();
  await assert.rejects(pending,{name:'AbortError'});
  assert.equal(bodyCancelled,true);assert.equal(f.cacheStorage.row('a.jsonl'),undefined);
});

test('parsing yields in bounded batches, aborts promptly and leaves the parsed memory cache empty',async()=>{
  const words=[...letters].map(letter=>['a'+letter,'','n. 定义','']);
  const f=fixture({'a.jsonl':words}),controller=new AbortController();let batches=0;
  const dictionary=f.dictionary({yieldEvery:4,yieldControl:async()=>{if(++batches===2)controller.abort();}});
  await assert.rejects(dictionary.lookup('aa',{signal:controller.signal}),{name:'AbortError'});
  assert.equal(batches,2);assert.deepEqual(dictionary.memoryLetters(),[]);
  const valid=f.dictionary();assert.equal((await valid.lookup('aa')).word,'aa');
  assert.equal(f.requests.filter(path=>path==='a.jsonl').length,1,'verified source remains cached after only parsing was cancelled');
});

test('an already-aborted lookup rejects even a memory hit and an aborted manifest caller does not poison another caller',async()=>{
  const f=fixture(),dictionary=f.dictionary();await dictionary.lookup('a');
  const controller=new AbortController();controller.abort();
  await assert.rejects(dictionary.lookup('a',{signal:controller.signal}),{name:'AbortError'});
  assert.equal(f.requests.length,2);
  const second=fixture();let release,started;
  const ready=new Promise(resolve=>{started=resolve;});let calls=0;
  second.responses.set('dictionary.manifest.json',async()=>{
    if(++calls===1){started();await new Promise(resolve=>{release=resolve;});}
    return new Response(second.manifestBytes);
  });
  const shared=second.dictionary(),cancelled=new AbortController();
  const lookup=shared.lookup('a',{signal:cancelled.signal});await ready;
  const status=shared.status();cancelled.abort();release();
  await assert.rejects(lookup,{name:'AbortError'});
  assert.equal((await status).total,26);
});

test('a cancelled queued caller rejects immediately without cancelling the lookup ahead of it',async()=>{
  const f=fixture(),controller=new AbortController();let release,started,firstSignal;
  const ready=new Promise(resolve=>{started=resolve;});
  f.responses.set('dictionary.manifest.json',async options=>{
    firstSignal=options.signal;started();await new Promise(resolve=>{release=resolve;});
    return new Response(f.manifestBytes);
  });
  const dictionary=f.dictionary(),first=dictionary.lookup('a');await ready;
  let settled=false;
  const second=dictionary.lookup('b',{signal:controller.signal});
  const rejection=assert.rejects(second,{name:'AbortError'}).then(()=>{settled=true;});
  try{
    controller.abort();await new Promise(resolve=>setImmediate(resolve));
    assert.equal(settled,true,'the queued caller must reject before the earlier fetch is released');
    assert.equal(firstSignal.aborted,false,'another caller owns the earlier request');
    assert.deepEqual(f.requests,['dictionary.manifest.json']);
  }finally{release();}
  await rejection;
  assert.equal((await first).word,'a');
  assert.equal((await dictionary.lookup('c')).word,'c','the queue remains usable after the cancelled entry');
  assert.ok(!f.requests.includes('b.jsonl'));
});

test('missing storage supports online lookup while bulk download clearly refuses nonpersistent storage',async()=>{
  const f=fixture();f.cacheStorage.deny=true;
  assert.equal((await f.dictionary().lookup('a')).word,'a');
  assert.equal((await f.dictionary().status()).storageAvailable,false);
  await assert.rejects(f.dictionary().downloadAll(),/存储权限/);
  const quota=fixture();quota.cacheStorage.quota=true;
  await assert.rejects(quota.dictionary().downloadAll(),/空间不足/);
  quota.cacheStorage.quota=false;assert.equal((await quota.dictionary().downloadAll()).complete,true);
});

test('parser rejects invalid rows, duplicate untranslated entries and ordinal-order violations',async()=>{
  for(const content of [
    '["a", "", "n. 好", null]\n',
    encode([['a','','',''],['a','','n. 重复','']]).toString(),
    encode([['ab','','n. 后',''],['aa','','n. 前','']]).toString(),
    encode([['b','','n. 错片','']]).toString(),
    encode([['A','','n. 未规范化','']]).toString()
  ])await assert.rejects(parseDictionaryShard(content,'a'),/校验失败/);
  assert.equal(dictionaryEntry(['running','','n. 跑步','0:run/0:RUN']).lemma,'run');
  assert.equal(dictionaryEntry(['running','','n. 跑步','0:run/0:runs']).lemma,null);
});

test('all real bundled assets retain their SHA256, size, 400181-row format and usable common inflections',async()=>{
  const directory=new URL('../dictionary/',import.meta.url),manifestBytes=await readFile(new URL('dictionary.manifest.json',directory));
  const manifest=JSON.parse(manifestBytes);let rows=0,usable=0,bytes=0,observedIntroduced;
  for(const asset of manifest.files){
    const content=await readFile(new URL(asset.path,directory));
    assert.equal(content.length,asset.bytes,asset.path+' byte count');assert.equal(sha(content),asset.sha256,asset.path+' SHA256');
    if(asset.role!=='entries')continue;
    const source=content.toString('utf8');rows+=source.split('\n').filter(Boolean).length;bytes+=content.length;
    const parsed=await parseDictionaryShard(source,asset.path[0],{yieldEvery:4096,yieldControl:async()=>{}});
    usable+=parsed.size;
    if(asset.path==='i.jsonl')observedIntroduced=dictionaryEntry(parsed.get('introduced'));
  }
  assert.equal(rows,400181);assert.equal(rows,manifest.statistics.entryCount);
  assert.equal(usable,rows-manifest.statistics.entriesWithoutTranslation);
  assert.equal(bytes,manifest.totals.entryBytes);
  assert.equal(observedIntroduced.word,'introduced');assert.equal(observedIntroduced.lemma,'introduce');
  assert.ok(observedIntroduced.translation.includes('介绍'));
});
