"""Compare decoded scientific worksheet cells, independently of ZIP/XML encoding.

Usage: python compare_workbooks.py baseline_debug updated_debug output.json
Only the three numerical stage directories are compared. Library build metadata
and ZIP container bytes are intentionally outside the numerical equality check.
"""
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile

NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"


def cells(path):
    digest, count = hashlib.sha256(), 0
    with ZipFile(path) as archive:
        for name in sorted(n for n in archive.namelist() if n.startswith("xl/worksheets/sheet") and n.endswith(".xml")):
            digest.update(name.encode())
            with archive.open(name) as stream:
                for _, row in ET.iterparse(stream, events=("end",)):
                    if row.tag != NS + "row":
                        continue
                    for cell in row.findall(NS + "c"):
                        value = [cell.get("r"), cell.get("t"), cell.get("s"), "".join(cell.itertext())]
                        digest.update(json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
                        count += 1
                    row.clear()
    return {"Cells": count, "Sha256": digest.hexdigest()}


def main():
    before, after, output = map(Path, sys.argv[1:])
    stages = ("04-solar-positions", "05-transposition", "06-shading")
    def files(root):
        return {p.relative_to(root) for stage in stages for p in (root / stage).glob("*.xlsx")}
    assert files(before) == files(after), "Numerical workbook sets differ"
    comparisons = []
    for relative in sorted(files(before)):
        old, new = cells(before / relative), cells(after / relative)
        comparisons.append({"File": str(relative), "Before": old, "After": new, "Equal": old == new})
    output.write_text(json.dumps(comparisons, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"Workbooks": len(comparisons), "Cells": sum(c["After"]["Cells"] for c in comparisons), "AllEqual": all(c["Equal"] for c in comparisons)}))
    return 0 if all(c["Equal"] for c in comparisons) else 1


if __name__ == "__main__":
    sys.exit(main())
