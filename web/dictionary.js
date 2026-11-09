import {normalWord} from './learning-data.js';
const letters='abcdefghijklmnopqrstuvwxyz';
const tags='(?:n|v|vt|vi|a|adj|ad|adv|prep|pron|art|conj|interj|int|aux|num|phr)';
const posPattern=new RegExp('^\\s*('+tags+'\\.(?:\\s*[&/,]\\s*'+tags+'\\.)*)\\s*(.*)$','i');
const canceled=()=>new DOMException('请求已取消。','AbortError');
function check(signal){if(signal?.aborted)throw canceled();}
function callerResult(task,signal){
  if(!signal)return task;
  return new Promise((resolve,reject)=>{
    const finish=(settle,value)=>{signal.removeEventListener('abort',onAbort);settle(value);};
    const onAbort=()=>finish(reject,canceled());
    signal.addEventListener('abort',onAbort,{once:true});
    // This only settles the caller. The queued task remains chained to loadTail
    // and will check its own signal when its turn arrives.
    task.then(value=>finish(resolve,value),error=>finish(reject,error));
    if(signal.aborted)onAbort();
  });
}
const digest=async bytes=>Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),n=>n.toString(16).padStart(2,'0')).join('');
function fail(){throw new Error('离线词典分片校验失败，请联网重试下载。');}
export function dictionaryEntry(row){
  if(!Array.isArray(row) || row.length!==4 || row.some(value=>typeof value!=='string'))fail();
  const [word,phonetic,translation,exchange]=row;
  const candidates=[...new Set(exchange.split('/').filter(item=>item.startsWith('0:')).map(item=>item.slice(2).toLowerCase()))];
  let lemma=null;
  if(candidates.length===1)try{const value=normalWord(candidates[0]);if(value!==word)lemma=value;}catch{}
  const meanings=translation.split('\n').filter(line=>line.trim()).map(line=>{
    const value=line.trim(),match=posPattern.exec(value);return {pos:match?match[1].trim():'',definition:match?match[2].trim():value};
  });
  return {word,translation,phonetic:phonetic.trim(),meanings,lemma,source:'offline'};
}
export async function parseDictionaryShard(text,letter,{signal,yieldEvery=512,yieldControl=()=>new Promise(resolve=>setTimeout(resolve,0))}={}){
  check(signal);
  if(typeof text!=='string' || !/^[a-z]$/.test(letter) || !Number.isSafeInteger(yieldEvery) || yieldEvery<1 || yieldEvery>4096)fail();
  const words=new Map();let offset=0,count=0,previous='';
  while(offset<text.length){
    check(signal);const end=text.indexOf('\n',offset),line=text.slice(offset,end<0?undefined:end).replace(/\r$/,'');offset=end<0?text.length:end+1;
    if(!line)continue;if(line.length>1048576)fail();
    let row;try{row=JSON.parse(line);}catch{fail();}
    if(!Array.isArray(row) || row.length!==4 || row.some(value=>typeof value!=='string'))fail();
    let key;try{key=normalWord(row[0]);}catch{fail();}
    if(key!==row[0] || key[0]!==letter || (previous && key<=previous))fail();
    previous=key;
    if(row[2].trim())words.set(key,row);
    if(++count%yieldEvery===0){await yieldControl();check(signal);}
  }
  check(signal);return words;
}
export function createOfflineDictionary({baseURL=new URL('./dictionary/',import.meta.url),fetcher=(...args)=>fetch(...args),cacheStorage=globalThis.caches,maxMemoryShards=2,yieldEvery=512,yieldControl}={}){
  const base=new URL(baseURL),memory=new Map();let manifestValue=null,loadTail=Promise.resolve();
  const memoryLimit=Number.isSafeInteger(maxMemoryShards) && maxMemoryShards>=1 && maxMemoryShards<=2?maxMemoryShards:2;
  const assetURL=path=>new URL(path,base).href;
  const manifestURL=assetURL('dictionary.manifest.json');
  async function fetchBytes(url,signal,maximum){
    check(signal);const controller=new AbortController(),abort=()=>controller.abort();signal?.addEventListener('abort',abort,{once:true});
    const timeout=setTimeout(()=>controller.abort(),30000);
    try{
      const response=await fetcher(url,{signal:controller.signal,credentials:'omit',mode:'same-origin',referrerPolicy:'no-referrer',cache:'no-store',redirect:'error'});
      if(controller.signal.aborted){try{await response.body?.cancel();}catch{}check(signal);throw new DOMException('timeout','AbortError');}
      check(signal);if(!response.ok || response.redirected || response.type==='opaque' || (response.url && new URL(response.url).origin!==base.origin))throw new Error('离线词典暂时无法下载，请联网后重试。');
      const claimed=Number(response.headers.get('Content-Length'));if(Number.isFinite(claimed) && claimed>maximum)fail();
      let bytes;
      if(response.body?.getReader){
        const reader=response.body.getReader(),chunks=[];let size=0;
        const cancelReader=()=>{reader.cancel().catch(()=>{});};
        controller.signal.addEventListener('abort',cancelReader,{once:true});
        try{for(;;){check(signal);const next=await reader.read();check(signal);if(controller.signal.aborted)throw new DOMException('timeout','AbortError');if(next.done)break;size+=next.value.byteLength;if(size>maximum){await reader.cancel();fail();}chunks.push(next.value);}}
        finally{controller.signal.removeEventListener('abort',cancelReader);reader.releaseLock();}
        bytes=new Uint8Array(size);let at=0;for(const chunk of chunks){bytes.set(chunk,at);at+=chunk.byteLength;}
      }else bytes=new Uint8Array(await response.arrayBuffer());
      check(signal);if(bytes.byteLength>maximum)fail();return bytes;
    }catch(error){check(signal);if(error.name==='AbortError')throw new Error('离线词典下载超时，请重试。');throw error;}
    finally{clearTimeout(timeout);signal?.removeEventListener('abort',abort);}
  }
  async function manifestCache(){try{return await cacheStorage?.open?.('tuci:dictionary:manifest:v1');}catch{return null;}}
  async function manifest(signal){
    check(signal);
    if(manifestValue)return manifestValue;
    // Each caller owns its cancellation. A cancelled lookup cannot cancel a
    // concurrent settings/download request waiting for the same manifest.
    const cache=await manifestCache();let bytes,fetched=false;
    try{bytes=await fetchBytes(manifestURL,signal,256*1024);fetched=true;}
    catch(error){
      check(signal);
      let saved;try{saved=await cache?.match(manifestURL);}catch{}
      if(!saved)throw error;
      bytes=new Uint8Array(await saved.arrayBuffer());
      if(bytes.byteLength>256*1024 || saved.headers.get('X-Tuci-SHA256')!==await digest(bytes))fail();
    }
    let parsed;
    try{parsed=JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes));}catch{fail();}
    if(!parsed || parsed.formatVersion!==1 || !Array.isArray(parsed.files) || parsed.files.length>128)fail();
    const files=new Map();
    for(const file of parsed.files){
      if(!file || typeof file!=='object' || typeof file.path!=='string')fail();
      if(!/^[a-z]\.jsonl$/.test(file.path))continue;
      if(files.has(file.path) || !Number.isSafeInteger(file.bytes) || file.bytes<0 || file.bytes>4*1024*1024 || !/^[\da-f]{64}$/i.test(file.sha256))fail();
      files.set(file.path,{path:file.path,bytes:file.bytes,sha256:file.sha256.toLowerCase()});
    }
    if(files.size!==26 || ![...letters].every(letter=>files.has(letter+'.jsonl')))fail();
    const result={files,version:await digest(bytes),totalBytes:[...files.values()].reduce((sum,file)=>sum+file.bytes,0),
      entryCount:Number.isSafeInteger(parsed.statistics?.entryCount)?parsed.statistics.entryCount:0};
    check(signal);
    if(fetched && cache)try{await cache.put(manifestURL,new Response(bytes,{headers:{'Content-Type':'application/json','X-Tuci-SHA256':result.version}}));}catch{}
    check(signal);manifestValue=result;return result;
  }
  async function disk(info,required=false){
    try{if(!cacheStorage?.open)throw Error();return await cacheStorage.open('tuci:dictionary:'+base.href+':'+info.version);}
    catch{if(required)throw new Error('此浏览器暂不允许保存离线词典，请检查网站存储权限后重试。');return null;}
  }
  async function verify(bytes,asset,signal){check(signal);if(bytes.byteLength!==asset.bytes || await digest(bytes)!==asset.sha256)fail();check(signal);}
  async function assetBytes(asset,info,signal,required=false){
    const cache=await disk(info,required),url=assetURL(asset.path);let stored;
    if(cache){try{stored=await cache.match(url);}catch{}}
    if(stored){
      try{const bytes=new Uint8Array(await stored.arrayBuffer());await verify(bytes,asset,signal);return bytes;}
      catch(error){check(signal);try{await cache.delete(url);}catch{};}
    }
    const bytes=await fetchBytes(url,signal,asset.bytes);await verify(bytes,asset,signal);
    if(cache){
      try{await cache.put(url,new Response(bytes,{headers:{'Content-Type':'application/x-ndjson; charset=utf-8','Content-Length':String(bytes.byteLength),'X-Tuci-SHA256':asset.sha256}}));}
      catch{if(required)throw new Error('离线词典未能保存，可能是存储空间不足。已下载分片仍会保留，请稍后重试。');}
    }
    check(signal);return bytes;
  }
  async function lookup(word,{signal}={}){
    const target=normalWord(word),letter=target[0];check(signal);
    const task=loadTail.catch(()=>{}).then(async()=>{
      check(signal);
      let words=memory.get(letter);
      if(words){memory.delete(letter);memory.set(letter,words);}
      else{
        // Evict before building another parsed map, so even a large incoming
        // letter does not leave three parsed shards alive at the same time.
        while(memory.size>=memoryLimit)memory.delete(memory.keys().next().value);
        const info=await manifest(signal),bytes=await assetBytes(info.files.get(letter+'.jsonl'),info,signal);
        let text;try{text=new TextDecoder('utf-8',{fatal:true}).decode(bytes);}catch{fail();}
        words=await parseDictionaryShard(text,letter,{signal,yieldEvery,...(yieldControl?{yieldControl}:{})});check(signal);memory.set(letter,words);
        while(memory.size>memoryLimit)memory.delete(memory.keys().next().value);
      }
      check(signal);const row=words.get(target);return row?dictionaryEntry(row):null;
    });
    loadTail=task.then(()=>{},()=>{});return callerResult(task,signal);
  }
  function progress(completed,bytes,info,storageAvailable=true,letter=''){
    return {completed,downloaded:completed,total:26,bytes,downloadedBytes:bytes,totalBytes:info.totalBytes,
      entryCount:info.entryCount,storageAvailable,available:storageAvailable,complete:completed===26,letter};
  }
  async function status({signal}={}){
    const info=await manifest(signal),cache=await disk(info);let completed=0,bytes=0;
    if(cache)for(const asset of info.files.values()){
      check(signal);const url=assetURL(asset.path);let cached;try{cached=await cache.match(url);}catch{}
      if(cached)try{await verify(new Uint8Array(await cached.arrayBuffer()),asset,signal);completed++;bytes+=asset.bytes;}
      catch(error){check(signal);try{await cache.delete(url);}catch{}}
    }
    check(signal);return progress(completed,bytes,info,!!cache);
  }
  async function downloadAll({signal,onProgress=()=>{}}={}){
    const info=await manifest(signal);await disk(info,true);let completed=0,bytes=0;
    onProgress(progress(completed,bytes,info));
    for(const asset of info.files.values()){
      check(signal);await assetBytes(asset,info,signal,true);completed++;bytes+=asset.bytes;onProgress(progress(completed,bytes,info,true,asset.path[0]));
    }
    check(signal);return progress(completed,bytes,info);
  }
  return {lookup,status,downloadAll,memoryLetters:()=>[...memory.keys()]};
}
let shared;
export function offlineDictionary(){return shared ||= createOfflineDictionary();}
export function lookupOffline(word,options){return offlineDictionary().lookup(word,options);}
export function downloadDictionary(options){return offlineDictionary().downloadAll(options);}
export function dictionaryStatus(options){return offlineDictionary().status(options);}
