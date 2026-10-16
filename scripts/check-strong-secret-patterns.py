import json
from pathlib import Path
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
paths = subprocess.check_output(['git', '-C', str(root), 'ls-files', '-z'], text=True).split('\0')
patterns = {
    'github-token': re.compile(r'gh[pousr]_[A-Za-z0-9]{20,}'),
    'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
}
findings = []
for name in paths:
    if not name:
        continue
    path = root / name
    if not path.is_file() or path.stat().st_size > 2_000_000:
        continue
    try:
        text = path.read_text(encoding='utf-8-sig')
    except (UnicodeError, OSError):
        continue
    for number, line in enumerate(text.splitlines(), 1):
        for kind, pattern in patterns.items():
            if pattern.search(line):
                findings.append({'path': name, 'line': number, 'kind': kind})
print(json.dumps({'tracked_files': len([p for p in paths if p]), 'strong_secret_pattern_findings': findings}, indent=2))
sys.exit(1 if findings else 0)
