// Notebook and review views share the Markdown files used by Windows and iPhone.
const dayMs = 86_400_000;
const normalize = value => value.normalize("NFC").toLowerCase();
const basename = path => path.split("/").pop();
const folderOf = path => path.slice(0, Math.max(0, path.lastIndexOf("/")));
const byName = new Intl.Collator("zh-CN", { numeric: true, sensitivity: "base" }).compare;

export function describeNote(note) {
  const markdown = note.markdown.replace(/\r\n?/g, "\n").replace(/^\uFEFF/, "");
  const front = /^---\n([\s\S]*?)^---[ \t]*(?:\n|$)/m.exec(markdown);
  const hasFront = front?.index === 0;
  const body = hasFront ? markdown.slice(front[0].length) : markdown;
  const created = hasFront && /^created:[ \t]*['"]?([^'"\s]+)['"]?[ \t]*$/m.exec(front[1]);
  const title = basename(note.path).replace(/\.md$/i, "").replace(/^_(con|aux|prn|nul)$/i, "$1");
  const meaning = /^\*\*释义\*\*[：:]?[^\n]*\n(?:[ \t]*\n)*((?:>[^\n]*(?:\n|$))+)/m.exec(body);
  return {
    path: note.path, title, folder: folderOf(note.path), body,
    id: note.id || note.path,
    added: Date.parse(String(created?.[1] || note.added || "").replace(/(\.\d{3})\d+/, "$1")),
    word: !note.path.startsWith("词根/") && title.length <= 64 && /^[a-z]+(?:[-'][a-z]+)*$/i.test(title),
    phonetics: /^\*\*发音\*\*[：:][ \t]*(.*)$/m.exec(body)?.[1]?.trim() || "",
    meaning: meaning?.[1].trim().split("\n").map(line => line.replace(/^>\s*/, "").trim()).join("\n") || "",
  };
}

export function resolveWiki(link, current, notes, vaultPrefix = "英语单词") {
  const raw = normalize(link.split("|")[0].split("#")[0].trim());
  if (!raw) return [];
  const path = /\.md$/.test(raw) ? raw : raw + ".md";
  if (!path.includes("/")) return notes.filter(note => normalize(basename(note.path)) === path);
  const matches = name => notes.filter(note => normalize(note.path) === name);
  for (const name of [path, normalize(current.folder) + "/" + path]) {
    const found = matches(name);
    if (found.length) return found;
  }
  const prefix = normalize(vaultPrefix || "").replace(/^\/+|\/+$/g, "");
  return prefix && path.startsWith(prefix + "/") ? matches(path.slice(prefix.length + 1)) : [];
}

export function dateKey(now) {
  const day = new Date(now);
  return [day.getFullYear(), String(day.getMonth() + 1).padStart(2, "0"), String(day.getDate()).padStart(2, "0")].join("-");
}

export function reviewWeight(record, added, now = Date.now()) {
  const age = time => Math.max(0, (now - time) / dayMs);
  const remembered = record?.day === dateKey(now) ? record.remembered || 0 : 0;
  const skipped = record?.day === dateKey(now) ? record.skipped || 0 : 0;
  const ratio = (remembered + 1) / (remembered + skipped + 2);
  return 0.5 + 3 * Math.log1p(age(record?.lastShown ?? added)) + Math.exp(-age(added) / 3)
    + 2 * (1 - ratio) + Math.min(skipped, 3) / 3;
}

export function nextReview(history, words, finished, now = Date.now(), random = Math.random()) {
  if (finished && history.current?.exposure !== finished.exposure) return history.current;
  if (finished) {
    const record = history.records[history.current.id];
    if (record.day !== dateKey(now)) {
      Object.assign(record, { day: dateKey(now), remembered: 0, skipped: 0 });
    }
    if (finished.remembered) record.remembered += 1;
    else record.skipped += 1;
  }
  history.current = null;
  if (!words.length) return null;
  // Remember first observation for legacy notes that have no creation date.
  const weighted = words.map(note => {
    const key = "note:" + note.id;
    const record = history.records[key] ??= { firstSeen: now };
    const added = Number.isFinite(note.added) ? note.added : record.firstSeen;
    return { key, weight: reviewWeight(record, added, now) };
  });
  let needle = random * weighted.reduce((sum, choice) => sum + choice.weight, 0);
  const picked = weighted.find(choice => (needle -= choice.weight) < 0) ?? weighted.at(-1);
  history.records[picked.key].lastShown = now;
  history.current = { id: picked.key, exposure: crypto.randomUUID() };
  return history.current;
}

// Project the shared immutable events; restoring a screen never creates another visit.
export function projectReview(entries, now = Date.now()) {
  const records = {}, activity = {}, totals = new Map(), seen = new Map();
  const values = new Map(entries.filter(row => !row.deleted).map(row => [row.key, row.value]));
  function add(id, day, remembered, skipped, shown, at) {
    if (typeof id !== "string" || !id || !/^\d{4}-\d{2}-\d{2}$/.test(day || "")) return;
    const key = id + "\n" + day, old = totals.get(key) || { id, day, remembered: 0, skipped: 0, shown: 0 };
    for (const [name, amount] of Object.entries({ remembered, skipped, shown })) {
      if (Number.isSafeInteger(amount) && amount >= 0) old[name] += amount;
    }
    totals.set(key, old);
    const timestamp = typeof at === "number" ? at : Date.parse(at);
    if (Number.isFinite(timestamp) && timestamp > (seen.get(id) ?? -Infinity)) seen.set(id, timestamp);
  }
  for (const [key, value] of values) {
    if (!value || typeof value !== "object") continue;
    if (/^event\/[^/]+\/shown$/.test(key)) add(value.noteId, value.day, 0, 0, 1, value.at);
    else if (/^event\/[^/]+\/completed$/.test(key)) add(value.noteId, value.day, value.remembered ? 1 : 0, value.remembered ? 0 : 1, 0, null);
    else if (key.startsWith("legacy/")) add(value.noteId, value.day, value.remembered, value.skipped, value.shown, value.lastShown);
  }
  for (const row of totals.values()) {
    const key = "note:" + row.id, record = records[key] ??= { remembered: 0, skipped: 0 };
    if (!record.day || row.day > record.day) Object.assign(record, { day: row.day, remembered: row.remembered, skipped: row.skipped });
    if (row.shown > 0) (activity[row.day] ??= {})[row.id] = row.shown;
  }
  for (const [id, at] of seen) (records["note:" + id] ??= { remembered: 0, skipped: 0 }).lastShown = at;
  const current = values.get("roaming/current");
  const active = current && typeof current.noteId === "string" && typeof current.exposureId === "string"
    && !values.has("event/" + current.exposureId + "/completed") ? {
      id: "note:" + current.noteId, exposure: current.exposureId, at: current.at,
      weight: current.weight, weightLevel: current.weightLevel
    } : null;
  return { records, activity, current: active, today: dateKey(now) };
}

function shiftedDay(day, amount) {
  const value = new Date(day + "T12:00:00"); value.setDate(value.getDate() + amount); return dateKey(value);
}

export function roamingStatistics(history, words, now = Date.now()) {
  const today = dateKey(now), weekday = (new Date(today + "T12:00:00").getDay() + 6) % 7;
  const weekStart = shiftedDay(today, -weekday), monthStart = today.slice(0, 7) + "-01", gridStart = shiftedDay(weekStart, -77);
  const ids = new Set(words.map(note => note.id));
  const covered = start => new Set(Object.entries(history.activity || {}).filter(([day]) => day >= start && day <= today)
    .flatMap(([, counts]) => Object.keys(counts)).filter(id => ids.has(id))).size;
  return { today, weekStart, monthStart, totalWords: ids.size, weekReviewed: covered(weekStart), monthReviewed: covered(monthStart),
    days: Array.from({ length: 84 }, (_, index) => {
      const day = shiftedDay(gridStart, index), counts = history.activity?.[day] || {}, future = day > today;
      return { day, count: future ? 0 : Object.values(counts).reduce((sum, count) => sum + count, 0), unique: future ? 0 : Object.keys(counts).length, future };
    }) };
}

export function planReview(entries, words, finished = null, now = Date.now(), random = Math.random(), uuid = () => crypto.randomUUID()) {
  const history = projectReview(entries, now), writes = [];
  if (finished && history.current?.exposure !== finished.exposure) return { writes, current: history.current };
  if (!finished && words.some(note => "note:" + note.id === history.current?.id)) return { writes, current: history.current };
  const at = new Date(now).toISOString(), day = dateKey(now);
  if (finished && history.current) {
    const noteId = history.current.id.slice(5), key = "event/" + finished.exposure + "/completed";
    writes.push({ key, value: { noteId, at, day, remembered: Boolean(finished.remembered) } });
    const record = history.records[history.current.id] ??= {};
    if (record.day !== day) Object.assign(record, { day, remembered: 0, skipped: 0 });
    record[finished.remembered ? "remembered" : "skipped"] = (record[finished.remembered ? "remembered" : "skipped"] || 0) + 1;
  }
  if (!words.length) return { writes, current: null };
  const choices = words.map(note => ({ note, weight: reviewWeight(history.records["note:" + note.id], Number.isFinite(note.added) ? note.added : now, now) }));
  const unit = Number.isNaN(random) ? 0 : Math.max(0, Math.min(1, random));
  let needle = unit * choices.reduce((sum, choice) => sum + choice.weight, 0);
  const picked = choices.find(choice => (needle -= choice.weight) < 0) || choices.at(-1);
  const exposureId = uuid(), weight = picked.weight, weightLevel = Math.max(0, Math.min(1, weight / choices.reduce((max, choice) => Math.max(max, choice.weight), 0)));
  const value = { noteId: picked.note.id, exposureId, at, weight, weightLevel };
  writes.push({ key: "event/" + exposureId + "/shown", value: { noteId: picked.note.id, at, day, weight, weightLevel } },
    { key: "roaming/current", value });
  return { writes, current: { id: "note:" + picked.note.id, exposure: exposureId, at, weight, weightLevel } };
}

const sessions = new Map();
function session(ctx) {
  const key = ctx.scope();
  if (!sessions.has(key)) sessions.set(key, { stack: [{ kind: "folder", path: "", query: "", scroll: 0 }], roamPath: [] });
  return sessions.get(key);
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}
function button(text, className, action) {
  const node = element("button", className, text);
  node.type = "button";
  if (action) node.addEventListener("click", action);
  return node;
}

function icon(name) {
  const paths = {
    folder: "M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
    note: "M6 3h8l4 4v14H6z M14 3v5h4 M9 12h6 M9 16h6",
    roam: "M3 7h3c4 0 5 10 10 10h4m-3-3 3 3-3 3 M3 17h3c2 0 3-2 4-4m3-3c1-2 2-3 4-3h3m-3-3 3 3-3 3",
    search: "M16 16l4 4 M18 10a8 8 0 1 1-16 0 8 8 0 1 1 16 0",
    arrow: "M9 5l7 7-7 7", back: "M15 5l-7 7 7 7", plus: "M12 5v14 M5 12h14",
    book: "M3 4c4-1 6 0 9 2 3-2 5-3 9-2v14c-4-1-6 0-9 2-3-2-5-3-9-2z M12 6v14",
    calendar: "M7 3v4 M17 3v4 M4 9h16 M5 5h14a1 1 0 0 1 1 1v14H4V6a1 1 0 0 1 1-1z M8 13h1 M12 13h1 M16 13h1 M8 17h1 M12 17h1",
  };
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24"); svg.setAttribute("class", "nb-icon"); svg.setAttribute("aria-hidden", "true");
  const line = document.createElementNS(svg.namespaceURI, "path");
  line.setAttribute("d", paths[name]); svg.append(line);
  return svg;
}

function iconButton(text, name, run) {
  const btn = button("", "nb-icon-btn", run);
  btn.setAttribute("aria-label", text); btn.title = text; btn.append(icon(name));
  return btn;
}

function emptyState(title, message, action, run) {
  const panel = element("section", "card nb-empty");
  panel.append(icon("book"), element("h2", "", title), element("p", "", message));
  if (action) panel.append(button(action, "button secondary", run));
  return panel;
}

const markdownEntities = { "&amp;": "&", "&lt;": "<", "&gt;": ">", "&#91;": "[", "&#93;": "]" };

// Decode inline literal text only, after tokenization, so an escaped wiki link
// or HTML tag from a saved example stays ordinary, selectable text.
export function inlineLiteral(text) {
  return text.replace(/\\([\\*_#|`])|&(?:amp|lt|gt|#91|#93);/g,
    (token, escaped) => escaped ?? markdownEntities[token]);
}

function scrub(text) {
  return inlineLiteral(text.replace(/<!--[^]*?(?:-->|$)/g, "").replace(/\[\[([^\]\n]+)\]\]/g, (_, value) => value.split("|").pop().split("/").pop())
    .replace(/\*\*|__/g, "")).trim();
}

function excerpt(note) {
  if (note.meaning) return scrub(note.meaning);
  return scrub(note.body).split("\n").filter(line => line.trim() && !/^\s*(?:#|---)/.test(line)).slice(0, 4).join("\n").slice(0, 340);
}

function appendInline(parent, line, onWiki) {
  const tokens = /(\\[\\*_#|\x60]|`[^`\n]+`|\[\[[^\[\]\n]+\]\]|\*\*[^*\n]+\*\*|__[^_\n]+__|\[[^\]\n]+\]\(https?:\/\/[^\s)]+\))/gi;
  let offset = 0;
  for (const match of line.matchAll(tokens)) {
    parent.append(inlineLiteral(line.slice(offset, match.index)));
    const token = match[0];
    if (token.startsWith("[[")) {
      const target = token.slice(2, -2);
      const display = target.includes("|") ? target.split("|").pop() : basename(target);
      let timer;
      const wiki = button(inlineLiteral(display), "nb-wiki", event => {
        if (timer != null) {
          clearTimeout(timer); timer = undefined;
          onWiki(target, true);
        } else if (event.detail === 0) {
          onWiki(target, false);
        } else {
          timer = setTimeout(() => { timer = undefined; if (wiki.isConnected) onWiki(target, false); }, 350);
        }
      });
      parent.append(wiki);
    } else if (token.startsWith("\\")) {
      parent.append(inlineLiteral(token));
    } else if (token.startsWith("`")) {
      parent.append(element("code", "", token.slice(1, -1)));
    } else if (token.startsWith("[")) {
      const bits = /^\[([^\]]+)\]\((.*)\)$/.exec(token);
      const link = element("a", "", inlineLiteral(bits[1]));
      link.href = bits[2]; link.target = "_blank"; link.rel = "noopener noreferrer";
      parent.append(link);
    } else {
      const strong = element("strong");
      appendInline(strong, token.slice(2, -2), onWiki);
      parent.append(strong);
    }
    offset = match.index + token.length;
  }
  parent.append(inlineLiteral(line.slice(offset)));
}

function noteBody(note, onWiki) {
  const article = element("article", "nb-markdown");
  let pre, fence, comment = false;
  for (const raw of note.body.split("\n")) {
    const line = raw.trim();
    if (fence) {
      if (line.startsWith(fence)) { fence = null; pre = null; }
      else pre.append(raw + "\n");
      continue;
    }
    if (comment) { if (line.includes("-->")) comment = false; continue; }
    if (line.startsWith("<!--")) { comment = !line.includes("-->"); continue; }
    if (/^(?:```|~~~)/.test(line)) { fence = line.slice(0, 3); pre = element("pre"); article.append(pre); continue; }
    if (!line || line === "---" || (line.startsWith("# ") && normalize(line.slice(2)) === normalize(note.title))) continue;
    const heading = /^(#{1,6})\s+(.+)$/.exec(line);
    const bullet = /^(?:[-*]|(\d+)\.)\s+(.*)$/.exec(line);
    let tag = "p", text = line, css = "";
    if (heading) { tag = heading[1].length <= 4 ? "h2" : "h3"; text = heading[2]; }
    else if (line.startsWith(">")) { tag = "blockquote"; text = line.replace(/^>\s*/, ""); }
    else if (bullet) { text = (bullet[1] ? bullet[1] + ". " : "• ") + bullet[2]; css = /^\s/.test(raw) ? "nb-subitem" : "nb-item"; }
    const node = element(tag, css);
    appendInline(node, text, onWiki);
    article.append(node);
  }
  return article;
}

function modal(ctx, title) {
  const dialog = element("dialog", "nb-dialog");
  dialog.setAttribute("aria-label", title);
  const close = () => dialog.close();
  const header = element("div", "nb-dialog-head");
  header.append(element("span", "", title), button("关闭", "nb-text-btn", close));
  dialog.append(header);
  dialog.addEventListener("click", event => { if (event.target === dialog) {
    const rect = dialog.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) close();
  }});
  dialog.addEventListener("close", () => dialog.remove(), { once: true });
  ctx.root.append(dialog);
  return dialog;
}

function settings(ctx) {
  return (typeof ctx.settings === "function" ? ctx.settings() : ctx.settings) || {};
}

function follow(ctx, source, link, double, open) {
  const matches = resolveWiki(link, source, ctx.notes().map(describeNote), settings(ctx).vaultPrefix);
  if (matches.length === 1 && double) { open(matches[0].path); return; }
  const popup = modal(ctx, matches.length > 1 ? "选择笔记" : "词条卡片");
  function preview(note) {
    popup.append(element("h2", "nb-word", note.title));
    if (note.phonetics) popup.append(element("p", "nb-sound", inlineLiteral(note.phonetics)));
    popup.append(element("p", "nb-meaning", excerpt(note) || "这篇笔记暂无正文。"),
      button("打开笔记", "button", () => { popup.close(); open(note.path); }));
  }
  if (matches.length === 1) preview(matches[0]);
  else if (!matches.length) popup.append(element("p", "", "词库中找不到这篇笔记"), element("small", "nb-meta", link.split("|")[0]));
  else {
    const list = element("div", "nb-choices");
    for (const note of matches) list.append(button(note.path, "nb-list-button", () => {
      if (double) { popup.close(); open(note.path); }
      else { list.remove(); preview(note); }
    }));
    popup.append(list);
  }
  popup.showModal();
}

function editView(ctx, frame, note, back, done) {
  const head = element("header", "page-head nb-head");
  head.append(element("h1", "", note ? "编辑笔记" : "新建笔记"), button("取消", "nb-text-btn", back));
  const form = element("form", "panel nb-edit");
  const pathLabel = element("label", "field", "笔记路径");
  const path = element("input"); path.required = true; path.type = "text"; path.autocapitalize = "off";
  path.placeholder = "摘记/阅读随想.md"; path.value = frame.path; path.readOnly = Boolean(note);
  path.oninput = () => { frame.path = path.value; };
  pathLabel.append(path);
  const source = element("textarea"); source.rows = 14; source.value = frame.source; source.placeholder = "写下正文，可用 [[词条名]] 关联笔记";
  source.setAttribute("aria-label", "笔记正文"); source.oninput = () => { frame.source = source.value; };
  const save = element("button", "button", "保存笔记"); save.type = "submit";
  const status = element("p", "nb-error"); status.setAttribute("role", "alert");
  form.append(pathLabel, source, status, save);
  form.addEventListener("submit", async event => {
    event.preventDefault();
    const value = path.value.trim();
    if (!value) return;
    const dest = /\.md$/i.test(value) ? value : value + ".md";
    const latest = ctx.notes().find(n => normalize(n.path) === normalize(dest));
    if ((!note && latest) || (note && (!latest || latest.markdown !== frame.original))) {
      status.textContent = note ? "这篇笔记已在其他地方变更。请复制你的改动，重新打开后再合并。" : "这个路径已经有一篇笔记。";
      return;
    }
    save.disabled = true;
    try {
      await ctx.saveNote(dest, source.value, undefined, note ? frame.original : null);
      done(dest); ctx.toast("笔记已保存");
    } catch (error) { status.textContent = error.message; }
    finally { save.disabled = false; }
  });
  ctx.root.append(head, form);
}

function showNote(ctx, note, back, open, edit, move) {
  const header = element("header", "nb-detail-head");
  header.append(iconButton("返回", "back", back), element("span", "nb-meta", note.folder || "词库"));
  if (edit) {
    const actions = element("div", "nb-note-actions");
    if (move) actions.append(button("移动 / 改名", "nb-text-btn", move));
    actions.append(button("编辑", "nb-text-btn", edit)); header.append(actions);
  }
  const content = noteBody(note, (link, double) => follow(ctx, note, link, double, open));
  ctx.root.append(header, element("h1", "nb-note-title", note.title), content);
  return content;
}

function deletePrompt(ctx, frame, back) {
  const dialog = modal(ctx, "移入回收站");
  dialog.append(element("p", "", "将「" + basename(frame.path) + "」移入回收站？之后可恢复，其他设备的词库也会同步移除。"));
  const submit = button("移入回收站", "button nb-danger", async () => {
    submit.disabled = true;
    try { await (ctx.trashNote || ctx.deleteNote)(frame.path, frame.original); dialog.close(); back(); ctx.toast("已移入回收站"); }
    catch (error) { ctx.toast(error.message); submit.disabled = false; }
  });
  dialog.append(submit);
  dialog.showModal();
}

function movePrompt(ctx, raw, moved) {
  const dialog = modal(ctx, "移动 / 改名"), form = element("form", "nb-edit");
  const label = element("label", "field", "完整笔记路径"), input = element("input");
  input.value = raw.path; input.required = true; input.autocapitalize = "off"; input.spellcheck = false;
  label.append(input);
  const hint = element("p", "nb-meta", "修改文件名可改名；在文件名前填写目录可移动。"), error = element("p", "nb-error");
  error.setAttribute("role", "alert");
  const submit = button("保存路径", "button"); submit.type = "submit";
  form.append(label, hint, error, submit); dialog.append(form);
  form.addEventListener("submit", async event => {
    event.preventDefault(); let path = input.value.trim(); if (!path) return;
    if (!/\.md$/i.test(path)) path += ".md";
    submit.disabled = true;
    try { await ctx.moveNote(raw.path, path, raw.markdown); dialog.close(); moved(path); ctx.toast("笔记路径已更新"); }
    catch (problem) { error.textContent = problem.message; submit.disabled = false; }
  });
  dialog.showModal(); input.focus();
}

function trashView(ctx, back, redraw) {
  const head = element("header", "nb-detail-head");
  head.append(iconButton("返回词库", "back", back), element("h1", "", "回收站")); ctx.root.append(head);
  const items = ctx.trashItems?.() || [];
  if (!items.length) { ctx.root.append(emptyState("回收站是空的", "移除的笔记会暂存在这里，随时可以恢复。")); return; }
  ctx.root.append(element("p", "nb-meta", "回收记录保存在此设备。恢复后，笔记会重新同步到其他设备。"));
  const list = element("section", "nb-index");
  for (const item of items) {
    const row = element("div", "nb-trash-row"), label = element("div", "nb-row-label");
    label.append(element("span", "", item.path), element("small", "", new Date(item.deletedAt).toLocaleString("zh-CN", { dateStyle: "medium", timeStyle: "short" })));
    const restore = button("恢复", "nb-text-btn", async () => {
      restore.disabled = true;
      try { await ctx.restoreNote(item.id); ctx.toast("笔记已恢复"); redraw(); }
      catch (problem) { ctx.toast(problem.message); restore.disabled = false; }
    });
    row.append(label, restore); list.append(row);
  }
  ctx.root.append(list);
}

function libraryRow(name, kind, detail, run) {
  const row = button("", "nb-list-button", run);
  const marker = element("span", "nb-row-icon"); marker.append(icon(kind));
  const label = element("span", "nb-row-label");
  label.append(element("span", kind === "note" ? "nb-file-name" : "", name));
  if (detail) label.append(element("small", "", detail));
  row.append(marker, label, icon("arrow"));
  return row;
}

function directory(ctx, frame, back, enter, edit, openTrash) {
  const notes = ctx.notes().map(describeNote);
  const prefix = frame.path ? frame.path + "/" : "";
  const under = notes.filter(note => note.path.startsWith(prefix));
  const children = new Map();
  for (const note of under) {
    const relative = note.path.slice(prefix.length);
    if (relative.includes("/")) {
      const name = relative.split("/")[0]; children.set(name, (children.get(name) || 0) + 1);
    }
  }
  if (frame.path) {
    const breadcrumb = element("div", "nb-detail-head");
    breadcrumb.append(iconButton("上一级目录", "back", back), element("span", "nb-meta", "词库 / " + frame.path));
    ctx.root.append(breadcrumb);
  }
  const header = element("header", "page-head nb-head");
  const heading = element("div"); heading.append(element("h1", "", frame.path ? basename(frame.path) : "词库"));
  const actions = element("div", "nb-note-actions");
  if (openTrash) actions.append(button("回收站", "nb-text-btn", openTrash));
  actions.append(iconButton("新建笔记", "plus", () => edit(prefix))); header.append(heading, actions);
  ctx.root.append(header);
  if (!notes.length) {
    ctx.root.append(emptyState("词库还是空的", "收录第一个单词，或在设置中连接已有词库。", "去收录", () => ctx.navigate("capture")));
    return;
  }
  const searchLabel = element("label", "nb-search"); searchLabel.append(icon("search"));
  const search = element("input"); search.type = "search"; search.setAttribute("aria-label", "搜索词条或笔记正文");
  search.placeholder = "搜索词条或笔记正文"; search.value = frame.query || ""; searchLabel.append(search);
  ctx.root.append(searchLabel);
  const list = element("section", "nb-directory"); ctx.root.append(list);
  function filtered() {
    const q = normalize(frame.query || "").trim(); list.replaceChildren();
    if (!q && !frame.path && notes.some(note => note.word)) {
      const roam = libraryRow("漫游复习", "roam", "按记忆情况抽取下一张词卡", () => ctx.navigate("roam"));
      roam.classList.add("nb-roam-entry"); list.append(roam);
    }
    const entries = (q ? under.filter(note => normalize(note.title).includes(q) || normalize(note.body).includes(q))
      : under.filter(note => note.folder === frame.path)).sort((a, b) => byName(a.path, b.path));
    const names = q ? [] : [...children.keys()].sort(byName);
    if (!names.length && !entries.length) {
      list.append(emptyState(q ? "没有找到相关笔记" : "这个目录还是空的", q ? "试试其他词条或正文关键词。" : "可以在这里新建一篇笔记。"));
      return;
    }
    const section = element("div", "nb-index");
    list.append(element("h2", "section-title nb-section-title", q ? "搜索结果 · " + entries.length : "笔记目录"), section);
    for (const name of names) section.append(libraryRow(name, "folder", String(children.get(name)) + " 篇笔记", () => enter("folder", prefix + name)));
    for (const note of entries) section.append(libraryRow(note.title, "note", q ? note.path : scrub(note.meaning).split("\n")[0], () => enter("note", note.path)));
  }
  search.addEventListener("input", () => { frame.query = search.value; filtered(); });
  filtered();
}

function rememberScroll(frame) { frame.scroll = window.scrollY; }
function restoreScroll(frame) {
  window.scrollTo({ top: frame.scroll || 0, behavior: "auto" });
}

export function noteReadingPosition(blocks, scrollY, height, viewport) {
  const progress = Math.max(0, Math.min(1, scrollY / Math.max(1, height - viewport)));
  let index = 0;
  for (let i = 0; i < blocks.length; i++) if (blocks[i].getBoundingClientRect().top <= 110) index = i;
  return { progress, anchor: "p:" + index };
}

function mountNoteProgress(ctx, note, content, frame) {
  if (!ctx.setState || !ctx.stateEntries || !note.id) return () => {};
  const scope = ctx.scope(), key = "note-reading/" + note.id, blocks = [...content.children];
  let active = true, ready = false, dirty = false, touched = false, restored = false, timer, animation;
  const canRestore = !frame.progressInitialized;
  const snapshot = () => ({ noteId: note.id, title: note.title,
    ...noteReadingPosition(blocks, window.scrollY, document.documentElement.scrollHeight, window.innerHeight) });
  const save = () => {
    clearTimeout(timer);
    if (!active || !ready || !dirty || ctx.scope() !== scope) return;
    dirty = false;
    ctx.setState(key, snapshot()).catch(() => { if (active) { dirty = true; ctx.toast("阅读位置暂未保存，稍后会重试。"); } });
  };
  const scroll = () => { if (!ready) return; touched = true; dirty = true; clearTimeout(timer); timer = setTimeout(save, 700); };
  const hide = () => { if (document.visibilityState === "hidden") save(); };
  function restoreSaved() {
    if (!active || !ready || !canRestore || restored || touched || ctx.scope() !== scope || !content.isConnected) return;
    const saved = ctx.stateEntries().find(row => row.key === key && !row.deleted)?.value;
    if (!saved) return;
    const anchor = /^(?:p|paragraph):(\d+)$/.exec(saved.anchor || ""), block = anchor && blocks[Number(anchor[1])];
    const progress = Math.max(0, Math.min(1, Number(saved.progress) || 0));
    if (block && progress > 0) window.scrollTo({ top: Math.max(0, window.scrollY + block.getBoundingClientRect().top - 90), behavior: "auto" });
    else window.scrollTo({ top: progress * Math.max(0, document.documentElement.scrollHeight - window.innerHeight), behavior: "auto" });
    restored = true;
  }
  const unsubscribe = ctx.subscribeState?.(restoreSaved) || (() => {});
  animation = requestAnimationFrame(() => {
    if (!active || ctx.scope() !== scope || !content.isConnected) return;
    frame.progressInitialized = true; ready = true; restoreSaved();
    window.addEventListener("scroll", scroll, { passive: true });
    window.addEventListener("pagehide", save); document.addEventListener("visibilitychange", hide);
  });
  return () => {
    save(); active = false; clearTimeout(timer); cancelAnimationFrame(animation); unsubscribe();
    window.removeEventListener("scroll", scroll); window.removeEventListener("pagehide", save); document.removeEventListener("visibilitychange", hide);
  };
}

export function renderLibrary(ctx, path) {
  const state = session(ctx);
  const stack = state.stack;
  if (path && stack.at(-1)?.path !== path) stack.push({ kind: "note", path, scroll: 0 });
  let displayed, cleaned = false, disposeProgress = () => {};
  const back = () => { if (stack.length > 1) stack.pop(); draw(); };
  function enter(kind, nextPath) {
    rememberScroll(stack.at(-1));
    stack.push({ kind, path: nextPath, query: "", scroll: 0 });
    draw();
  }
  function edit(path, note) {
    rememberScroll(stack.at(-1));
    stack.push({ kind: "edit", path, editing: Boolean(note), original: note?.markdown, source: note?.markdown || "", scroll: 0 });
    draw();
  }
  function draw() {
    if (cleaned) return;
    disposeProgress(); disposeProgress = () => {};
    const frame = stack.at(-1);
    displayed = frame;
    ctx.root.replaceChildren();
    const raw = ctx.notes().find(note => frame.noteId && note.id === frame.noteId || note.path === frame.path);
    if (raw && frame.kind === "note") { frame.noteId = raw.id; frame.path = raw.path; }
    if (frame.kind === "folder") directory(ctx, frame, back, enter, edit, () => enter("trash", ""));
    else if (frame.kind === "trash") trashView(ctx, back, draw);
    else if (frame.kind === "edit") {
      editView(ctx, frame, frame.editing, back, dest => {
        stack.pop();
        if (stack.at(-1)?.kind !== "note") stack.push({ kind: "note", path: dest, scroll: 0 });
        draw();
      });
      if (frame.editing) ctx.root.append(button("移入回收站", "nb-delete", () => deletePrompt(ctx, frame, () => {
        stack.pop(); if (stack.at(-1)?.kind === "note") stack.pop(); draw();
      })));
    } else if (raw) {
      const note = describeNote(raw);
      const content = showNote(ctx, note, back, nextPath => enter("note", nextPath), () => edit(note.path, raw),
        ctx.moveNote ? () => movePrompt(ctx, raw, destination => {
          for (const item of stack) if (item.path === raw.path) item.path = destination;
          draw();
        }) : null);
      restoreScroll(frame); disposeProgress = mountNoteProgress(ctx, note, content, frame); return;
    } else ctx.root.append(emptyState("这篇笔记已移除", "返回目录查看其他笔记。", "返回", back));
    restoreScroll(frame);
  }
  function onNotesChanged() {
    if (displayed?.kind === "edit" || ctx.root.querySelector("dialog[open]")) return;
    const input = ctx.root.querySelector('input[type="search"]');
    const hasFocus = input && document.activeElement === input;
    const position = hasFocus ? input.selectionStart : null;
    if (displayed) rememberScroll(displayed);
    draw();
    if (hasFocus) {
      const restored = ctx.root.querySelector('input[type="search"]');
      restored?.focus({ preventScroll: true });
      if (position != null) restored?.setSelectionRange(position, position);
    }
  }
  window.addEventListener("tuci-notes", onNotesChanged);
  draw();
  return () => {
    disposeProgress();
    cleaned = true;
    window.removeEventListener("tuci-notes", onNotesChanged);
    if (displayed) rememberScroll(displayed);
  };
}

export function mountHoldReveal(trigger, meaning, surfaces = { window, document }, options = {}) {
  let visible = false, pointer = null, disposed = false;
  const label = trigger.querySelector?.(".nb-reveal-hint");
  const describe = (text, name) => {
    if (label) label.textContent = text;
    else trigger.textContent = text;
    trigger.setAttribute("aria-label", name);
  };
  const canShow = () => !disposed && !trigger.disabled && trigger.isConnected !== false;
  const show = () => {
    if (!canShow()) return;
    visible = true; meaning.classList.add("is-revealed"); meaning.setAttribute("aria-hidden", "false");
    trigger.setAttribute("aria-expanded", "true");
    describe("松开隐藏", options.text ? "释义：" + options.text + "。松开后隐藏。" : "释义已显示，松开后隐藏");
  };
  const hide = () => {
    const oldPointer = pointer; pointer = null;
    visible = false; meaning.classList.remove("is-revealed"); meaning.setAttribute("aria-hidden", "true");
    trigger.setAttribute("aria-expanded", "false"); describe("按住释义查看", "释义，按住查看，松开隐藏");
    if (oldPointer != null && trigger.hasPointerCapture?.(oldPointer)) {
      try { trigger.releasePointerCapture(oldPointer); } catch { /* the browser may have already canceled this contact */ }
    }
  };
  const down = event => {
    if (!canShow() || event.button != null && event.button !== 0 || event.isPrimary === false) return;
    event.preventDefault(); try { trigger.setPointerCapture?.(event.pointerId); } catch { /* release still hides through window */ }
    pointer = event.pointerId ?? null;
    show();
  };
  const move = event => {
    if (!visible || pointer == null || event.pointerId !== pointer) return;
    const box = trigger.getBoundingClientRect?.();
    if (box && (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom)) hide();
  };
  const keydown = event => {
    if (event.key === " " || event.key === "Enter") { event.preventDefault(); if (!event.repeat) show(); }
    else if (event.key === "Escape") hide();
  };
  const keyup = event => { if (event.key === " " || event.key === "Enter" || event.key === "Escape") { event.preventDefault(); hide(); } };
  const hidden = () => { if (surfaces.document.visibilityState !== "visible") hide(); };
  const contextmenu = event => event.preventDefault();
  const handlers = [[trigger, "pointerdown", down], [trigger, "pointermove", move], [trigger, "pointerup", hide], [trigger, "pointercancel", hide],
    [trigger, "lostpointercapture", hide], [trigger, "pointerleave", hide], [trigger, "keydown", keydown], [trigger, "keyup", keyup],
    [trigger, "blur", hide], [trigger, "contextmenu", contextmenu], [surfaces.window, "pointerup", hide],
    [surfaces.window, "pointercancel", hide], [surfaces.window, "blur", hide], [surfaces.window, "scroll", hide],
    [surfaces.window, "pagehide", hide], [surfaces.document, "visibilitychange", hidden]];
  for (const [target, type, callback] of handlers) target.addEventListener(type, callback);
  hide();
  return () => { disposed = true; hide(); for (const [target, type, callback] of handlers) target.removeEventListener(type, callback); };
}

export function createRoamingReveal(text, surfaces = { window, document }) {
  // The diffusion is a static visual layer. Only opacity changes while held;
  // neither blur nor text layout is animated on a mobile graphics device.
  const control = button("", "nb-reveal"), meaning = element("span", "nb-meaning-mask");
  meaning.id = "roaming-meaning";
  const ink = element("span", "nb-meaning nb-meaning-ink", text), clear = element("span", "nb-meaning nb-meaning-clear", text);
  ink.setAttribute("aria-hidden", "true");
  meaning.append(ink, clear);
  const hint = element("span", "nb-reveal-hint", "按住释义查看"); hint.setAttribute("aria-hidden", "true");
  control.append(meaning, hint); control.setAttribute("aria-controls", meaning.id);
  return { control, dispose: mountHoldReveal(control, meaning, surfaces, { text }) };
}

function compactCoverage(stats) {
  const group = element("div", "nb-roam-coverage");
  group.setAttribute("role", "group"); group.setAttribute("aria-label", "周与月复习覆盖率");
  for (const [label, count] of [["本周", stats.weekReviewed], ["本月", stats.monthReviewed]]) {
    const row = element("div", "nb-mini-coverage"), progress = element("progress", "nb-coverage nb-mini-progress");
    progress.max = Math.max(1, stats.totalWords); progress.value = count;
    progress.setAttribute("aria-label", label + "已漫游 " + count + " / " + stats.totalWords + " 个不同单词");
    row.append(element("span", "nb-meta", label), progress, element("span", "nb-mini-count", count + "/" + stats.totalWords));
    group.append(row);
  }
  return group;
}

function activityContent(stats) {
  const panel = element("section", "nb-activity");
  const heatHead = element("div", "nb-heat-head"), detail = element("p", "nb-meta nb-day-detail", "每格记录当天展示次数");
  detail.setAttribute("aria-live", "polite"); heatHead.append(element("span", "nb-meta", "近 12 周"));
  const chart = element("div", "nb-heat-chart"), labels = element("div", "nb-weekdays");
  for (const text of ["一", "", "三", "", "五", "", "日"]) labels.append(element("span", "", text));
  const grid = element("div", "nb-heat-grid"); grid.setAttribute("role", "group"); grid.setAttribute("aria-label", "近84天漫游活动");
  const peak = Math.max(1, ...stats.days.map(day => day.count));
  for (const day of stats.days) {
    const level = day.count ? Math.max(1, Math.ceil(day.count / peak * 4)) : 0;
    const cell = button("", "nb-heat-cell", () => { detail.textContent = day.day + " · 展示 " + day.count + " 次 · " + day.unique + " 个不同单词"; });
    cell.dataset.level = String(level); cell.dataset.day = day.day; cell.disabled = day.future;
    cell.tabIndex = day.day === stats.today ? 0 : -1;
    cell.title = day.day + (day.future ? " · 尚未到来" : " · " + day.count + " 次 · " + day.unique + " 词"); cell.setAttribute("aria-label", cell.title);
    if (day.day === stats.today) cell.classList.add("is-today"); grid.append(cell);
  }
  grid.addEventListener("keydown", event => {
    const step = { ArrowLeft: -7, ArrowRight: 7, ArrowUp: -1, ArrowDown: 1 }[event.key];
    if (!step) return;
    const cells = [...grid.children], index = cells.indexOf(event.target), target = cells[index + step];
    if (!target || target.disabled) return;
    event.preventDefault(); event.target.tabIndex = -1; target.tabIndex = 0; target.focus(); target.click();
  });
  chart.append(labels, grid);
  const legend = element("div", "nb-heat-legend"); legend.append("少");
  for (let level = 0; level <= 4; level++) { const cell = element("span", "nb-heat-cell"); cell.dataset.level = String(level); legend.append(cell); }
  legend.append("多"); panel.append(heatHead, chart, detail, legend); return panel;
}

export function renderRoam(ctx) {
  const state = session(ctx), scope = ctx.scope();
  let cleaned = false, busy = false, error = "", pendingPlan = null, signature = "", queued = false;
  let disposeReveal = () => {}, disposeProgress = () => {}, activityDialog = null, resizeObserver;
  const entries = () => ctx.stateEntries?.() || [];
  const currentIdentity = () => entries().find(row => row.key === "roaming/current" && !row.deleted)?.value?.exposureId || null;
  const relevantSignature = () => JSON.stringify(entries().filter(row => /^(event|legacy|roaming)\//.test(row.key)))
    + JSON.stringify(ctx.notes().map(note => [note.id, note.path, note.markdown]));
  function measureViewport() {
    if (cleaned || ctx.root.dataset.roamMode !== "cards") return;
    const header = document.querySelector(".masthead")?.getBoundingClientRect();
    const tabs = document.querySelector("#tabs")?.getBoundingClientRect();
    if (header) ctx.root.style.setProperty("--roam-header-height", Math.ceil(header.height) + "px");
    if (tabs) {
      const height = window.visualViewport?.height || window.innerHeight;
      ctx.root.style.setProperty("--roam-tabs-height", Math.ceil(Math.max(tabs.height, height - tabs.top)) + "px");
    }
    const meaning = ctx.root.querySelector(".nb-meaning-mask");
    if (meaning) {
      const line = parseFloat(getComputedStyle(meaning).fontSize) * 1.75;
      const count = Math.max(1, Math.floor(meaning.getBoundingClientRect().height / (line || 30)));
      meaning.style.setProperty("--roam-meaning-lines", String(count));
    }
  }
  function dismissActivity() {
    const popup = activityDialog; activityDialog = null;
    if (popup) { if (popup.open) popup.close(); popup.remove(); }
  }
  function openActivity() {
    if (cleaned || busy || ctx.scope() !== scope || activityDialog) return;
    const popup = modal(ctx, "每日漫游"); activityDialog = popup;
    popup.classList.add("nb-activity-dialog");
    popup.append(activityContent(roamingStatistics(projectReview(entries()), ctx.notes().map(describeNote).filter(note => note.word))));
    popup.addEventListener("close", () => {
      if (activityDialog === popup) activityDialog = null;
      if (!cleaned && ctx.scope() === scope) refresh();
    }, { once: true });
    popup.showModal();
  }
  function open(path) {
    if (state.roamPath.length) rememberScroll(state.roamPath.at(-1));
    state.roamPath.push({ path, scroll: 0 }); draw();
  }
  function back() { state.roamPath.pop(); draw(); }
  async function advance(finished = null) {
    if (busy || cleaned || ctx.scope() !== scope) return;
    const words = ctx.notes().map(describeNote).filter(note => note.word);
    if (!words.length) return;
    const focusAction = document.activeElement?.dataset?.reviewAction;
    busy = true; error = ""; disposeReveal();
    for (const control of ctx.root.querySelectorAll(".nb-review-controls button, .nb-reveal, .nb-activity-entry")) control.disabled = true;
    try {
      pendingPlan ??= { ...planReview(entries(), words, finished), baseExposure: currentIdentity() };
      if (pendingPlan.writes.length) {
        if (ctx.setStates) await ctx.setStates(pendingPlan.writes);
        else for (const row of pendingPlan.writes) await ctx.setState(row.key, row.value);
      }
      pendingPlan = null;
    } catch (problem) { error = problem.message || "复习记录暂未保存，当前卡片已保留。"; }
    finally {
      busy = false;
      if (!cleaned && ctx.scope() === scope) {
        draw(); if (focusAction && !error) ctx.root.querySelector('[data-review-action="' + focusAction + '"]')?.focus({ preventScroll: true });
      }
    }
  }
  function draw() {
    if (cleaned || ctx.scope() !== scope) return;
    if (!busy && pendingPlan && pendingPlan.baseExposure !== currentIdentity()) { pendingPlan = null; error = ""; }
    disposeReveal(); disposeReveal = () => {}; disposeProgress(); disposeProgress = () => {};
    signature = relevantSignature();
    const words = ctx.notes().map(describeNote).filter(note => note.word), viewed = state.roamPath.at(-1);
    dismissActivity();
    ctx.root.replaceChildren();
    ctx.root.dataset.roamMode = "note";
    if (viewed) {
      const raw = ctx.notes().find(note => viewed.noteId && note.id === viewed.noteId || note.path === viewed.path);
      if (raw) {
        viewed.noteId = raw.id; viewed.path = raw.path;
        const note = describeNote(raw), content = showNote(ctx, note, back, open);
        restoreScroll(viewed); disposeProgress = mountNoteProgress(ctx, note, content, viewed); return;
      }
      state.roamPath.pop();
    }
    const history = projectReview(entries()), current = history.current, note = words.find(word => "note:" + word.id === current?.id);
    ctx.root.dataset.roamMode = "cards";
    const pageHead = element("header", "page-head nb-roam-heading");
    const activity = iconButton("每日漫游", "calendar", openActivity); activity.classList.add("nb-activity-entry"); activity.disabled = busy;
    pageHead.append(element("h1", "", "漫游复习"), compactCoverage(roamingStatistics(history, words)), activity);
    ctx.root.append(pageHead);
    const layout = element("div", "nb-roam-layout"); ctx.root.append(layout);
    if (!words.length) {
      layout.append(emptyState("还没有可复习的单词", "收录到词库的英文单词会出现在这里。", "去收录", () => ctx.navigate("capture")));
    } else if (note) {
      const record = history.records[current.id], card = element("article", "card nb-review");
      const top = element("div", "nb-review-top"), metadata = element("div", "nb-review-meta");
      metadata.append(element("span", "nb-meta", note.folder || "单词"));
      if (record?.day === dateKey(Date.now()) && record.remembered > 0) metadata.append(element("small", "nb-known", "✓ 今天记住过"));
      const weight = element("progress", "nb-coverage nb-roam-weight"); weight.max = 1;
      weight.value = Number.isFinite(current.weightLevel) ? Math.max(0, Math.min(1, current.weightLevel)) : 0;
      weight.setAttribute("aria-label", "本次相对抽取倾向 " + Math.round(weight.value * 100) + "%");
      top.append(metadata, weight);
      const body = element("div", "nb-review-body"), title = element("h2", "nb-word", note.title);
      if (note.title.length > 18) title.classList.add("is-long");
      if (note.title.length > 28) title.classList.add("is-extra-long");
      title.setAttribute("aria-live", "polite"); body.append(title);
      if (note.phonetics) body.append(element("p", "nb-sound", inlineLiteral(note.phonetics)));
      const reveal = createRoamingReveal(excerpt(note) || "暂无释义");
      body.append(reveal.control);
      card.append(top, body, button("阅读整篇笔记 →", "nb-text-btn nb-read-note", () => open(note.path)));
      disposeReveal = reveal.dispose;
      const controls = element("div", "nb-review-controls");
      const skip = button("下一张", "button secondary", () => advance({ exposure: current.exposure, remembered: false }));
      const remember = button("今天已记住", "button", () => advance({ exposure: current.exposure, remembered: true }));
      skip.dataset.reviewAction = "skip"; remember.dataset.reviewAction = "remember";
      skip.disabled = remember.disabled = busy || Boolean(pendingPlan); controls.append(skip, remember); card.append(controls); layout.append(card);
    } else layout.append(element("p", "nb-meta", busy ? "正在准备词卡…" : "准备下一张词卡"));
    if (error) {
      const message = element("p", "nb-error", error); message.setAttribute("role", "alert");
      const failure = element("div", "nb-roam-failure"); failure.append(message, button("重试", "nb-text-btn", () => advance())); ctx.root.append(failure);
    }
    measureViewport();
    if (words.length && !note && !busy && !error) void advance();
  }
  const refresh = () => {
    if (cleaned || busy || queued || ctx.root.querySelector("dialog[open]") || relevantSignature() === signature) return;
    queued = true; queueMicrotask(() => {
      queued = false; if (cleaned || busy || ctx.scope() !== scope || ctx.root.querySelector("dialog[open]")) return;
      if (state.roamPath.length) rememberScroll(state.roamPath.at(-1)); draw();
    });
  };
  const unsubscribe = ctx.subscribeState?.(refresh) || (() => {});
  window.addEventListener("tuci-notes", refresh); window.addEventListener("resize", measureViewport);
  window.visualViewport?.addEventListener("resize", measureViewport);
  if (window.ResizeObserver) {
    resizeObserver = new window.ResizeObserver(measureViewport);
    const header = document.querySelector(".masthead"), tabs = document.querySelector("#tabs");
    if (header) resizeObserver.observe(header); if (tabs) resizeObserver.observe(tabs);
  }
  draw();
  return () => {
    disposeReveal(); disposeProgress(); cleaned = true; unsubscribe();
    dismissActivity(); resizeObserver?.disconnect();
    delete ctx.root.dataset.roamMode;
    ctx.root.style.removeProperty("--roam-header-height"); ctx.root.style.removeProperty("--roam-tabs-height");
    if (state.roamPath.length) rememberScroll(state.roamPath.at(-1));
    window.removeEventListener("tuci-notes", refresh);
    window.removeEventListener("resize", measureViewport); window.visualViewport?.removeEventListener("resize", measureViewport);
  };
}
