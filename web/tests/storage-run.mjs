// From the repository root (Node 20+, .NET 10):
//   npm install --prefix /tmp/tuci-browser-qa playwright
//   /tmp/tuci-browser-qa/node_modules/.bin/playwright install webkit
//   PLAYWRIGHT_MODULE=/tmp/tuci-browser-qa/node_modules/playwright node web/tests/storage-run.mjs
// Set DOTNET if necessary. BROWSER=chromium CHROME_BIN=/path/to/chrome tests Chromium instead.
import { createRequire } from "node:module";
import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:http";
import { mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createInterface } from "node:readline";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const dir = realpathSync(mkdtempSync(join(tmpdir(), "tuci-web-storage-")));
const database = join(dir, "test.sqlite3");
const env = {...process.env, TUCI_DB: database};
const require = createRequire(import.meta.url);
const playwright = require(process.env.PLAYWRIGHT_MODULE || "playwright");
const dotnet = process.env.DOTNET || "dotnet";
const syncProject = join(root,"server");
const driver = join(root,"tests", "VocabCollector.Sync.RoundTrip");
const serverDll = join(syncProject, "bin/Release/net10.0/Tuci.Sync.dll");
const driverDll = join(driver, "bin/Release/net10.0/VocabCollector.Sync.RoundTrip.dll");
const example = join(root, "docs/examples/introduced.md");

const run = (args, options={}) => execFileSync(dotnet,args,{cwd:root, env, ...options});
const invite = () => JSON.parse(run([serverDll,"invite"])).code;

let backend, proxy, browser;
try {
  run(["build",syncProject,"-c","Release","--nologo"]);
  run(["build",driver,"-c","Release","--nologo"]);
  backend = spawn(dotnet,[serverDll,"--urls","http://127.0.0.1:0"],{env,stdio:["ignore","pipe","inherit"]});
  const lines = createInterface({input:backend.stdout});
  let base;
  for await (const line of lines) {
    const match = /Now listening on: (http:\/\/127\.0\.0\.1:\d+)/.exec(line);
    if (match) { base = match[1]; break; }
  }
  backend.stdout.resume();
  if (!base) throw new Error("Local test server could not start.");

  const windowsDir = join(dir, "windows");
  mkdirSync(windowsDir);
  const windowsEnv = {...env, TUCI_TEST_SERVER:base, TUCI_TEST_NOTE:example, TUCI_TEST_CODE:invite()};
  const windows = mode => run([driverDll,mode,windowsDir],{env:windowsEnv});
  windows("seed");
  proxy = createServer(async (req,res) => {
    try {
      const prefix = "/tuci-sync";
      if (req.url.startsWith(prefix + "/")) {
        const chunks = [];
        for await (const chunk of req) chunks.push(chunk);
        const upstream = await fetch(base + req.url.slice(prefix.length), {
          method:req.method, headers:{"Content-Type":"application/json", Authorization:req.headers.authorization || ""},
          body:req.method==="GET" ? undefined : Buffer.concat(chunks)
        });
        res.writeHead(upstream.status,{"Content-Type":"application/json"});
        return res.end(await upstream.text());
      }
      if (["/storage.js","/managed-links.js","/tests/storage-roundtrip.js"].includes(req.url)) {
        res.writeHead(200,{"Content-Type":"text/javascript"});
        return res.end(readFileSync(join(root,"web",req.url)));
      }
      res.writeHead(200,{"Content-Type":"text/html"});
      res.end("<title>兔词本地测试</title>");
    } catch (error) {
      res.writeHead(500);
      res.end(error.message);
    }
  });
  proxy.listen(0,"127.0.0.1");
  await once(proxy,"listening");
  const engine = playwright[process.env.BROWSER || "webkit"];
  browser = await engine.launch({executablePath:process.env.CHROME_BIN});
  const page = await browser.newPage();
  await page.goto("http://127.0.0.1:" + proxy.address().port);
  console.log(await page.evaluate(async ([a,b]) => (await import("/tests/storage-roundtrip.js")).testStorage(a,b),[invite(),invite()]));
  console.log(await page.evaluate(async ([code,body]) => (await import("/tests/storage-roundtrip.js")).testWindowsPairing(code,body),
    [readFileSync(join(windowsDir,"pair-code.txt"),"utf8").trim(),readFileSync(example,"utf8").replace(/^\uFEFF/,"")]));
  windows("verify");
  console.log("Windows verified the original and the browser note on disk.");
} finally {
  if (browser) await browser.close();
  if (proxy) proxy.close();
  if (backend?.exitCode === null) {
    const ended = once(backend, "exit");
    backend.kill();
    await ended;
  }
  rmSync(dir,{recursive:true,force:true});
}
