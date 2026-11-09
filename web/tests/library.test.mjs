import assert from "node:assert/strict";
import { test } from "node:test";
import { describeNote, resolveWiki, reviewWeight, nextReview, inlineLiteral } from "../library.js";

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
