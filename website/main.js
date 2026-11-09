import { releaseKey } from './release-key.js';
import { setupMotion, fadePreviousPreview, animateMenu, reveal } from './motion.js';

const menu = document.querySelector('.menu-toggle');
const navigation = document.querySelector('.main-nav');
function closeMenu(restoreFocus = false) { const focusedInside = navigation?.contains(document.activeElement); navigation?.classList.remove('is-open'); menu?.setAttribute('aria-expanded', 'false'); menu?.setAttribute('aria-label', '展开导航'); if (restoreFocus && focusedInside) menu?.focus(); }
menu?.addEventListener('click', () => { const open = menu.getAttribute('aria-expanded') !== 'true'; navigation.classList.toggle('is-open', open); menu.setAttribute('aria-expanded', String(open)); menu.setAttribute('aria-label', open ? '收起导航' : '展开导航'); animateMenu(navigation, open); });
document.addEventListener('keydown', event => { if (event.key === 'Escape') closeMenu(true); });
document.addEventListener('click', event => { if (!event.target.closest('.site-header')) closeMenu(); });

const previews = {
  capture: ['记下遇见它的那一句', '兔词收录页面：输入 resilient，并阅读生成的词汇笔记'],
  library: ['把相关的词，连成自己的词库', '兔词词库页面：左侧文件列表，右侧 Markdown 笔记阅读'],
  roaming: ['回想一下，再轻轻按住释义', '兔词漫游页面：回想单词释义，并查看本地复习记录'],
  reading: ['在阅读里，顺手留住一个新词', '兔词每日阅读页面：阅读英文文章并选择单词查释义'],
};
let previewGeneration = 0;
let finishPreviewTransition = () => {};
for (const button of document.querySelectorAll('[data-preview]')) {
  button.addEventListener('click', async () => {
    const kind = button.dataset.preview;
    if (!previews[kind]) return;
    const generation = ++previewGeneration;
    document.querySelectorAll('[data-preview]').forEach(tab => tab.setAttribute('aria-pressed', String(tab === button)));
    const preview = document.querySelector('#app-preview');
    if (preview.dataset.kind === kind) return;
    const image = new Image(); image.src = button.dataset.previewSrc || new URL(`${kind}.png`, preview.src).href;
    try { await image.decode(); } catch {
      if (generation === previewGeneration) document.querySelectorAll('[data-preview]').forEach(tab => tab.setAttribute('aria-pressed', String(tab.dataset.preview === preview.dataset.kind)));
      return;
    }
    if (generation !== previewGeneration) return;
    finishPreviewTransition();
    finishPreviewTransition = fadePreviousPreview(preview);
    preview.src = image.src; preview.alt = previews[kind][1];
    preview.dataset.kind = kind;
    document.querySelector('#preview-caption').textContent = previews[kind][0];
    document.querySelector('.visual-number').textContent = `${String(Object.keys(previews).indexOf(kind) + 1).padStart(2, '0')} — 04`;
  });
}

const decode64 = value => Uint8Array.from(atob(value), char => char.charCodeAt(0));
async function verifiedRelease(envelope) {
  if (!crypto.subtle || typeof envelope.payload !== 'string' || typeof envelope.signature !== 'string') throw new Error('Verification unavailable');
  const publicKey = decode64(releaseKey.replace(/-----(?:BEGIN|END) PUBLIC KEY-----|\s/g, ''));
  const key = await crypto.subtle.importKey('spki', publicKey, { name: 'RSA-PSS', hash: 'SHA-256' }, false, ['verify']);
  const payload = decode64(envelope.payload);
  if (!(await crypto.subtle.verify({ name: 'RSA-PSS', saltLength: 32 }, key, decode64(envelope.signature), payload))) throw new Error('Invalid signature');
  const release = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(payload));
  const expected = `https://101.133.134.128/tuci-updates/packages/tuci-win-x64-${release.version}.zip`;
  if (!/^\d+\.\d+\.\d+$/.test(release.version) || release.platform !== 'win-x64' || release.packageUrl !== expected || !Number.isSafeInteger(release.sizeBytes) || release.sizeBytes < 1 || release.sizeBytes > 536870912 || !/^[a-f0-9]{64}$/i.test(release.sha256) || !Array.isArray(release.releaseNotes) || !release.releaseNotes.every(note => typeof note === 'string') || !Number.isFinite(Date.parse(release.publishedAt))) throw new Error('Invalid release');
  return release;
}
async function refreshRelease() {
  try {
    const response = await fetch('/tuci-updates/stable.json', { cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(10000) });
    if (!response.ok || Number(response.headers.get('content-length')) > 65536) throw new Error('Feed unavailable');
    const text = await response.text();
    if (new TextEncoder().encode(text).byteLength > 65536) throw new Error('Feed too large');
    const release = await verifiedRelease(JSON.parse(text));
    document.querySelectorAll('[data-release-version]').forEach(node => { node.textContent = release.version; });
    document.querySelectorAll('[data-release-size]').forEach(node => { node.textContent = `${(release.sizeBytes / 1048576).toFixed(1)} MB`; });
    document.querySelectorAll('[data-release-date]').forEach(node => { node.textContent = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai' }).format(new Date(release.publishedAt)); });
    document.querySelectorAll('[data-release-sha]').forEach(node => { node.textContent = release.sha256; });
    document.querySelectorAll('[data-download]').forEach(node => { node.href = release.packageUrl; });
    document.querySelectorAll('[data-release-notes]').forEach(list => { list.replaceChildren(...release.releaseNotes.map(note => { const item = document.createElement('li'); item.textContent = note; return item; })); });
    document.querySelectorAll('[data-release-status]').forEach(node => { node.textContent = '已核验最新版本 · 内含离线词典和翻译模型'; });
    document.documentElement.dataset.releaseVerified = 'true';
  } catch {
    document.querySelectorAll('[data-release-status]').forEach(node => { node.textContent = '暂时无法检查最新版本，仍可下载下方已发布的完整包。'; });
  }
}
void refreshRelease();
setupMotion();
document.querySelector('[data-copy-hash]')?.addEventListener('click', async () => {
  const text = document.querySelector('[data-release-sha]')?.textContent.trim();
  const feedback = document.querySelector('[data-copy-feedback]');
  try { await navigator.clipboard.writeText(text); feedback.textContent = '已复制，可以与下载文件的 SHA-256 对照。'; reveal(feedback, { distance: 3, duration: 180 }); }
  catch { feedback.textContent = '请选中上方校验值，再手动复制。'; }
});
