import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync,readFileSync,readdirSync,statSync,realpathSync,rmSync} from 'node:fs';
import {join,resolve,dirname,basename} from 'node:path';
import {fileURLToPath} from 'node:url';
import {tmpdir} from 'node:os';
import {createHash} from 'node:crypto';
test('the published dictionary is complete while first install precaches only the small shell',()=>{
  const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..'),target=realpathSync(mkdtempSync(join(tmpdir(),'tuci-web-build-')));
  try{
    execFileSync(process.env.PYTHON||'python',[join(root,'web','build.py'),target],{cwd:root,windowsHide:true,stdio:'pipe'});
    const manifest=JSON.parse(readFileSync(join(target,'dictionary','dictionary.manifest.json'),'utf8'));
    for(const file of manifest.files){
      const bytes=readFileSync(join(target,'dictionary',file.path));assert.equal(bytes.length,file.bytes);assert.equal(createHash('sha256').update(bytes).digest('hex'),file.sha256);
    }
    const worker=readFileSync(join(target,'sw.js'),'utf8'),assets=JSON.parse(worker.match(/const files = (\{[^\n]+\});/)[1]);
    const fonts=JSON.parse(readFileSync(join(target,'fonts','manifest.json'),'utf8'));
    const fontAssets=JSON.parse(worker.match(/const fontFiles = (\{[^\n]+\});/)[1]);
    assert.equal(fonts.files.length,101);assert.equal(Object.keys(fontAssets).length,101);
    for(const file of fonts.files){
      const bytes=readFileSync(join(target,'fonts',file.path));assert.equal(bytes.subarray(0,4).toString(),'wOF2');
      assert.equal(bytes.length,file.bytes);assert.equal(createHash('sha256').update(bytes).digest('hex'),file.sha256);
      assert.equal(Object.hasOwn(assets,'fonts/'+file.path),file.precache);
    }
    const fontCss=readFileSync(join(target,'fonts','serif.css'),'utf8');
    assert.doesNotMatch(fontCss,/url\(['"]?https?:/);assert.match(fontCss,/font-family:'Tuci Serif SC'/);
    assert.equal(fonts.files.filter(file=>file.precache).length,6);
    assert.ok(Object.hasOwn(assets,'dictionary/dictionary.manifest.json'));assert.ok(Object.hasOwn(assets,'dictionary/LICENSE.MIT.txt'));
    assert.equal(Object.keys(assets).some(path=>path.endsWith('.jsonl')),false);
    for(const path of ['update.html','update-recovery.js']){assert.equal(Object.hasOwn(assets,path),false);assert.ok(statSync(join(target,path)).size>0);}
    assert.ok(Object.keys(assets).reduce((sum,path)=>sum+statSync(join(target,path)).size,0)<1024*1024,'default install must stay below 1 MiB');
    const inventory=[];function visit(directory,prefix=''){for(const entry of readdirSync(directory,{withFileTypes:true})){const path=prefix+entry.name;if(entry.isDirectory())visit(join(directory,entry.name),path+'/');else inventory.push(path);}}
    visit(target);assert.equal(inventory.filter(path=>path.endsWith('.jsonl')).length,26);assert.ok(inventory.every(path=>!path.endsWith('.md') && !path.split('/').some(part=>part.startsWith('.'))));
  }finally{
    const resolved=realpathSync(target);if(dirname(resolved)!==realpathSync(tmpdir()) || !basename(resolved).startsWith('tuci-web-build-'))throw Error('Unexpected build fixture path');rmSync(resolved,{recursive:true,force:true});
  }
});
