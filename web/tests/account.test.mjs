import test from 'node:test';
import assert from 'node:assert/strict';
import {accountRequest,checkedIdentity,themeNames,localThemes} from '../account.js';
import {readingKey,readingPosition} from '../reading-state.js';
test('account replies expose identity only, without persisting a bearer credential',()=>{
  const value=checkedIdentity({userId:'u',vaultId:'v',username:'名字',deviceId:'d',token:'must-not-keep'});
  assert.deepEqual(Object.keys(value),['userId','vaultId','username','deviceId']);assert.throws(()=>checkedIdentity({userId:'u'}));
  for(const [local,wire]of Object.entries(themeNames))assert.equal(localThemes[wire],local);
});
test('browser account operations use same-origin cookies and explicit account fields',async t=>{
  const calls=[];t.mock.method(globalThis,'fetch',async(url,request)=>{calls.push({url,request});return Response.json({userId:'u'});});
  await accountRequest('login',{username:'alice',password:'fixture-password',deviceName:'iPhone',browser:true});
  const {url,request}=calls[0];assert.equal(url,'/tuci-sync/v1/account/login');assert.equal(request.credentials,'same-origin');assert.equal(request.mode,'same-origin');assert.equal(request.redirect,'error');assert.equal(request.headers.Authorization,undefined);
  assert.equal(request.referrerPolicy,'same-origin','same-origin POST must retain Origin even when an installed page has no-referrer');
  assert.deepEqual(Object.keys(JSON.parse(request.body)),['username','password','deviceName','browser']);
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
