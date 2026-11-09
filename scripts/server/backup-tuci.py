#!/usr/bin/env python3
"""Consistent daily SQLite snapshot; root-only directory, 14-day retention."""
from datetime import datetime, timezone
from pathlib import Path
import os
import sqlite3
import time

os.umask(0o077)
directory=Path('/var/backups/tuci-sync')
directory.mkdir(parents=True,exist_ok=True,mode=0o700)
if directory.is_symlink(): raise RuntimeError('Backup directory cannot be a symbolic link')
directory.chmod(0o700)
stamp=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
temporary=directory/(stamp+'.partial')
try:
    with sqlite3.connect('file:/var/lib/tuci-sync/tuci.sqlite3?mode=ro',uri=True) as source, sqlite3.connect(temporary) as target:
        source.backup(target)
        if target.execute('PRAGMA integrity_check').fetchone()[0]!='ok': raise RuntimeError('Backup integrity failed')
    temporary.rename(directory/(stamp+'.sqlite3'))
finally:
    if temporary.exists(): temporary.unlink()
for previous in directory.glob('*.sqlite3'):
    if previous.is_file() and not previous.is_symlink() and previous.stat().st_mtime<time.time()-14*86400:
        previous.unlink()
print('Tuci database backup verified')
