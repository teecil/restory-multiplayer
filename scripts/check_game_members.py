"""Every game member the mod finds by name (a string the compiler cannot check), looked up in a decompiled game
version. Usage: python -I local/scripts/check_game_members.py <decomp dir>   e.g. local/decomp/restory-1.0.033r
Prints each missing one and exits 1 if any is missing."""
import os
import re
import sys

LOCAL = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLUGIN = os.path.join(LOCAL, "mod", "RestoryVersus.Plugin")
decomp = sys.argv[1]

# typeof(T) ... "name" in AccessTools / HarmonyPatch calls, and FieldRefAccess<T, X>("name").
patterns = [
    re.compile(r'(?:AccessTools\.(?:Method|Field|Property|PropertySetter|PropertyGetter|Constructor|FirstConstructor)|HarmonyPatch)\(\s*typeof\(([\w.]+)\)\s*,\s*"(\w+)"'),
    re.compile(r'FieldRefAccess<\s*([\w.]+)\s*,[^>]*>\(\s*"(\w+)"\s*\)'),
]

# Index the decompiled sources: class name -> file text (all files that declare it).
classes = {}
for root, _, files in os.walk(decomp):
    for f in files:
        if not f.endswith(".cs"):
            continue
        text = open(os.path.join(root, f), encoding="utf-8", errors="replace").read()
        for m in re.finditer(r'\b(?:class|struct|interface)\s+(\w+)', text):
            classes.setdefault(m.group(1), []).append(text)

# Looked up with a fallback in the mod (gone in some game versions on purpose).
OPTIONAL = {"CompetitionGameMode.deviceShadow"}

missing, checked = [], 0
for f in sorted(os.listdir(PLUGIN)):
    if not f.endswith(".cs"):
        continue
    src = open(os.path.join(PLUGIN, f), encoding="utf-8").read()
    for pat in patterns:
        for m in pat.finditer(src):
            type_name = m.group(1).split(".")[-1]
            member = m.group(2)
            texts = classes.get(type_name)
            if texts is None:
                continue  # not a game type (a mod or Unity type)
            checked += 1
            if not any(re.search(r'\b' + re.escape(member) + r'\b', t) for t in texts):
                if f"{type_name}.{member}" in OPTIONAL:
                    print(f"  optional, not in this version: {type_name}.{member}")
                else:
                    missing.append(f"{f}: {type_name}.{member}")

print(f"{checked} game members found by name checked against {decomp}")
for m in missing:
    print("  MISSING " + m)
sys.exit(1 if missing else 0)
