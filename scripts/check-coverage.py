"""Check production line coverage from the two Coverlet Cobertura reports."""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def coverage(report_root, suite, assembly):
    reports = list((report_root / suite).glob("*/coverage.cobertura.xml"))
    if len(reports) != 1:
        raise SystemExit(f"Expected one {suite} coverage report, found {len(reports)}")

    lines = {}
    for package in ET.parse(reports[0]).findall("./packages/package"):
        if package.get("name") != assembly:
            continue
        for cls in package.findall("./classes/class"):
            for line in cls.findall("./lines/line"):
                key = (cls.attrib["filename"], line.attrib["number"])
                lines[key] = lines.get(key, False) or int(line.attrib["hits"]) > 0
    if not lines:
        raise SystemExit(f"No instrumented lines found for {assembly}")
    return sum(lines.values()), len(lines)


def main(report_root):
    failed = False
    for suite, assembly, floor in (("engine", "Rmut.Engine", 95), ("cli", "Rmut.Cli", 55)):
        covered, total = coverage(report_root, suite, assembly)
        passed = 100 * covered >= floor * total
        print(f"{assembly}: {covered}/{total} lines ({covered / total:.1%}), "
              f"minimum {floor}% — {'pass' if passed else 'fail'}")
        failed |= not passed
    return int(failed)


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))
