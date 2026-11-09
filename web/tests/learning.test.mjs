import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { analysisCard, manualCard, matches, regionHashes, pickDaily, offlineArticles } from '../learning-data.js';
const source = new URL('./fixtures/legacy-capture.json', import.meta.url);
const swiftFixture = JSON.parse(await readFile(source,'utf8'));
const notes = ['名词/economy.md','名词/resilience.md'].map(path=>({path,markdown:''}));

test('imports the Windows / iPhone fixture and only links unambiguous existing notes', async()=>{
  const card=analysisCard(swiftFixture,'resilient','[[raw]] <p>',true,notes);
  assert.equal(card.path,'形容词/resilient.md');
  assert.match(card.markdown, /The \[\[名词\/economy\|economy\]\] is \*\*resilient\*\*/);
  assert.match(card.markdown, /\[\[名词\/resilience\|resilience\]\]/);
  assert.match(card.markdown, /&#91;&#91;raw&#93;&#93; &lt;p&gt;/);
  assert.deepEqual(Object.keys(await regionHashes(card.markdown)).sort(), ['examples','relations','roots']);
  const extra={path:'archive/economy.md',markdown:''};
  assert.doesNotMatch(analysisCard(swiftFixture,'resilient','',true,[...notes,extra]).markdown,/\|economy\]\]/);
});

test('preserves Windows-safe filenames and bounds correction to likely typos',()=>{
  assert.equal(manualCard('CON','控制','','verb',true).path,'动词/_con.md');
  assert.throws(()=>manualCard('../a','','','other',true));
  assert.equal(matches('con',[{path:'动词/_con.md'}]).length,1);
  const misspelled=structuredClone(swiftFixture);
  misspelled.spelling.status='corrected';
  misspelled.spelling.input_word='resilent';
  assert.match(analysisCard(misspelled,'resilent','',false,[]).markdown,/original_input: "resilent"/);
  misspelled.spelling.input_word='resilients';
  assert.throws(()=>analysisCard(misspelled,'resilients','',false,[]));
});
test('the offline selection includes two general texts and one technology text with readable bodies',()=>{
  const selected=pickDaily(offlineArticles,'2026-11-05');
  assert.deepEqual(selected.map(a=>a.category),['general','general','technology']);
  assert.ok(selected.every(a=>a.paragraphs.length>=3));
});

test('accepts completed event streams and rejects missing stop / truncation markers',async()=>{
  const {streamContent}=await import('../learning.js');
  const delta=s=>'data: '+JSON.stringify({choices:[{delta:{content:s},finish_reason:null}]})+'\r\n\r\n';
  const stop='data: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\n', end='data: [DONE]\n\n';
  assert.equal(streamContent(': heartbeat\n\n'+delta('中')+delta('文')+stop+end),'中文');
  assert.throws(()=>streamContent(delta('partial')+end));
  assert.throws(()=>streamContent(delta('partial')+stop));
  assert.throws(()=>streamContent(delta('partial')+stop.replace('stop','length')+end));
});
test('falls back only on explicit rejection of streaming',async t=>{
  const {requestAnalysis}=await import('../learning.js');
  const called=[];
  t.mock.method(globalThis,'fetch',async (_,req)=>{
    const {stream}=JSON.parse(req.body);called.push(stream);
    return stream?new Response('unsupported parameter: stream',{status:400}):
      Response.json({choices:[{message:{content:JSON.stringify(swiftFixture)},finish_reason:'stop'}]});
  });
  const result=await requestAnalysis({aiModel:'foo',aiKey:'fixture-key',aiEndpoint:'https://fixture.invalid/v1'},'resilient','',new AbortController().signal);
  assert.equal(result.spelling.word,'resilient');
  assert.deepEqual(called,[true,false]);
});
