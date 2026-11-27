import hashlib
from pathlib import Path
import tempfile
import unittest

from reports import differential, rmut_records, stryker_records
from run import GATES, summarize


class DifferentialTests(unittest.TestCase):
    def test_repeated_operators_keep_distinct_utf16_offsets(self):
        source = '/* 🧪 */ a + b + c'
        path = 'Subject/Expression.cs'
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / path).parent.mkdir()
            (root / path).write_text(source, encoding="utf-8")
            stryker = {"files": {str(root / path): {
                "source": source,
                "mutants": [
                    {"location": {"start": {"line": 1, "column": 10},
                                  "end": {"line": 1, "column": 15}},
                     "replacement": "a - b", "status": "Killed"},
                    {"location": {"start": {"line": 1, "column": 10},
                                  "end": {"line": 1, "column": 19}},
                     "replacement": "a + b - c", "status": "Survived"},
                ],
            }}}
            rmut = {"baseline": {"outcome": "passed"}, "mutants": [
                {"path": path, "line": 1, "original": "+", "replacement": "-",
                 "id": hashlib.sha256(f"{offset}:+:-:{path}".encode()).hexdigest(),
                 "outcome": outcome}
                for offset, outcome in [(11, "killed"), (15, "survived")]
            ]}
            reference = stryker_records(stryker, root, "Subject/Subject.csproj")
            candidate = rmut_records(rmut, root)
        self.assertEqual(len(reference), 2)
        self.assertFalse(any(differential(reference, candidate).values()))

    def test_outcome_mismatch_and_missing_mutant_fail_the_gate(self):
        shared = ("Calculator.cs", 12, ">", ">=")
        extra = ("Calculator.cs", 12, ">", "<")
        reference = {shared: {"outcome": "noCoverage"}, extra: {"outcome": "noCoverage"}}
        candidate = {shared: {"outcome": "survived"}}
        result = differential(reference, candidate)
        self.assertEqual(result["stryker_only"], [list(extra)])
        self.assertEqual(result["outcome_mismatches"][0]["identity"], list(shared))

    def test_ignored_file_is_reported_even_without_mutants(self):
        report = {"files": {"Calculator.cs": {"language": "none", "mutants": []}}}
        records = stryker_records(report, Path("/unused"), "Project/Project.csproj")
        self.assertEqual(records[("Project/Calculator.cs", -1, "", "")]["outcome"], "ignored")


class PerformanceTests(unittest.TestCase):
    def test_variability_crossing_a_gate_is_inconclusive(self):
        reference = dict.fromkeys(GATES, 100)
        pairs = [{"stryker": reference, "rmut": dict.fromkeys(GATES, value)}
                 for value in (40, 42, 45, 49, 53)]
        summary = summarize(pairs)
        self.assertEqual(summary["elapsed_seconds"]["verdict"], "inconclusive")
        self.assertEqual(summary["elapsed_seconds"]["ratio_of_medians"], 0.45)
        self.assertEqual(summary["cpu_seconds"]["verdict"], "pass")


if __name__ == "__main__":
    unittest.main()
