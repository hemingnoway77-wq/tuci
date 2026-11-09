export async function readingKey(url) {
  const parsed=new URL(url);
  if(parsed.protocol!=="https:" || parsed.username || parsed.password)throw new Error("阅读链接无效。");
  const bytes=await crypto.subtle.digest("SHA-256",new TextEncoder().encode(url));
  return "reading/"+Array.from(new Uint8Array(bytes),n=>n.toString(16).padStart(2,"0")).join("");
}
export function readingPosition(paragraphs,scrollY,height,viewport) {
  const progress=Math.max(0,Math.min(1,scrollY/Math.max(1,height-viewport)));
  let index=0;
  for(let i=0;i<paragraphs.length;i++)if(paragraphs[i].getBoundingClientRect().top<=110)index=i;
  return {progress,anchor:"p:"+index};
}
export function mountReadingProgress(ctx,story,content,{paused=()=>false,restore=true}={}) {
  if(!story.url || !ctx.setState || !ctx.stateEntries)return ()=>{};
  const scope=ctx.scope();let key=null,active=true,ready=false,timer;
  const paragraphs=[...content.querySelectorAll('p')];
  const snapshot=()=>({...readingPosition(paragraphs,window.scrollY,document.documentElement.scrollHeight,window.innerHeight),url:story.url,title:story.title});
  const write=value=>{if(active && ready && key && !paused() && ctx.scope()===scope)ctx.setState(key,value).catch(()=>{});};
  const scroll=()=>{clearTimeout(timer);if(!paused())timer=setTimeout(()=>write(snapshot()),700);};
  const hide=()=>{if(document.visibilityState==='hidden')write(snapshot());};
  readingKey(story.url).then(value=>{
    if(!active || ctx.scope()!==scope)return;key=value;
    const saved=ctx.stateEntries().find(row=>row.key===key)?.value;
    requestAnimationFrame(()=>{
      if(!active || ctx.scope()!==scope)return;
      if(restore && saved && !paused()){
        const anchor=/^p:(\d+)$/.exec(saved.anchor||''),p=anchor && paragraphs[Number(anchor[1])];
        if(p)window.scrollTo(0,Math.max(0,window.scrollY+p.getBoundingClientRect().top-90));
        else window.scrollTo(0,Math.max(0,Number(saved.progress)||0)*Math.max(0,document.documentElement.scrollHeight-window.innerHeight));
      }
      ready=true;window.addEventListener('scroll',scroll,{passive:true});document.addEventListener('visibilitychange',hide);
    });
  }).catch(()=>{});
  const cleanup=()=>{clearTimeout(timer);write(snapshot());active=false;window.removeEventListener('scroll',scroll);document.removeEventListener('visibilitychange',hide);};
  cleanup.flush=()=>{clearTimeout(timer);write(snapshot());};
  return cleanup;
}
