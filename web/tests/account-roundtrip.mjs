// Real loopback server + Windows Core + browser IndexedDB adapter, without browser automation.
// npm install --prefix .artifacts/web-qa fake-indexeddb --no-audit --no-fund
// node web/tests/account-roundtrip.mjs
// Set DOTNET / FAKE_INDEXEDDB_MODULE if needed. TUCI_KEEP_TEST_ARTIFACTS=1 retains the disposable fixture.
import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {mkdtempSync,readFileSync,realpathSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {dirname,basename,join,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {createInterface} from 'node:readline';
import {once} from 'node:events';
import {openStore} from '../storage.js';
import {accountRequest,checkedIdentity} from '../account.js';
import {readingKey} from '../reading-state.js';
const {IDBFactory}=await import(process.env.FAKE_INDEXEDDB_MODULE || '../../.artifacts/web-qa/node_modules/fake-indexeddb/build/esm/index.js');
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const directory=realpathSync(mkdtempSync(join(tmpdir(),'tuci-account-roundtrip-')));
const dotnet=process.env.DOTNET || 'dotnet';
const environment={...process.env,TUCI_DB:join(directory,'server.sqlite3'),TUCI_TRUST_LOOPBACK_PROXY:'0'};
const serverProject=join(root,'server','Tuci.Sync.csproj'),driverProject=join(root,'tests','VocabCollector.Sync.AccountRoundTrip','VocabCollector.Sync.AccountRoundTrip.csproj');
const serverDll=join(root,'server','bin','Release','net10.0','Tuci.Sync.dll');
const driverDll=join(root,'tests','VocabCollector.Sync.AccountRoundTrip','bin','Release','net10.0','VocabCollector.Sync.AccountRoundTrip.dll');
const nativeFetch=globalThis.fetch;
let backend,store,completed=false;
function dotnetRun(args,env=environment){
  const result=spawnSync(dotnet,args,{cwd:root,env,encoding:'utf8',windowsHide:true,maxBuffer:8*1024*1024});
  if(result.error)throw result.error;
  if(result.status!==0){
    // Test fixtures use generated accounts, and opaque credentials never go into diagnostics.
    const output=(result.stderr || result.stdout || '').replace(/\b[A-Fa-f0-9]{64}\b/g,'[redacted]');
    throw new Error('Dotnet phase failed ('+result.status+'): '+output);
  }
  return result.stdout;
}
async function startServer(){
  backend=spawn(dotnet,[serverDll,'--urls','http://127.0.0.1:0'],{cwd:root,env:environment,stdio:['ignore','pipe','pipe'],windowsHide:true});
  backend.stderr.resume();
  const lines=createInterface({input:backend.stdout});
  return await new Promise((accept,reject)=>{
    const timeout=setTimeout(()=>reject(new Error('Isolated sync server did not start in 30 seconds.')),30000);
    backend.once('error',error=>{clearTimeout(timeout);reject(error);});
    backend.once('exit',code=>{clearTimeout(timeout);reject(new Error('Isolated sync server exited ('+code+').'));});
    lines.on('line',line=>{const match=/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/.exec(line);if(match){clearTimeout(timeout);lines.close();backend.stdout.resume();accept(match[1]+'/');}});
  });
}
try{
  if(process.env.TUCI_SKIP_DOTNET_BUILD!=='1'){
    dotnetRun(['build',serverProject,'-c','Release','--nologo','-p:RestoreLockedMode=true']);
    dotnetRun(['build',driverProject,'-c','Release','--nologo','-p:RestoreLockedMode=true']);
  }
  const invitation=JSON.parse(dotnetRun([serverDll,'invite-web']));
  const base=await startServer();
  const driverEnv={...environment,TUCI_TEST_SERVER:base,TUCI_TEST_INVITE_CODE:invitation.code};
  dotnetRun([driverDll,'seed',directory],driverEnv);
  const fixture=JSON.parse(readFileSync(join(directory,'fixture.json'),'utf8').replace(/^\uFEFF/,''));
  assert.equal(fixture.server.replace(/\/$/,''),base.replace(/\/$/,''));
  const cookies=new Map();
  globalThis.fetch=async(url,options={})=>{
    if(typeof url!=='string' || !url.startsWith('/tuci-sync/v1/'))throw new Error('Roundtrip attempted an unexpected network destination.');
    const headers=new Headers(options.headers);
    assert.equal(headers.has('Authorization'),false,'browser adapter must use cookies, never bearer credentials');
    if(options.credentials==='same-origin' && cookies.size)headers.set('Cookie',[...cookies.values()].join('; '));
    const response=await nativeFetch(new URL(url.slice('/tuci-sync/'.length),base),{...options,headers});
    for(const raw of response.headers.getSetCookie()){
      const pair=raw.split(';')[0],separator=pair.indexOf('=');cookies.set(pair.slice(0,separator),pair);
    }
    return response;
  };
  const identity=checkedIdentity(await accountRequest('login',{username:fixture.username,password:fixture.password,deviceName:'Isolated mobile IndexedDB adapter',browser:true}));
  assert.equal(identity.vaultId,fixture.vaultId);assert.ok(cookies.size>0);
  globalThis.indexedDB=new IDBFactory();globalThis.window=new EventTarget();
  globalThis.document=Object.assign(new EventTarget(),{visibilityState:'visible'});
  store=await openStore({identity,autoSync:false});
  // A fresh mobile installation created the same card offline before its first pull.
  await store.saveNote('apple.md',fixture.originalMarkdown);
  const provisional=store.notes()[0].id,preExposure=crypto.randomUUID();
  await store.setStates([
    {key:'event/'+preExposure+'/shown',value:{noteId:provisional,at:new Date().toISOString(),day:fixture.day,weight:2,weightLevel:.5}},
    {key:'legacy/web-roundtrip/'+provisional+'/'+fixture.day,value:{noteId:provisional,day:fixture.day,remembered:0,skipped:0,shown:0,lastShown:new Date().toISOString()}},
    {key:'note-reading/'+provisional,value:{noteId:provisional,progress:.25,anchor:'p:3'}}
  ]);
  await store.sync();
  assert.equal(store.status().pending,0);
  assert.equal(store.notes()[0].id,fixture.noteId,'initial duplicate did not acquire the canonical cloud identity');
  assert.ok(store.stateEntries().every(row=>row.value?.noteId!==provisional),'provisional learning identity survived canonical merge');
  const note=store.notes().find(n=>n.id===fixture.noteId);assert.ok(note,'Windows canonical note is missing in the web store');
  assert.equal(note.markdown,fixture.originalMarkdown);
  const initial=store.stateEntries();
  assert.equal(initial.find(row=>row.key==='preference/theme')?.value.name,'pink');
  assert.equal(initial.find(row=>row.key==='roaming/current')?.value.exposureId,fixture.exposureId);
  const articleURL=fixture.readingUrl || 'https://example.org/tuci-roundtrip/article',articleKey=await readingKey(articleURL);
  assert.equal(initial.find(row=>row.key===articleKey)?.value.progress,.25);
  assert.equal(initial.find(row=>row.key==='note-reading/'+note.id)?.value.progress,.25);
  await store.saveNote(note.path,note.markdown+'\nEdited on the phone\n',undefined,note.markdown);
  const exposureId=crypto.randomUUID(),at=new Date().toISOString(),day=fixture.day;
  await store.setStates([
    {key:'event/'+fixture.exposureId+'/completed',value:{noteId:note.id,at,day,remembered:false}},
    {key:'event/'+exposureId+'/shown',value:{noteId:note.id,at,day,weight:3,weightLevel:.75}},
    {key:'roaming/current',value:{noteId:note.id,exposureId,at,weight:3,weightLevel:.75}},
    {key:'preference/theme',value:{name:'mono'}},
    {key:articleKey,value:{url:articleURL,progress:.75,anchor:'p:7',title:'Roundtrip article'}},
    {key:'note-reading/'+note.id,value:{noteId:note.id,progress:.75,anchor:'p:7',title:'apple'}}
  ]);
  await store.sync();assert.equal(store.status().pending,0);assert.equal(store.notes().find(n=>n.path===note.path).id,note.id);
  store.close();store=await openStore({identity,autoSync:false});
  assert.ok(store.notes().find(n=>n.id===note.id).markdown.includes('Edited on the phone'));
  dotnetRun([driverDll,'verify',directory],driverEnv);
  completed=true;
  console.log('PASS: Windows Core → cookie-authenticated web IndexedDB → Windows Core; notes, immutable review events, activity, theme, article and note positions.');
}finally{
  store?.close();globalThis.fetch=nativeFetch;
  if(backend && backend.exitCode===null){backend.kill();await Promise.race([once(backend,'exit'),new Promise(resolve=>setTimeout(resolve,5000))]);}
  if(completed && process.env.TUCI_KEEP_TEST_ARTIFACTS!=='1'){
    const target=realpathSync(directory),parent=realpathSync(tmpdir());
    if(dirname(target)!==parent || !basename(target).startsWith('tuci-account-roundtrip-'))throw new Error('Refusing to remove an unexpected test directory.');
    rmSync(target,{recursive:true,force:true});
  }else if(!completed)console.error('Disposable roundtrip fixture retained at '+directory);
}

