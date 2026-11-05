// Run from the repository root after building and serving .artifacts/web on port 8787:
//   node web/tests/reading-run.mjs chromium
//   node web/tests/reading-run.mjs webkit
// PLAYWRIGHT_MODULE may point to an installed playwright package; TUCI_QA_URL overrides the local URL.
import { createRequire } from 'node:module';
import { mkdirSync } from 'node:fs';
import assert from 'node:assert/strict';
const require=createRequire(import.meta.url);
const {chromium,webkit}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
mkdirSync('.artifacts/visual',{recursive:true});
const base=process.env.TUCI_QA_URL||'http://127.0.0.1:8787/';
(async()=>{
  const engine=process.argv[2]||'chromium';
  const browser=await({chromium,webkit}[engine]).launch({headless:true});
  try {
    const context=await browser.newContext({viewport:{width:402,height:874},deviceScaleFactor:2,isMobile:true,hasTouch:true,timezoneId:'Asia/Shanghai'});
    await context.addInitScript(()=>{
      const now=new Date(),day=[now.getFullYear(),String(now.getMonth()+1).padStart(2,'0'),String(now.getDate()).padStart(2,'0')].join('-');
      const id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
      localStorage.setItem('tuci.web.preferences.v1',JSON.stringify({theme:'ink'}));
      localStorage.setItem('tuci:reading:edition-v1',JSON.stringify({day,articles:[{id,title:'A little apple story',summary:'A short reading fixture.',source:'Test reading',category:'general',url:'https://www.bbc.com/news/test'}]}));
      localStorage.setItem('tuci:reading:bodies-v1',JSON.stringify([{id,paragraphs:['An apple fell. She chose another apple! Don’t forget the well-known boat. Apple trees grow slowly.','Reading helps us discover something new every day. A longer paragraph leaves enough room for selection and its translated meaning.']} ]));
      sessionStorage.setItem('draft-sentinel','preserve unrelated draft');
      window.readerCalls=[];window.mediaCalls=[];window.speechCalls=[];let retries=0;
      const nativeFetch=window.fetch.bind(window);
      window.fetch=async(url,options)=>{
        const u=new URL(url,location.href);
        if(u.hostname==='api.mymemory.translated.net') {
          const word=u.searchParams.get('q');window.readerCalls.push({service:'translation',word,credentials:options.credentials});
          await new Promise(r=>setTimeout(r,word==='fell'?250:20));
          if(word==='forget'&&retries++===0)return Response.json({responseStatus:403,quotaFinished:true});
          return Response.json({responseStatus:200,responseData:{translatedText:({apple:'苹果',boat:'船',fell:'落下',forget:'忘记'})[word]||'参考译文'}});
        }
        if(u.hostname==='api.dictionaryapi.dev') {
          const word=decodeURIComponent(u.pathname.split('/').pop());window.readerCalls.push({service:'pronunciation',word});
          if(word==='boat')throw new TypeError('Dictionary unavailable');
          return Response.json([{word,phonetic:'/ˈæpəl/',phonetics:[{text:'/ˈæpəl/',audio:'https://api.dictionaryapi.dev/media/pronunciations/en/apple-us.mp3',sourceUrl:'https://commons.wikimedia.org/wiki/File:Apple.ogg'}]}]);
        }
        return nativeFetch(url,options);
      };
      HTMLMediaElement.prototype.play=function(){window.mediaCalls.push(this.src);setTimeout(()=>this.dispatchEvent(new Event('ended')),0);return Promise.resolve();};
      Object.defineProperty(window,'speechSynthesis',{value:{getVoices:()=>[{lang:'en-US'}],cancel(){},speak(u){window.speechCalls.push(u.text);setTimeout(()=>u.onend?.(),0);}}});
      window.SpeechSynthesisUtterance=class{constructor(text){this.text=text;}};
    });
    const page=await context.newPage(),errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.goto(base+'#reading');
    await page.getByRole('button',{name:/A little apple story/}).click();
    await page.locator('.learn-token').first().waitFor();
    const apple=page.locator('.learn-token').filter({hasText:/^apple$/i});
    await apple.nth(1).tap();
    assert.equal(await page.locator('.learn-token.is-selected').count(),1);
    assert.equal(await apple.nth(1).getAttribute('aria-pressed'),'true');
    assert.equal(await apple.first().getAttribute('aria-pressed'),'false');
    assert.equal(await page.evaluate(()=>window.readerCalls.length),0);
    const snapshot=()=>page.evaluate(async()=>{
      const {openStore}=await import('./storage.js');const store=await openStore({autoSync:false});
      const notes=store.notes();store.close();
      return {local:Object.entries(localStorage).sort(),session:Object.entries(sessionStorage).sort(),notes};
    });
    const before=await snapshot();
    await page.getByRole('button',{name:'翻译',exact:true}).click();
    await page.getByText('苹果',{exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>window.mediaCalls.length+window.speechCalls.length),0);
    const after=await snapshot();
    assert.deepEqual(after,before,'temporary translation changed persistent data/drafts');
    await page.getByRole('button',{name:'播放读音',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.mediaCalls.length),1);
    for(const width of [320,402,1280]) {
      await page.setViewportSize({width,height:874});
      await apple.nth(1).scrollIntoViewIfNeeded();
      if(await page.locator('.learn-word-tools').isHidden()){await apple.nth(1).click();await page.getByRole('button',{name:'翻译',exact:true}).click();await page.getByText('苹果',{exact:true}).waitFor();}
      const box=await page.locator('.learn-word-tools').boundingBox();
      assert.ok(box&&box.x>=0&&box.x+box.width<=width+1,`${width}px popup overflows`);
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
      for(const name of ['收录','翻译'])assert.ok((await page.getByRole('button',{name,exact:true}).boundingBox()).height>=44);
    }
    await page.setViewportSize({width:402,height:874});
    await page.evaluate(()=>new Promise(requestAnimationFrame));
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,'rotation left stale popup coordinates');
    await page.screenshot({path:`.artifacts/visual/reader-${engine}.png`,fullPage:true});
    await page.getByRole('button',{name:'关闭单词选项'}).click();
    assert.equal(await apple.nth(1).evaluate(el=>document.activeElement===el),true,'dismiss lost trigger focus');
    assert.equal(await page.locator('.learn-token.is-selected').count(),0);
    const fell=page.locator('.learn-token').filter({hasText:/^fell$/});
    await fell.click();await page.getByRole('button',{name:'翻译',exact:true}).click();
    const boat=page.locator('.learn-token').filter({hasText:/^boat$/});
    await boat.dispatchEvent('click',{bubbles:true,detail:1});
    await page.getByRole('button',{name:'翻译',exact:true}).click();
    await page.getByText('船',{exact:true}).waitFor();
    await page.waitForFunction(()=>window.readerCalls.some(c=>c.word==='fell'));
    await page.waitForTimeout(300);
    assert.equal(await page.locator('.learn-word-meaning').textContent(),'船','late response overwrote current word');
    await page.getByRole('button',{name:'播放读音',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.speechCalls.at(-1)),'boat');
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('.learn-token.is-selected').count(),0);
    await page.locator('.learn-token').filter({hasText:/^forget$/}).click();
    await page.getByRole('button',{name:'翻译',exact:true}).click();
    await page.getByText(/额度已用完/).waitFor();
    assert.equal(await page.getByRole('button',{name:'收录',exact:true}).isEnabled(),true);
    await page.getByRole('button',{name:'重试翻译',exact:true}).click();await page.getByText('忘记',{exact:true}).waitFor();
    await page.locator('h1').click();
    assert.equal(await page.locator('.learn-word-tools').isHidden(),true);
    await page.setViewportSize({width:568,height:240});
    await apple.nth(1).click();
    await page.evaluate(()=>new Promise(requestAnimationFrame));
    const landscape=await page.locator('.learn-word-tools').boundingBox();
    const navigation=await page.locator('#tabs').boundingBox();
    assert.ok(landscape.y+landscape.height<=navigation.y+1,'landscape popup covered navigation');
    await page.getByRole('button',{name:'关闭单词选项'}).click();
    await page.setViewportSize({width:402,height:874});
    await apple.nth(1).click();
    await page.getByRole('button',{name:'收录',exact:true}).click();
    await page.getByRole('heading',{name:'收录生词'}).waitFor();
    assert.equal(await page.locator('.learn-word').inputValue(),'apple');
    assert.equal(await page.locator('.learn-sentence').inputValue(),'She chose another apple!');
    assert.deepEqual(errors,[]);
    console.log(JSON.stringify({engine,result:'PASS',singleOccurrenceHighlight:true,temporaryLookupPreservesStorage:true,playbackUserTriggered:true,lateResponseIgnored:true,systemSpeechFallback:true,quotaRetry:true,viewportWidths:[320,402,1280],collectIncludesCorrectSentence:true,errors}));
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
