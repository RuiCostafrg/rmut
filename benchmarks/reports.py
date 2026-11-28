"""Normalize source edits, outcomes and exclusions without using tool-local IDs."""

import hashlib
import re
from pathlib import Path


STATUS = {
    "Killed": "killed", "Survived": "survived", "NoCoverage": "noCoverage",
    "Timeout": "timedOut", "CompileError": "buildFailed",
    "Ignored": "ignored", "Pending": "pending", "RuntimeError": "runnerError",
}


def utf16_length(text):
    return len(text.encode("utf-16-le")) // 2


def source_offset(source, position):
    lines = source.splitlines(keepends=True)
    line = position["line"] - 1
    column = position["column"] - 1
    if line == len(lines) and column == 0 and source.endswith(("\r", "\n")):
        return len(source)
    if not 0 <= line < len(lines) or column < 0:
        raise ValueError(f"Invalid source location {position}")
    encoded = lines[line].rstrip("\r\n").encode("utf-16-le")
    if column * 2 > len(encoded):
        raise ValueError(f"Invalid source column {position}")
    return sum(map(len, lines[:line])) + len(encoded[:column * 2].decode("utf-16-le"))


def canonical_edit(path, source, start, original, replacement):
    # Stryker can replace an entire expression for a one-token edit. Preserve
    # whole-token boundaries for such edits so adjacent identical tokens stay distinct.
    tokens = re.compile(r"\s+|\w+|(?:==|!=|>=|<=|&&|\|\||>>>|>>|<<)|[^\w\s]")
    if original == replacement:
        raise ValueError(f"Mutant reports no source edit in {path}")
    before = list(tokens.finditer(original))
    after = list(tokens.finditer(replacement))
    while before and after and before[-1][0] == after[-1][0]:
        before.pop()
        after.pop()
    prefix = 0
    while prefix < min(len(before), len(after)) and before[prefix][0] == after[prefix][0]:
        prefix += 1
    if prefix:
        start += before[prefix - 1].end()
    return (
        path, utf16_length(source[:start]),
        "".join(match[0] for match in before[prefix:]),
        "".join(match[0] for match in after[prefix:]),
    )


def unique(records, identity, outcome, reason):
    if identity in records:
        raise ValueError(f"Ambiguous normalized mutant {identity}")
    records[identity] = {"outcome": outcome, "reason": reason}


def relative_path(path, root, project):
    if Path(path).is_absolute():
        return Path(path).relative_to(root).as_posix()
    if (root / path).is_file():
        return Path(path).as_posix()
    return (Path(project).parent / path).as_posix()


BLOCK_FILTER_REASON = "Removed by block already covered filter"


def located_mutants(source, mutants):
    for mutant in mutants:
        start = source_offset(source, mutant["location"]["start"])
        end = source_offset(source, mutant["location"]["end"])
        if end < start:
            raise ValueError("Invalid Stryker source range")
        yield start, end, mutant


def stryker_block_filter_audit(report):
    """Replay containment using Stryker's own post-instrumentation eligibility."""
    audit = {"blocks": 0, "compile_errors": 0, "ignored": 0, "disagreements": []}
    for path, file in report["files"].items():
        rows = list(located_mutants(file.get("source", ""), file.get("mutants", [])))
        # A block removed by this filter was still eligible when the filter ran.
        eligible = [(start, end, mutant) for start, end, mutant in rows
                    if mutant["status"] not in {"Ignored", "CompileError"}
                    or mutant.get("statusReason") == BLOCK_FILTER_REASON]
        for start, end, mutant in rows:
            if mutant.get("mutatorName") != "Block removal mutation":
                continue
            audit["blocks"] += 1
            if mutant["status"] == "CompileError":
                audit["compile_errors"] += 1
                continue
            ignored = mutant.get("statusReason") == BLOCK_FILTER_REASON
            audit["ignored"] += ignored
            if mutant["status"] == "Ignored" and not ignored:
                continue
            predicted = any(start <= inner_start and inner_end <= end and inner is not mutant
                            for inner_start, inner_end, inner in eligible)
            if predicted != ignored:
                audit["disagreements"].append({"path": path, "location": mutant["location"],
                                               "reported_ignored": ignored})
    return audit


def stryker_records(report, root, project):
    records = {}
    for filename, file in report["files"].items():
        path = relative_path(filename, root, project)
        if file.get("language") == "none":
            unique(records, (path, -1, "", ""), "ignored", "Excluded file")
        source = file.get("source", "")
        mutants = file.get("mutants", [])
        if mutants and (root / path).read_bytes().decode("utf-8-sig") != source:
            raise ValueError(f"Stryker report source differs from the workspace: {path}")
        for start, end, mutant in located_mutants(source, mutants):
            identity = canonical_edit(path, source, start, source[start:end], mutant["replacement"])
            unique(records, identity, STATUS[mutant["status"]], mutant.get("statusReason"))
    return records


def rmut_records(report, root):
    if report["baseline"]["outcome"] != "passed" or report.get("error"):
        raise ValueError("Rmut did not complete a passing baseline and mutation run")
    records = {}
    sources = {}
    for mutant in report["mutants"]:
        path = mutant["path"]
        if path not in sources:
            sources[path] = (root / path).read_bytes().decode("utf-8-sig")
        source = sources[path]
        original, replacement = mutant["original"], mutant["replacement"]
        if "offset" in mutant:
            offset = mutant["offset"]
            if not 0 <= offset <= utf16_length(source):
                raise ValueError(f"Invalid Rmut source offset in {path}: {offset}")
            start = len(source.encode("utf-16-le")[:2 * offset].decode("utf-16-le"))
            candidates = [start] if source.startswith(original, start) else []
        else:
            # The managed report exposes a line and SHA-256 ID. Resolve the ID
            # against the frozen source, including repeated operators on one line.
            line = mutant["line"] - 1
            lines = source.splitlines(keepends=True)
            start = sum(map(len, lines[:line]))
            candidates = []
            for match in re.finditer(f"(?={re.escape(original)})", lines[line]):
                position = start + match.start()
                value = f"{utf16_length(source[:position])}:{original}:{replacement}:{path}"
                if hashlib.sha256(value.encode()).hexdigest().lower() == mutant["id"].lower():
                    candidates.append(position)
        if len(candidates) != 1:
            raise ValueError(f"Cannot locate Rmut mutant {mutant['id']} in {path}")
        identity = canonical_edit(path, source, candidates[0], original, replacement)
        unique(records, identity, mutant["outcome"], mutant.get("details"))
    return records


def differential(reference, candidate):
    return {
        "stryker_only": [list(key) for key in sorted(reference.keys() - candidate.keys())],
        "rmut_only": [list(key) for key in sorted(candidate.keys() - reference.keys())],
        "outcome_mismatches": [
            {"identity": list(key), "stryker": reference[key], "rmut": candidate[key]}
            for key in sorted(reference.keys() & candidate.keys())
            if reference[key]["outcome"] != candidate[key]["outcome"]
        ],
        "unfinished": [
            {"tool": tool, "identity": list(key)}
            for tool, records in (("stryker", reference), ("rmut", candidate))
            for key, detail in sorted(records.items())
            if detail["outcome"] not in {"killed", "survived", "noCoverage", "timedOut",
                                         "buildFailed", "ignored"}
        ],
    }
