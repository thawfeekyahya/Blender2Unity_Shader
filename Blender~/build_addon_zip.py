"""Packs the Blender add-on into a zip installable via Blender's Preferences > Add-ons > Install from Disk.

    python build_addon_zip.py [output_dir]
"""
import os
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = "b2u_unity_exporter"


def build(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, PKG + ".zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, dirs, files in os.walk(os.path.join(HERE, PKG)):
            dirs[:] = [d for d in dirs if d != "__pycache__"]
            for f in files:
                if f.endswith(".pyc"):
                    continue
                full = os.path.join(root, f)
                zf.write(full, os.path.relpath(full, HERE))
    return out


if __name__ == "__main__":
    print(build(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "dist")))
