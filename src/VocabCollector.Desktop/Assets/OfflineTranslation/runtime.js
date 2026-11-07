/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. See LICENSE-MPL-2.0.txt in this directory. */
"use strict";
(() => {
  const worker = new Worker("translation-worker.js");
  const send = value => window.chrome.webview.postMessage(value);
  worker.onmessage = event => send(event.data);
  worker.onerror = event => {
    event.preventDefault();
    send({type: "error", message: "离线翻译引擎意外停止，请重试。"});
  };
  window.tuciTranslate = request => {
    if (!request || !Number.isSafeInteger(request.id) || typeof request.text !== "string") return;
    worker.postMessage({type: "translate", id: request.id, text: request.text});
  };
  // Ask the worker to drop queued requests. The caller ignores late results;
  // disposing this WebView terminates a synchronous inference already running.
  window.tuciCancel = id => {
    if (Number.isSafeInteger(id)) worker.postMessage({type: "cancel", id});
  };
  window.addEventListener("unload", () => worker.terminate(), {once: true});
})();
