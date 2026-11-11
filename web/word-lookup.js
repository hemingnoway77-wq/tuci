import { lookupOffline } from './dictionary.js';
import { normalWord } from './learning-data.js';
import { translateOffline } from './offline-translation.js';

const successes=new Map(), cacheLimit=128;
const dictionarySource='https://dictionaryapi.dev/';
const audioPaths=[
  ['https://api.dictionaryapi.dev','/media/pronunciations/en/'],
  ['https://ssl.gstatic.com','/dictionary/static/sounds/']
];

function abortError() { return new DOMException('请求已取消。','AbortError'); }
function checkAbort(signal) { if(signal?.aborted) throw abortError(); }
function cached(key) {
  if(!successes.has(key)) return;
  const value=successes.get(key);
  successes.delete(key);successes.set(key,value);
  return typeof value==='string' ? value : {...value};
}
function remember(key,value) {
  successes.delete(key);
  successes.set(key,typeof value==='string' ? value : {...value});
  if(successes.size>cacheLimit) successes.delete(successes.keys().next().value);
  return value;
}
function text(value,limit=200) {
  return typeof value==='string' && value.length<=limit ? value.replace(/[\u0000-\u001f\u007f]/g,' ').trim() : '';
}
function secureURL(value) {
  try {
    const url=new URL(text(value,2000));
    return url.protocol==='https:' && !url.username && !url.password && !url.port ? url : null;
  } catch { return null; }
}
function audioURL(value) {
  const url=secureURL(value);
  return url && audioPaths.some(([origin,path]) => url.origin===origin && url.pathname.startsWith(path) && url.pathname.length>path.length) ? url.href : '';
}
function sourceURL(value) {
  const url=secureURL(value);
  return url && (url.hostname==='commons.wikimedia.org' || url.hostname==='wiktionary.org' || url.hostname.endsWith('.wiktionary.org')) ? url.href : '';
}

async function getJSON(url,signal,service) {
  checkAbort(signal);
  const controller=new AbortController();
  let timedOut=false;
  const abort=()=>controller.abort();
  signal?.addEventListener('abort',abort,{once:true});
  const timer=setTimeout(()=>{timedOut=true;controller.abort();},10000);
  function check() {
    checkAbort(signal);
    if(timedOut) throw Error(service+'请求超时，请稍后重试。');
  }
  try {
    let response;
    try {
      response=await fetch(url,{signal:controller.signal,credentials:'omit',referrerPolicy:'no-referrer',cache:'no-store',redirect:'error'});
    } catch {
      check();
      throw Error(service+'服务暂时不可用，请稍后重试。');
    }
    check();
    if(response.status===429) throw Error(service+'请求受限，请稍后再试。');
    if(!response.ok) throw Error(service+'服务暂时不可用，请稍后重试。');
    let data;
    try { data=await response.json(); }
    catch {
      check();
      throw Error(service+'服务返回的数据无效，请稍后重试。');
    }
    check();
    return data;
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener('abort',abort);
  }
}

// These lookups stay separate from AI capture and never write browser storage or notes.
export async function translateWord(word,{signal}={}) {
  const target=normalWord(word), key='translation:'+target;
  checkAbort(signal);
  const saved=cached(key);
  if(saved!==undefined) return saved;
  const url=new URL('https://api.mymemory.translated.net/get');
  url.searchParams.set('q',target);url.searchParams.set('langpair','en|zh-CN');
  const data=await getJSON(url.href,signal,'免费翻译');
  checkAbort(signal);
  const status=Number(data?.responseStatus), raw=data?.responseData?.translatedText;
  if(data?.quotaFinished===true || status===429 || status===403 ||
    (typeof raw==='string' && /^\s*MYMEMORY WARNING:/i.test(raw))) {
    throw Error('免费翻译服务额度已用完或请求受限，请稍后再试。');
  }
  const translation=text(raw,2000);
  if(status!==200 || !translation) throw Error('免费翻译服务未返回有效译文，请稍后重试。');
  return remember(key,translation);
}

export async function lookupPronunciation(word,{signal}={}) {
  const target=normalWord(word), key='pronunciation:'+target;
  checkAbort(signal);
  const saved=cached(key);
  if(saved!==undefined) return saved;
  const data=await getJSON('https://api.dictionaryapi.dev/api/v2/entries/en/'+encodeURIComponent(target),signal,'发音查询');
  checkAbort(signal);
  if(!Array.isArray(data) || !data.every(entry => entry && typeof entry==='object' && !Array.isArray(entry))) {
    throw Error('发音查询服务返回的数据无效，请稍后重试。');
  }
  let result={phonetic:'',audio:'',sourceUrl:dictionarySource}, score=-1;
  for(const entry of data) {
    const phonetics=Array.isArray(entry.phonetics) ? entry.phonetics.filter(p => p && typeof p==='object') : [];
    const source=(Array.isArray(entry.sourceUrls) ? entry.sourceUrls.map(sourceURL).find(Boolean) : '') || dictionarySource;
    const entryText=text(entry.phonetic), fallback=entryText || phonetics.map(p=>text(p.text)).find(Boolean) || '';
    if(score<0 || (score===0 && fallback)) {
      result={phonetic:fallback,audio:'',sourceUrl:source};score=fallback ? 1 : 0;
    }
    for(const item of phonetics) {
      const audio=audioURL(item.audio);
      if(!audio) continue;
      const ownText=text(item.text) || entryText, phonetic=ownText || fallback;
      const rank=ownText ? 4 : phonetic ? 3 : 2;
      if(rank>score) {
        result={phonetic,audio,sourceUrl:sourceURL(item.sourceUrl) || source};score=rank;
      }
    }
  }
  return remember(key,result);
}

// Public assets are shared across accounts. Online lookup follows both local options.
export async function lookupWord(word,{signal,dictionaryLookup=lookupOffline,offlineTranslator=translateOffline}={}) {
  const target=normalWord(word);checkAbort(signal);
  try{
    const offline=await dictionaryLookup(target,{signal});checkAbort(signal);
    if(offline?.translation?.trim())return offline;
  }catch(error){checkAbort(signal);if(error.name==='AbortError')throw error;}
  try{
    const translation=await offlineTranslator(target,{signal});checkAbort(signal);
    if(translation?.trim())return {word:target,translation,phonetic:'',lemma:null,meanings:null,source:'offline-translation'};
  }catch(error){checkAbort(signal);if(error.name==='AbortError')throw error;}
  return {word:target,translation:await translateWord(target,{signal}),phonetic:'',lemma:null,meanings:null,source:'online'};
}
