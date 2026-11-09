#!/usr/bin/env python3
"""Run on the existing Linux host: verified release, online DB backup, staging smoke, atomic service update.
No credentials, new listeners or Nginx changes. Requires root; retains the previous binary and database.
"""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.request
import uuid
import zipfile


def main(archive, expected):
    os.umask(0o077)
    source=Path(archive)
    if hashlib.sha256(source.read_bytes()).hexdigest()!=expected: raise ValueError('Archive hash mismatch')
    stamp=time.strftime('%Y%m%dT%H%M%SZ',time.gmtime())+'-'+uuid.uuid4().hex[:8]
    backup=Path('/root/tuci-account-backups')/stamp; backup.mkdir(parents=True,mode=0o700)
    database=Path('/var/lib/tuci-sync/tuci.sqlite3')
    snapshot=backup/'tuci-before.sqlite3'
    with sqlite3.connect(database) as current, sqlite3.connect(snapshot) as target: current.backup(target)
    with sqlite3.connect(snapshot) as con:
        if con.execute('PRAGMA integrity_check').fetchone()[0]!='ok': raise ValueError('Backup integrity failed')
        before={t:con.execute('SELECT COUNT(*) FROM '+t).fetchone()[0] for t in ['vaults','devices','changes']}
    for config in ['/etc/systemd/system/tuci-sync.service','/etc/nginx/sites-available/qingqing']:
        shutil.copyfile(config,backup/Path(config).name)
    previous=Path('/opt/tuci-sync/current').resolve()
    (backup/'previous-release.txt').write_text(str(previous))
    (backup/'listeners-before.txt').write_bytes(subprocess.check_output(['ss','-ltn']))
    nginx={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in Path('/etc/nginx/sites-available').iterdir() if p.is_file()}
    (backup/'nginx-before.json').write_text(json.dumps(nginx))
    release=Path('/opt/tuci-sync/releases')/('accounts-'+expected[:20]); release.mkdir(mode=0o755); release.chmod(0o755)
    with zipfile.ZipFile(source) as package:
        entries=package.infolist(); seen=set()
        if len(entries)>1500 or sum(e.file_size for e in entries)>250*1024*1024: raise ValueError('Oversized release')
        for entry in entries:
            path=PurePosixPath(entry.filename)
            if path.is_absolute() or '..' in path.parts or any(p.startswith('.') for p in path.parts) or '\\' in entry.filename or entry.filename.lower() in seen:
                raise ValueError('Unsafe archive')
            seen.add(entry.filename.lower())
            if entry.is_dir(): continue
            target=release/entry.filename; target.parent.mkdir(parents=True,exist_ok=True)
            for directory in target.parents:
                if directory==release.parent: break
                directory.chmod(0o755)
            with package.open(entry) as src,target.open('xb') as out: shutil.copyfileobj(src,out)
            target.chmod(0o755 if target.name=='Tuci.Sync' or target.suffix=='.so' or target.name=='createdump' else 0o644)
    # Exercise the exact Linux runtime against a copy before touching the live service.
    with tempfile.TemporaryDirectory(prefix='stage-',dir='/var/lib/tuci-sync') as temporary:
        staged=Path(temporary)/'copy.sqlite3'; shutil.copyfile(snapshot,staged)
        import pwd
        owner=pwd.getpwnam('tuci-sync')
        os.chown(temporary,owner.pw_uid,owner.pw_gid); os.chown(staged,owner.pw_uid,owner.pw_gid)
        with socket.socket() as sock: sock.bind(('127.0.0.1',0)); port=sock.getsockname()[1]
        env=dict(os.environ,TUCI_DB=str(staged),ASPNETCORE_URLS='http://127.0.0.1:'+str(port),ASPNETCORE_ENVIRONMENT='Production')
        with (backup/'stage.log').open('wb') as log:
            process=subprocess.Popen(['runuser','-u','tuci-sync','--',str(release/'Tuci.Sync')],cwd=release,env=env,stdout=log,stderr=subprocess.STDOUT)
            try:
                base='http://127.0.0.1:'+str(port)
                for _ in range(100):
                    if process.poll() is not None: raise RuntimeError('Staged service exited; inspect stage.log')
                    try:
                        if json.load(urllib.request.urlopen(base+'/health',timeout=2))['ok']: break
                    except OSError: time.sleep(.1)
                else: raise RuntimeError('Staged service did not become healthy')
                req=urllib.request.Request(base+'/v1/account/register',data=json.dumps(dict(username='staging-'+uuid.uuid4().hex[:10],password=uuid.uuid4().hex,deviceName='Deployment check')).encode(),headers={'Content-Type':'application/json'})
                result=json.load(urllib.request.urlopen(req,timeout=10))
                req=urllib.request.Request(base+'/v1/account/me',headers={'Authorization':'Bearer '+result['token']})
                if json.load(urllib.request.urlopen(req,timeout=5))['userId']!=result['userId']: raise RuntimeError('Staged login failed')
            finally: process.terminate(); process.wait(timeout=10)
    link=Path('/opt/tuci-sync')/('next-'+uuid.uuid4().hex); link.symlink_to(release); os.replace(link,'/opt/tuci-sync/current')
    subprocess.run(['systemctl','restart','tuci-sync'],check=True)
    for _ in range(100):
        try:
            if json.load(urllib.request.urlopen('http://127.0.0.1:18096/health',timeout=2))['ok']: break
        except OSError: time.sleep(.1)
    else: raise RuntimeError('New service failed health; database and prior release retained at '+str(backup))
    with sqlite3.connect(database) as con:
        after={t:con.execute('SELECT COUNT(*) FROM '+t).fetchone()[0] for t in before}
        version=con.execute('PRAGMA user_version').fetchone()[0]
        if any(after[t]<n for t,n in before.items()) or version!=2: raise RuntimeError('Migration counts changed unexpectedly')
        if con.execute('PRAGMA integrity_check').fetchone()[0]!='ok': raise RuntimeError('Migrated database integrity failed')
    report=dict(backup=str(backup),previous=str(previous),release=str(release),before=before,after=after,schemaVersion=version,
                nginxUnchanged=nginx=={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in Path('/etc/nginx/sites-available').iterdir() if p.is_file()})
    (backup/'report.json').write_text(json.dumps(report,indent=2))
    print(json.dumps(report))


if __name__=='__main__': main(*sys.argv[1:])
