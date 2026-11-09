#!/usr/bin/env python3
"""Restricted static-site publisher. Does not bind ports or edit Nginx."""
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import sys
import tempfile
import uuid
import zipfile

ROOT = Path('/srv/tuci-website')
MAX_ARCHIVE = 25 * 1024 * 1024
MAX_EXPANDED = 75 * 1024 * 1024
REQUIRED = {'index.html', 'features/index.html', 'download/index.html', 'guide/index.html',
            'assets/site.css', 'assets/site.js', 'assets/release-key.js', 'assets/icon.png', '404.html'}


def publish():
    command = os.environ.get('SSH_ORIGINAL_COMMAND', '')
    if command == 'site-status-v1':
        current = ROOT / 'current'
        print(json.dumps({'status': 'ready', 'url': 'https://101.133.134.128/',
                          'release': current.resolve().name if current.exists() else None}))
        return
    if command != 'deploy-site-v1':
        raise ValueError('This key only publishes the Tuci website.')
    with (ROOT / 'publish.lock').open('a') as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        header = sys.stdin.buffer.readline(513)
        if not header.endswith(b'\n') or len(header) > 512:
            raise ValueError('Invalid upload header.')
        metadata = json.loads(header)
        length = metadata['bytes']
        if type(length) is not int or not 1 <= length <= MAX_ARCHIVE or not re.fullmatch('[0-9a-f]{64}', metadata['sha256']):
            raise ValueError('Invalid archive length or digest.')
        temporary = Path(tempfile.mkdtemp(prefix='site-', dir=ROOT / 'incoming'))
        release = ROOT / 'releases' / uuid.uuid4().hex
        try:
            digest = hashlib.sha256()
            remaining = length
            package = temporary / 'website.zip'
            with package.open('xb') as target:
                while remaining:
                    chunk = sys.stdin.buffer.read(min(remaining, 1024 * 1024))
                    if not chunk:
                        raise ValueError('Incomplete upload.')
                    target.write(chunk)
                    digest.update(chunk)
                    remaining -= len(chunk)
            if digest.hexdigest() != metadata['sha256']:
                raise ValueError('Archive digest mismatch.')
            with zipfile.ZipFile(package) as archive:
                entries = archive.infolist()
                if len(entries) > 1000 or sum(entry.file_size for entry in entries) > MAX_EXPANDED:
                    raise ValueError('Static site exceeds extraction limits.')
                seen = set()
                for entry in entries:
                    name = entry.filename
                    path = PurePosixPath(name)
                    mode = entry.external_attr >> 16
                    if (not name or len(name) > 220 or '\\' in name or ':' in name or path.is_absolute()
                            or '..' in path.parts or any(part.startswith('.') for part in path.parts)
                            or stat.S_ISLNK(mode) or name.lower() in seen):
                        raise ValueError('Unsafe or duplicate website path.')
                    seen.add(name.lower())
                    if path.parts[0] not in {'assets', 'features', 'guide', 'download', 'index.html', '404.html', 'robots.txt', 'sitemap.xml'}:
                        raise ValueError('Unexpected website content.')
                    if entry.flag_bits & 1:
                        raise ValueError('Encrypted ZIP entries are not supported.')
                if not REQUIRED.issubset(seen):
                    raise ValueError('The website is missing required pages or assets.')
                release.mkdir(mode=0o755)
                for entry in entries:
                    target = release / entry.filename
                    if entry.is_dir():
                        target.mkdir(parents=True, exist_ok=True)
                        continue
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(entry) as source, target.open('xb') as output:
                        shutil.copyfileobj(source, output, 65536)
                    target.chmod(0o644)
            current = ROOT / 'current'
            previous = str(current.resolve()) if current.exists() else None
            link = ROOT / ('current-' + uuid.uuid4().hex)
            link.symlink_to(release)
            os.replace(link, current)
            print(json.dumps({'status': 'published', 'url': 'https://101.133.134.128/',
                              'release': release.name, 'previous': previous, 'sha256': digest.hexdigest()}))
        except Exception:
            if release.exists() and (not (ROOT / 'current').exists() or (ROOT / 'current').resolve() != release):
                shutil.rmtree(release)
            raise
        finally:
            shutil.rmtree(temporary)


if __name__ == '__main__':
    try:
        publish()
    except Exception as error:
        print(json.dumps({'status': 'error', 'message': str(error)}), file=sys.stderr)
        sys.exit(1)
