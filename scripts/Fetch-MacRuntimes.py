#!/usr/bin/env python3
"""Fetch pinned official Mac npm archives for the existing native package builder."""
import argparse
import base64
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import tarfile
import urllib.request


def extract(archive_path, destination, spec):
    with archive_path.open('rb') as source:
        digest = hashlib.sha512()
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    if base64.b64encode(digest.digest()).decode() != spec['archiveSha512']:
        raise ValueError('Official runtime archive SHA-512 mismatch')
    destination.mkdir(parents=True, exist_ok=False)
    prefix = spec['archivePrefix']
    with tarfile.open(archive_path, 'r:gz') as archive:
        for entry in archive:
            if not entry.name.startswith(prefix) or entry.isdir():
                continue
            relative = entry.name[len(prefix):]
            parts = PurePosixPath(relative)
            if (not entry.isfile() or not relative or parts.is_absolute()
                    or '..' in parts.parts or '\\' in relative or ':' in relative):
                raise ValueError('Unsafe runtime archive entry: ' + entry.name)
            target = destination.joinpath(*parts.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(entry) as source, target.open('xb') as output:
                shutil.copyfileobj(source, output)
            target.chmod(0o755 if entry.mode & 0o111 else 0o644)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    lock = json.loads((Path(__file__).resolve().parent.parent / 'packaging/mac/runtime-lock.json').read_text())
    for name in ('opencode', 'codex'):
        spec = lock['runtimes'][name]
        archive = args.output / (name + '.tgz')
        with urllib.request.urlopen(spec['source'], timeout=120) as source, archive.open('xb') as output:
            shutil.copyfileobj(source, output)
        extract(archive, args.output / name, spec)
        print(name + ': pinned archive verified', flush=True)


if __name__ == '__main__':
    main()
