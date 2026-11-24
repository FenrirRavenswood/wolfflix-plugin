"""Ajoute une version à manifest.json (dépôt d'extensions Jellyfin)."""
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

version, url, md5 = sys.argv[1:4]
chemin = Path("manifest.json")
manifeste = json.loads(chemin.read_text("utf-8"))
ext = manifeste[0]
journal = Path("changelog.txt").read_text("utf-8").strip() if Path("changelog.txt").exists() else ""
ext["versions"] = [v for v in ext["versions"] if v["version"] != version]
ext["versions"].insert(0, {
    "version": version,
    "changelog": journal,
    "targetAbi": "12.0.0.0",
    "sourceUrl": url,
    "checksum": md5,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})
chemin.write_text(json.dumps(manifeste, ensure_ascii=False, indent=2) + "\n", "utf-8")
