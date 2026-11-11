import assert from "node:assert/strict";
import { test } from "node:test";
import { describeNote, resolveWiki, reviewWeight, nextReview, inlineLiteral, importMarkdownFiles } from "../library.js";

const now = new Date(2026, 10, 5, 12).getTime();
const day = 86_400_000;
const note = (path, markdown = "# 示例") => describeNote({ path, markdown });
test("reads Windows frontmatter and meaning and resolves exact, relative and vault-prefixed links", () => {
  const source = note("动词/introduced.md", "---\r\ncreated: '2026-10-22T09:06:44.6864924+08:00'\r\n---\r\n# introduced\r\n\r\n**释义**：\r\n\r\n> v. 介绍。  \r\n> v. 引进。");
  const a = note("名词/policy.md"), b = note("摘记/policy.md"), relative = note("动词/例子/run.md");
  const notes = [source, a, b, relative];
  assert.ok(Number.isFinite(source.added));
  assert.equal(source.meaning, "v. 介绍。\nv. 引进。");
  assert.doesNotMatch(source.body, /created:/);
  assert.deepEqual(resolveWiki("英语单词/名词/POLICY|政策", source, notes), [a]);
  assert.deepEqual(resolveWiki("例子/run#搭配", source, notes), [relative]);
  assert.deepEqual(resolveWiki("policy", source, notes), [a, b]);
  assert.deepEqual(resolveWiki("unknown/path/policy", source, notes), []);
  assert.equal(note("词根/duc.md").word, false);
  assert.equal(note("副词/_con.md").title, "con");
});

test("decodes Apple and Windows Markdown escapes once for note text", () => {
  assert.equal(inlineLiteral("A &amp; B &#91;C&#93; &lt;D&gt; &amp;lt;"), "A & B [C] <D> &lt;");
  assert.equal(inlineLiteral(String.raw`x\_y \* z`), "x_y * z");
});

test("an empty, hand-edited meaning section can contain many blank lines", () => {
  const blanks = " \t\n".repeat(4_096);
  const start = "**释义**：\n" + blanks;
  assert.equal(note("动词/run.md", start + "我的笔记").meaning, "");
  assert.equal(note("动词/run.md", start + "> v. 奔跑").meaning, "v. 奔跑");
});

test("Markdown import accepts multiple UTF-8 files and refuses collisions, traversal and invalid data", async () => {
  const file = (name, contents) => {
    const bytes = typeof contents === "string" ? new TextEncoder().encode(contents) : Uint8Array.from(contents);
    return { name, size: bytes.length, arrayBuffer: async () => bytes.buffer };
  };
  const writes = [];
  const result = await importMarkdownFiles({
    scope: () => "alice",
    notes: () => [{ path: "Policy.md" }, { path: "folder.md/child.md" }],
    async saveNote(...args) {
      writes.push(args);
      if (args[0] === "race.md") throw Error("A different tab already saved this path.");
    }
  }, [file("POLICY.md", "same name"), file("folder.md", "already a directory"), file("学习.md", "# 学习\r\n"),
    file("cafe\u0301.MD", "\uFEFF# café"), file("CAFÉ.md", "same batch"), file("../escape.md", "bad path"),
    file("notes.txt", "wrong extension"), file("binary.md", [0xff]),
    { name: "large.md", size: 1024 * 1024 + 4, arrayBuffer: () => assert.fail("oversized files must not be opened") }, file("race.md", "late collision")]);
  assert.deepEqual(result, { imported: 2, duplicates: 3, failed: 5 });
  assert.deepEqual(writes, [["学习.md", "# 学习\r\n", undefined, null], ["café.MD", "# café", undefined, null],
    ["race.md", "late collision", undefined, null]]);
});

test("Markdown import never writes into a new account after a file read completes", async () => {
  let scope = "alice", finishRead;
  const ctx = { scope: () => scope, notes: () => [], saveNote: () => assert.fail("file belongs to the old account") };
  const file = { name: "note.md", size: 5, arrayBuffer: () => new Promise(resolve => { finishRead = resolve; }) };
  const result = importMarkdownFiles(ctx, [file]);
  scope = "bob";
  finishRead(new TextEncoder().encode("hello").buffer);
  assert.deepEqual(await result, { imported: 0, duplicates: 0, failed: 0 });
});

test("each exposure finishes once, skips outweigh marks, recency and gap contribute independently", () => {
  const words = [note("动词/run.md")];
  function trial(remembered) {
    const history = { records: {}, current: null };
    const card = nextReview(history, words, null, now, 0);
    nextReview(history, words, { exposure: card.exposure, remembered }, now, 0);
    nextReview(history, words, { exposure: card.exposure, remembered }, now, 0);
    return history.records["note:动词/run.md"];
  }
  const skip = trial(false), mark = trial(true);
  assert.equal(skip.skipped, 1);
  assert.equal(mark.remembered, 1);
  assert.ok(Math.abs(reviewWeight(skip, now, now) - reviewWeight(mark, now, now) - 1) < .000001);
  assert.ok(reviewWeight(mark, now, now) > 0);
  assert.ok(reviewWeight(null, now - 30 * day, now) > reviewWeight(skip, now, now));
  assert.ok(reviewWeight(skip, now, now) > reviewWeight(skip, now - 30 * day, now));
  assert.equal(reviewWeight(skip, now, now + day), reviewWeight(mark, now, now + day));
});
