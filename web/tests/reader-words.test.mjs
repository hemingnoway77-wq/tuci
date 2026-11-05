import test from 'node:test';
import assert from 'node:assert/strict';
import { wordSegments, wordSentence } from '../reader-words.js';

test('tokenization preserves original text, punctuation and separate occurrences', () => {
  const text = 'Hello, apple! Apple is well-known; don’t forget it.';
  const tokens = wordSegments(text);
  assert.equal(tokens.map(t => t.text).join(''), text);
  const apples = tokens.filter(t => t.word === 'apple');
  assert.equal(apples.length, 2);
  assert.notEqual(apples[0].start, apples[1].start);
  assert.equal(text.slice(apples[1].start, apples[1].end), 'Apple');
  assert.ok(tokens.some(t => t.word === 'well-known'));
  assert.ok(tokens.some(t => t.word === "don't" && t.text === 'don’t'));
});

test('capture context uses the tapped occurrence rather than the first matching word', () => {
  const text = 'An apple fell. She chose another apple! They went home.';
  const apples = wordSegments(text).filter(t => t.word === 'apple');
  assert.equal(wordSentence(text, apples[0].start, apples[0].end), 'An apple fell.');
  assert.equal(wordSentence(text, apples[1].start, apples[1].end), 'She chose another apple!');
});

test('uncollectable long tokens remain literal text and context stays bounded', () => {
  const text = 'a'.repeat(65) + ' okay';
  const tokens = wordSegments(text);
  assert.equal(tokens.map(t => t.text).join(''), text);
  assert.equal(tokens.filter(t => t.word).length, 1);
  assert.ok(wordSentence('word '.repeat(1000), 10, 14).length <= 2000);
});
