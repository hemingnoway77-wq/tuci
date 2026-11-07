The bundled Bergamot runtime runs entirely in a local Web Worker. It makes no
remote requests, downloads no models at runtime, and needs neither Node nor
Firefox. The application maps this directory to `https://tuci-offline.invalid/`.

Upstream files are unmodified:

- `bergamot-translator.wasm`: Mozilla Remote Settings `translations-wasm`, record
  `4fd32605-9889-4dd9-9fc7-577ad1136746`, version `3.0`, release `v0.6.0`,
  upstream revision `1de4a085d3a7afb625c51a60aabb5ad298e4059f`.
- `bergamot-translator.js`: Mozilla Firefox's vendored Emscripten loader,
  `BERGAMOT_VERSION_FULL = v0.6.0+eea6e5a8`. The upstream `moz.yaml` identifies
  `mozilla/translations` revision `eea6e5a80aa4ddd86d9cc35ce9a65b79aa3ab96d`.
  The exact loader is pinned to Firefox commit
  `d9bd6be9a67efb1c2667ef91a89856f35eb7cf5e`; its downloaded SHA-256 matches
  the bundled file byte for byte.
- `LICENSE-MPL-2.0.txt`: the upstream license, shipped with source-form JavaScript.

The exact bundled bytes, hashes, download URLs and sizes are recorded in
`runtime.manifest.json`. Model files and their own provenance are recorded in
`models.manifest.json`.

The TUCI adapter files `index.html`, `runtime.js`, and `translation-worker.js`
are also provided under MPL-2.0. Their initialization and model configuration
follow Firefox's `content/translations-engine.worker.js`. This source is included
in the package; no additional source download is needed to inspect modifications.

The `base-memory` English-to-Chinese model uses a 256-byte-aligned model, a
64-byte-aligned lexical shortlist, two separate 64-byte-aligned SentencePiece
vocabularies in source/target order, and `int8shiftAlphaAll` GEMM precision.

Host protocol:

- Worker initialized: `{type:"ready"}`.
- Host call: `window.tuciTranslate({id:1,text:"hello"})`.
- Result: `{type:"result",id:1,text:"你好"}`.
- Failure: `{type:"error",id:1,message:"…"}`; initialization errors have no id.
- Optional cancellation: `window.tuciCancel(1)`. A synchronous inference already
  running cannot be interrupted by another worker message. The C# caller must
  ignore late results; destroying the WebView terminates the worker on timeout.

Bergamot and model licensing: Mozilla Public License 2.0. The WASM also contains
dependencies under their individual permissive licenses. Full third-party
copyright and license texts are reproduced in `LICENSES-THIRD-PARTY.txt`, including
Marian, SentencePiece, Protocol Buffers lite, intgemm, yaml-cpp, pathie-cpp,
spdlog/fmt, CLI11, cnpy, mio, half-float and sse_mathfun helpers, and the
Emscripten runtime. Dependency revisions were read from Mozilla's pinned
inference source tree. Its WASM build disables native-only optional backends and
ssplit-cpp; no PCRE2 or native Firefox optimized GEMM binary is bundled here.

See `LICENSE-MPL-2.0.txt`, `LICENSES-THIRD-PARTY.txt`, and `models.manifest.json`
for full notices and official sources. The immutable Bergamot WASM source
revision and its official Remote Settings record are in `runtime.manifest.json`.
