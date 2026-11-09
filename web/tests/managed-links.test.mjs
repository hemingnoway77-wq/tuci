import { test } from "node:test";
import assert from "node:assert/strict";
import { regionHashes, linkIndex, refreshManaged } from "../managed-links.js";

const template = [
  "# guide", "", "**词根**：duc / duct（引导）", "", "**词形**：guided 是过去式。", "",
  "#### 例句", "1. We guide Alice to the library, library, market, office and town.",
  "   - library 是图书馆。", "2. Read `library` at https://x.test/library.",
  "", "#### 语义关系", "##### 近义词", "- lead：意思接近 guide。", "",
  "#### 我的笔记", "library 和 lead 由我手动编辑。"
].join("\r\n");
const note = async () => ({id:"1",path:"动词/guide.md",markdown:template,regions:await regionHashes(template)});
const index = linkIndex(["library","market","office","town","guide","guided","lead","the"].map(w =>
  ({path:"单词/"+w+".md"})).concat([{path:"词根/-duc-.md"}]));

test("four protected regions match native rules, retaining original CRLF and ordinary notes", async () => {
  const original = await note();
  const next = await refreshManaged(original,index);
  assert.equal(next.markdown,template
    .replace("：duc / duct（","：[[词根/-duc-|duc / duct]]（")
    .replace("：guided 是","：[[单词/guided|guided]] 是")
    .replace("the library, library, market, office and town","the [[单词/library|library]], library, [[单词/market|market]], [[单词/office|office]] and town")
    .replace("- lead：","- [[单词/lead|lead]]："));
  assert.equal(await refreshManaged({...original,...next},index),null);
  assert.deepEqual(next.regions,await regionHashes(next.markdown));
});
test("manual changes, ambiguous headings and ordinary notes are protected", async () => {
  const original = await note();
  original.markdown = original.markdown.replace("town.","town tomorrow.").replace("- lead：","- lead：替换一下，");
  const next = await refreshManaged(original,index);
  assert.match(next.markdown,/the library, library/);
  assert.match(next.markdown,/- lead：替换一下/);
  assert.equal(next.regions.examples,original.regions.examples);
  assert.equal(await refreshManaged({...original,regions:null},index),null);
  assert.equal((await regionHashes("#### 例句\n1. hi\n#### 例句\n2. duplicate")).examples,undefined);
});
test("a matching existing vault-prefixed link is preserved", async () => {
  const markdown = "#### 例句\n1. A [[英语单词/单词/library|library]] opens.";
  assert.equal(await refreshManaged({path:"note.md",markdown,regions:await regionHashes(markdown)},index),null);
});
