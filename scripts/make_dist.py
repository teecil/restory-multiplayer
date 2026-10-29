"""Builds the mod-only package for players: BepInEx 5.4.23.5 + RestoryVersus.dll + README.
No game files and no saves (the versus workshop is made from the player's own save). Usage:
python -I local/scripts/make_dist.py
Prints the file list, refuses if anything that looks like a game file or a save slipped in, and prints the SHA-256."""
import hashlib
import os
import re
import sys
import zipfile

LOCAL = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLUGIN_SRC = os.path.join(LOCAL, "mod", "RestoryVersus.Plugin")


def plugin_version():
    with open(os.path.join(PLUGIN_SRC, "Plugin.cs"), encoding="utf-8") as fh:
        m = re.search(r'public const string Version = "([^"]+)"', fh.read())
    return m.group(1)


def main():
    version = plugin_version()
    bep = os.path.join(LOCAL, "downloads", "bepinex-5.4.23.5-extracted")
    dll = os.path.join(PLUGIN_SRC, "bin", "Release", "net472", "RestoryVersus.dll")
    out = os.path.join(LOCAL, "mod", "dist", f"RestoryVersus-{version}.zip")

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(bep):
            for f in files:
                full = os.path.join(root, f)
                z.write(full, os.path.relpath(full, bep).replace(os.sep, "/"))
        z.write(dll, "BepInEx/plugins/RestoryVersus/RestoryVersus.dll")
        z.write(os.path.join(LOCAL, "mod", "dist", "README_RU.txt"), "README_RU.txt")

    with zipfile.ZipFile(out) as z:
        names = z.namelist()
    suspicious = [n for n in names if re.search(r"(?i)(Restory_Data|\.assets$|\.resS$|\.bank$|steam_api|steam_emu|Restory\.exe|UnityPlayer|\.restory$)", n)]
    print(f"{out}\n{len(names)} files")
    for n in names:
        print("  " + n)
    if suspicious:
        os.remove(out)
        sys.exit("refused: game files or saves in the package: " + ", ".join(suspicious))
    with open(out, "rb") as fh:
        print("sha256", hashlib.sha256(fh.read()).hexdigest())


if __name__ == "__main__":
    main()
