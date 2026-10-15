"""Publish immutable launcher files from the same ZIPs offered on the website.

python publish.py --beta path.zip --industrial path.zip --industrial-version 0.3.8-r33
The catalog is replaced last, after both manifests and every object are ready.
"""
import argparse
import hashlib
import json
import os
import subprocess
import shutil
import re
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

BASE = Path.cwd()
OUTPUT = BASE / 'launcher'
SERVER = 'friendzone-order.duckdns.org'


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix('.tmp')
    tmp.write_text(json.dumps(value, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    os.replace(tmp, path)


def policy(path):
    """Never publish player data; settings shipped in ZIP are seed-only."""
    parts = path.lower().split('/')
    name = parts[-1]
    if any(p in ('saves', 'screenshots', 'logs', 'crash-reports', 'stats', 'texturepacks', 'resourcepacks') for p in parts[:-1]):
        return None
    if name in ('nickname.txt', 'lastlogin', 'session.lock') or name.endswith(('.log', '.log.lck', '.old', '.broken', '.bak')):
        return None
    if name.startswith('forgemodloader-client-') or name.startswith('client-launch'):
        return None
    if name in ('options.txt', 'optionsof.txt', 'servers.dat', 'servers.dat_old',
                'invtweaks.cfg', 'invtweaksrules.txt', 'invtweakstree.txt', 'nei.cfg', 'neisubset.cfg'):
        return 'seed'
    return 'managed'


def valid_path(path):
    p = PurePosixPath(path)
    return bool(path) and not p.is_absolute() and '\\' not in path and ':' not in path and all(
        s not in ('', '.', '..') and not s.endswith((' ', '.')) and not s.lower().startswith('.fzo-')
        and not re.match(r'^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)', s, re.I)
        for s in path.split('/'))


def publish_client(archive, client_id, version):
    objects = OUTPUT / 'objects'
    objects.mkdir(parents=True, exist_ok=True)
    files, seen = [], set()
    with zipfile.ZipFile(archive) as zip_file:
        for info in zip_file.infolist():
            if info.is_dir():
                continue
            _, separator, path = info.filename.partition('/')
            if not separator or not valid_path(path):
                raise ValueError('Unsafe archive path: ' + info.filename)
            mode = policy(path)
            if mode is None:
                continue
            if path.lower() in seen:
                raise ValueError('Duplicate file: ' + path)
            seen.add(path.lower())
            digest = hashlib.sha256()
            with zip_file.open(info) as source, tempfile.NamedTemporaryFile(dir=objects, delete=False) as target:
                temporary = Path(target.name)
                try:
                    while True:
                        chunk = source.read(1024 * 1024)
                        if not chunk:
                            break
                        digest.update(chunk)
                        target.write(chunk)
                except BaseException:
                    target.close()
                    temporary.unlink(missing_ok=True)
                    raise
            sha = digest.hexdigest()
            destination = objects / sha
            if destination.exists():
                # Verify existing blobs, including a previous interrupted publication.
                with destination.open('rb') as check:
                    existing = hashlib.file_digest(check, 'sha256').hexdigest()
                if existing != sha:
                    os.replace(temporary, destination)
                else:
                    temporary.unlink()
            else:
                os.replace(temporary, destination)
            files.append(dict(path=path, size=info.file_size, sha256=sha, mode=mode))
    required = {'beta': ['runtime-x64/bin/javaw.exe', 'game/minecraft.jar'],
                'industrial': ['java7/bin/javaw.exe', 'game/minecraft-forge.jar', 'game/minecraft.jar']}
    if not all(path in {f['path'] for f in files} for path in required[client_id]):
        raise ValueError('Missing launch files in ' + str(archive))
    manifest = dict(schema=1, id=client_id, version=version, files=files)
    encoded = json.dumps(manifest, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    manifest_sha = hashlib.sha256(encoded).hexdigest()
    manifest_path = OUTPUT / 'manifests' / (manifest_sha + '.json')
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_bytes(encoded)
    print(f'{client_id}: {len(files)} files, {sum(f["size"] for f in files) / 1048576:.1f} MB, {version}')
    return dict(id=client_id, name='Beta 1.7.3' if client_id == 'beta' else 'Industrial 1.4.7',
                version=version, manifest=manifest_sha, host=SERVER, port=25565 if client_id == 'beta' else 25566)


def main():
    global OUTPUT
    parser = argparse.ArgumentParser()
    parser.add_argument('--public-root', type=Path, required=True)
    parser.add_argument('--signing-key', type=Path, required=True, help='DPAPI-protected private key outside the website and repository')
    parser.add_argument('--powershell', default=shutil.which('pwsh') or shutil.which('powershell'), help='PowerShell executable used for the local signer')
    parser.add_argument('--launcher-exe', type=Path, help='Final reviewed (and signed, when available) launcher EXE')
    parser.add_argument('--launcher-version', help='Four-part assembly version, for example 1.2.0.0')
    parser.add_argument('--beta', type=Path, required=True)
    parser.add_argument('--industrial', type=Path, required=True)
    parser.add_argument('--beta-version', default='b1.7.3-current')
    parser.add_argument('--industrial-version', default='0.3.8-r33')
    args = parser.parse_args()
    OUTPUT = args.public_root.resolve() / 'launcher'
    servers = [publish_client(args.beta, 'beta', args.beta_version),
               publish_client(args.industrial, 'industrial', args.industrial_version)]
    catalog = dict(schema=1, servers=servers)
    if bool(args.launcher_exe) != bool(args.launcher_version):
        raise ValueError('Specify both --launcher-exe and --launcher-version')
    if args.launcher_exe:
        if not re.fullmatch(r'[1-9][0-9]*\.[0-9]+\.[0-9]+\.[0-9]+', args.launcher_version):
            raise ValueError('Launcher requires a four-part version')
        data = args.launcher_exe.read_bytes()
        if not 1024 <= len(data) <= 32 * 1024 * 1024:
            raise ValueError('Invalid launcher size')
        sha = hashlib.sha256(data).hexdigest()
        destination = OUTPUT / 'objects' / sha
        if destination.exists() and destination.read_bytes() != data:
            raise ValueError('Existing launcher object is corrupt')
        if not destination.exists():
            temporary_exe = destination.with_suffix('.pending')
            temporary_exe.write_bytes(data)
            os.replace(temporary_exe, destination)
        catalog['launcher'] = dict(version=args.launcher_version, sha256=sha, size=len(data))

    # Signing must succeed before publishing either live catalog.
    temporary = OUTPUT / 'catalog.pending.json'
    atomic_json(temporary, catalog)
    try:
        if not args.powershell:
            raise RuntimeError('PowerShell is required for the local signer.')
        preserve = []
        if not args.launcher_exe and (OUTPUT / 'catalog.signed.json').exists():
            preserve = ['-PreserveLauncherFrom', str(OUTPUT / 'catalog.signed.json')]
        subprocess.run([args.powershell, '-NoProfile', '-NonInteractive', '-File',
                        str(Path(__file__).with_name('sign-catalog.ps1')), '-Mode', 'Sign',
                        '-KeyFile', str(args.signing_key.resolve()), '-CatalogFile', str(temporary),
                        '-OutputFile', str(OUTPUT / 'catalog.signed.json')] + preserve, check=True)
        atomic_json(OUTPUT / 'catalog.json', catalog)
    finally:
        temporary.unlink(missing_ok=True)
    print('Published:', OUTPUT / 'catalog.json')


if __name__ == '__main__':
    main()
