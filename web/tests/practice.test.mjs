import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { buildPracticeSenses, bindPracticeContexts, choosePracticeQuestion, completePractice, evaluateSpelling, preparePracticeBank } from '../practice.js';
import { planReview } from '../library.js';

const hash = value => createHash('sha256').update(value).digest('hex');
const raw = { id: 'note-a', path: '名词/supply.md', markdown: '---\nlemma: "supply"\n---\n```md\n**释义**：\n> ignore\n```\n**释义**：\n> n. 补给（常用复数 supplies）\n\n> v. 提供 [[object|物品]] &amp; 服务\n> v. 提供 物品 &amp; 服务\n#### 原句\n> Ignore this.' };
async function fixture() {
  const senses = await buildPracticeSenses(raw);
  const contexts = [
    {senseId:senses[0].id, english:'They left the supplies inside the building.', chinese:'他们把补给留在楼里。', answerForm:'supplies'},
    {senseId:senses[1].id, english:'We supply fresh water to the village.', chinese:'我们向村里提供淡水。'}
  ];
  return {senses, contexts};
}

test('uses Windows sense fingerprints and rejects partial, duplicate or unlicensed context forms', async () => {
  const {senses, contexts} = await fixture();
  assert.deepEqual(senses.map(s => s.id), ['n. 补给（常用复数 supplies）','v. 提供 物品 & 服务'].map(hash));
  assert.equal(bindPracticeContexts(senses,[...contexts].reverse())[0].answerForm,'supplies');
  for (const invalid of [[contexts[0]], [contexts[0], contexts[0]],
    [contexts[0], {...contexts[1], english:contexts[0].english, answerForm:'supplies'}],
    [{...contexts[0], chinese:'&#91;旧例句]'}, contexts[1]]]) {
    assert.throws(() => bindPracticeContexts(senses, invalid));
  }
});

test('retains one question per exposure, hides stored and actual forms, and rotates all sense/kind pairs', async () => {
  const {senses,contexts}=await fixture(), bound=bindPracticeContexts(senses,contexts);
  let ledger; const pairs = new Set();
  for (let i=0;i<4;i++) {
    const exposure='e'+i, selected=choosePracticeQuestion(ledger,bound,raw.id,exposure);
    ledger=selected.ledger;
    const {question}=selected;
    assert.deepEqual(choosePracticeQuestion(ledger,bound,raw.id,exposure).question,question);
    pairs.add(question.senseId + question.kind);
    if (i===0) {
      assert.equal(question.word,'supplies');
      assert.match(question.prompt,/____/);
      assert.doesNotMatch(question.prompt + question.hint,/\bsuppl(?:y|ies)\b/i);
      assert.equal(evaluateSpelling(question,'  Supplies  '),'correct');
      assert.equal(evaluateSpelling(question,'supply'),'lemmaCorrect');
      assert.equal(evaluateSpelling(question,'supplyed'),'incorrect');
    }
    ledger=completePractice(ledger,question,'skipped','');
    assert.equal(completePractice(ledger,question,'correct','supplies'),ledger);
  }
  assert.equal(pairs.size,4);
});

test('a cloud lookup never calls generation and an explicit preparation coalesces with capture', async () => {
  const {contexts}=await fixture();
  let bank, fetches=0, generations=0;
  const store={ readBank:async()=>bank, saveBank:async(_key,value)=>{bank=value;}, fetchBanks:async()=>{fetches++;throw Error('offline');} };
  const generate=async()=>{generations++;return contexts;};
  assert.equal(await preparePracticeBank(store,raw,{generate}),null);
  assert.equal(generations,0);
  const banks=await Promise.all([preparePracticeBank(store,raw,{generate,settings:{}}),preparePracticeBank(store,raw,{generate,settings:{}})]);
  assert.equal(generations,1);
  assert.equal(banks[0][0].answerForm,'supplies');
  assert.equal(banks[1][0].answerForm,'supplies');
  assert.equal(fetches,2);
  assert.deepEqual(await preparePracticeBank(store,raw),banks[0]);
});

test('neutral practice completion advances without grading or immediately repeating a note', () => {
  const words=[{id:'a',added:0},{id:'b',added:0}], now=1_000_000;
  const first=planReview([],words,null,now,0,()=> 'a1');
  const neutral=planReview(first.writes,words,{exposure:'a1',remembered:null,practice:true},now+1,0,()=> 'b1');
  assert.equal(neutral.current.id,'note:b');
  assert.deepEqual(neutral.writes.map(w=>w.key),['event/b1/shown','roaming/current']);
});
