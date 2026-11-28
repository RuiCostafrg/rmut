import json
import sys
from pathlib import Path
from xml.etree import ElementTree as ET


def only(paths):
    path, = paths
    return path


def verify_trx(directory):
    run = ET.parse(only(directory.glob("*.trx"))).getroot()
    counters = run.find("./{*}ResultSummary/{*}Counters")
    for counter in ("total", "executed", "passed"):
        assert counters.attrib[counter] == "2", counters.attrib
    expected = {"AddsTwoNumbers", "RecognizesPositiveNumbers"}
    for section, item, attribute in (
        ("TestDefinitions", "UnitTest", "name"),
        ("Results", "UnitTestResult", "testName"),
    ):
        entries = run.findall(f"./{{*}}{section}/{{*}}{item}")
        assert sorted(entry.attrib[attribute].rsplit(".", 1)[-1] for entry in entries) == sorted(expected)
    assert all(entry.attrib["outcome"] == "Passed"
               for entry in run.findall("./{*}Results/{*}UnitTestResult"))


def verify_mutants(report_path):
    report = json.loads(report_path.read_text())
    assert report["baseline"]["outcome"] == "passed", report
    assert report.get("error") is None, report
    assert sorted(
        (mutant["original"], mutant["replacement"], mutant["outcome"])
        for mutant in report["mutants"]
    ) == [("+", "-", "killed"), (">", ">=", "survived")], report


def verify_coverage(directory):
    # VSTest can also copy the collector report into the TRX attachment directory.
    reports = {path.read_bytes() for path in directory.rglob("coverage.cobertura.xml")}
    report = ET.fromstring(only(reports))
    production_class = only(entry for entry in report.findall(".//class")
                            if entry.attrib["name"] == "FrameworkMatrix.Calculator")
    assert Path(production_class.attrib["filename"]).name == "Calculator.cs"
    lines = {int(line.attrib["number"]): int(line.attrib["hits"])
             for line in production_class.findall("./lines/line")}
    assert lines[5] > 0 and lines[7] > 0 and lines[9] == 0, lines


def main():
    directory, framework, target, sdk = sys.argv[1:]
    artifacts = Path(directory)
    for mode in ("off", "on"):
        verify_trx(artifacts / mode)
        verify_mutants(artifacts / f"{mode}.json")
    assert not list((artifacts / "off").rglob("coverage.cobertura.xml"))
    verify_coverage(artifacts / "on")

    print(f"{framework} {target}, SDK {sdk}: VSTest project baseline and switched VSTest assembly.")
    print("Both modes executed the two expected tests and yielded one killed and one surviving mutant.")
    print("Coverlet on: Calculator.cs lines 5 and 7 covered, line 9 uncovered. Coverlet off: no report.")


if __name__ == "__main__":
    main()
