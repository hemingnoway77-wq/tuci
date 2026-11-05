import { analysisCard, capturePrompt, manualCard, matches, normalWord, offlineArticles, parts, pickDaily, regionHashes } from './learning-data.js';

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}
function button(text, className, handler) {
  const node = el('button',className,text); node.type='button';
  node.addEventListener('click',handler); return node;
}
function label(title, field) {
  const node=el('label','field'), text=el('span','',title);
  node.append(text,field); return node;
}
function readCache(key, fallback, storage=localStorage) {
  try {return JSON.parse(storage.getItem(key)) ?? fallback;} catch {return fallback;}
}
function setCache(key,value,storage=localStorage) {
  try {storage.setItem(key,JSON.stringify(value));} catch { /* Storage pressure cannot block reading. */ }
}
function dayKey(date=new Date()) {
  return [date.getFullYear(),date.getMonth()+1,date.getDate()].map((n,i)=>i?String(n).padStart(2,'0'):String(n)).join('-');
}

export async function requestAnalysis(settings,input,context,signal,onReceiving=()=>{}) {
  let url;
  try {url = new URL(settings.aiEndpoint);} catch {throw Error('请到设置中填写 AI 接口地址。');}
  if (url.protocol!=='https:' || url.search || url.hash || url.username || url.password) throw Error('AI 接口需要使用不带账号或查询参数的 HTTPS 地址。');
  url.pathname = url.pathname.replace(/\/+$/,'');
  if (!url.pathname.toLowerCase().endsWith('/chat/completions')) url.pathname+='/chat/completions';
  const model=(settings.aiModel||'').trim(), key=(settings.aiKey||'').trim();
  if (!model || !key) throw Error('请先在设置中填写 API Key 和模型名称，也可以手动收录。');
  const payload = {model, stream:true, messages:[{role:'system',content:capturePrompt}, {role:'user',content:JSON.stringify({word:input,context})}]};
  if (url.hostname==='api.deepseek.com' && ['deepseek-flash','deepseek-v4-pro'].includes(model.toLowerCase())) payload.thinking={type:'disabled'};
  for (const stream of [true, false]) {
    let res;
    try {
      res=await fetch(url.href,{method:'POST',headers:{'Content-Type':'application/json',Authorization:'Bearer '+key},
        body:JSON.stringify({...payload,stream}),credentials:'omit',redirect:'error',cache:'no-store',referrerPolicy:'no-referrer',signal});
    } catch (error) {
      if (signal.aborted) throw error;
      throw Error('无法连接 AI 接口。请检查网络和地址，并确认服务允许浏览器跨域请求。');
    }
    if (!res.ok) {
      if (stream && [400,422].includes(res.status)) {
        const reason=await responseText(res);
        if (/stream.{0,30}(?:not supported|unsupported|not allowed|unrecognized|不支持)|(?:unsupported|unknown|unrecognized|does not support|不支持).{0,30}stream/i.test(reason)) continue;
      }
      if ([401,403].includes(res.status)) throw Error('API 鉴权失败，请检查设置里的 Key 和权限。');
      if (res.status===429) throw Error('AI 请求受限或额度不足，请稍后再试。');
      throw Error('AI 接口返回 HTTP '+res.status+'，请检查接口和模型。');
    }
    const raw=await responseText(res,onReceiving);
    try {
      let content;
      if((res.headers.get('Content-Type')||'').toLowerCase().includes('text/event-stream')) {
        content=streamContent(raw);
      } else {
        const root=JSON.parse(raw), choice=root.choices?.[0];
        if(root.error || root.choices.length!==1 || choice.finish_reason!=='stop' || choice.message.refusal || typeof choice.message.content!=='string') throw Error();
        content=choice.message.content;
      }
      const json=content.trim().replace(/^\x60{3}(?:json)?\s*\n/i,'').replace(/\n?\x60{3}$/,'');
      return JSON.parse(json.split(key).join('[已隐藏]'));
    }catch{throw Error('AI 返回的词卡格式不完整，请重试。');}
  }
  throw Error('AI 接口未返回有效词卡。');
}
async function responseText(res,onReceiving=()=>{}) {
  if(Number(res.headers.get('Content-Length'))>1048576 || !res.body) throw Error('AI 返回的数据无效或过大。');
  const reader=res.body.getReader(), decoder=new TextDecoder();
  let raw='', size=0;
  try {
    while(true){
      const {done,value}=await reader.read();
      if(done) break;
      if(!size) onReceiving();
      size+=value.byteLength;
      if(size>1048576){await reader.cancel();throw Error('AI 返回的数据过大。');}
      raw+=decoder.decode(value,{stream:true});
    }
    return raw+decoder.decode();
  }finally{reader.releaseLock();}
}
export function streamContent(raw){
  let content='', stopped=false;
  for(const event of raw.replace(/\r\n?/g,'\n').split(/\n\n+/)){
    const payload=event.split('\n').filter(line=>line.startsWith('data:')).map(line=>line.slice(5).replace(/^ /,'')).join('\n');
    if(!payload) continue;
    if(payload==='[DONE]'){
      if(!stopped || !content) break;
      return content;
    }
    const root=JSON.parse(payload);
    if(root.error || !Array.isArray(root.choices)) throw Error('流式响应无效。');
    if(!root.choices.length) continue;
    const choice=root.choices[0];
    if(stopped || root.choices.length!==1 || (choice.index??0)!==0 || !choice.delta || choice.delta.refusal) throw Error('流式响应无效。');
    if(choice.delta.content!=null) {
      if(typeof choice.delta.content!=='string') throw Error('流式响应无效。');
      content+=choice.delta.content;
    }
    if(choice.finish_reason!=null){
      if(choice.finish_reason!=='stop') throw Error('流式响应截断。');
      stopped=true;
    }
  }
  throw Error('流式响应没有正常结束。');
}

export function renderCapture(ctx, initial) {
  const {root}=ctx, scope=ctx.scope(), draftKey='tuci:capture:'+scope;
  const hasAI=Boolean(ctx.settings().aiKey?.trim() && ctx.settings().aiModel?.trim() && ctx.settings().aiEndpoint?.trim());
  const primaryLabel=hasAI?'解析并保存':'保存词条';
  const draft=readCache(draftKey,{},sessionStorage);
  const head=el('header','page-head learn-capture-head'); head.append(el('h1','','收录生词'));
  const form=el('form','panel learn-form');
  const word=el('input','learn-word');
  Object.assign(word,{placeholder:'输入英文单词',maxLength:80,autocapitalize:'none',autocomplete:'off',spellcheck:false,
    value:typeof initial==='string'? initial : (initial?.word ?? draft.word ?? '')});
  word.setAttribute('lang','en'); word.setAttribute('enterkeyhint','next');
  const sentence=el('textarea','learn-sentence');
  Object.assign(sentence,{placeholder:'粘贴遇见这个词时的句子',rows:3,maxLength:2000,value:initial ? initial.context||'' : draft.context||''});
  const meaning=el('textarea'); Object.assign(meaning,{rows:2,maxLength:2000,placeholder:'写下你记得的意思',value:draft.definition||''});
  const part=el('select');
  for(const [code,[name]] of Object.entries(parts)){ const op=el('option','',name);op.value=code;part.append(op); }
  part.value=Object.hasOwn(parts,draft.pos)?draft.pos:'other';

  const feedback=el('div','learn-feedback'); feedback.setAttribute('role','status');
  const actions=el('div','learn-actions'), submit=el('button','button',primaryLabel); submit.type='submit';
  const cancel=button('取消','secondary',()=>request?.abort()); cancel.hidden=true;
  const manual=el('details','learn-manual'); const summary=el('summary','',hasAI?'手动收录':'补充释义与词性');
  const saveManual=button('保存词条','secondary',()=>begin(true));
  manual.append(summary,label('释义（选填）',meaning),label('词性',part));
  if(hasAI) manual.append(saveManual);
  const reconfigure=button('设置 AI 接口','learn-text',()=>ctx.navigate('settings'));
  actions.append(submit,cancel);
  if(!hasAI) actions.append(reconfigure);
  const wordLabel=label('英文单词',word);
  form.append(wordLabel,label('原句（选填）',sentence), ...(hasAI?[actions,manual]:[manual,actions]));
  root.replaceChildren(head,form,feedback);

  let request=null, active=true, pending=null;
  function storeDraft(){setCache(draftKey,{word:word.value,context:sentence.value,definition:meaning.value,pos:part.value},sessionStorage);}
  form.addEventListener('input',()=>{pending=null;feedback.replaceChildren();storeDraft();});
  function existing(target, prefix='已收录') {
    const found=matches(target,ctx.notes());
    if(!found.length) return false;
    feedback.replaceChildren(el('p','',prefix+' · '+target));
    for (const n of found) feedback.append(button(found.length===1?'打开笔记 →':n.path,'learn-text',()=>ctx.navigate('library',n.path)));
    return true;
  }
  function busy(on, manualMode){
    submit.disabled=saveManual.disabled=word.disabled=sentence.disabled=meaning.disabled=part.disabled=on;
    cancel.hidden=!on || manualMode;
    submit.textContent=on?(manualMode?'正在保存…':'正在解析…'):primaryLabel;
  }
  async function persist(card,generated) {
    if (ctx.scope()!==scope || !active) return;
    const managed=generated?await regionHashes(card.markdown):undefined;
    if (ctx.scope()!==scope || !active) return;
    if (existing(card.word)) return;
    pending={card,generated};
    await ctx.saveNote(card.path,card.markdown,managed,null);
    pending=null;
    if (!active) return;
    word.value=card.word;
    setCache(draftKey,{},sessionStorage);
    feedback.replaceChildren(el('p','','已保存 · '+card.word),button('打开笔记 →','learn-text',()=>ctx.navigate('library',card.path)));
  }
  async function begin(manualMode) {
    if (request) return;
    const controller=new AbortController();
    let timeout=false;
    const timer=setTimeout(()=>{timeout=true;controller.abort();},90000);
    try {
      const target=normalWord(word.value);
      if (existing(target)) return;
      request=controller; busy(true,manualMode); feedback.replaceChildren();
      const settings=ctx.settings(), context=sentence.value.trim();
      if (!manualMode && !settings.aiKey) {
        feedback.append(el('p','','设置好 AI 接口后就能自动生成词卡，也可以先手动收录。'),button('前往设置','learn-text',()=>ctx.navigate('settings')));
        return;
      }
      const card=manualMode?manualCard(target,meaning.value,context,part.value,settings.classify!==false)
         : analysisCard(await requestAnalysis(settings,target,context,controller.signal,()=>{if(active)submit.textContent='正在生成词卡…';}),target,context,settings.classify!==false,ctx.notes());
      if(controller.signal.aborted || !active) return;
      await persist(card,!manualMode);
    } catch(error) {
      if(!active) return;
      feedback.replaceChildren(el('p','',controller.signal.aborted ? (timeout?'请求超时，请重试。':'已取消，输入已保留。') : error.message));
      if(pending) {
        const held=pending;
        const retry=button('重试保存','secondary',async()=>{
          retry.disabled=true;
          try {await persist(held.card,held.generated);} catch(err){ctx.toast(err.message); retry.disabled=false;}
        });
        feedback.append(retry);
      }
    } finally {clearTimeout(timer);request=null;if(active)busy(false,manualMode);}
  }
  form.addEventListener('submit',e=>{e.preventDefault();begin(!hasAI);});
  return ()=>{active=false;request?.abort();};
}

const readAPI='/tuci-sync/v1/reading';
const editionKey='tuci:reading:edition-v1', bodyKey='tuci:reading:bodies-v1';
const catNames={general:'泛读',technology:'科技'};
function safeStory(a) {
  return typeof a?.id==='string' && /^[a-f\d]{32}$/.test(a.id) && typeof a.title==='string' &&
    typeof a.summary==='string' && typeof a.source==='string' && Object.hasOwn(catNames,a.category) &&
    typeof a.url==='string' && /^https:\/\/(?:www\.sciencedaily\.com|blog\.cloudflare\.com|www\.bbc\.com|www\.bbc\.co\.uk)\//i.test(a.url);
}

export function renderReading(ctx) {
  const {root}=ctx;
  const cache=readCache(editionKey,{}), today=dayKey();
  let articles=(Array.isArray(cache.articles)?cache.articles:[]).filter(safeStory);
  let filter='all',current=null,alive=true,controller=null,loading=false;
  let note=cache.day!==today&&articles.length?'显示上次保存的推荐。':'';
  const openKey='tuci:reading:open:'+ctx.scope();
  function selection(story,paragraph,ev,tools) {
    const selected=window.getSelection();
    const range=selected?.rangeCount && !selected.isCollapsed ? selected.getRangeAt(0) : null;
    let raw=range && paragraph.contains(range.commonAncestorContainer) ? selected.toString().trim() : '';
    let offset=range ? range.startOffset:0;
    if (!raw) {
      const caret=document.caretRangeFromPoint?.(ev.clientX,ev.clientY);
      const position=!caret && document.caretPositionFromPoint?.(ev.clientX,ev.clientY);
      const node=caret?.startContainer || position?.offsetNode;
      if(node?.nodeType!==Node.TEXT_NODE || !paragraph.contains(node)) return;
      offset=caret?.startOffset ?? position.offset;
      for(const match of node.textContent.matchAll(/[a-z]+(?:[-'’][a-z]+)*/gi)) {
        if(match.index<=offset && offset<match.index+match[0].length){raw=match[0]; break;}
      }
    }
    let target; try {target=normalWord(raw);} catch{return;}
    const whole=paragraph.textContent, left=whole.slice(0,offset).search(/[^.!?]*$/), right=whole.slice(offset).search(/[.!?](?:\s|$)/);
    const context=whole.slice(Math.max(0,left),right<0 ? undefined : offset+right+1).trim().slice(0,2000);
    tools.hidden=false;
    tools.replaceChildren(el('strong','',target),button('收录此词','button',()=>{
      setCache(openKey,story.id,sessionStorage);
      setCache('tuci:capture:'+ctx.scope(),{word:target,context},sessionStorage);
      ctx.navigate('capture');
    }),button('关闭','learn-text',()=>{tools.hidden=true;}));
  }
  function bodyView(story,body,message) {
    const back=button('← 每日阅读','learn-text',()=>{controller?.abort();current=null;setCache(openKey,null,sessionStorage);list();});
    const head=el('header','learn-article-head');head.append(back,el('p','learn-meta',catNames[story.category]+' · '+story.source),el('h1','',story.title));
    const content=el('article','learn-prose'),tools=el('aside','learn-word-tools'); tools.hidden=true;
    const link=story.url ? el('a','learn-text','查看来源原文 ↗') : null;
    if(link){link.href=story.url;link.target='_blank';link.rel='noopener noreferrer';}
    if(message) content.append(el('p','learn-message',message));
    for(const paragraph of body){
      const p=el('p','',paragraph);p.lang='en';
      p.addEventListener('click',ev=>selection(story,p,ev,tools));content.append(p);
    }
    const tip=el('p','learn-reader-tip','轻点正文中的英文单词即可收录。');
    root.replaceChildren(head,...(body.length?[tip]:[]),content,...(link?[link]:[]),tools);
  }
  async function open(story) {
    current=story.id;
    setCache(openKey,story.id,sessionStorage);
    if(story.paragraphs){bodyView(story,story.paragraphs,'');window.scrollTo(0,0);return;}
    const bodies=readCache(bodyKey,[]);
    const saved=Array.isArray(bodies) && bodies.find(b=>b.id===story.id && Array.isArray(b.paragraphs));
    if(saved){bodyView(story,saved.paragraphs,'');window.scrollTo(0,0);return;}
    controller?.abort();controller=new AbortController();
    const request=controller, timeout=setTimeout(()=>request.abort(),14000);
    bodyView(story,[story.summary].filter(Boolean),'正在载入原文…');window.scrollTo(0,0);
    try {
      const res=await fetch(readAPI+'/'+story.id,{signal:request.signal,credentials:'omit'});
      if(!res.ok) throw Error();
      const data=await res.json();
      if(!Array.isArray(data.paragraphs) || !data.paragraphs.length || !data.paragraphs.every(p=>typeof p==='string')) throw Error();
      if(!alive || current!==story.id) return;
      const latest=readCache(bodyKey,[]);
      setCache(bodyKey,[{id:story.id,paragraphs:data.paragraphs},...(Array.isArray(latest)?latest:[]).filter(b=>b.id!==story.id)].slice(0,8));
      bodyView(story,data.paragraphs,'');
    }catch{
      if(alive && current===story.id) bodyView(story,[story.summary].filter(Boolean),'暂时无法载入全文，下面是来源摘要。');
    }finally{clearTimeout(timeout);}
  }
  function list(){
    if(!alive || current) return;
    const head=el('header','page-head learn-reading-head'), title=el('div');
    title.append(el('p','learn-date',new Intl.DateTimeFormat('zh-CN',{month:'long',day:'numeric',weekday:'long'}).format(new Date())),el('h1','','每日阅读'));
    const refresh=button(loading?'更新中…':'刷新推荐','learn-text',()=>load(true)); refresh.disabled=loading; head.append(title,refresh);
    const tabs=el('div','learn-filters');tabs.setAttribute('aria-label','文章分类');
    for(const [key,name] of [['all','全部'],...Object.entries(catNames)]) {
      const b=button(name,'',()=>{filter=key;list();});b.setAttribute('aria-pressed',String(filter===key));tabs.append(b);
    }
    const stories=articles.length ? [...articles,...offlineArticles.filter(a=>!articles.some(s=>s.category===a.category))] : offlineArticles;
    const series=el('div','learn-stories');
    for(const story of stories.filter(a=>filter==='all'||a.category===filter)){
      const card=button('','learn-story',()=>open(story));
      const label=el('span','learn-meta',catNames[story.category]+' · '+story.source);
      const h=el('strong','',story.title);h.lang='en';
      card.append(label,h,el('span','learn-summary',story.summary)); series.append(card);
    }
    root.replaceChildren(head,tabs,...(note?[el('p','learn-message',note)]:[]),series);
  }
  async function load(refresh=false){
    if(loading || (!refresh && cache.day===today && articles.length)) return;
    loading=true;list();
    const request=new AbortController(); const timeout=setTimeout(()=>request.abort(),14500);
    try{
      const res=await fetch(readAPI,{cache:'no-store',credentials:'omit',signal:request.signal});
      if(!res.ok) throw Error();
      const data=await res.json(), pool=data.stories.filter(safeStory);
      if(!pool.length) throw Error();
      if(!alive) return;
      if(cache.day!==today || !articles.length) articles=pickDaily(pool,today);
      note=''; setCache(editionKey,{day:today,articles});
    }catch{if(alive) note=articles.length?'来源暂不可用，显示上次保存的推荐。':'来源暂不可用，可以先读兔词原创短文。';}
    finally{clearTimeout(timeout);loading=false;list();}
  }
  const previousId=readCache(openKey,null,sessionStorage), previous=[...articles,...offlineArticles].find(a=>a.id===previousId);
  if(previous) open(previous); else list();
  load();
  return ()=>{alive=false;controller?.abort();};
}
