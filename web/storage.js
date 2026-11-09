import { prepareLegacyRoaming } from './legacy-roaming.js';
import { linkTarget, linkIndex, refreshManaged } from "./managed-links.js";

// The outbox, note versions and pull cursor belong to one IndexedDB database.
// Its transactions serialize writers from Safari tabs and the Home Screen app.
const name = "tuci-notebook-v1";
const stores = ["meta", "notes", "outbox"];
const encoder = new TextEncoder();
const uuid = () => crypto.randomUUID();
const invalidReply = () => new Error("同步服务返回了无法识别的数据。");

function read(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function openDatabase(dbName) {
  const req = indexedDB.open(dbName, 2);
  req.onupgradeneeded = () => {
    const db = req.result;
    if (!db.objectStoreNames.contains("meta")) {
    db.createObjectStore("meta");
    db.createObjectStore("notes", { keyPath: "id" }).createIndex("pathKey", "pathKey");
    db.createObjectStore("outbox", { keyPath: "key", autoIncrement: true }).createIndex("noteId", "noteId");
    db.createObjectStore("backups", { autoIncrement: true });
    }
    if (!db.objectStoreNames.contains("state")) db.createObjectStore("state", {keyPath:"key"});
    if (!db.objectStoreNames.contains("stateOutbox")) db.createObjectStore("stateOutbox", {keyPath:"keyId",autoIncrement:true});
    if (!db.objectStoreNames.contains("trash")) db.createObjectStore("trash", {keyPath:"id"});
  };
  return read(req);
}

async function transaction(db, names, mode, callback) {
  const tx = db.transaction(names, mode);
  const done = new Promise((resolve, reject) => {
    tx.oncomplete = resolve;
    tx.onabort = () => reject(tx.error || new Error("本地存储写入中断。"));
  });
  const tables = Object.fromEntries(names.map(key => [key, tx.objectStore(key)]));
  try {
    const value = await callback(tables);
    await done;
    return value;
  } catch (error) {
    try { tx.abort(); } catch { /* already completed */ }
    await done.catch(() => {});
    throw error;
  }
}

function checkPath(input) {
  if (typeof input !== "string") throw new Error("请输入笔记路径。");
  const path = input.normalize("NFC");
  const invalid = path.length > 1024 || !/\.md$/i.test(path) || path.split("/").some(part => {
    const stem = part.split(".")[0].trimEnd();
    return !part || part.startsWith(".") || /[. ]$/.test(part) || encoder.encode(part).length > 255 ||
      /[<>:"\\|?*\p{Cc}]/u.test(part) || /^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|(?:COM|LPT)[0-9¹²³])$/i.test(stem);
  });
  if (invalid) throw new Error("请输入词库内有效的 Markdown 文件路径。");
  return path;
}

function checkBody(markdown) {
  if (typeof markdown !== "string" || encoder.encode(markdown).length > 1048576)
    throw new Error("一篇笔记最多可保存 1 MiB 的文本。");
}

function checkRegions(regions) {
  if (regions == null) return null;
  if (typeof regions !== "object" || Array.isArray(regions) || Object.entries(regions).some(
    ([key, val]) => !["examples","roots","relations","forms"].includes(key) || typeof val !== "string" || !/^[a-f0-9]{64}$/i.test(val)))
    throw new Error("笔记的自动维护区域记录无效。");
  return regions;
}

function addedDate(markdown) {
  const header = /^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(markdown)?.[1];
  const date = /^created:\s*['"]?([^'"\r\n]+?)['"]?\s*$/m.exec(header || "")?.[1];
  return new Date(date && Number.isFinite(Date.parse(date)) ? date : Date.now()).toISOString();
}

function operation(note) {
  return { operationId: uuid(), noteId: note.id, baseVersion: note.version,
    path: note.path, markdown: note.markdown, deleted: note.deleted, regions: note.regions };
}

function validId(id) {
  return typeof id === "string" && /^[a-zA-Z0-9_-]{1,128}$/.test(id);
}

function version(v) { return Number.isSafeInteger(v) && v >= 1; }

async function ack({ notes, outbox, state, stateOutbox, trash }, op, receipt) {
  if (!validId(receipt.noteId) || !version(receipt.version) || typeof receipt.conflict !== "boolean")
    throw invalidReply();
  const path = checkPath(receipt.path);
  const head = (await read(outbox.openCursor()))?.value;
  if (op.operationId !== head?.operationId) return; // Another tab already recorded this receipt.
  await read(outbox.delete(op.key));
  const local = await read(notes.get(op.noteId));
  if (!local) throw invalidReply();
  const firstIdentity=op.baseVersion===0 && local.version===0 && !local.cloudKnown;
  if (op.noteId !== receipt.noteId) {
    await read(notes.delete(op.noteId));
    if(firstIdentity && state && stateOutbox) {
      const remap=row=>{
        if(row.value?.noteId!==op.noteId)return null;
        let key=row.key;
        if(key==='note-reading/'+op.noteId)key='note-reading/'+receipt.noteId;
        else if(key.startsWith('legacy/')){const parts=key.split('/');if(parts[2]===op.noteId){parts[2]=receipt.noteId;key=parts.join('/');}}
        return {...row,key,value:{...row.value,noteId:receipt.noteId}};
      };
      for(const row of await read(state.getAll())){const mapped=remap(row);if(mapped){if(mapped.key!==row.key)await read(state.delete(row.key));await read(state.put(mapped));}}
      for(const row of await read(stateOutbox.getAll())){const mapped=remap(row);if(mapped)await read(stateOutbox.put({...mapped,operationId:uuid()}));}
      if(trash)for(const item of await read(trash.getAll()))if(item.noteId===op.noteId)await read(trash.put({...item,noteId:receipt.noteId}));
    }
  }
  local.id = receipt.noteId;local.cloudKnown=true;
  local.version = receipt.version;
  if (local.path === op.path) local.path = path;
  if (!local.deleted) local.pathKey = local.path.toUpperCase();
  await read(notes.put(local));
  // Only the FIFO head ever goes on the wire. Later edits can safely be
  // rebased onto its accepted version, even when the reply assigned a new ID.
  const later = await read(outbox.index("noteId").getAll(op.noteId));
  for (const edit of later) {
    edit.noteId = receipt.noteId;
    if (edit.path === op.path) edit.path = path;
    edit.baseVersion = receipt.version;
    await read(outbox.put(edit));
  }
}

async function applyPage({meta, notes, outbox, trash}, after, page) {
  const state = await read(meta.get("profile"));
  if (state.cursor !== after || await read(outbox.count())) return false;
  if (!Array.isArray(page.changes) || typeof page.hasMore !== "boolean" ||
      !Number.isSafeInteger(page.cursor) || (page.hasMore && !page.changes.length)) throw invalidReply();
  let seen = after;
  for (const item of page.changes) {
    if (!validId(item.noteId) || !version(item.version) || !Number.isSafeInteger(item.seq) ||
        item.seq <= seen || item.seq > page.cursor || typeof item.deleted !== "boolean") throw invalidReply();
    seen = item.seq;
    const path = checkPath(item.path);
    checkBody(item.markdown);
    const regions = checkRegions(item.regions);
    const old = await read(notes.get(item.noteId));
    if (old && old.version > item.version) continue;
    if (item.deleted && old && !old.deleted && old.markdown && trash) {
      await read(trash.put({id:uuid(),noteId:old.id,path:old.path,markdown:old.markdown,regions:old.regions,added:old.added,deletedAt:Date.now()}));
    }
    const note = { id: item.noteId, path, markdown: item.markdown, deleted: item.deleted,
      version: item.version, cloudKnown:true, regions, added: old?.added || addedDate(item.markdown) };
    if (!item.deleted) note.pathKey = path.toUpperCase();
    await read(notes.put(note));
  }
  if (seen !== page.cursor) throw invalidReply();
  state.cursor = seen;
  if (!page.hasMore) state.lastSync = Date.now();
  await read(meta.put(state, "profile"));
  return true;
}

async function request(route, token, body, cookie = false, expectedVault = null) {
  const headers = {};
  if (token) headers.Authorization = "Bearer " + token;
  if (cookie && expectedVault) headers["X-Tuci-Vault"] = expectedVault;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  let res;
  const abort = new AbortController();
  const timeout = setTimeout(() => abort.abort(), 30000);
  try {
    res = await fetch("/tuci-sync/v1/" + route, {
      method: body === undefined ? "GET" : "POST", headers, body: JSON.stringify(body),
      credentials: cookie ? "same-origin" : "omit", mode: "same-origin", referrerPolicy: "same-origin", redirect: "error", cache: "no-store", signal: abort.signal
    });
    const reply = await res.json().catch(() => null);
    if (!res.ok) {
      const error = new Error(reply?.error || `同步服务暂时不可用（${res.status}）。`);
      error.status = res.status;
      throw error;
    }
    if (!reply) throw invalidReply();
    return reply;
  } catch (error) {
    if (error instanceof TypeError || error.name === "AbortError") throw new Error("连接同步服务失败，请检查网络后重试。");
    throw error;
  } finally { clearTimeout(timeout); }
}

export async function openStore({ dbName = name, autoSync = true, identity = null } = {}) {
  if (identity) dbName = "tuci-account-v1:" + identity.userId;
  const db = await openDatabase(dbName);
  const tx = (tables, mode, fn) => transaction(db, tables, mode, fn);
  await tx(["meta"], "readwrite", async ({meta}) => {
    if (!await read(meta.get("profile"))) await read(meta.put({
      scope: identity ? "account:" + identity.userId : uuid(), vaultId: identity?.vaultId || null, token: null, accountMode: !!identity, cursor: 0, stateCursor: 0, lastSync: 0, needsRecovery: false
    }, "profile"));
  });
  if (identity) await tx(["meta"], "readwrite", async ({meta}) => {
    const current = await read(meta.get("profile"));
    if (current.vaultId !== identity.vaultId) throw new Error("账号词库与本机记录不一致，已保留离线数据。");
    current.accountMode = true;
    current.needsBinding = false;
    current.token = null;
    await read(meta.put(current,"profile"));
  });
  const channel = typeof BroadcastChannel === "function" ? new BroadcastChannel(dbName) : null;
  const listeners = new Set();
  let profile, pending, statePending = 0, cache = [], stateCache = [], trashCache = [];
  let inFlight = null, syncing = false, error = "", timer = null, closed = false;
  const notify = () => listeners.forEach(fn => { try { fn(); } catch (e) { console.error(e); } });
  async function reload() {
    const data = await tx([...stores,"state","stateOutbox","trash"], "readonly", ({meta, notes, outbox,state,stateOutbox,trash}) =>
      Promise.all([read(meta.get("profile")), read(notes.getAll()), read(outbox.count()),read(state.getAll()),read(stateOutbox.count()),read(trash.getAll())]));
    [profile, , pending, stateCache, statePending, trashCache] = data;
    cache = data[1].filter(note => !note.deleted).sort((a,b) => a.path.localeCompare(b.path, "zh-CN"));
    notify();
  }
  async function changed() {
    await reload();
    channel?.postMessage("changed");
  }
  function schedule() {
    if (closed || !autoSync || !profile?.vaultId || profile.needsRecovery || profile.needsBinding) return;
    clearTimeout(timer);
    timer = setTimeout(() => { sync().catch(() => {}); }, 350);
  }
  // Hashes are asynchronous, so calculate them outside IndexedDB. Commit
  // only against the same note body, ownership and word index that we read.
  async function refreshLinks(trigger = null) {
    const live = rows => rows.filter(n => !n.deleted);
    const identity = rows => JSON.stringify(rows.filter(n => linkTarget(n.path)).map(n => [n.id,n.path]));
    for (let attempt = 0; attempt < 3; attempt++) {
      const snapshot = live(await tx(["notes"], "readonly", ({notes}) => read(notes.getAll())));
      const index = linkIndex(snapshot);
      const signature = identity(snapshot);
      const edits = [];
      for (const old of snapshot) {
        if (trigger && !old.markdown.toLowerCase().replaceAll("’","'").includes(trigger.term)) continue;
        const next = await refreshManaged(old,index);
        if (next) edits.push({old,next});
      }
      if (!edits.length) return 0;
      const committed = await tx(["notes","outbox"], "readwrite", async ({notes,outbox}) => {
        const current = live(await read(notes.getAll()));
        if (identity(current) !== signature) return false;
        const byId = new Map(current.map(n => [n.id,n]));
        for (const {old} of edits) {
          const latest = byId.get(old.id);
          if (!latest || latest.markdown !== old.markdown || latest.path !== old.path ||
              JSON.stringify(latest.regions) !== JSON.stringify(old.regions)) return false;
        }
        for (const {old,next} of edits) {
          const note = {...byId.get(old.id), ...next};
          await read(notes.put(note));
          await read(outbox.add(operation(note)));
        }
        return true;
      });
      if (committed) { await changed(); schedule(); return edits.length; }
    }
    throw new Error("词库正在更新，请稍后重试整理链接。");
  }
  async function save(pathInput, markdown, regions, expectedMarkdown, deleting = false) {
    const path = checkPath(pathInput);
    checkBody(markdown);
    const trigger = await tx(["notes", "outbox"], "readwrite", async ({notes, outbox}) => {
      const old = await read(notes.index("pathKey").get(path.toUpperCase()));
      if (expectedMarkdown !== undefined && (old?.markdown ?? null) !== expectedMarkdown)
        throw new Error(old && expectedMarkdown === null ? "词库中已有同名笔记。" : "笔记已在其他位置更新，请重新打开后再编辑。");
      if (deleting && !old) return;
      const owned = checkRegions(deleting ? null : regions === undefined ? old?.regions : regions);
      if (!deleting && old?.markdown === markdown && regions === undefined) return;
      const note = { id: old?.id || uuid(), path: old?.path || path, version: old?.version ?? 0, cloudKnown:old?.cloudKnown || (old?.version||0)>0,
        markdown, regions: owned, added: old?.added || addedDate(markdown), deleted: deleting };
      if (!deleting) note.pathKey = path.toUpperCase();
      await read(notes.put(note));
      await read(outbox.add(operation(note)));
      return !old || deleting ? linkTarget(path) : null;
    });
    await changed();
    if (trigger) {
      try { await refreshLinks(trigger); }
      catch (e) { error = "笔记已保存，整理链接时遇到问题。" + e.message; notify(); }
    }
    schedule();
  }
  async function bind(code, deviceName = "兔词网页版") {
    const grant = await request("redeem", null, {code: code.trim(), deviceName});
    if (!validId(grant.vaultId) || typeof grant.token !== "string" || !grant.token.length) throw invalidReply();
    await tx(["meta"], "readwrite", async ({meta}) => {
      const state = await read(meta.get("profile"));
      if (state.vaultId && state.vaultId !== grant.vaultId)
        throw new Error("这个浏览器已绑定其他词库，请使用原词库的配对码。");
      state.vaultId = grant.vaultId;
      state.token = grant.token;
      state.needsBinding = false;
      await read(meta.put(state, "profile"));
    });
    error = "";
    await changed();
    await sync().catch(() => {}); // Grant is durable even if the first transfer loses connectivity.
  }
  async function authorised(route, token, body) {
    try {
      return await request(route, token, body, !!identity, identity?.vaultId);
    } catch (problem) {
      if (problem.status === 401 || problem.status === 403) {
        await tx(["meta"], "readwrite", async ({meta}) => {
          const state = await read(meta.get("profile"));
          if (state.token === token) {
            state.needsBinding = true;
            await read(meta.put(state, "profile"));
          }
        });
        await changed();
      }
      throw problem;
    }
  }
  async function pair() {
    if (!profile.token) throw new Error("请先绑定词库。");
    const answer = await authorised("pair", profile.token, {});
    if (!validId(answer.code)) throw invalidReply();
    return answer.code;
  }
  async function runSync() {
    syncing = true;
    error = "";
    notify();
    let mutated = false;
    let conflicts = 0;
    try {
      let checkedHistory = false;
      for (;;) {
        const [state, first] = await tx(["meta","outbox"], "readonly", ({meta, outbox}) =>
          Promise.all([read(meta.get("profile")), read(outbox.openCursor())]));
        if (!state.token && !state.accountMode) break;
        if (state.needsBinding) throw new Error("请使用原词库的新配对码重新连接。");
        if (state.needsRecovery) throw new Error("服务器历史已回退，请先重新核对词库。");
        const pull = () => authorised("changes?after=" + state.cursor, state.token).catch(async problem => {
          if (problem.status === 409) {
            await tx(["meta"], "readwrite", async ({meta}) => {
              const latest = await read(meta.get("profile"));
              latest.needsRecovery = true;
              await read(meta.put(latest, "profile"));
            });
            mutated = true;
          }
          throw problem;
        });
        const op = first?.value;
        if (op) {
          // Check the cursor before pushing anything into a potentially restored database.
          if (state.cursor > 0 && !checkedHistory) await pull();
          checkedHistory = true;
          const {key, ...wire} = op;
          const receipt = await authorised("operations", state.token, wire);
          await tx(["notes","outbox","state","stateOutbox","trash"], "readwrite", tables => ack(tables, op, receipt));
          if (receipt.conflict) conflicts++;
          mutated = true;
          continue;
        }
        const page = await pull();
        checkedHistory = true;
        const applied = await tx([...stores,"trash"], "readwrite", tables => applyPage(tables, state.cursor, page));
        if (applied) mutated = true;
        if (applied && !page.hasMore) break;
      }
      if (profile.accountMode) await syncState();
      return {conflicts};
    } catch (e) {
      error = e.message;
      throw e;
    } finally {
      syncing = false;
      if (mutated) await changed();
      else await reload();
    }
  }
  function sync() {
    if (inFlight) return inFlight;
    // The receipt/cursor checks also make duplicate attempts safe when a browser
    // does not offer Web Locks (or one of the pages is closed during a request).
    const work = globalThis.navigator?.locks ? navigator.locks.request(dbName + "-sync", runSync) : runSync();
    inFlight = work.finally(() => { inFlight = null; });
    return inFlight;
  }
  async function recover() {
    await tx([...stores, "backups","state","stateOutbox"], "readwrite", async ({meta, notes, outbox, backups,state:stateTable,stateOutbox}) => {
      const state = await read(meta.get("profile"));
      if (!state.needsRecovery) return;
      const all = await read(notes.getAll());
      await read(backups.add({cursor: state.cursor, captured: Date.now(), notes: all, outbox: await read(outbox.getAll())}));
      await read(outbox.clear());
      await read(notes.clear());
      for (const note of all.filter(n => !n.deleted)) {
        note.cloudKnown=note.cloudKnown || note.version>0;note.version = 0;
        await read(notes.put(note));
        await read(outbox.add(operation(note)));
      }
      const learning=await read(stateTable.getAll());
      await read(backups.add({captured:Date.now(),stateCursor:state.stateCursor||0,learning}));
      await read(stateOutbox.clear());
      for(const row of learning) await read(stateOutbox.add({operationId:uuid(),key:row.key,value:row.value,deleted:!!row.deleted}));
      state.cursor = 0;state.stateCursor=0;
      state.needsRecovery = false;
      await read(meta.put(state, "profile"));
    });
    await changed();
    return sync();
  }
  function checkStateInput({key,value,deleted=false}) {
    const parts=typeof key==='string'?key.split('/'):[];
    const event=parts.length===3 && parts[0]==='event' && /^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i.test(parts[1]) && ['shown','completed'].includes(parts[2]);
    const legacy=parts.length===4 && parts[0]==='legacy' && validId(parts[1]) && validId(parts[2]);
    const reading=parts.length===2 && parts[0]==='reading' && /^[\da-f]{64}$/i.test(parts[1]);
    const noteReading=parts.length===2 && parts[0]==='note-reading' && validId(parts[1]);
    const current=key==='roaming/current',theme=key==='preference/theme';
    if(typeof key!=='string' || key.length>300 || !(event||legacy||reading||noteReading||current||theme))throw new Error("学习状态标识无效。");
    if(deleted){if(event||legacy)throw new Error("复习记录不能删除。");return;}
    if(!value || typeof value!=="object" || Array.isArray(value) || encoder.encode(JSON.stringify(value)).length>16384)throw new Error("学习状态内容无效或过长。");
    const fields=theme?['name']:reading?['url','progress','anchor','title']:noteReading?['noteId','progress','anchor','title']:
      legacy?['noteId','day','remembered','skipped','shown','lastShown']:current?['noteId','exposureId','at','weight','weightLevel']:
      parts[2]==='shown'?['noteId','at','day','weight','weightLevel']:['noteId','at','day','remembered'];
    if(Object.keys(value).some(field=>!fields.includes(field)))throw new Error("学习状态包含不支持的字段。");
    const number=(name,min,max)=>typeof value[name]==='number' && Number.isFinite(value[name]) && value[name]>=min && value[name]<=max;
    const iso=entry=>typeof entry==='string' && /^\d{4}-\d{2}-\d{2}T/.test(entry) && Number.isFinite(Date.parse(entry));
    const day=typeof value.day==='string' && /^\d{4}-\d{2}-\d{2}$/.test(value.day) && Number.isFinite(Date.parse(value.day));
    let good=true;
    if(event||legacy||current||noteReading)good=validId(value.noteId);
    if(event||current)good=good && iso(value.at);
    if(event||legacy)good=good && day;
    if(current)good=good && /^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i.test(value.exposureId||'');
    if(current || event && parts[2]==='shown')good=good && number('weight',Number.MIN_VALUE,1e9) && number('weightLevel',0,1);
    if(event && parts[2]==='completed')good=good && typeof value.remembered==='boolean';
    if(legacy)good=good && parts[2]===value.noteId && parts[3]===value.day && ['remembered','skipped','shown'].every(name=>Number.isSafeInteger(value[name]) && number(name,0,1e6)) && (value.lastShown==null || iso(value.lastShown));
    if(theme)good=good && ['warmNeutral','mono','pink'].includes(value.name);
    if(reading || noteReading){
      good=good && number('progress',0,1) && (value.anchor==null || typeof value.anchor==='string') && (value.title==null || typeof value.title==='string');
      if(noteReading)good=good && parts[1]===value.noteId;
      if(reading)try{const url=new URL(value.url);good=good && url.protocol==='https:' && !url.username && !url.password;}catch{good=false;}
    }
    if(!good)throw new Error("学习状态内容无效。");
  }
  async function setStates(entries) {
    if (!Array.isArray(entries) || entries.length>128) throw new Error("学习状态批次无效。");
    entries.forEach(checkStateInput);
    await tx(["state","stateOutbox"],"readwrite",async({state,stateOutbox})=>{
      for(const {key,value,deleted=false} of entries){
        const old=await read(state.get(key));
        if (/^(event|legacy)\//.test(key) && old) continue;
        if (old && JSON.stringify(old.value)===JSON.stringify(value) && !!old.deleted===deleted) continue;
        await read(state.put({key,value:deleted?null:value,deleted}));
        await read(stateOutbox.add({operationId:uuid(),key,value:deleted?null:value,deleted}));
      }
    });
    await changed();schedule();
  }
  function setState(key,value,options={}) {return setStates([{key,value,...options}]);}
  async function syncState() {
    for (;;) {
      const [state,queued] = await tx(["meta","stateOutbox"],"readonly",({meta,stateOutbox})=>
        Promise.all([read(meta.get("profile")),read(stateOutbox.getAll())]));
      for (const op of queued) {
        const {keyId,...wire}=op;
        const receipt = await authorised("state/operations",null,wire);
        if (receipt.key !== op.key || !Number.isSafeInteger(receipt.seq) || receipt.seq < 1) throw invalidReply();
        await tx(["stateOutbox"],"readwrite",({stateOutbox})=>read(stateOutbox.delete(op.keyId)));
      }
      const after = state.stateCursor || 0;
      let page;
      try {page=await authorised("state/changes?after="+after,null);}
      catch(problem){
        if(problem.status===409) await tx(["meta"],"readwrite",async({meta})=>{
          const latest=await read(meta.get("profile"));latest.needsRecovery=true;await read(meta.put(latest,"profile"));
        });
        throw problem;
      }
      if (!Array.isArray(page.changes) || typeof page.hasMore !== "boolean" || !Number.isSafeInteger(page.cursor) || page.cursor < after)
        throw invalidReply();
      const applied = await tx(["meta","state","stateOutbox"],"readwrite",async({meta,state:table,stateOutbox})=>{
        const latest=await read(meta.get("profile"));
        if ((latest.stateCursor || 0)!==after || await read(stateOutbox.count())) return false;
        let seq=after;
        for (const row of page.changes) {
          if (!Number.isSafeInteger(row.seq) || row.seq<=seq || row.seq>page.cursor || typeof row.key!=="string" || typeof row.deleted!=="boolean") throw invalidReply();
          seq=row.seq;
          if (row.deleted) await read(table.delete(row.key));
          else await read(table.put({key:row.key,value:row.value,deleted:false}));
        }
        if (seq!==page.cursor || (page.hasMore && !page.changes.length)) throw invalidReply();
        latest.stateCursor=seq;
        await read(meta.put(latest,"profile")); return true;
      });
      if (applied && !page.hasMore) break;
    }
  }
  async function moveNote(pathInput,newInput,expected) {
    const path=checkPath(pathInput), destination=checkPath(newInput);
    await tx(["notes","outbox"],"readwrite",async({notes,outbox})=>{
      const old=await read(notes.index("pathKey").get(path.toUpperCase()));
      if (!old || old.markdown!==expected) throw new Error("笔记已更新，请重新打开后再移动。");
      const occupied=await read(notes.index("pathKey").get(destination.toUpperCase()));
      if (occupied && occupied.id!==old.id) throw new Error("目标路径已有笔记。");
      const note={...old,path:destination,pathKey:destination.toUpperCase()};
      await read(notes.put(note)); await read(outbox.add(operation(note)));
    });
    await changed(); await refreshLinks(); schedule();
  }
  async function trashNote(pathInput,expected) {
    const path=checkPath(pathInput);
    await tx(["notes","outbox","trash"],"readwrite",async({notes,outbox,trash})=>{
      const old=await read(notes.index("pathKey").get(path.toUpperCase()));
      if (!old || old.markdown!==expected) throw new Error("笔记已更新，请重新打开后再删除。");
      await read(trash.put({id:uuid(),noteId:old.id,path:old.path,markdown:old.markdown,regions:old.regions,added:old.added,deletedAt:Date.now()}));
      const note={...old,markdown:"",deleted:true,regions:null}; delete note.pathKey;
      await read(notes.put(note)); await read(outbox.add(operation(note)));
    });
    await changed(); await refreshLinks(); schedule();
  }
  async function restoreNote(id) {
    await tx(["notes","outbox","trash"],"readwrite",async({notes,outbox,trash})=>{
      const item=await read(trash.get(id));
      if (!item) throw new Error("回收记录已变化，请刷新后重试。");
      if (await read(notes.index("pathKey").get(item.path.toUpperCase()))) throw new Error("原路径已有笔记，请先将其移动后再恢复。");
      const tombstone=await read(notes.get(item.noteId));
      const note={id:item.noteId,path:item.path,pathKey:item.path.toUpperCase(),markdown:item.markdown,regions:item.regions,added:item.added,deleted:false,version:tombstone?.version||0,cloudKnown:tombstone?.cloudKnown || (tombstone?.version||0)>0};
      await read(notes.put(note)); await read(outbox.add(operation(note))); await read(trash.delete(id));
    });
    await changed(); await refreshLinks(); schedule();
  }
  async function importLegacy(legacy,{roaming=null}={}) {
    if(!identity || !legacy || (legacy.profile.vaultId && legacy.profile.vaultId!==identity.vaultId))return false;
    const sameVault=legacy.profile.vaultId===identity.vaultId;
    let migration=null;
    if(sameVault)try{migration=prepareLegacyRoaming(legacy,roaming);migration.entries.forEach(checkStateInput);}
    catch{migration={entries:[],status:'invalid',sourceDeviceId:'web-'+legacy.profile.scope};}
    const imported=await tx(["meta","notes","outbox","state","stateOutbox"],"readwrite",async({meta,notes,outbox,state,stateOutbox})=>{
      const prior=await read(meta.get('legacyImported'));
      if(prior && prior.scope!==legacy.profile.scope)return false;
      if(!prior){
        if(await read(notes.count()))return false;
        for(const note of legacy.notes)await read(notes.put(note));
        for(const op of legacy.outbox)await read(outbox.add(op));
        const profile=await read(meta.get('profile'));profile.cursor=legacy.profile.cursor||0;await read(meta.put(profile,'profile'));
        await read(meta.put({scope:legacy.profile.scope,at:Date.now()},'legacyImported'));
      }
      const migrated=await read(meta.get('legacyRoamingMigration:v1'));
      if(migration && (!migrated || migrated.status==='invalid')){
        for(const entry of migration.entries){
          if(await read(state.get(entry.key)))continue;
          await read(state.put({...entry,deleted:false}));
          await read(stateOutbox.add({...entry,deleted:false,operationId:uuid()}));
        }
        await read(meta.put({sourceDeviceId:migration.sourceDeviceId,status:migration.status,at:Date.now()},'legacyRoamingMigration:v1'));
      }
      return true;
    });
    if(imported){await changed();schedule();}return imported;
  }
  function onVisible() {
    if (document.visibilityState === "visible") reload().then(schedule).catch(console.error);
  }
  if (channel) channel.onmessage = () => { reload().catch(console.error); };
  await reload();
  let interval;
  if (autoSync) {
    window.addEventListener("online", schedule);
    document.addEventListener("visibilitychange", onVisible);
    interval = setInterval(() => { if (document.visibilityState === "visible") schedule(); }, 60000);
    schedule();
  }
  return {
    notes: () => cache,
    stateEntries: () => stateCache.map(row => ({...row})),
    setState, setStates, moveNote, trashNote, restoreNote,
    trashItems: () => trashCache.slice().sort((a,b)=>b.deletedAt-a.deletedAt),
    importLegacy,
    scope: () => profile.scope,
    status: () => ({vaultId: profile.vaultId, bound: !!profile.token || !!profile.accountMode, pending: pending + statePending, lastSync: profile.lastSync,
      syncing, error, needsRecovery: profile.needsRecovery, needsBinding: !!profile.needsBinding}),
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    saveNote: save, deleteNote: trashNote,
    bind, pair, sync, recover, refreshLinks,
    close() {
      closed = true;
      clearInterval(interval);
      clearTimeout(timer);
      window.removeEventListener("online", schedule);
      document.removeEventListener("visibilitychange", onVisible);
      listeners.clear();
      channel?.close();
      db.close();
    }
  };
}

// Only used once to claim an existing paired vault; account databases never store tokens.
export async function readLegacyStore() {
  const db=await openDatabase(name);
  try {
    return await transaction(db,["meta","notes","outbox"],"readonly",async({meta,notes,outbox})=>({
      profile:await read(meta.get("profile")) || {}, notes:await read(notes.getAll()),outbox:await read(outbox.getAll())
    }));
  } finally {db.close();}
}
