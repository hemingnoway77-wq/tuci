import test from 'node:test';
import assert from 'node:assert/strict';
import {createLoginLifecycle,playLoginTransition,settleLoginViewport,loginStages} from '../login-transition.js';
const deferred=()=>{let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};};
const tick=async()=>{for(let i=0;i<8;i++)await Promise.resolve();};
function elements(){return {
  surface:{style:{},animate(){},getBoundingClientRect:()=>({top:0,width:390,height:844})},
  word:{style:{},getBoundingClientRect:()=>({top:560,width:55,height:32})},line:{style:{}}
};}
function animationRecorder(){const calls=[];return {calls,animate:(element,frames,options)=>{
  const pending=deferred(),record={element,frames,options,canceled:false,pending};calls.push(record);
  return {finished:pending.promise,cancel(){record.canceled=true;pending.reject(new DOMException('canceled','AbortError'));}};
}};}
test('login succeeds only after authentication, private store preparation and transition finish in order',async()=>{
  const flow=createLoginLifecycle(),auth=deferred(),prepare=deferred(),animation=deferred(),calls=[];
  const result=flow.run({authenticate:()=>{calls.push('authenticate');return auth.promise;},prepare:()=>{calls.push('prepare');return prepare.promise;},transition:()=>{calls.push('transition');return animation.promise;},reveal:()=>calls.push('reveal')});
  assert.deepEqual(calls,['authenticate']);auth.resolve({userId:'A'});await tick();assert.deepEqual(calls,['authenticate','prepare']);
  prepare.resolve({userId:'A'});await tick();assert.deepEqual(calls,['authenticate','prepare','transition']);
  animation.resolve(true);assert.equal(await result,true);assert.deepEqual(calls,['authenticate','prepare','transition','reveal']);assert.equal(flow.active,false);
});
test('authentication failure keeps the form error path and never prepares or animates the main page',async()=>{
  const flow=createLoginLifecycle(),calls=[];
  assert.equal(await flow.run({authenticate:async()=>{throw Error('invalid password');},prepare:()=>assert.fail('must not prepare'),transition:()=>assert.fail('must not animate'),reveal:()=>assert.fail('must not reveal'),onError:error=>calls.push(error.message)}),false);
  assert.deepEqual(calls,['invalid password']);
});
test('a canceled late authentication cannot reveal or replace a later successful account',async()=>{
  const flow=createLoginLifecycle(),first=deferred(),revealed=[],errors=[];
  const stale=flow.run({authenticate:()=>first.promise,prepare:async value=>value,reveal:value=>revealed.push(value.userId),onError:error=>errors.push(error.message)});
  const current=flow.run({authenticate:async()=>({userId:'B'}),prepare:async value=>value,reveal:value=>revealed.push(value.userId)});
  assert.equal(await current,true);first.resolve({userId:'A'});assert.equal(await stale,false);assert.deepEqual(revealed,['B']);assert.deepEqual(errors,[]);
});
test('canceling preparation or a moving curtain disposes only its own prepared account',async()=>{
  const flow=createLoginLifecycle(),pending=deferred(),disposed=[];
  const work=flow.run({authenticate:async()=>({userId:'A'}),prepare:()=>pending.promise,reveal:()=>assert.fail('late reveal'),dispose:value=>disposed.push(value.userId)});
  await tick();flow.cancel();pending.resolve({userId:'A'});assert.equal(await work,false);assert.deepEqual(disposed,['A']);
  const curtain=deferred();const second=flow.run({authenticate:async()=>({userId:'B'}),prepare:async value=>value,transition:()=>curtain.promise,reveal:()=>assert.fail('canceled curtain revealed'),dispose:value=>disposed.push(value.userId)});
  await tick();flow.cancel();curtain.resolve(true);assert.equal(await second,false);assert.deepEqual(disposed,['A','B']);
});
test('the four visible phases animate only transform and opacity and lift the entire login surface',async()=>{
  const nodes=elements(),recorder=animationRecorder(),stages=[];
  const result=playLoginTransition({...nodes,animate:recorder.animate,onStage:stage=>stages.push(stage)});
  assert.deepEqual(stages,['sink']);recorder.calls[0].pending.resolve();await tick();assert.deepEqual(stages,['sink','collapse']);
  assert.equal(recorder.calls.length,3);recorder.calls[1].pending.resolve();recorder.calls[2].pending.resolve();await tick();assert.equal(stages.at(-1),'stretch');
  recorder.calls[3].pending.resolve();await tick();assert.equal(stages.at(-1),'lift');assert.equal(recorder.calls[4].element,nodes.surface);
  recorder.calls[4].pending.resolve();assert.equal(await result,true);assert.deepEqual(stages,['sink','collapse','stretch','lift','ready']);
  assert.equal(loginStages.reduce((sum,stage)=>sum+stage.duration,0),1020);
  for(const call of recorder.calls)for(const frame of call.frames)assert.ok(Object.keys(frame).every(key=>['transform','opacity','offset'].includes(key)));
  assert.ok(recorder.calls.every(call=>call.canceled));assert.equal(nodes.surface.style.transform,'translateY(-846px)');assert.equal(nodes.line.style.top,'583px');
});
test('aborting the morph cancels its animation handles and prevents line stretch or reveal',async()=>{
  const controller=new AbortController(),nodes=elements(),recorder=animationRecorder(),stages=[];
  const result=playLoginTransition({...nodes,signal:controller.signal,animate:recorder.animate,onStage:stage=>stages.push(stage)});
  recorder.calls[0].pending.resolve();await tick();controller.abort();assert.equal(await result,false);
  assert.deepEqual(stages,['sink','collapse']);assert.ok(recorder.calls.every(call=>call.canceled));assert.equal(recorder.calls.length,3);
});
test('reduced motion and offline readiness reveal directly without geometry reads or animations',async()=>{
  for(const mode of [{reducedMotion:true},{offline:true}]){
    const stages=[];assert.equal(await playLoginTransition({...mode,surface:{animate(){},getBoundingClientRect:()=>assert.fail('must not measure')},onStage:value=>stages.push(value),animate:()=>assert.fail('must not animate')}),true);assert.deepEqual(stages,['ready']);
  }
});
test('keyboard blur precedes stable visual viewport measurement without long blocking timers',async()=>{
  let time=0,blurred=false,frames=0;const view={innerWidth:390,innerHeight:844,visualViewport:{width:390,height:500,offsetTop:0}};
  await settleLoginViewport({view,document:{activeElement:{tagName:'INPUT',blur(){blurred=true;}}},now:()=>time,frame:callback=>queueMicrotask(()=>{assert.ok(blurred);time+=16;frames++;if(frames===3)view.visualViewport.height=844;callback();})});
  assert.ok(time>=180 && time<=450);assert.equal(view.visualViewport.height,844);
});

test('a compositor cancellation without account invalidation safely reveals the already prepared main',async()=>{
  const nodes=elements(),recorder=animationRecorder(),stages=[];
  const result=playLoginTransition({...nodes,animate:recorder.animate,onStage:stage=>stages.push(stage)});
  recorder.calls[0].pending.reject(new DOMException('browser canceled animation','AbortError'));
  assert.equal(await result,true);assert.deepEqual(stages,['sink','ready']);assert.equal(nodes.surface.style.transform,'translateY(-846px)');assert.ok(recorder.calls.every(call=>call.canceled));
});
