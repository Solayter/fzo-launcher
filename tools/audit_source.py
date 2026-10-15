"""Conservative allowlist and secret-pattern audit of the publishable project.

No credential values are printed. This complements manual review; it does not
prove the absence of every possible secret or vulnerability.
"""
import hashlib
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ALLOWED = {
    '.gitignore', '.gitattributes', '.github/CODEOWNERS', '.github/workflows/build.yml',
    'README.md', 'LICENSE', 'NOTICE.md', 'SECURITY.md', 'PRIVACY.md',
    'CODE_SIGNING_POLICY.md', 'SIGNPATH_APPLICATION.md',
    'Launcher.cs', 'Updater.cs', 'LauncherUpdate.cs', 'SignedCatalog.cs', 'app.manifest',
    'FZO-Launcher.exe.config', 'build.ps1', 'test.ps1', 'publish.py',
    'sign-catalog.ps1', 'prepare-owl-assets.ps1',
    'assets/owl.png', 'assets/owl.ico', 'assets/owl-minecraft-master.png',
    'assets/update-public-key.xml', 'tests/UpdaterTests.cs',
    'tests/ReleaseSmoke.cs', 'tests/smoke.ps1', 'tools/audit_source.py',
}
PATTERNS = [
    ('private PEM key', re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----')),
    ('private RSA XML', re.compile(r'<(?:D|P|Q|DP|DQ|InverseQ)>')),
    ('GitHub credential', re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,})\b')),
    ('AWS access key', re.compile(r'\b(?:AKIA|ASIA)[A-Z0-9]{16}\b')),
    ('literal credential assignment', re.compile(r'''(?i)(?:password|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[:=]\s*["'][A-Za-z0-9_+/=-]{16,}["']''')),
    ('operator private path', re.compile(r'(?i)(?:C:[\\/]+Users[\\/]+admin|C:[\\/]+Server[\\/]+(?:Private|Apps[\\/]+ControlAPI))')),
]


def main():
    try:
        result = subprocess.run(['git', '-C', str(ROOT), 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], capture_output=True, check=True)
        paths = sorted(set(p for p in result.stdout.decode('utf-8').split('\0') if p))
    except (subprocess.CalledProcessError, FileNotFoundError):
        paths = sorted(p.relative_to(ROOT).as_posix() for p in ROOT.rglob('*')
                       if p.is_file() and not any(part in ('.git', 'dist', '__pycache__') for part in p.relative_to(ROOT).parts)
                       and not p.name.endswith('.exe'))
    problems = []
    inventory = []
    for relative in paths:
        path = ROOT / relative
        if relative not in ALLOWED:
            problems.append((relative, 'not in publication allowlist'))
            continue
        if path.is_symlink():
            problems.append((relative, 'symbolic link'))
            continue
        data = path.read_bytes()
        if len(data) > 5 * 1024 * 1024:
            problems.append((relative, 'unexpectedly large source asset'))
        if path.suffix not in ('.png', '.ico'):
            text = data.decode('utf-8-sig')
            # The scanner necessarily contains patterns resembling credentials.
            if relative != 'tools/audit_source.py':
                for label, pattern in PATTERNS:
                    if pattern.search(text):
                        problems.append((relative, label))
        inventory.append(dict(path=relative, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
    missing = ALLOWED - set(paths)
    for path in sorted(missing):
        problems.append((path, 'required file missing'))
    if problems:
        for path, reason in problems:
            print(f'FAIL: {path}: {reason}')
        raise SystemExit(1)
    print(json.dumps(dict(status='pass', files=len(inventory), inventory=inventory), indent=2))


if __name__ == '__main__':
    main()
