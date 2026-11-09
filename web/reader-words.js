import { normalWord } from './learning-data.js';
import { lookupWord, lookupPronunciation } from './word-lookup.js';

export function wordSegments(text) {
  const segments = [];
  let offset = 0;
  for (const match of text.matchAll(/[a-z]+(?:[-'‘’][a-z]+)*/gi)) {
    if (match.index > offset) segments.push({ text: text.slice(offset, match.index), start: offset, end: match.index });
    const end = match.index + match[0].length;
    let word;
    try { word = normalWord(match[0]); } catch { /* Keep unusually long words as readable text. */ }
    segments.push({ text: match[0], start: match.index, end, ...(word ? { word } : {}) });
    offset = end;
  }
  if (offset < text.length) segments.push({ text: text.slice(offset), start: offset, end: text.length });
  return segments;
}

export function wordSentence(text, start, end) {
  const left = text.slice(0, start).search(/[^.!?]*$/);
  const right = text.slice(end).search(/[.!?](?:\s|$)/);
  return text.slice(Math.max(0, left), right < 0 ? undefined : end + right + 1).trim().slice(0, 2000);
}

function node(tag, className, text) {
  const el = document.createElement(tag);
  if (className) el.className = className;
  if (text != null) el.textContent = text;
  return el;
}
function action(text, className, handler) {
  const el = node('button', className, text);
  el.type = 'button'; el.addEventListener('click', handler);
  return el;
}

export function mountReadingWords({ container, paragraphs, tools, onCollect }) {
  const tokens = new WeakMap();
  let active, request, revision = 0, destroyed = false, audio, utterance, playbackTimer;
  tools.setAttribute('role', 'dialog');
  tools.setAttribute('aria-label', '单词选项');
  const speechAvailable = () => 'speechSynthesis' in window && 'SpeechSynthesisUtterance' in window;
  function stopPlayback() {
    clearTimeout(playbackTimer);
    if (audio) { audio.onended = audio.onerror = null; audio.pause(); audio.src = ''; audio = null; }
    if (utterance) { utterance.onend = utterance.onerror = null; window.speechSynthesis.cancel(); utterance = null; }
  }
  function close() {
    revision++; request?.abort(); request = null; stopPlayback();
    if (active) { active.element.classList.remove('is-selected'); active.element.setAttribute('aria-pressed', 'false'); }
    active = null; tools.hidden = true; tools.replaceChildren();
  }
  function position() {
    if (!active || tools.hidden) return;
    const rect = active.element.getBoundingClientRect(), box = container.getBoundingClientRect();
    const view = window.visualViewport;
    const top = (view?.offsetTop || 0) + 8;
    const bottom = Math.min((view?.offsetTop || 0) + (view?.height || innerHeight) - 8,
      document.querySelector('#tabs')?.getBoundingClientRect().top - 8 || innerHeight - 8);
    if (rect.bottom < top || rect.top > bottom) { close(); return; }
    const below = bottom - rect.bottom - 8, above = rect.top - top - 8;
    const putBelow = below >= Math.min(170, tools.scrollHeight) || below >= above;
    tools.style.maxHeight = Math.max(0, putBelow ? below : above) + 'px';
    tools.style.setProperty('--word-left', Math.max(0, Math.min(rect.left - box.left, box.width - tools.offsetWidth)) + 'px');
    tools.style.top = (putBelow ? rect.bottom - box.top + 8 : rect.top - box.top - tools.offsetHeight - 8) + 'px';
  }
  function select(element, token, keyboard = false) {
    if (active?.element === element) { close(); return; }
    close();
    active = { element, ...token };
    element.classList.add('is-selected'); element.setAttribute('aria-pressed', 'true');
    const selected = active;
    const heading = node('div', 'learn-word-heading');
    const dismiss = action('×', 'learn-word-close', () => { close(); element.focus({ preventScroll: true }); }); dismiss.setAttribute('aria-label', '关闭单词选项');
    heading.append(node('strong', '', token.word), dismiss);
    const actions = node('div', 'learn-word-actions');
    const collect = action('收录', 'secondary', () => { close(); onCollect(token.word, token.context); });
    const translate = action('翻译', 'button', lookup);
    actions.append(collect, translate);
    const result = node('div', 'learn-word-result'); result.hidden = true;
    tools.replaceChildren(heading, actions, result); tools.hidden = false;
    position();
    if (keyboard) collect.focus({ preventScroll: true });

    function lookup() {
      request?.abort(); stopPlayback(); request = new AbortController();
      const signal = request.signal, version = ++revision;
      const valid = () => !destroyed && active === selected && revision === version && !signal.aborted && tools.isConnected;
      translate.disabled = true; result.hidden = false;
      const meaning = node('p', 'learn-word-meaning', '正在查词…'); meaning.setAttribute('role', 'status');
      const sound = node('div', 'learn-word-sound');
      const phonetic = node('span', 'learn-word-phonetic', '英文读音');
      const notice = node('p', 'learn-word-notice'); notice.setAttribute('role', 'status');
      let pronunciation = { phonetic: '', audio: '', sourceUrl: '' }, useSpeech = false, playback = 0;
      const listen = action('播放读音', 'learn-text learn-word-listen', play);
      listen.disabled = !speechAvailable(); sound.append(phonetic, listen);
      const attribution = node('div', 'learn-word-attribution');
      const provider = node('a', '', '译文：MyMemory'); provider.href = 'https://mymemory.translated.net/';
      provider.target = '_blank'; provider.rel = 'noopener noreferrer'; attribution.append(provider);
      result.replaceChildren(meaning, sound, notice, attribution); position();
      function applyPronunciation(data){
        if(!valid())return;pronunciation=data;phonetic.textContent=data.phonetic || '英文读音';
        listen.disabled=!data.audio && !speechAvailable();
        if(data.sourceUrl && (data.phonetic || data.audio)){
          const source=node('a','','词典与发音来源');source.href=data.sourceUrl;source.target='_blank';source.rel='noopener noreferrer';attribution.append(source);
        }
        if(listen.disabled)notice.textContent='暂未找到可播放的读音。';position();
      }
      lookupWord(token.word,{signal}).then(data=>{
        if(!valid())return;meaning.textContent=data.translation;translate.textContent='翻译';
        if(data.source==='offline'){
          provider.textContent='词典：ECDICT · MIT';provider.href=new URL('./dictionary/LICENSE.MIT.txt',import.meta.url).href;
          pronunciation={phonetic:data.phonetic||'',audio:'',sourceUrl:''};phonetic.textContent=data.phonetic || '英文读音';useSpeech=true;
          listen.textContent='系统朗读';listen.disabled=!speechAvailable();
          if(data.lemma)notice.textContent='词形：'+data.word+' → '+data.lemma;
          if(listen.disabled)notice.textContent+=(data.lemma?'。':'')+'此设备暂不支持系统朗读。';
        }else{
          lookupPronunciation(token.word,{signal}).then(applyPronunciation).catch(()=>{
            if(valid() && !speechAvailable()){notice.textContent='读音暂不可用，请稍后重试。';position();}
          });
        }
      }).catch(error=>{
        if(valid()){meaning.textContent=error.message || '暂时无法查词，请稍后重试。';translate.textContent='重试翻译';}
      }).finally(()=>{if(valid()){translate.disabled=false;position();}});

      function play() {
        if (!valid()) return;
        stopPlayback(); notice.textContent = ''; listen.disabled = true;
        const attempt = ++playback, playing = () => valid() && playback === attempt;
        function restored() { if (playing()) { playback++; stopPlayback(); listen.disabled = false; } }
        function fallback() {
          if (!playing()) return;
          playback++; stopPlayback(); useSpeech = true;
          listen.disabled = !speechAvailable(); listen.textContent = '系统朗读';
          notice.textContent = speechAvailable() ? '词典音频暂不可用，可点系统朗读。' : '读音暂不可用。';
          position();
        }
        try {
          if (pronunciation.audio && !useSpeech) {
            audio = new Audio(pronunciation.audio); audio.preload = 'none'; audio.onended = restored; audio.onerror = fallback;
            playbackTimer = setTimeout(fallback, 12000);
            audio.play().catch(fallback);
          } else if (speechAvailable()) {
            utterance = new SpeechSynthesisUtterance(token.word); utterance.lang = 'en-US'; utterance.rate = .9;
            const voices = window.speechSynthesis.getVoices();
            utterance.voice = voices.find(v => /^en-US$/i.test(v.lang)) || voices.find(v => /^en[-_]/i.test(v.lang)) || null;
            utterance.onend = restored;
            utterance.onerror = () => { if (playing()) { restored(); notice.textContent = '暂时无法朗读，请稍后重试。'; position(); } };
            playbackTimer = setTimeout(restored, 12000);
            window.speechSynthesis.speak(utterance);
          } else { listen.disabled = true; notice.textContent = '此设备暂不支持播放读音。'; position(); }
        } catch { if (pronunciation.audio && !useSpeech) fallback(); else { restored(); notice.textContent = '暂时无法朗读，请稍后重试。'; position(); } }
      }
    }
  }
  for (const paragraph of paragraphs) {
    const text = paragraph.textContent, fragment = document.createDocumentFragment();
    for (const token of wordSegments(text)) {
      if (!token.word) { fragment.append(document.createTextNode(token.text)); continue; }
      const el = node('button', 'learn-token', token.text); el.type = 'button';
      el.setAttribute('aria-pressed', 'false'); el.setAttribute('aria-haspopup', 'dialog');
      tokens.set(el, { ...token, context: wordSentence(text, token.start, token.end) }); fragment.append(el);
    }
    paragraph.replaceChildren(fragment);
  }
  function clicked(event) {
    const element = event.target.closest?.('.learn-token');
    if (element && tokens.has(element)) select(element, tokens.get(element), event.detail === 0);
  }
  function outside(event) {
    if (!active || tools.contains(event.target) || event.target.closest?.('.learn-token') && container.contains(event.target)) return;
    close();
  }
  function keydown(event) {
    if (event.key === 'Escape' && active) { const el = active.element; close(); el.focus({ preventScroll: true }); }
  }
  container.addEventListener('click', clicked);
  document.addEventListener('pointerdown', outside, true);
  document.addEventListener('keydown', keydown);
  window.addEventListener('scroll', position, { passive: true });
  window.addEventListener('resize', position);
  window.visualViewport?.addEventListener('resize', position);
  return () => {
    destroyed = true; close();
    container.removeEventListener('click', clicked);
    document.removeEventListener('pointerdown', outside, true);
    document.removeEventListener('keydown', keydown);
    window.removeEventListener('scroll', position);
    window.removeEventListener('resize', position);
    window.visualViewport?.removeEventListener('resize', position);
  };
}
