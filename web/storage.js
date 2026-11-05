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
  const req = indexedDB.open(dbName, 1);
  req.onupgradeneeded = () => {
    const db = req.result;
    db.createObjectStore("meta");
    db.createObjectStore("notes", { keyPath: "id" }).createIndex("pathKey", "pathKey");
    db.createObjectStore("outbox", { keyPath: "key", autoIncrement: true }).createIndex("noteId", "noteId");
    db.createObjectStore("backups", { autoIncrement: true });
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

async function ack({ notes, outbox }, op, receipt) {
  if (!validId(receipt.noteId) || !version(receipt.version) || typeof receipt.conflict !== "boolean")
    throw invalidReply();
  const path = checkPath(receipt.path);
  const head = (await read(outbox.openCursor()))?.value;
  if (op.operationId !== head?.operationId) return; // Another tab already recorded this receipt.
  await read(outbox.delete(op.key));
  const local = await read(notes.get(op.noteId));
  if (!local) throw invalidReply();
  if (op.noteId !== receipt.noteId) await read(notes.delete(op.noteId));
  local.id = receipt.noteId;
  local.version = receipt.version;
  local.path = path;
  if (!local.deleted) local.pathKey = path.toUpperCase();
  await read(notes.put(local));
  // Only the FIFO head ever goes on the wire. Later edits can safely be
  // rebased onto its accepted version, even when the reply assigned a new ID.
  const later = await read(outbox.index("noteId").getAll(op.noteId));
  for (const edit of later) {
    edit.noteId = receipt.noteId;
    edit.path = path;
    edit.baseVersion = receipt.version;
    await read(outbox.put(edit));
  }
}

async function applyPage({meta, notes, outbox}, after, page) {
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
    const note = { id: item.noteId, path, markdown: item.markdown, deleted: item.deleted,
      version: item.version, regions, added: old?.added || addedDate(item.markdown) };
    if (!item.deleted) note.pathKey = path.toUpperCase();
    await read(notes.put(note));
  }
  if (seen !== page.cursor) throw invalidReply();
  state.cursor = seen;
  if (!page.hasMore) state.lastSync = Date.now();
  await read(meta.put(state, "profile"));
  return true;
}

async function request(route, token, body) {
  const headers = {};
  if (token) headers.Authorization = "Bearer " + token;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  let res;
  const abort = new AbortController();
  const timeout = setTimeout(() => abort.abort(), 30000);
  try {
    res = await fetch("/tuci-sync/v1/" + route, {
      method: body === undefined ? "GET" : "POST", headers, body: JSON.stringify(body),
      credentials: "omit", mode: "same-origin", redirect: "error", cache: "no-store", signal: abort.signal
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

export async function openStore({ dbName = name, autoSync = true } = {}) {
  const db = await openDatabase(dbName);
  const tx = (tables, mode, fn) => transaction(db, tables, mode, fn);
  await tx(["meta"], "readwrite", async ({meta}) => {
    if (!await read(meta.get("profile"))) await read(meta.put({
      scope: uuid(), vaultId: null, token: null, cursor: 0, lastSync: 0, needsRecovery: false
    }, "profile"));
  });
  const channel = typeof BroadcastChannel === "function" ? new BroadcastChannel(dbName) : null;
  const listeners = new Set();
  let profile, pending, cache = [];
  let inFlight = null, syncing = false, error = "", timer = null, closed = false;
  const notify = () => listeners.forEach(fn => { try { fn(); } catch (e) { console.error(e); } });
  async function reload() {
    const data = await tx(stores, "readonly", ({meta, notes, outbox}) =>
      Promise.all([read(meta.get("profile")), read(notes.getAll()), read(outbox.count())]));
    [profile, , pending] = data;
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
      const note = { id: old?.id || uuid(), path: old?.path || path, version: old?.version ?? 0,
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
      return await request(route, token, body);
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
        if (!state.token) break;
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
          await tx(["notes","outbox"], "readwrite", tables => ack(tables, op, receipt));
          if (receipt.conflict) conflicts++;
          mutated = true;
          continue;
        }
        const page = await pull();
        checkedHistory = true;
        const applied = await tx(stores, "readwrite", tables => applyPage(tables, state.cursor, page));
        if (applied) mutated = true;
        if (applied && !page.hasMore) break;
      }
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
    await tx([...stores, "backups"], "readwrite", async ({meta, notes, outbox, backups}) => {
      const state = await read(meta.get("profile"));
      if (!state.needsRecovery) return;
      const all = await read(notes.getAll());
      await read(backups.add({cursor: state.cursor, captured: Date.now(), notes: all, outbox: await read(outbox.getAll())}));
      await read(outbox.clear());
      await read(notes.clear());
      for (const note of all.filter(n => !n.deleted)) {
        note.version = 0;
        await read(notes.put(note));
        await read(outbox.add(operation(note)));
      }
      state.cursor = 0;
      state.needsRecovery = false;
      await read(meta.put(state, "profile"));
    });
    await changed();
    return sync();
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
    scope: () => profile.scope,
    status: () => ({vaultId: profile.vaultId, bound: !!profile.token, pending, lastSync: profile.lastSync,
      syncing, error, needsRecovery: profile.needsRecovery, needsBinding: !!profile.needsBinding}),
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    saveNote: save, deleteNote: (path, expected) => save(path, "", null, expected, true),
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
