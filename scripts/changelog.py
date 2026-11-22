"""One version's section of CHANGELOG.md, as plain text.

Usage: python scripts/changelog.py 0.1.1        (prints it - the release workflow uses this for the release notes)

update_repo_json.py puts the same text in repo.json's "Changelog", which Dalamud shows in the plugin installer.
"""
import re
import sys
from pathlib import Path

FILE = Path(__file__).resolve().parent.parent / "CHANGELOG.md"


def section(version: str) -> str:
    """The text under "## <version>" up to the next "## " heading ("" if there is none). A 4-part version
    (0.1.1.0) matches its 3-part heading (0.1.1)."""
    if not FILE.exists():
        return ""
    want = {version, re.sub(r"\.0$", "", version)} if version.count(".") == 3 else {version}
    out, on = [], False
    for line in FILE.read_text(encoding="utf-8").splitlines():
        if line.startswith("## "):
            if on:
                break
            on = line[3:].strip().split(" ")[0] in want
            continue
        if on:
            out.append(line)
    return "\n".join(out).strip()


if __name__ == "__main__":
    text = section(sys.argv[1])
    if not text:
        sys.exit(f"CHANGELOG.md has no section for {sys.argv[1]}")
    print(text)
