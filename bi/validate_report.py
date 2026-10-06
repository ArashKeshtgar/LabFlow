"""Validates every generated PBIP JSON file against the schema named in its own $schema.

    python bi/validate_report.py     (needs: pip install jsonschema)

Schemas are downloaded once into bi/.schemas/ (git-ignored). Exit code 1 on any error.
"""
import json
import sys
import urllib.request
from pathlib import Path

from jsonschema import Draft7Validator

HERE = Path(__file__).resolve().parent
CACHE = HERE / ".schemas"


def schema_for(url: str) -> dict:
    CACHE.mkdir(exist_ok=True)
    f = CACHE / (url.split("/json-schemas/")[-1].replace("/", "_"))
    if not f.exists():
        with urllib.request.urlopen(url, timeout=30) as r:
            f.write_bytes(r.read())
    return json.loads(f.read_text(encoding="utf-8"))


def main() -> int:
    errors = checked = 0
    for path in sorted(HERE.rglob("*.json")) + sorted(HERE.rglob("*.pbir")) + sorted(HERE.rglob("*.pbism")) + [HERE / "LabFlow.pbip"]:
        if ".schemas" in path.parts or ".pbi" in path.parts:
            continue
        doc = json.loads(path.read_text(encoding="utf-8"))
        url = doc.get("$schema") if isinstance(doc, dict) else None
        if not url:
            continue
        checked += 1
        for e in Draft7Validator(schema_for(url)).iter_errors(doc):
            errors += 1
            where = "/".join(map(str, e.absolute_path)) or "(root)"
            print(f"{path.relative_to(HERE)} :: {where} :: {e.message[:300]}")
    print(f"{checked} files checked, {errors} errors")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
