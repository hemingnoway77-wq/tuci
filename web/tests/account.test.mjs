import test from 'node:test';
import assert from 'node:assert/strict';
import {accountRequest,checkedIdentity,themeNames,localThemes} from '../account.js';
import {readingKey,readingPosition} from '../reading-state.js';
test('account replies expose identity only, without persisting a bearer credential',()=>{
  const value=checkedIdentity({userId:'u',vaultId:'v',username:'名字',deviceId:'d',token:'must-not-keep'});
  assert.deepEqual(Object.keys(value),['userId','vaultId','username','deviceId','canInvite']);assert.throws(()=>checkedIdentity({userId:'u'}));
  for(const [local,wire]of Object.entries(themeNames))assert.equal(localThemes[wire],local);
});
test('browser account operations use same-origin cookies and explicit account fields',async t=>{
  const calls=[];t.mock.method(globalThis,'fetch',async(url,request)=>{calls.push({url,request});return Response.json({userId:'u'});});
  await accountRequest('login',{username:'alice',password:'fixture-password',deviceName:'iPhone',browser:true});
  const {url,request}=calls[0];assert.equal(url,'/tuci-sync/v1/account/login');assert.equal(request.credentials,'same-origin');assert.equal(request.mode,'same-origin');assert.equal(request.redirect,'error');assert.equal(request.headers.Authorization,undefined);
  assert.equal(request.referrerPolicy,'same-origin','same-origin POST must retain Origin even when an installed page has no-referrer');
  assert.deepEqual(Object.keys(JSON.parse(request.body)),['username','password','deviceName','browser']);
});
test('only an explicit origin proof challenge retries an account POST, once',async t=>{
  const calls=[];
  const proof='A'.repeat(96);
  const blocked=()=>Response.json({error:'origin proof required'},{status:403,headers:{'X-Tuci-Origin-Error':'proof-required'}});
  const results=[blocked(),Response.json({proof,expiresIn:120}),blocked(),
    Response.json({error:'forbidden'},{status:403}),Response.json({error:'credentials'},{status:401})];
  t.mock.method(globalThis,'fetch',async(url,request)=>{
    calls.push({url,request});
    return results.shift();
  });
  const body={username:'alice',password:'fixture-password',browser:true};
  await assert.rejects(accountRequest('login',body),{status:403,message:'origin proof required'});
  assert.deepEqual(calls.map(({url,request})=>[url,request.method]),[
    ['/tuci-sync/v1/account/login','POST'],
    ['/tuci-sync/v1/account/origin-proof','GET'],
    ['/tuci-sync/v1/account/login','POST']
  ]);
  const [original,challenge,retry]=calls.map(({request})=>request);
  assert.equal(challenge.body,undefined);
  assert.equal(challenge.credentials,'same-origin');
  assert.equal(challenge.mode,'same-origin');
  assert.equal(challenge.cache,'no-store');
  assert.notEqual(challenge.signal,original.signal);
  assert.equal(retry.body,original.body);
  assert.equal(retry.headers['X-Tuci-Origin-Proof'],proof);
  assert.equal(retry.signal,original.signal);
  await assert.rejects(accountRequest('login',body),{status:403,message:'forbidden'});
  await assert.rejects(accountRequest('login',body),{status:401,message:'credentials'});
  assert.equal(calls.length,5);
});
test('concurrent opaque requests share a proof even when the first caller aborts',async t=>{
  const {accountRequest}=await import('../account.js?proof-concurrency');
  const controller=new AbortController();
  const proof='B'.repeat(96);
  let completeChallenge, notifyChallenge, challengeCount=0, challengeSignal, retries=0;
  const started=new Promise(resolve=>{notifyChallenge=resolve;});
  t.mock.method(globalThis,'fetch',async(url,request)=>{
    if (url.endsWith('/origin-proof')) {
      challengeCount++;
      challengeSignal=request.signal;
      return new Promise(resolve=>{completeChallenge=resolve;notifyChallenge();});
    }
    if (request.headers['X-Tuci-Origin-Proof']===proof) {
      retries++;
      return Response.json({ok:true});
    }
    return Response.json({error:'origin proof required'},{status:403,headers:{'X-Tuci-Origin-Error':'proof-required'}});
  });
  const first=accountRequest('login',{},null,null,{signal:controller.signal});
  const second=accountRequest('login',{});
  await started;
  controller.abort();
  await assert.rejects(first,{name:'AbortError'});
  assert.equal(challengeSignal.aborted,false);
  assert.equal(challengeCount,1);
  completeChallenge(Response.json({proof,expiresIn:120}));
  assert.deepEqual(await second,{ok:true});
  assert.equal(retries,1);
});
test('reading keys hash the complete URL and positions clamp to the scroll range',async()=>{
  assert.equal(await readingKey('https://example.com/article'),'reading/'+(await import('node:crypto')).createHash('sha256').update('https://example.com/article').digest('hex'));
  await assert.rejects(readingKey('http://example.com/article'));await assert.rejects(readingKey('https://user:pass@example.com/article'));
  const paragraphs=[{getBoundingClientRect:()=>({top:-50})},{getBoundingClientRect:()=>({top:90})},{getBoundingClientRect:()=>({top:400})}];
  assert.deepEqual(readingPosition(paragraphs,200,1000,600),{progress:.5,anchor:'p:1'});assert.equal(readingPosition([],2000,1000,600).progress,1);
});

test('canceling a login aborts its network request and does not report a spurious credential failure',async t=>{
  const controller=new AbortController();let aborted=false;
  t.mock.method(globalThis,'fetch',async(_,options)=>await new Promise((resolve,reject)=>{options.signal.addEventListener('abort',()=>{aborted=true;reject(new DOMException('aborted','AbortError'));},{once:true});}));
  const request=accountRequest('login',{username:'alice',password:'fixture-password'},null,null,{signal:controller.signal});controller.abort();await assert.rejects(request,{name:'AbortError'});assert.equal(aborted,true);
});
