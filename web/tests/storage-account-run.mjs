// npm install --prefix .artifacts/web-qa fake-indexeddb --no-audit --no-fund
// node web/tests/storage-account-run.mjs (or set FAKE_INDEXEDDB_MODULE to its ESM entry)
import test from "node:test";
import assert from "node:assert/strict";
const {IDBFactory}=await import(process.env.FAKE_INDEXEDDB_MODULE || '../../.artifacts/web-qa/node_modules/fake-indexeddb/build/esm/index.js');
import {openStore} from '../storage.js';
globalThis.indexedDB=new IDBFactory();
globalThis.window=new EventTarget();globalThis.document=Object.assign(new EventTarget(),{visibilityState:'visible'});
const fixture=id=>({userId:'user-'+id,vaultId:'vault-'+id,username:id,deviceId:'device-'+id});
const databases=new Map();let account='alice',calls=[];
function remote(){if(!databases.has(account))databases.set(account,{notes:new Map(),changes:[],state:new Map(),stateChanges:[],receipts:new Map()});return databases.get(account);}
globalThis.fetch=async(url,options)=>{
  assert.equal(options.referrerPolicy,'same-origin','sync POST must retain Origin under a cached no-referrer page');
  assert.equal(options.credentials,'same-origin');assert.equal(options.headers.Authorization,undefined);if(options.headers['X-Tuci-Vault']!=='vault-'+account)return Response.json({error:'wrong account'},{status:403});
  const target=new URL(url,'https://fixture.invalid'),path=target.pathname.replace('/tuci-sync/v1/',''),db=remote();
  const input=options.body?JSON.parse(options.body):null;calls.push({account,path,input});
  if(path==='operations'){
    if(db.receipts.has(input.operationId))return Response.json(db.receipts.get(input.operationId));
    const duplicate=[...db.notes.values()].find(n=>!n.deleted && n.path===input.path && n.markdown===input.markdown);
    if(input.baseVersion===0 && !db.notes.has(input.noteId) && duplicate){const echo={...duplicate,seq:db.changes.length+1};db.changes.push(echo);const receipt={...echo,conflict:false};db.receipts.set(input.operationId,receipt);return Response.json(receipt);}
    const targetId=db.forceConflict?crypto.randomUUID():input.noteId,conflict=!!db.forceConflict;db.forceConflict=false;
    const note={seq:db.changes.length+1,noteId:targetId,version:(db.notes.get(input.noteId)?.version||0)+1,path:input.path,markdown:input.markdown,deleted:input.deleted,regions:input.regions};
    db.notes.set(note.noteId,note);db.changes.push(note);const receipt={...note,conflict};db.receipts.set(input.operationId,receipt);return Response.json(receipt);
  }
  if(path==='changes'){const after=Number(target.searchParams.get('after'));return Response.json({changes:db.changes.filter(n=>n.seq>after),cursor:db.changes.length,hasMore:false});}
  if(path==='state/operations'){
    if(input.value?.noteId)assert.ok(db.notes.has(input.value.noteId),'state must follow canonical note upload');
    let change=db.state.get(input.key);
    if(!change || !/^(event|legacy)\//.test(input.key)){
      change={seq:db.stateChanges.length+1,key:input.key,value:input.value,deleted:input.deleted};db.state.set(input.key,change);db.stateChanges.push(change);
    }
    return Response.json({key:change.key,seq:change.seq});
  }
  if(path==='state/changes'){const after=Number(target.searchParams.get('after'));return Response.json({changes:db.stateChanges.filter(n=>n.seq>after),cursor:db.stateChanges.length,hasMore:false});}
  return Response.json({error:'unknown route'},{status:404});
};
test('account databases isolate private notes and preserve the correct offline outbox',async()=>{
  const a=await openStore({identity:fixture('alice'),autoSync:false}),b=await openStore({identity:fixture('bob'),autoSync:false});
  try{
    await a.saveNote('名词/private.md','Alice only');assert.equal(b.notes().length,0);
    await b.saveNote('名词/private.md','Bob only');account='alice';await a.sync();account='bob';await b.sync();
    assert.equal(remote().notes.values().next().value.markdown,'Bob only');account='alice';assert.equal(remote().notes.values().next().value.markdown,'Alice only');
    assert.ok(calls.every(c=>!JSON.stringify(c.input).includes('aiKey')));
  }finally{a.close();b.close();}
});
test('offline moves keep identity and ordered state writes retain the last theme',async()=>{
  account='moves';const store=await openStore({identity:fixture(account),autoSync:false});
  try{
    await store.saveNote('名词/first.md','word');const id=store.notes()[0].id;
    await store.moveNote('名词/first.md','动词/renamed.md','word');assert.equal(store.notes()[0].id,id);
    await store.setState('preference/theme',{name:'warmNeutral'});await store.setState('preference/theme',{name:'pink'});
    await store.setState('event/12345678-1234-4234-8234-123456789012/shown',{noteId:id,at:'2026-11-07T12:00:00Z',day:'2026-11-07',weight:2,weightLevel:.5});
    await store.sync();assert.equal(remote().notes.get(id).path,'动词/renamed.md');assert.equal(remote().state.get('preference/theme').value.name,'pink');
    assert.equal(store.status().pending,0);
    await store.trashNote('动词/renamed.md','word');assert.equal(store.notes().length,0);await store.sync();
    assert.equal(store.trashItems().length,1);await store.restoreNote(store.trashItems()[0].id);await store.sync();
    assert.equal(store.notes()[0].id,id);assert.equal(store.notes()[0].markdown,'word');assert.equal(store.trashItems().length,0);
  }finally{store.close();}
});
test('immutable exposure events finish once and remain durable after reopening',async()=>{
  account='events';let store=await openStore({identity:fixture(account),autoSync:false});
  const key='event/12345678-1234-4234-8234-123456789013/completed';
  try{
    await store.saveNote('verb/test.md','test');const noteId=store.notes()[0].id;
    await store.setState(key,{noteId,at:'2026-11-07T12:00:00Z',day:'2026-11-07',remembered:true});
    await store.setState(key,{noteId,at:'2026-11-07T12:00:01Z',day:'2026-11-07',remembered:false});
    assert.equal(store.stateEntries().find(s=>s.key===key).value.remembered,true);store.close();
    store=await openStore({identity:fixture(account),autoSync:false});assert.equal(store.notes().length,1);await store.sync();
    assert.equal(remote().stateChanges.length,1);assert.equal(store.status().pending,0);
  }finally{store.close();}
});

test('a review transition commits atomically and rejects private configuration in sync values',async()=>{
  account='atomic';const store=await openStore({identity:fixture(account),autoSync:false});
  try{
    await assert.rejects(store.setStates([{key:'preference/theme',value:{name:'pink'}},{key:'preference/theme',value:{name:'mono',aiKey:'private'}}]));
    assert.equal(store.stateEntries().length,0);assert.equal(store.status().pending,0);
    await store.setStates([{key:'preference/theme',value:{name:'pink'}},{key:'reading/'+ 'a'.repeat(64),value:{url:'https://example.com',progress:.25,anchor:'p:1'}}]);
    assert.equal(store.stateEntries().length,2);assert.equal(store.status().pending,2);
  }finally{store.close();}
});

test('deletions received from another device remain recoverable in the local recycle bin',async()=>{
  account='remote-trash';const a=await openStore({identity:fixture(account),autoSync:false});
  try{
    await a.saveNote('名词/remote.md','original remote word');await a.sync();
    const note=a.notes()[0],db=remote(),deleted={seq:db.changes.length+1,noteId:note.id,version:note.version+1,path:note.path,markdown:'',deleted:true,regions:null};
    db.notes.set(note.id,deleted);db.changes.push(deleted);await a.sync();
    assert.equal(a.notes().length,0);assert.equal(a.trashItems()[0].markdown,'original remote word');
    await a.restoreNote(a.trashItems()[0].id);await a.sync();assert.equal(a.notes()[0].id,note.id);
  }finally{a.close();}
});

test('a cookie changed in another tab cannot upload the previous account outbox',async()=>{
  account='switch-a';let a=await openStore({identity:fixture(account),autoSync:false});
  try{
    await a.saveNote('名词/private.md','Account A secret');account='switch-b';await assert.rejects(a.sync());
    assert.equal(a.status().needsBinding,true);assert.equal(a.status().pending,1);assert.equal(databases.has('switch-b'),false);
    a.close();account='switch-a';a=await openStore({identity:fixture(account),autoSync:false});await a.sync();
    assert.equal(remote().notes.values().next().value.markdown,'Account A secret');assert.equal(a.status().pending,0);
  }finally{a.close();}
});

test('first upload canonical dedupe remaps unsent review, reading, legacy and trash identities atomically',async()=>{
  account='canonical';const store=await openStore({identity:fixture(account),autoSync:false});
  try{
    const canonical=crypto.randomUUID(),serverNote={seq:1,noteId:canonical,version:1,path:'apple.md',markdown:'same content',deleted:false,regions:null};remote().notes.set(canonical,serverNote);remote().changes.push(serverNote);
    await store.saveNote('apple.md','same content');const provisional=store.notes()[0].id,exposure=crypto.randomUUID(),at='2026-11-07T12:00:00Z',day='2026-11-07';
    await store.setStates([
      {key:'event/'+exposure+'/shown',value:{noteId:provisional,at,day,weight:2,weightLevel:.5}},
      {key:'roaming/current',value:{noteId:provisional,exposureId:exposure,at,weight:2,weightLevel:.5}},
      {key:'legacy/source/'+provisional+'/'+day,value:{noteId:provisional,day,remembered:1,skipped:0,shown:0,lastShown:at}},
      {key:'note-reading/'+provisional,value:{noteId:provisional,progress:.4,anchor:'p:2'}}]);
    await store.trashNote('apple.md','same content');await store.sync();
    assert.equal(store.status().pending,0);assert.equal(store.trashItems()[0].noteId,canonical);
    assert.ok(store.stateEntries().every(row=>row.value?.noteId!==provisional));
    assert.ok(store.stateEntries().some(row=>row.key==='legacy/source/'+canonical+'/'+day));
    assert.ok(store.stateEntries().some(row=>row.key==='note-reading/'+canonical));
    await store.restoreNote(store.trashItems()[0].id);await store.sync();assert.equal(store.notes()[0].id,canonical);
  }finally{store.close();}
});
test('a conflict copy of an already synced note does not steal the original card history',async()=>{
  account='historical-conflict';const store=await openStore({identity:fixture(account),autoSync:false});
  try{
    await store.saveNote('apple.md','original');await store.sync();const original=store.notes()[0],exposure=crypto.randomUUID();
    await store.setState('event/'+exposure+'/shown',{noteId:original.id,at:'2026-11-07T12:00:00Z',day:'2026-11-07',weight:2,weightLevel:.5});await store.sync();
    await store.saveNote('apple.md','conflicting local',undefined,'original');remote().forceConflict=true;await store.sync();
    assert.notEqual(store.notes().find(n=>n.markdown==='conflicting local').id,original.id);
    assert.equal(store.stateEntries().find(s=>s.key==='event/'+exposure+'/shown').value.noteId,original.id);
  }finally{store.close();}
});

test('legacy roaming migrates once with its original exposure and stable source, without inventing shown counts',async()=>{
  account='migration';let store=await openStore({identity:fixture(account),autoSync:false});
  const noteId=crypto.randomUUID(),scope=crypto.randomUUID(),exposure=crypto.randomUUID(),at=Date.parse('2026-11-07T12:00:00Z');
  const legacy={profile:{scope,vaultId:fixture(account).vaultId,cursor:0},notes:[{id:noteId,path:'apple.md',pathKey:'APPLE.MD',version:1,markdown:'apple',added:'2026-11-01T00:00:00Z',deleted:false}],outbox:[]};
  const roaming=JSON.stringify({records:{['note:'+noteId]:{lastShown:at,day:'2026-11-7',remembered:2,skipped:1}},current:{id:'note:'+noteId,exposure}});
  try{
    assert.equal(await store.importLegacy(legacy,{roaming}),true);
    const rows=store.stateEntries(),baseline=rows.find(row=>row.key.startsWith('legacy/'));
    assert.equal(baseline.key,'legacy/web-'+scope+'/'+noteId+'/2026-11-07');assert.equal(baseline.value.remembered,2);assert.equal(baseline.value.skipped,1);assert.equal(baseline.value.shown,0);
    assert.equal(rows.find(row=>row.key==='roaming/current').value.exposureId,exposure);assert.equal(rows.some(row=>row.key.startsWith('event/')),false);assert.equal(store.status().pending,2);
    store.close();store=await openStore({identity:{...fixture(account),deviceId:'different-login-device'},autoSync:false});
    await store.importLegacy(legacy,{roaming});assert.equal(store.status().pending,2);assert.deepEqual(store.stateEntries(),rows);assert.equal(legacy.notes[0].markdown,'apple');
  }finally{store.close();}
});
test('damaged legacy history neither overwrites source notes nor blocks the new account',async()=>{
  account='damaged-migration';const store=await openStore({identity:fixture(account),autoSync:false});
  const legacy={profile:{scope:crypto.randomUUID(),vaultId:fixture(account).vaultId},notes:[],outbox:[]};
  try{
    assert.equal(await store.importLegacy(legacy,{roaming:'{broken JSON'}),true);assert.equal(store.stateEntries().length,0);assert.equal(store.status().pending,0);
    await store.setState('preference/theme',{name:'mono'});await store.sync();assert.equal(store.status().pending,0);assert.equal(remote().state.get('preference/theme').value.name,'mono');
  }finally{store.close();}
});

test('a failed legacy migration rolls notes and its migration marker back before retry',async()=>{
  account='atomic-migration';let store=await openStore({identity:fixture(account),autoSync:false});
  const noteId=crypto.randomUUID(),scope=crypto.randomUUID(),legacy={profile:{scope,vaultId:fixture(account).vaultId},notes:[{id:noteId,path:'word.md',pathKey:'WORD.MD',version:1,markdown:'kept source',deleted:false}],outbox:[]};
  const roaming=JSON.stringify({records:{['note:'+noteId]:{day:'2026-11-7',remembered:1,skipped:0,lastShown:Date.parse('2026-11-07T12:00:00Z')}},current:null});
  const {IDBObjectStore}=await import(process.env.FAKE_INDEXEDDB_MODULE || '../../.artifacts/web-qa/node_modules/fake-indexeddb/build/esm/index.js'),original=IDBObjectStore.prototype.add;
  try{
    IDBObjectStore.prototype.add=function(...args){if(this.name==='stateOutbox')throw new DOMException('simulated quota','QuotaExceededError');return original.apply(this,args);};
    await assert.rejects(store.importLegacy(legacy,{roaming}));IDBObjectStore.prototype.add=original;store.close();
    store=await openStore({identity:fixture(account),autoSync:false});assert.equal(store.notes().length,0);assert.equal(store.stateEntries().length,0);
    await store.importLegacy(legacy,{roaming});assert.equal(store.notes()[0].markdown,'kept source');assert.equal(store.stateEntries().length,1);assert.equal(store.status().pending,1);
  }finally{IDBObjectStore.prototype.add=original;store.close();}
});
