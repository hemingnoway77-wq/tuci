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

function dateKey(now) {
  const day = new Date(now);
  return [day.getFullYear(), day.getMonth() + 1, day.getDate()].join("-");
}

export function reviewWeight(record, added, now = Date.now()) {
  const age = time => Math.max(0, (now - time) / dayMs);
  const remembered = record?.day === dateKey(now) ? record.remembered : 0;
  const skipped = record?.day === dateKey(now) ? record.skipped : 0;
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
  node.addEventListener("click", action);
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

function showNote(ctx, note, back, open, edit) {
  const header = element("header", "nb-detail-head");
  header.append(iconButton("返回", "back", back), element("span", "nb-meta", note.folder || "词库"));
  if (edit) header.append(button("编辑", "nb-text-btn", edit));
  ctx.root.append(header, element("h1", "nb-note-title", note.title), noteBody(note, (link, double) => follow(ctx, note, link, double, open)));
}

function deletePrompt(ctx, frame, back) {
  const dialog = modal(ctx, "删除笔记");
  dialog.append(element("p", "", "删除「" + basename(frame.path) + "」？连接同步后，其他设备也会删除这篇笔记。"));
  const submit = button("删除笔记", "button nb-danger", async () => {
    submit.disabled = true;
    try { await ctx.deleteNote(frame.path, frame.original); dialog.close(); back(); ctx.toast("笔记已删除"); }
    catch (error) { ctx.toast(error.message); submit.disabled = false; }
  });
  dialog.append(submit);
  dialog.showModal();
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

function directory(ctx, frame, back, enter, edit) {
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
  header.append(heading, iconButton("新建笔记", "plus", () => edit(prefix)));
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
  window.scrollTo({ top: frame.scroll || 0, behavior: "instant" });
}

export function renderLibrary(ctx, path) {
  const state = session(ctx);
  const stack = state.stack;
  if (path && stack.at(-1)?.path !== path) stack.push({ kind: "note", path, scroll: 0 });
  let displayed, cleaned = false;
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
    const frame = stack.at(-1);
    displayed = frame;
    ctx.root.replaceChildren();
    const raw = ctx.notes().find(note => note.path === frame.path);
    if (frame.kind === "folder") directory(ctx, frame, back, enter, edit);
    else if (frame.kind === "edit") {
      editView(ctx, frame, frame.editing, back, dest => {
        stack.pop();
        if (stack.at(-1)?.kind !== "note") stack.push({ kind: "note", path: dest, scroll: 0 });
        draw();
      });
      if (frame.editing) ctx.root.append(button("删除这篇笔记", "nb-delete", () => deletePrompt(ctx, frame, () => {
        stack.pop(); if (stack.at(-1)?.kind === "note") stack.pop(); draw();
      })));
    } else if (raw) {
      const note = describeNote(raw);
      showNote(ctx, note, back, nextPath => enter("note", nextPath), () => edit(note.path, raw));
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
    cleaned = true;
    window.removeEventListener("tuci-notes", onNotesChanged);
    if (displayed) rememberScroll(displayed);
  };
}

export function renderRoam(ctx) {
  const state = session(ctx);
  const storageKey = "tuci.web.roaming.v1:" + ctx.scope();
  let cleaned = false;
  const read = () => JSON.parse(localStorage.getItem(storageKey) || '{"records":{},"current":null}');
  function open(path) {
    if (state.roamPath.length) rememberScroll(state.roamPath.at(-1));
    state.roamPath.push({ path, scroll: 0 });
    draw();
  }
  function back() { state.roamPath.pop(); draw(); }
  function draw(finished = null) {
    if (cleaned) return;
    const words = ctx.notes().map(describeNote).filter(note => note.word);
    const viewed = state.roamPath.at(-1);
    ctx.root.replaceChildren();
    if (viewed) {
      const raw = ctx.notes().find(note => note.path === viewed.path);
      if (raw) {
        showNote(ctx, describeNote(raw), back, open);
        restoreScroll(viewed); return;
      }
      state.roamPath.pop();
    }
    ctx.root.append(element("h1", "", "漫游复习"));
    window.scrollTo({ top: 0, behavior: "instant" });
    if (!words.length) { ctx.root.append(emptyState("还没有可复习的单词", "收录到词库的英文单词会出现在这里。", "去收录", () => ctx.navigate("capture"))); return; }
    try {
      const history = read();
      let current = history.current;
      if (finished || !words.some(word => "note:" + word.id === current?.id)) {
        current = nextReview(history, words, finished);
        localStorage.setItem(storageKey, JSON.stringify(history));
      }
      const note = words.find(word => "note:" + word.id === current?.id);
      if (!note) { draw(); return; }
      const record = history.records[current.id];
      const card = element("article", "card nb-review");
      const top = element("div", "nb-review-top"); top.append(element("span", "nb-meta", note.folder || "单词"));
      if (record?.day === dateKey(Date.now()) && record.remembered > 0) top.append(element("small", "nb-known", "✓ 今天记住过"));
      card.append(top, element("h2", "nb-word", note.title));
      if (note.phonetics) card.append(element("p", "nb-sound", inlineLiteral(note.phonetics)));
      card.append(element("p", "nb-meaning", excerpt(note) || "暂无释义"), button("阅读整篇笔记", "nb-text-btn", () => open(note.path)));
      const controls = element("div", "nb-review-controls");
      controls.append(button("下一张", "button secondary", () => draw({ exposure: current.exposure, remembered: false })),
        button("今天已记住", "button", () => draw({ exposure: current.exposure, remembered: true })));
      ctx.root.append(card, controls);
    } catch {
      ctx.root.append(emptyState("复习记录暂时无法保存", "请检查浏览器存储空间后重试。", "重试", () => draw()));
    }
  }
  const syncTabs = event => { if (event.key === storageKey) draw(); };
  const refresh = () => {
    if (ctx.root.querySelector("dialog[open]")) return;
    if (state.roamPath.length) rememberScroll(state.roamPath.at(-1));
    draw();
  };
  window.addEventListener("tuci-notes", refresh);
  window.addEventListener("storage", syncTabs);
  draw();
  return () => {
    cleaned = true;
    if (state.roamPath.length) rememberScroll(state.roamPath.at(-1));
    window.removeEventListener("storage", syncTabs);
    window.removeEventListener("tuci-notes", refresh);
  };
}
