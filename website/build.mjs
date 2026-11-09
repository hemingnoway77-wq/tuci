import { readFile, writeFile, mkdir, rm, cp, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join, basename } from 'node:path';
import { verify, constants, createHash } from 'node:crypto';

const site = dirname(fileURLToPath(import.meta.url));
const repo = resolve(site, '..');
const dist = resolve(site, 'dist');
const origin = 'https://101.133.134.128';
const source = await readFile(join(repo, 'src/VocabCollector.Core/Updates/UpdateConfiguration.cs'), 'utf8');
const key = source.match(/-----BEGIN PUBLIC KEY-----[\s\S]+?-----END PUBLIC KEY-----/)?.[0];
if (!key) throw new Error('Release verification key is missing.');
let raw;
try {
  const response = await fetch(`${origin}/tuci-updates/stable.json`, { signal: AbortSignal.timeout(15000), redirect: 'error' });
  if (!response.ok) throw new Error(`Feed returned ${response.status}`);
  raw = await response.text();
} catch {
  raw = await readFile(join(repo, 'dist/releases/1.1.0/stable.json'), 'utf8');
  console.log('Using the locally retained signed release manifest.');
}
const envelope = JSON.parse(raw);
const payload = Buffer.from(envelope.payload, 'base64');
if (!verify('sha256', payload, { key, padding: constants.RSA_PKCS1_PSS_PADDING, saltLength: 32 }, Buffer.from(envelope.signature, 'base64'))) throw new Error('Release signature is invalid.');
const release = JSON.parse(payload.toString('utf8'));
if (!/^\d+\.\d+\.\d+$/.test(release.version) || release.platform !== 'win-x64' || release.packageUrl !== `${origin}/tuci-updates/packages/tuci-win-x64-${release.version}.zip`) throw new Error('Unexpected release metadata.');
if (dirname(dist) !== site || basename(dist) !== 'dist') throw new Error('Unsafe build output path.');
await rm(dist, { recursive: true, force: true });
await mkdir(join(dist, 'assets'), { recursive: true });
await cp(join(site, 'assets'), join(dist, 'assets'), { recursive: true });
const css = await readFile(join(site, 'styles.css'), 'utf8') + '\n' + await readFile(join(site, 'pages/content.css'), 'utf8');
const motionCss = await readFile(join(site, 'motion.css'), 'utf8');
await writeFile(join(dist, 'assets/site.css'), css + '\n' + motionCss);
const js = await readFile(join(site, 'main.js'), 'utf8');
const motionJs = await readFile(join(site, 'motion.js'), 'utf8');
const imageVersions = Object.fromEntries(await Promise.all(['capture', 'library', 'roaming', 'reading'].map(async name => [name, createHash('sha256').update(await readFile(join(site, `assets/${name}.png`))).digest('hex').slice(0, 10)])));
const assetVersion = createHash('sha256').update(css + motionCss + js + motionJs + JSON.stringify(imageVersions)).digest('hex').slice(0, 10);
await writeFile(join(dist, 'assets/site.js'), js.replace("'./motion.js'", `'./motion.js?v=${assetVersion}'`));
await writeFile(join(dist, 'assets/motion.js'), motionJs);
await writeFile(join(dist, 'assets/release-key.js'), `export const releaseKey = ${JSON.stringify(key)};\n`);
const escape = value => String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);
const arrow = '<svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M5 12h14M13 6l6 6-6 6"/></svg>';
const nav = [['/', '首页', 'home'], ['/features/', '功能一览', 'features'], ['/guide/', '使用指南', 'guide'], ['/tuci/', '手机网页版', 'web'], ['/download/', '下载', 'download']];
function shell(content, page) {
  const title = page.title + ' · 兔词';
  return `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="theme-color" content="#f1e9da"><title>${escape(title)}</title><meta name="description" content="${escape(page.description)}"><link rel="canonical" href="${origin}${page.path}"><meta property="og:type" content="website"><meta property="og:locale" content="zh_CN"><meta property="og:title" content="${escape(title)}"><meta property="og:description" content="${escape(page.description)}"><meta property="og:image" content="${origin}/assets/capture.png?v=${imageVersions.capture}"><link rel="icon" href="/assets/favicon.ico"><link rel="stylesheet" href="/assets/site.css?v=${assetVersion}"><script type="module" src="/assets/site.js?v=${assetVersion}"></script></head>
<body class="page-${page.name}"><a class="skip-link" href="#main-content">跳至正文</a>
<header class="site-header"><div class="header-inner"><a class="brand" href="/" aria-label="兔词首页"><img src="/assets/icon.png" alt="" width="35" height="35"><span>兔词<small>WORDS, WORTH KEEPING.</small></span></a><button class="menu-toggle" type="button" aria-controls="main-navigation" aria-expanded="false" aria-label="展开导航"><span></span><span></span></button><nav id="main-navigation" class="main-nav" aria-label="主导航">${nav.map(([path, label, name]) => `<a href="${path}"${name === page.name ? ' aria-current="page"' : ''}>${label}</a>`).join('')}</nav><a class="header-cta" href="/download/">下载兔词 ${arrow}</a></div></header>
${content}
<footer class="site-footer"><div class="container footer-main"><div><a class="brand footer-brand" href="/"><img src="/assets/icon.png" alt="" width="31" height="31"><span>兔词</span></a><p>让遇见的单词，慢慢变成自己的语言。</p></div><div class="footer-links"><a href="/features/">探索功能</a><a href="/guide/">开始使用</a><a href="/download/">下载 Windows 版</a></div><div class="footer-aside"><span class="eyebrow">A LITTLE MORE, EVERY DAY.</span><p>本地词库 · Markdown 笔记 · 日常阅读</p></div></div><div class="container footer-bottom"><span>© 2026 兔词</span><span>为每一次认真阅读而作。</span><a href="/guide/#faq">常见问题 ${arrow}</a></div></footer></body></html>`;
}
const pages = [
  { name: 'home', file: 'home', path: '/', title: '让遇见的单词，有下文', description: '兔词是一款 Windows 英语词汇笔记工具。收录生词、阅读 Markdown 笔记、漫游复习，在日常阅读中积累自己的本地词库。' },
  { name: 'features', file: 'features', path: '/features/', title: '功能一览', description: '从收录、词库到漫游与每日阅读，了解兔词如何把遇见的英文单词变成可阅读、可连接、可复习的笔记。' },
  { name: 'download', file: 'download', path: '/download/', title: '下载 Windows 版', description: '下载兔词 Windows x64 完整免安装包，包含离线词典、翻译模型和在线更新功能。完整解压即可开始。' },
  { name: 'guide', file: 'guide', path: '/guide/', title: '使用指南', description: '兔词上手指南：下载与解压、本地词库、API 设置、收录新词、离线阅读和应用内更新，以及常见问题解答。' },
];
for (const page of pages) {
  let content = await readFile(join(site, `pages/${page.file}.html`), 'utf8');
  const fields = { version: release.version, size: `${(release.sizeBytes / 1048576).toFixed(1)} MB`, packageUrl: release.packageUrl, sha256: release.sha256, publishedDate: new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai' }).format(new Date(release.publishedAt)) };
  content = content.replace(/\{\{(\w+)\}\}/g, (_, field) => { if (!(field in fields)) throw new Error(`Unknown template field ${field}`); return escape(fields[field]); });
  content = content.replace('<!-- RELEASE_NOTES -->', release.releaseNotes.map(note => `<li>${escape(note)}</li>`).join(''));
  content = content.replace('id="app-preview"', 'id="app-preview" data-kind="capture"');
  content = content.replace(/\/assets\/(capture|library|roaming|reading)\.png/g, (_, name) => `/assets/${name}.png?v=${imageVersions[name]}`);
  content = content.replace(/data-preview="(capture|library|roaming|reading)"/g, (_, name) => `data-preview="${name}" data-preview-src="/assets/${name}.png?v=${imageVersions[name]}"`);
  const folder = join(dist, page.path.replace(/^\//, ''));
  await mkdir(folder, { recursive: true });
  await writeFile(join(folder, 'index.html'), shell(content, page));
}
await writeFile(join(dist, '404.html'), shell('<main id="main-content" class="container error-page"><span class="eyebrow">PAGE NOT FOUND</span><h1 class="page-title">这一页，暂时没找到。</h1><p class="lead">回到首页，继续认识兔词。</p><a class="button button-primary" href="/">回到首页 '+arrow+'</a></main>', { name: '404', title: '页面未找到', description: '回到兔词官网首页。', path: '/404.html' }));
await writeFile(join(dist, 'robots.txt'), `User-agent: *\nAllow: /\nDisallow: /tuci-sync/\nDisallow: /tuci-updates/\nSitemap: ${origin}/sitemap.xml\n`);
await writeFile(join(dist, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${pages.map(page => `<url><loc>${origin}${page.path}</loc></url>`).join('')}</urlset>`);
// Local preview uses the exact signed production feed; production Nginx keeps its dedicated update route.
await mkdir(join(dist, 'tuci-updates'), { recursive: true });
await writeFile(join(dist, 'tuci-updates/stable.json'), raw);
const files = [];
async function inventory(folder) { for (const entry of await (await import('node:fs/promises')).readdir(folder, { withFileTypes: true })) { const file = join(folder, entry.name); if (entry.isDirectory()) await inventory(file); else files.push({ path: file.slice(dist.length + 1).replaceAll('\\', '/'), bytes: (await stat(file)).size }); } }
await inventory(dist);
await writeFile(join(site, 'build-report.json'), JSON.stringify({ version: release.version, assetVersion, files }, null, 2));
console.log(`Built ${pages.length} pages for release ${release.version}: ${dist}`);
