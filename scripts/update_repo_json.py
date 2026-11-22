"""Writes repo.json - the list Dalamud reads when Yassify's repo is added under Custom Plugin Repositories.

Usage: python scripts/update_repo_json.py <built Yassify.json> <github owner/repo>
e.g.   python scripts/update_repo_json.py Yassify/bin/Release/Yassify/Yassify.json someone/Yassify

The built manifest (made by `dotnet build`) has the version; the download links point at the newest GitHub release,
so a friend's Dalamud updates the plugin as soon as repo.json shows a higher AssemblyVersion. The version's section of
CHANGELOG.md goes in as "Changelog".
"""
import json
import sys
import time
from pathlib import Path

from changelog import section

built, slug = Path(sys.argv[1]), sys.argv[2]
m = json.loads(built.read_text(encoding="utf-8-sig"))
base = f"https://github.com/{slug}"
zip_url = f"{base}/releases/latest/download/latest.zip"
entry = {
    "Author": m["Author"],
    "Name": m["Name"],
    "InternalName": m["InternalName"],
    "AssemblyVersion": m["AssemblyVersion"],
    "Description": m["Description"],
    "ApplicableVersion": m.get("ApplicableVersion", "any"),
    "RepoUrl": base,
    "Tags": m.get("Tags", []),
    "DalamudApiLevel": m["DalamudApiLevel"],
    "LoadPriority": m.get("LoadPriority", 0),
    "IconUrl": f"https://raw.githubusercontent.com/{slug}/main/images/icon.png",
    "Punchline": m.get("Punchline", ""),
    "AcceptsFeedback": False,
    "IsHide": False,
    "IsTestingExclusive": False,
    "DownloadLinkInstall": zip_url,
    "DownloadLinkUpdate": zip_url,
    "DownloadLinkTesting": zip_url,
    "LastUpdate": int(time.time()),
}
notes = section(m["AssemblyVersion"])      # this version's part of CHANGELOG.md - Dalamud shows it in the plugin installer
if notes:
    entry["Changelog"] = notes
out = Path(__file__).resolve().parent.parent / "repo.json"
out.write_text(json.dumps([entry], indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"repo.json: {entry['Name']} {entry['AssemblyVersion']} -> {zip_url}")
