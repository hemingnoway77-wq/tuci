// Exercise two independent IndexedDB notebooks against the real v1 service.
// Run in a disposable browser origin with two invitations from separate test vaults.
import { openStore } from "../storage.js";
import { regionHashes } from "../managed-links.js";

export async function testStorage(firstInvite, otherInvite) {
  const assert = (condition, message) => { if (!condition) throw Error(message); };
  const dbName = crypto.randomUUID();
  let a = await openStore({dbName, autoSync:false});
  const b = await openStore({dbName:crypto.randomUUID(), autoSync:false});
  const originalFetch = window.fetch;
  try {
    const original = "手写笔记\n[[tale]]";
    await a.saveNote("语言/learn.md", original);
    // Browser restarts before binding; both content and pending operation survive.
    a.close();
    a = await openStore({dbName, autoSync:false});
    assert(a.status().pending === 1 && a.notes()[0].markdown === original, "lost offline changes");
    await a.bind(firstInvite);
    assert(a.status().pending === 0 && a.status().bound, "failed to bind and push");
    await b.bind(await a.pair());
    assert(b.notes()[0].markdown === original, "missing remote markdown");
    await a.saveNote("语言/learn.md", "网页的编辑", undefined, original);
    await b.saveNote("语言/learn.md", "另一台设备的编辑", undefined, original);
    await b.sync();
    await a.sync();
    await b.sync();
    assert(a.notes().length === 2 && b.notes().length === 2, "missing conflict copy");
    assert(b.notes().some(n => n.path.includes("冲突") && n.markdown === "网页的编辑"), "lost conflicting local edit");

    await a.saveNote("重试.md", "第一次写入");
    let sent = false;
    window.fetch = async (...args) => {
      const answer = await originalFetch(...args);
      if (!sent && String(args[0]).endsWith("/operations")) {
        sent = true;
        throw new TypeError("lost acknowledgment");
      }
      return answer;
    };
    await a.sync().then(() => { throw Error("expected simulated disconnect"); }, () => {});
    assert(sent && a.status().pending === 1, "missing stable outbox after interrupted push");
    window.fetch = originalFetch;
    await a.saveNote("重试.md", "第二次写入");
    await a.sync();
    await b.sync();
    assert(b.notes().length === 3 && b.notes().find(n => n.path === "重试.md")?.markdown === "第二次写入", "lost retry edit");
    await a.deleteNote("重试.md", "第二次写入");
    await a.sync();
    await b.sync();
    assert(b.notes().length === 2, "delete did not propagate");

    let rejected = false;
    try { await a.bind(otherInvite); } catch { rejected = true; }
    assert(rejected && a.status().vaultId === b.status().vaultId && a.notes().length === 2, "switched vaults");
    let staleRejected = false;
    try { await a.saveNote("语言/learn.md", "过时编辑", undefined, original); } catch { staleRejected = true; }
    assert(staleRejected, "stale form overwrote a newer note");
    const denyNextPull = status => {
      window.fetch = async (...args) => {
        if (String(args[0]).includes("/changes?")) {
          window.fetch = originalFetch;
          return new Response(JSON.stringify({error:"Test server rejected this pull."}), {status});
        }
        return originalFetch(...args);
      };
    };
    denyNextPull(401);
    await a.sync().then(() => { throw Error("expected expired credential error"); }, () => {});
    a.close();
    a = await openStore({dbName, autoSync:false});
    assert(a.status().needsBinding && a.notes().length === 2, "did not persist need to repair binding");
    await a.bind(await b.pair());
    assert(!a.status().needsBinding && a.status().bound, "same-vault re-pair failed");

    denyNextPull(409);
    await a.sync().then(() => { throw Error("expected restored history error"); }, () => {});
    assert(a.status().needsRecovery && a.notes().length === 2, "did not preserve notes on rollback");
    await a.recover();
    assert(!a.status().needsRecovery && a.status().pending === 0 && a.notes().length === 2, "recovery lost notes");
    const source = "# visit\n\n#### 例句\n1. We visit the library daily.\n\n#### 我的笔记\nlibrary";
    await a.saveNote("动词/visit.md", source, await regionHashes(source));
    await a.saveNote("名词/library.md", "# library");
    const linked = source.replace("library daily", "[[名词/library|library]] daily");
    assert(a.notes().find(n=>n.path === "动词/visit.md")?.markdown === linked, "automatic back-link missing");
    assert(await a.refreshLinks() === 0, "manual refresh should be idempotent");
    await a.sync();
    await b.sync();
    assert(b.notes().find(n=>n.path === "动词/visit.md")?.markdown === linked, "back-link did not sync");
    await a.deleteNote("名词/library.md");
    assert(a.notes().find(n=>n.path === "动词/visit.md")?.markdown === source, "removed target stayed linked");
    return "managed back-link sync, re-pair, rollback recovery, offline persistence, pairing, merge conflicts, dropped response, chained edit, deletion, vault isolation and stale forms passed";
  } finally {
    window.fetch = originalFetch;
    a.close();
    b.close();
  }
}

// Existing Windows fixture creates a protected Markdown note, issues a pairing
// code, then checks that a different browser note arrives byte-for-byte.
export async function testWindowsPairing(code, markdown) {
  const store = await openStore({dbName:crypto.randomUUID(), autoSync:false});
  try {
    await store.saveNote("动词/introduced.md", markdown);
    const originalId = store.notes()[0].id;
    await store.bind(code);
    const [merged] = store.notes();
    if (store.notes().length !== 1 || merged.id === originalId || merged.markdown !== markdown || !merged.regions?.examples)
      throw Error("first-bind merge did not retain Windows ID, protected sections and body");
    const localHashes = await regionHashes(markdown);
    if (Object.entries(merged.regions).some(([key,hash]) => localHashes[key] !== hash))
      throw Error("web section hash differs from Windows");
    await store.saveNote("随笔/读书.md", "我在手机离线时写的原句。");
    await store.sync();
    return "Windows seed -> WebKit identical first-bind merge -> Windows pull";
  } finally { store.close(); }
}
