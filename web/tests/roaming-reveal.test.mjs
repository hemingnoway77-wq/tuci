import test from 'node:test';
import assert from 'node:assert/strict';
import { createRoamingReveal } from '../library.js';

class Element extends EventTarget {
  constructor(tag) { super(); this.tagName=tag.toUpperCase(); this.children=[]; this.attrs=new Map(); this.isConnected=true; this.className=''; this.textContent=''; this.classes=new Set(); this.classList={add:name=>this.classes.add(name),remove:name=>this.classes.delete(name)}; }
  setAttribute(name,value) { this.attrs.set(name,String(value)); }
  getAttribute(name) { return this.attrs.get(name); }
  append(...nodes) { this.children.push(...nodes); }
  querySelector(selector) {
    for(const child of this.children) {
      if(selector.startsWith('.') && child.className.split(' ').includes(selector.slice(1)))return child;
      const nested=child.querySelector(selector);if(nested)return nested;
    }
    return null;
  }
}
function dispatch(target,type,fields={}) {
  target.dispatchEvent(Object.assign(new Event(type,{cancelable:true}),fields));
}

test('the actual meaning area is the hold target and concealed content is absent from its accessible name',()=>{
  const previous=globalThis.document;
  const document=Object.assign(new EventTarget(),{createElement:tag=>new Element(tag),visibilityState:'visible'});
  const window=new EventTarget();
  globalThis.document=document;
  try {
    const text='v. 了解；理解\n一个保持原来换行的较长中文释义。'.repeat(10);
    const {control,dispose}=createRoamingReveal(text,{window,document});
    const meaning=control.querySelector('.nb-meaning-mask'),ink=control.querySelector('.nb-meaning-ink'),clear=control.querySelector('.nb-meaning-clear');
    assert.equal(control.tagName,'BUTTON');assert.equal(control.children[0],meaning);
    assert.equal(ink.textContent,text);assert.equal(clear.textContent,text);
    assert.equal(ink.getAttribute('aria-hidden'),'true');
    assert.equal(meaning.getAttribute('aria-hidden'),'true');
    assert.doesNotMatch(control.getAttribute('aria-label'),/了解|理解/);
    dispatch(control,'pointerdown',{button:0,pointerId:1});
    assert.equal(meaning.getAttribute('aria-hidden'),'false');
    assert.match(control.getAttribute('aria-label'),/了解；理解/);
    assert.equal(control.children.length,2,'label updates retain both visual layers');
    dispatch(control,'pointercancel');
    assert.equal(meaning.getAttribute('aria-hidden'),'true');
    assert.doesNotMatch(control.getAttribute('aria-label'),/了解|理解/);
    dispose();
  } finally { globalThis.document=previous; }
});

test('pointer capture is released when concealing and unrelated contact cannot reveal',()=>{
  const previous=globalThis.document;
  const document=Object.assign(new EventTarget(),{createElement:tag=>new Element(tag),visibilityState:'visible'});
  const window=new EventTarget();globalThis.document=document;
  try {
    const {control,dispose}=createRoamingReveal('n. 词义',{window,document});
    const capture=new Set();control.setPointerCapture=id=>capture.add(id);control.hasPointerCapture=id=>capture.has(id);control.releasePointerCapture=id=>capture.delete(id);
    dispatch(control,'pointerdown',{button:0,isPrimary:false,pointerId:2});
    assert.equal(capture.size,0);assert.equal(control.getAttribute('aria-expanded'),'false');
    dispatch(control,'pointerdown',{button:0,pointerId:1});assert.equal(capture.has(1),true);
    dispatch(window,'blur');assert.equal(capture.size,0);assert.equal(control.getAttribute('aria-expanded'),'false');
    dispatch(control,'pointerdown',{button:0,pointerId:1});dispose();
    assert.equal(capture.size,0);assert.equal(control.getAttribute('aria-expanded'),'false');
  } finally { globalThis.document=previous; }
});
