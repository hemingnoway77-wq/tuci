#!/usr/bin/env python3
"""Atomic publication of the /tuci/ PWA on the existing Nginx virtual host."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import time
import uuid
import zipfile


def main(archive, expected):
    source=Path(archive)
    if hashlib.sha256(source.read_bytes()).hexdigest()!=expected: raise ValueError('Archive hash mismatch')
    base=Path('/srv/tuci-web'); (base/'releases').mkdir(parents=True,exist_ok=True)
    base.chmod(0o755); (base/'releases').chmod(0o755)
    release=base/'releases'/uuid.uuid4().hex; release.mkdir(mode=0o755); release.chmod(0o755)
    required={'tuci/index.html','tuci/app.js','tuci/account.js','tuci/storage.js','tuci/sw.js','tuci/manifest.json','tuci/icons/apple-touch.png'}
    with zipfile.ZipFile(source) as package:
        entries=package.infolist(); seen=set()
        if len(entries)>256 or sum(e.file_size for e in entries)>96*1024*1024: raise ValueError('Oversized PWA')
        for entry in entries:
            path=PurePosixPath(entry.filename)
            translation_binary=re.fullmatch(r'tuci/offline-translation/[a-f0-9]{16}/(?:bergamot-translator\.wasm|en-zh/(?:model\.enzh\.intgemm\.alphas\.bin|lex\.50\.50\.enzh\.s2t\.bin|(?:srcvocab|trgvocab)\.enzh\.spm))', entry.filename)
            if (path.is_absolute() or '..' in path.parts or '\\' in entry.filename or path.parts[0]!='tuci' or
                any(p.startswith('.') for p in path.parts) or entry.filename.lower() in seen or stat.S_ISLNK(entry.external_attr>>16) or
                (path.suffix not in {'.html','.css','.js','.json','.jsonl','.png','.svg','.ico','.txt','.woff2'} and not translation_binary) or
                path.suffix=='.woff2' and path.parts[:2]!=('tuci','fonts')): raise ValueError('Unsafe PWA file')
            seen.add(entry.filename.lower())
            target=release/entry.filename; target.parent.mkdir(parents=True,exist_ok=True)
            for directory in target.parents:
                if directory==release.parent: break
                directory.chmod(0o755)
            with package.open(entry) as src,target.open('xb') as out: shutil.copyfileobj(src,out)
            target.chmod(0o644)
        if not required.issubset(seen): raise ValueError('Missing required PWA files')
    manifest=json.loads((release/'tuci/manifest.json').read_text())
    if manifest.get('display')!='standalone': raise ValueError('PWA must support standalone display')
    config=Path('/etc/nginx/sites-available/qingqing'); original=config.read_text()
    backup=Path('/root/tuci-account-backups')/('web-'+time.strftime('%Y%m%dT%H%M%SZ',time.gmtime()))
    backup.mkdir(parents=True,mode=0o700); backup.chmod(0o700); (backup/'qingqing.before.conf').write_text(original)
    other={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in config.parent.iterdir() if p.is_file() and p!=config}
    current=base/'current'; previous=str(current.resolve()) if current.exists() else None
    include='include /etc/nginx/snippets/tuci-web.conf;'
    if include not in original:
        marker='include /etc/nginx/snippets/tuci-website.conf;'
        if original.count(marker)!=1: raise ValueError('Unexpected Nginx structure')
        config.write_text(original.replace(marker,marker+'\n    '+include)); config.chmod(0o644)
    temporary=base/('current-'+uuid.uuid4().hex); temporary.symlink_to(release); os.replace(temporary,current)
    try:
        subprocess.run(['nginx','-t'],check=True,capture_output=True)
        subprocess.run(['systemctl','reload','nginx'],check=True)
    except Exception:
        config.write_text(original)
        if previous:
            restored=base/('restore-'+uuid.uuid4().hex); restored.symlink_to(previous); os.replace(restored,current)
        raise
    unchanged=other=={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in config.parent.iterdir() if p.is_file() and p!=config}
    report=dict(url='https://101.133.134.128/tuci/',release=str(release),previous=previous,sha256=expected,otherSitesUnchanged=unchanged,files=len(entries))
    (backup/'report.json').write_text(json.dumps(report,indent=2)); print(json.dumps(report))


if __name__=='__main__': main(*sys.argv[1:])
