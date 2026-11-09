#!/usr/bin/env python3
"""Restricted SSH endpoint: accept one signed Windows release, never a shell."""
import base64
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit

ROOT = Path('/srv/tuci-updates')
PUBLIC = ROOT / 'public'
PUBLIC_KEY = ROOT / 'release-public.pem'
MAX_PACKAGE = 512 * 1024 * 1024
VERSION = re.compile(r'^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$')
ORIGIN = 'https://101.133.134.128'


def read_exact(stream, length):
    chunks = []
    while length:
        chunk = stream.read(min(length, 65536))
        if not chunk:
            raise ValueError('Upload ended before the declared length.')
        chunks.append(chunk)
        length -= len(chunk)
    return b''.join(chunks)


def parse_manifest(raw, staging):
    envelope = json.loads(raw)
    payload_raw = base64.b64decode(envelope['payload'], validate=True)
    signature = base64.b64decode(envelope['signature'], validate=True)
    if len(payload_raw) > 49152 or len(signature) != 384:
        raise ValueError('Invalid manifest size.')
    (staging / 'payload.bin').write_bytes(payload_raw)
    (staging / 'signature.bin').write_bytes(signature)
    result = subprocess.run([
        '/usr/bin/openssl', 'dgst', '-sha256', '-verify', str(PUBLIC_KEY),
        '-signature', str(staging / 'signature.bin'), '-sigopt', 'rsa_padding_mode:pss',
        '-sigopt', 'rsa_pss_saltlen:digest', str(staging / 'payload.bin')],
        capture_output=True, check=False, timeout=10)
    if result.returncode:
        raise ValueError('Release signature is invalid.')
    release = json.loads(payload_raw)
    version = release.get('version', '')
    if not VERSION.fullmatch(version) or release.get('platform') != 'win-x64':
        raise ValueError('Invalid release version or platform.')
    expected_url = f'{ORIGIN}/tuci-updates/packages/tuci-win-x64-{version}.zip'
    if release.get('packageUrl') != expected_url or urlsplit(expected_url).username:
        raise ValueError('Unexpected package location.')
    size = release.get('sizeBytes')
    if not isinstance(size, int) or isinstance(size, bool) or not 1 <= size <= MAX_PACKAGE:
        raise ValueError('Invalid package size.')
    if not re.fullmatch(r'[0-9a-fA-F]{64}', release.get('sha256', '')):
        raise ValueError('Invalid package digest.')
    notes = release.get('releaseNotes')
    if not isinstance(notes, list) or len(notes) > 40 or any(not isinstance(note, str) or len(note) > 2000 for note in notes):
        raise ValueError('Invalid release notes.')
    datetime.datetime.fromisoformat(release['publishedAt'].replace('Z', '+00:00'))
    return release


def main():
    command = os.environ.get('SSH_ORIGINAL_COMMAND', '')
    if command == 'status-v1':
        manifest = PUBLIC / 'stable.json'
        version = None
        if manifest.is_file():
            envelope = json.loads(manifest.read_bytes())
            version = json.loads(base64.b64decode(envelope['payload']))['version']
        print(json.dumps({'status': 'ready', 'version': version, 'manifestUrl': ORIGIN + '/tuci-updates/stable.json'}))
        return
    if command != 'publish-v1':
        raise ValueError('This SSH key can only publish or inspect Tuci releases.')
    lock_file = (ROOT / 'publish.lock').open('a')
    fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX)
    header = sys.stdin.buffer.readline(513)
    if not header.endswith(b'\n') or len(header) > 512:
        raise ValueError('Invalid upload header.')
    lengths = json.loads(header)
    manifest_length, package_length = lengths['manifestLength'], lengths['packageLength']
    if type(manifest_length) is not int or not 1 <= manifest_length <= 65536:
        raise ValueError('Invalid manifest length.')
    if type(package_length) is not int or not 1 <= package_length <= MAX_PACKAGE:
        raise ValueError('Invalid package length.')
    incoming = ROOT / 'incoming'
    staging = Path(tempfile.mkdtemp(prefix='publish-', dir=incoming))
    try:
        manifest_raw = read_exact(sys.stdin.buffer, manifest_length)
        release = parse_manifest(manifest_raw, staging)
        if release['sizeBytes'] != package_length:
            raise ValueError('Signed and uploaded package lengths differ.')
        digest = hashlib.sha256()
        remaining = package_length
        package_temp = staging / 'package.zip'
        with package_temp.open('xb') as target:
            while remaining:
                chunk = sys.stdin.buffer.read(min(remaining, 1024 * 1024))
                if not chunk:
                    raise ValueError('Package upload was interrupted.')
                target.write(chunk)
                digest.update(chunk)
                remaining -= len(chunk)
            target.flush()
            os.fsync(target.fileno())
        if digest.hexdigest().lower() != release['sha256'].lower():
            raise ValueError('Package digest differs from the signed manifest.')
        current_manifest = PUBLIC / 'stable.json'
        if current_manifest.exists():
            current = json.loads(base64.b64decode(json.loads(current_manifest.read_bytes())['payload']))
            if tuple(map(int, release['version'].split('.'))) < tuple(map(int, current['version'].split('.'))):
                raise ValueError('Publishing an older version is not allowed.')
        package = PUBLIC / 'packages' / ('tuci-win-x64-' + release['version'] + '.zip')
        if package.exists():
            with package.open('rb') as source:
                previous_hash = hashlib.file_digest(source, 'sha256').hexdigest()
            if previous_hash.lower() != release['sha256'].lower():
                raise ValueError('A different package already exists for this version; increase the version.')
        else:
            package_temp.chmod(0o644)
            os.replace(package_temp, package)
        history = PUBLIC / 'manifests' / (release['version'] + '.json')
        manifest_temp = staging / 'manifest.json'
        manifest_temp.write_bytes(manifest_raw)
        manifest_temp.chmod(0o644)
        shutil.copyfile(manifest_temp, history)
        history.chmod(0o644)
        # The feed is always switched last, after its immutable ZIP is complete.
        os.replace(manifest_temp, current_manifest)
        print(json.dumps({'status': 'published', 'version': release['version'], 'bytes': package_length,
                          'sha256': digest.hexdigest(), 'manifestUrl': ORIGIN + '/tuci-updates/stable.json'}))
    finally:
        shutil.rmtree(staging)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(json.dumps({'status': 'error', 'message': str(error)}), file=sys.stderr)
        sys.exit(1)
