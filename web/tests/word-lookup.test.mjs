import test from 'node:test';
import assert from 'node:assert/strict';
import { translateWord, lookupPronunciation, lookupWord } from '../word-lookup.js';

const translated = text => Response.json({
  responseData: { translatedText:text, match:0.9 },
  quotaFinished:false, responseDetails:'', responseStatus:200,
  responderId:null, exception_code:null, matches:[]
});
const pronunciation = (word, phonetic, phonetics) => Response.json([{
  word, phonetic, phonetics, meanings:[],
  license:{name:'CC BY-SA 3.0',url:'https://creativecommons.org/licenses/by-sa/3.0/'},
  sourceUrls:['https://en.wiktionary.org/wiki/'+word]
}]);

test('lookup uses the installed model only after a dictionary miss and before online fallback', async t => {
  let modelCalls=0, networkCalls=0;
  t.mock.method(globalThis,'fetch',async () => { networkCalls++; return translated('在线后备'); });
  const installed=async word => { modelCalls++; assert.equal(word,'modelled'); return '本机译文'; };
  const dictionary={word:'book',translation:'书',source:'offline'};
  assert.equal(await lookupWord('book',{dictionaryLookup:async()=>dictionary,offlineTranslator:installed}),dictionary);
  assert.equal(modelCalls,0);
  assert.deepEqual(await lookupWord('Modelled',{dictionaryLookup:async()=>null,offlineTranslator:installed}),{
    word:'modelled',translation:'本机译文',phonetic:'',lemma:null,meanings:null,source:'offline-translation'
  });
  assert.equal(modelCalls,1);assert.equal(networkCalls,0);
  assert.equal((await lookupWord('fallback',{dictionaryLookup:async()=>null,offlineTranslator:async()=>null})).source,'online');
  assert.equal(networkCalls,1);
});

test('translation normalizes the selected word and sends only that word without credentials', async t => {
  t.mock.method(globalThis,'fetch',async (address,options) => {
    const url=new URL(address);
    assert.equal(url.origin+url.pathname,'https://api.mymemory.translated.net/get');
    assert.deepEqual([...url.searchParams],[['q',"don't"],['langpair','en|zh-CN']]);
    assert.equal(options.credentials,'omit');
    assert.equal(options.referrerPolicy,'no-referrer');
    assert.equal(options.cache,'no-store');
    assert.equal(options.redirect,'error');
    assert.ok(options.signal instanceof AbortSignal);
    assert.ok(!options.body);
    assert.ok(!options.headers?.Authorization);
    return translated('  不要  ');
  });
  assert.equal(await translateWord('  Don’t  '),'不要');
});

test('invalid input never reaches either public service', async t => {
  t.mock.method(globalThis,'fetch',() => assert.fail('invalid words must not leave this device'));
  await assert.rejects(translateWord('hello world'),/英文单词/);
  await assert.rejects(lookupPronunciation('../secret'),/英文单词/);
});

test('MyMemory quota failures never become translations or successful cache entries', async t => {
  let calls=0;
  t.mock.method(globalThis,'fetch',async () => {
    calls++;
    return calls===1 ? Response.json({responseStatus:403,quotaFinished:true,
      responseDetails:'quota exceeded',responseData:{translatedText:'MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY.'}})
      : translated('配额');
  });
  await assert.rejects(translateWord('quota'),/限额|额度|受限/);
  assert.equal(await translateWord('quota'),'配额');
  assert.equal(calls,2);
});

test('HTTP and embedded rate limits give an actionable Chinese error', async t => {
  const responses=[new Response('',{status:429}),Response.json({responseStatus:429,quotaFinished:false,responseData:{translatedText:'too many requests'}})];
  t.mock.method(globalThis,'fetch',async () => responses.shift());
  await assert.rejects(translateWord('restricted'),/受限|限额|额度/);
  await assert.rejects(translateWord('limited'),/受限|限额|额度/);
});

test('malformed, blank, and oversized translations are rejected rather than displayed', async t => {
  const responses=[
    new Response('{broken',{headers:{'Content-Type':'application/json'}}),
    Response.json({responseStatus:200,responseData:{translatedText:'   '}}),
    Response.json({responseStatus:200,responseData:{translatedText:'中'.repeat(2001)}}),
    Response.json({responseStatus:500,responseData:{translatedText:'not a translation'}}),
    Response.json({responseData:{translatedText:'missing status'}}),
    Response.json({responseStatus:200,quotaFinished:false,responseData:{translatedText:'MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY.'}})
  ];
  t.mock.method(globalThis,'fetch',async () => responses.shift());
  for(const word of ['malformed','blank','oversized','upstream','statusless','warning']) {
    await assert.rejects(translateWord(word),/翻译|额度|受限|限额/);
  }
});

test('successful repeated lookups use a memory cache and caller mutation cannot corrupt pronunciation', async t => {
  let calls=0;
  t.mock.method(globalThis,'fetch',async address => {
    calls++;
    return new URL(address).hostname==='api.mymemory.translated.net' ? translated('记忆') :
      pronunciation('memory','/ˈmeməri/',[{text:'/ˈmeməri/',audio:'https://api.dictionaryapi.dev/media/pronunciations/en/memory-us.mp3'}]);
  });
  assert.equal(await translateWord('Memory'),'记忆');
  assert.equal(await translateWord('memory'),'记忆');
  const first=await lookupPronunciation('memory');
  first.audio='https://bad.invalid/changed.mp3';
  assert.deepEqual(await lookupPronunciation('MEMORY'),{
    phonetic:'/ˈmeməri/',audio:'https://api.dictionaryapi.dev/media/pronunciations/en/memory-us.mp3',sourceUrl:'https://en.wiktionary.org/wiki/memory'
  });
  assert.equal(calls,2);
});

test('an aborted request cannot populate the cache even if its late response succeeds', async t => {
  let complete, calls=0;
  t.mock.method(globalThis,'fetch',async () => {
    calls++;
    return calls===1 ? await new Promise(resolve => {complete=resolve;}) : translated('重新请求');
  });
  const controller=new AbortController();
  const pending=translateWord('cancelled',{signal:controller.signal});
  controller.abort();
  complete(translated('过期结果'));
  await assert.rejects(pending,{name:'AbortError'});
  assert.equal(await translateWord('cancelled'),'重新请求');
  assert.equal(calls,2);
});

test('an already aborted signal rejects even a cached result', async t => {
  t.mock.method(globalThis,'fetch',async () => translated('已缓存'));
  assert.equal(await translateWord('cached'),'已缓存');
  const controller=new AbortController();controller.abort();
  await assert.rejects(translateWord('cached',{signal:controller.signal}),{name:'AbortError'});
});

test('slow services time out, abort their fetch, and remain retryable', async t => {
  t.mock.timers.enable({apis:['setTimeout']});
  let calls=0;
  t.mock.method(globalThis,'fetch',async (_address,{signal}) => {
    calls++;
    if(calls>1) return translated('重试成功');
    return await new Promise((_resolve,reject) => {
      signal.addEventListener('abort',() => reject(new DOMException('aborted','AbortError')),{once:true});
    });
  });
  const pending=translateWord('slow');
  const rejected=assert.rejects(pending,/超时/);
  t.mock.timers.tick(10000);
  await rejected;
  assert.equal(await translateWord('slow'),'重试成功');
});

test('pronunciation normalizes hyphens and prefers an entry with both phonetics and safe audio', async t => {
  t.mock.method(globalThis,'fetch',async (address,options) => {
    assert.equal(String(address),'https://api.dictionaryapi.dev/api/v2/entries/en/well-known');
    assert.equal(options.credentials,'omit');
    assert.equal(options.referrerPolicy,'no-referrer');
    assert.equal(options.cache,'no-store');
    assert.equal(options.redirect,'error');
    return Response.json([
      {word:'well-known',phonetic:'/first/',phonetics:[],sourceUrls:['https://en.wiktionary.org/wiki/well-known']},
      {word:'well-known',phonetics:[
        {text:'',audio:'https://ssl.gstatic.com/dictionary/static/sounds/oxford/well-known--_gb_1.mp3'},
        {text:'/ˌwel ˈnəʊn/',audio:'https://api.dictionaryapi.dev/media/pronunciations/en/well-known-us.mp3',sourceUrl:'https://commons.wikimedia.org/w/index.php?curid=123'}
      ],sourceUrls:['https://evil.invalid/source']}
    ]);
  });
  assert.deepEqual(await lookupPronunciation('  Well-Known  '),{
    phonetic:'/ˌwel ˈnəʊn/',audio:'https://api.dictionaryapi.dev/media/pronunciations/en/well-known-us.mp3',sourceUrl:'https://commons.wikimedia.org/w/index.php?curid=123'
  });
});

test('missing pronunciation is an empty result with a safe dictionary source', async t => {
  t.mock.method(globalThis,'fetch',async () => pronunciation('unvoiced','',[]));
  assert.deepEqual(await lookupPronunciation('unvoiced'),{
    phonetic:'',audio:'',sourceUrl:'https://en.wiktionary.org/wiki/unvoiced'
  });
});

test('audio-only pronunciation is usable and absent trusted attribution falls back safely', async t => {
  t.mock.method(globalThis,'fetch',async () => Response.json([{
    word:'audible',phonetics:[{audio:'https://ssl.gstatic.com/dictionary/static/sounds/oxford/audible--_gb_1.mp3',sourceUrl:'https://bad.invalid/source'}],sourceUrls:[]
  }]));
  assert.deepEqual(await lookupPronunciation('audible'),{
    phonetic:'',audio:'https://ssl.gstatic.com/dictionary/static/sounds/oxford/audible--_gb_1.mp3',sourceUrl:'https://dictionaryapi.dev/'
  });
});

test('unsafe audio and source URLs are never returned to the player or link', async t => {
  const urls=[
    'http://api.dictionaryapi.dev/media/pronunciations/en/test.mp3',
    'https://api.dictionaryapi.dev.evil.invalid/media/pronunciations/en/test.mp3',
    'https://api.dictionaryapi.dev/media/pronunciations/en/../secret.mp3',
    'https://user:secret@api.dictionaryapi.dev/media/pronunciations/en/test.mp3',
    'https://ssl.gstatic.com/another-path/test.mp3',
    'javascript:alert(1)',
    '//ssl.gstatic.com/dictionary/static/sounds/test.mp3'
  ];
  t.mock.method(globalThis,'fetch',async () => Response.json([{
    word:'unsafe',phonetic:'/safe/',phonetics:[{text:'/safe/',audio:urls.shift(),sourceUrl:'javascript:alert(1)'}],
    sourceUrls:['https://en.wiktionary.org.evil.invalid/wiki/unsafe']
  }]));
  for(const word of ['unsafe','untrusted','pathless','credentialed','misplaced','scripted','relative']) {
    assert.deepEqual(await lookupPronunciation(word),{phonetic:'/safe/',audio:'',sourceUrl:'https://dictionaryapi.dev/'});
  }
});

test('malformed pronunciation payloads and service errors reject without affecting translation', async t => {
  const responses=[Response.json({wrong:'shape'}),new Response('',{status:404}),new Response('',{status:429}),new Response('{bad')];
  t.mock.method(globalThis,'fetch',async address => new URL(address).hostname==='api.mymemory.translated.net' ? translated('独立翻译') : responses.shift());
  for(const word of ['unshaped','unavailable','ratelimited','undecodable']) {
    await assert.rejects(lookupPronunciation(word),/发音|受限/);
  }
  assert.equal(await translateWord('unavailable'),'独立翻译');
});

test('the success cache is bounded instead of retaining every queried word', async t => {
  let calls=0;
  t.mock.method(globalThis,'fetch',async () => {calls++;return translated('暂存');});
  const wordFor=i => 'bounded'+String.fromCharCode(97+Math.floor(i/26),97+i%26);
  for(let i=0;i<129;i++) await translateWord(wordFor(i));
  assert.equal(await translateWord(wordFor(0)),'暂存');
  assert.equal(calls,130);
});

test('offline details win before any external translation or pronunciation request',async t=>{
  const {lookupWord}=await import('../word-lookup.js');
  t.mock.method(globalThis,'fetch',()=>assert.fail('an offline dictionary hit must not leave the device'));
  const expected={word:'introduced',translation:'v. 介绍',phonetic:'intrədu:st',meanings:[{pos:'v.',definition:'介绍'}],lemma:'introduce',source:'offline'};
  const result=await lookupWord('Introduced',{dictionaryLookup:async(word)=>{assert.equal(word,'introduced');return expected;}});
  assert.deepEqual(result,expected);
});
test('offline misses and unavailable assets retain the existing online fallback, while abort never falls back',async t=>{
  const {lookupWord}=await import('../word-lookup.js');let calls=0;
  t.mock.method(globalThis,'fetch',async()=>{calls++;return translated('联网参考译文');});
  assert.equal((await lookupWord('offline-missing-word',{dictionaryLookup:async()=>null})).source,'online');
  assert.equal((await lookupWord('offline-broken-assets',{dictionaryLookup:async()=>{throw Error('network failed');}})).translation,'联网参考译文');
  const controller=new AbortController();controller.abort();
  await assert.rejects(lookupWord('offline-abort',{signal:controller.signal,dictionaryLookup:async()=>null}),{name:'AbortError'});assert.equal(calls,2);
});
