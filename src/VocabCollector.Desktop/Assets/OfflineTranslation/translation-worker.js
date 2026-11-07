/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. See LICENSE-MPL-2.0.txt in this directory.
 * The Bergamot initialization/configuration follows Mozilla Firefox's
 * toolkit/components/translations/content/translations-engine.worker.js. */
"use strict";
importScripts("bergamot-translator.js");

const MAX_TEXT_LENGTH = 2000;
const cancelled = new Set();
let engine;
let initializationError;
let queue = Promise.resolve();

async function localBytes(path, maximumBytes) {
  const url = new URL(path, self.location.href);
  if (url.origin !== self.location.origin) throw new Error("Nonlocal translation asset refused");
  const response = await fetch(url, {credentials: "omit", cache: "no-store", redirect: "error"});
  if (!response.ok) throw new Error("Local translation asset unavailable: " + path);
  const buffer = await response.arrayBuffer();
  if (!buffer.byteLength || buffer.byteLength > maximumBytes) throw new Error("Invalid local translation asset size: " + path);
  return new Uint8Array(buffer);
}

async function initialize() {
  const wasmBinary = await localBytes("bergamot-translator.wasm", 6_000_000);
  const bergamot = await new Promise((resolve, reject) => {
    const module = loadBergamot({
      INITIAL_MEMORY: 41_943_040,
      wasmBinary,
      print: () => {},
      printErr: () => {},
      onAbort: () => reject(new Error("Bergamot WASM initialization failed")),
      onRuntimeInitialized: () => Promise.resolve().then(() => resolve(module))
    });
  });
  const memories = [];
  const allocate = async (path, alignment, maximumBytes) => {
    const bytes = await localBytes(path, maximumBytes);
    const memory = new bergamot.AlignedMemory(bytes.byteLength, alignment);
    memory.getByteArrayView().set(bytes);
    memories.push(memory);
    return memory;
  };
  const model = await allocate("en-zh/model.enzh.intgemm.alphas.bin", 256, 50_000_000);
  const shortlist = await allocate("en-zh/lex.50.50.enzh.s2t.bin", 64, 8_000_000);
  const sourceVocabulary = await allocate("en-zh/srcvocab.enzh.spm", 64, 2_000_000);
  const targetVocabulary = await allocate("en-zh/trgvocab.enzh.spm", 64, 2_000_000);
  const vocabularies = new bergamot.AlignedMemoryList();
  vocabularies.push_back(sourceVocabulary);
  vocabularies.push_back(targetVocabulary);
  const config = Object.entries({
    "beam-size": 1,
    normalize: 1,
    "word-penalty": 0,
    "max-length-break": 128,
    "mini-batch-words": 1024,
    workspace: 128,
    "max-length-factor": 2,
    "skip-cost": true,
    "cpu-threads": 0,
    quiet: true,
    "quiet-translation": true,
    "gemm-precision": "int8shiftAlphaAll",
    alignment: "soft"
  }).map(([key, value]) => `${key}: ${value}`).join("\n");
  const translationModel = new bergamot.TranslationModel("en", "zh", config, model, shortlist, vocabularies, null);
  const service = new bergamot.BlockingService({cacheSize: 0});
  return {bergamot, translationModel, service, memories, vocabularies};
}

const ready = initialize().then(value => {
  engine = value;
  postMessage({type: "ready"});
}).catch(() => {
  initializationError = "离线翻译资源无法载入，请检查安装包是否完整。";
  postMessage({type: "error", message: initializationError});
});

onmessage = event => {
  const request = event.data;
  if (!request || !Number.isSafeInteger(request.id)) return;
  if (request.type === "cancel") {
    cancelled.add(request.id);
    if (cancelled.size > 128) cancelled.delete(cancelled.values().next().value);
    return;
  }
  if (request.type !== "translate") return;
  const {id, text} = request;
  queue = queue.then(async () => {
    await ready;
    if (cancelled.delete(id)) return;
    if (initializationError) throw new Error(initializationError);
    if (typeof text !== "string" || !text.trim() || text.length > MAX_TEXT_LENGTH)
      throw new Error("请选择不超过 2,000 个字符的英文内容。");
    const {bergamot, translationModel, service} = engine;
    const messages = new bergamot.VectorString();
    const options = new bergamot.VectorResponseOptions();
    let responses, response;
    try {
      messages.push_back(text);
      options.push_back({qualityScores: false, alignment: false, html: false});
      responses = service.translate(translationModel, messages, options);
      response = responses.get(0);
      const translated = response.getTranslatedText();
      if (typeof translated !== "string" || !translated.trim()) throw new Error("离线翻译暂未得到结果，请重试。");
      if (!cancelled.delete(id)) postMessage({type: "result", id, text: translated});
    } finally {
      response?.delete();
      responses?.delete();
      options.delete();
      messages.delete();
    }
  }).catch(error => postMessage({type: "error", id,
    message: initializationError || (error instanceof Error && error.message.startsWith("请选择") ? error.message : "离线翻译暂时失败，请重试。")
  }));
};
