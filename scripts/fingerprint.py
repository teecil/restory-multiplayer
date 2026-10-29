# Usage: python -I fingerprint.py <dir> <out.json>  -- path -> [size, mtime_ns, sha256] for every file
import hashlib, json, os, sys
root, out = sys.argv[1], sys.argv[2]
result = {}
for d, _, files in os.walk(root):
    for f in files:
        p = os.path.join(d, f)
        st = os.stat(p)
        h = hashlib.sha256()
        with open(p, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                h.update(chunk)
        result[os.path.relpath(p, root)] = [st.st_size, st.st_mtime_ns, h.hexdigest()]
with open(out, "w") as fh:
    json.dump(result, fh, indent=1, sort_keys=True)
print(len(result), "files")
