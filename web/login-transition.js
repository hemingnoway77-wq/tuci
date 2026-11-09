const canceled=()=>new DOMException('登录已取消。','AbortError');
const check=signal=>{if(signal?.aborted)throw canceled();};
function withAbort(promise,signal){
  check(signal);if(!signal)return promise;
  return new Promise((resolve,reject)=>{
    const abort=()=>reject(canceled());signal.addEventListener('abort',abort,{once:true});
    Promise.resolve(promise).then(resolve,reject).finally(()=>signal.removeEventListener('abort',abort));
  });
}
// Every async boundary belongs to one attempt; a late response can never reveal another session.
export function createLoginLifecycle(){
  let generation=0,controller=null;
  function cancel(){generation++;controller?.abort();controller=null;}
  async function run({authenticate,prepare,transition=async()=>true,reveal,onError=()=>{},dispose=()=>{}}){
    cancel();const id=generation,own=new AbortController();controller=own;let prepared,revealed=false;
    const current=()=>id===generation && !own.signal.aborted;
    try{
      const account=await authenticate(own.signal);check(own.signal);
      prepared=await prepare(account,own.signal);check(own.signal);
      const finished=await transition(prepared,own.signal);check(own.signal);
      if(!finished || !current())return false;
      reveal(prepared);revealed=true;return true;
    }catch(error){if(current() && error.name!=='AbortError')onError(error);return false;}
    finally{if(!revealed && prepared)dispose(prepared);if(controller===own)controller=null;}
  }
  return {run,cancel,get active(){return !!controller;}};
}
export async function settleLoginViewport({signal,view=globalThis.window,document=globalThis.document,now=()=>performance.now(),frame=callback=>requestAnimationFrame(callback)}={}){
  check(signal);const focused=document?.activeElement,keyboard=focused && /^(INPUT|TEXTAREA|SELECT)$/.test(focused.tagName);
  focused?.blur?.();const started=now();let previous='',stable=0;
  for(;;){
    await withAbort(new Promise(resolve=>frame(resolve)),signal);check(signal);
    const viewport=view.visualViewport,key=[viewport?.width||view.innerWidth,viewport?.height||view.innerHeight,viewport?.offsetTop||0].join(':');
    stable=key===previous?stable+1:0;previous=key;
    if(stable>=2 && now()-started>=(keyboard?180:0) || now()-started>=450)return;
  }
}
export const loginStages=Object.freeze([
  {name:'sink',duration:150}, {name:'collapse',duration:160}, {name:'stretch',duration:260}, {name:'lift',duration:450}
]);
export async function playLoginTransition({surface,word,line,signal,reducedMotion=false,offline=false,onStage=()=>{},animate=(element,frames,options)=>element.animate(frames,options)}={}){
  check(signal);if(reducedMotion || offline || typeof surface?.animate!=='function'){onStage('ready');return true;}
  const surfaceRect=surface.getBoundingClientRect(),wordRect=word.getBoundingClientRect();
  const width=Math.max(1,surfaceRect.width),height=Math.max(1,surfaceRect.height),short=Math.min(34,Math.max(20,wordRect.width*.6))/width;
  line.style.top=(wordRect.top-surfaceRect.top+wordRect.height/2+7)+'px';line.style.opacity='0';line.style.transform='scaleX('+short+')';
  const animations=new Set(),abort=()=>{for(const animation of animations)animation.cancel();};signal?.addEventListener('abort',abort,{once:true});
  async function run(element,frames,duration,easing){
    check(signal);const animation=animate(element,frames,{duration,easing,fill:'forwards'});animations.add(animation);
    await withAbort(animation.finished,signal);check(signal);
  }
  try{
    onStage('sink');await run(word,[{transform:'translateY(0px)',opacity:1},{transform:'translateY(7px)',opacity:1}],150,'cubic-bezier(.32,0,.67,0)');
    onStage('collapse');await Promise.all([
      run(word,[{transform:'translateY(7px) scale(1,1)',opacity:1},{transform:'translateY(7px) scale(.6,.045)',opacity:0}],160,'cubic-bezier(.4,0,.2,1)'),
      run(line,[{opacity:0,offset:0},{opacity:0,offset:.65},{opacity:1,offset:1}],160,'linear')
    ]);
    onStage('stretch');await run(line,[{transform:'scaleX('+short+')',opacity:1},{transform:'scaleX(1)',opacity:1}],260,'cubic-bezier(.22,1,.36,1)');
    onStage('lift');await run(surface,[{transform:'translateY(0px)',opacity:1},{transform:'translateY(-'+(height+2)+'px)',opacity:1}],450,'cubic-bezier(.76,0,.24,1)');
    surface.style.transform='translateY(-'+(height+2)+'px)';onStage('ready');return true;
  }catch(error){
    if(signal?.aborted)return false;
    // A browser may cancel a compositor animation while backgrounding. The authenticated
    // main is already ready, so finish directly instead of leaving a stranded curtain.
    if(error.name==='AbortError'){surface.style.transform='translateY(-'+(height+2)+'px)';onStage('ready');return true;}
    throw error;
  }
  finally{signal?.removeEventListener('abort',abort);for(const animation of animations)animation.cancel();}
}
