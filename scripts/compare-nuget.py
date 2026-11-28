"""Compare the attested package payload with the signed NuGet.org payload."""

import hashlib
import sys
from zipfile import ZipFile


def payload(path, *, signed):
    with ZipFile(path) as package:
        files = [entry for entry in package.infolist() if not entry.is_dir()]
        names = {entry.filename for entry in files}
        if len(names) != len(files):
            raise SystemExit(f"{path}: duplicate ZIP entry")
        signature = ".signature.p7s"
        if (signature in names) != signed:
            raise SystemExit(f"{path}: expected {'a signed' if signed else 'an unsigned'} package")
        hashes = {}
        for name in sorted(names - {signature}):
            with package.open(name) as stream:
                hashes[name] = hashlib.file_digest(stream, "sha256").hexdigest()
        return hashes


original = payload(sys.argv[1], signed=False)
published = payload(sys.argv[2], signed=True)
changed = sorted(
    name for name in original.keys() | published.keys()
    if original.get(name) != published.get(name)
)
if changed:
    raise SystemExit("NuGet package payload differs:\n" + "\n".join(changed))
print(f"All {len(original)} published files match the attested package.")
