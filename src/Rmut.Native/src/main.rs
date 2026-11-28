mod discovery;

use serde::{de::DeserializeOwned, Deserialize, Serialize};
use std::collections::HashSet;
use std::io::{self, BufRead, BufReader, Write};
use tree_sitter::Parser;

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
enum Outcome {
    Passed,
    TestFailed,
    Killed,
    Survived,
    NoCoverage,
    BuildFailed,
    TimedOut,
    RunnerError,
}

#[derive(Deserialize, Serialize)]
struct RunResult {
    outcome: Outcome,
    details: Option<String>,
}

#[derive(Deserialize)]
struct Source {
    path: String,
    content: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Sources {
    result: RunResult,
    paths: Vec<Source>,
    selector: Option<String>,
    coverage_path: Option<String>,
    coverage_scope: Option<String>,
}

#[derive(Deserialize)]
struct Preparation {
    ready: bool,
    hits: Option<HashSet<String>>,
}

#[derive(Serialize)]
struct MutantResult {
    id: String,
    path: String,
    line: usize,
    original: String,
    replacement: &'static str,
    #[serde(flatten)]
    result: RunResult,
}

#[derive(Serialize)]
struct Report {
    baseline: RunResult,
    mutants: Vec<MutantResult>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<RunResult>,
}

#[derive(Serialize)]
#[serde(
    tag = "command",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
enum Request<'a> {
    Baseline,
    Sources,
    Prepare {
        files: Vec<InstrumentedSource>,
        collect_coverage: bool,
    },
    EvaluateSwitched {
        id: String,
    },
    Evaluate {
        index: usize,
        mutated_source: String,
    },
    Log {
        text: String,
    },
    Finish {
        report: &'a Report,
    },
}

#[derive(Serialize)]
struct InstrumentedSource {
    index: usize,
    content: String,
}

struct Host {
    input: BufReader<io::Stdin>,
    output: io::Stdout,
}

impl Host {
    fn send(&mut self, request: Request<'_>) -> io::Result<()> {
        serde_json::to_writer(&mut self.output, &request)?;
        writeln!(self.output)?;
        self.output.flush()
    }

    fn call<T: DeserializeOwned>(&mut self, request: Request<'_>) -> io::Result<T> {
        self.send(request)?;
        let mut reply = String::new();
        self.input.read_line(&mut reply)?;
        Ok(serde_json::from_str(&reply)?)
    }
}

fn log_result(host: &mut Host, label: &str, result: &RunResult) -> io::Result<()> {
    host.send(Request::Log {
        text: format!("{label}: {:?}", result.outcome),
    })?;
    if let Some(details) = &result.details {
        host.send(Request::Log {
            text: details.clone(),
        })?;
    }
    Ok(())
}

fn run(host: &mut Host) -> Result<Report, Box<dyn std::error::Error>> {
    let baseline = host.call(Request::Baseline)?;
    log_result(host, "Baseline", &baseline)?;
    let mut report = Report {
        baseline,
        mutants: Vec::new(),
        error: None,
    };
    if !matches!(report.baseline.outcome, Outcome::Passed) {
        return Ok(report);
    }

    let sources: Sources = host.call(Request::Sources)?;
    if !matches!(sources.result.outcome, Outcome::Passed) {
        log_result(host, "Source discovery", &sources.result)?;
        report.error = Some(sources.result);
        return Ok(report);
    }

    let mut parser = Parser::new();
    parser.set_language(&tree_sitter_c_sharp::LANGUAGE.into())?;
    let mut candidates = Vec::new();
    for source in &sources.paths {
        match discovery::discover(&mut parser, &source.path, &source.content) {
            Ok(discovery) => candidates.push(discovery),
            Err(error) => {
                let result = RunResult {
                    outcome: Outcome::RunnerError,
                    details: Some(format!("{}: {error}", source.path)),
                };
                log_result(host, "Source discovery", &result)?;
                report.error = Some(result);
                return Ok(report);
            }
        }
    }
    let mut switched = false;
    let mut hits = None;
    if let Some(selector) = sources
        .selector
        .filter(|_| candidates.iter().all(|file| file.can_instrument))
    {
        let coverage = sources
            .coverage_path
            .as_deref()
            .filter(|_| candidates.iter().all(|file| file.can_collect_coverage));
        let files: Vec<_> = sources
            .paths
            .iter()
            .zip(&candidates)
            .enumerate()
            .filter_map(|(index, (source, file))| {
                discovery::instrument(
                    &source.content,
                    &file.mutations,
                    &selector,
                    coverage,
                    sources.coverage_scope.as_deref(),
                )
                .map(|content| InstrumentedSource { index, content })
            })
            .collect();
        if !files.is_empty() {
            let preparation: Preparation = host.call(Request::Prepare {
                files,
                collect_coverage: coverage.is_some(),
            })?;
            switched = preparation.ready;
            if switched && coverage.is_some() {
                let probes: HashSet<_> = candidates
                    .iter()
                    .flat_map(|file| &file.mutations)
                    .filter(|mutant| mutant.can_switch())
                    .map(|mutant| mutant.id.as_str())
                    .collect();
                hits = preparation.hits.filter(|hits| {
                    !hits.is_empty() && hits.iter().all(|hit| probes.contains(hit.as_str()))
                });
            }
        }
    }
    for (index, (source, file)) in sources.paths.iter().zip(candidates).enumerate() {
        for mutant in file.mutations {
            let switched_result = if switched && mutant.can_switch() {
                if hits.as_ref().is_some_and(|hits| !hits.contains(&mutant.id)) {
                    Some(RunResult {
                        outcome: Outcome::NoCoverage,
                        details: None,
                    })
                } else {
                    host.call(Request::EvaluateSwitched {
                        id: mutant.id.clone(),
                    })?
                }
            } else {
                None
            };
            let mut result: RunResult = match switched_result {
                Some(result) => result,
                None => host.call(Request::Evaluate {
                    index,
                    mutated_source: mutant.apply(&source.content),
                })?,
            };
            result.outcome = match result.outcome {
                Outcome::Passed => Outcome::Survived,
                Outcome::TestFailed => Outcome::Killed,
                outcome => outcome,
            };
            log_result(
                host,
                &format!(
                    "{} {}:{} {} → {}",
                    mutant.id, source.path, mutant.line, mutant.original, mutant.replacement
                ),
                &result,
            )?;
            report.mutants.push(MutantResult {
                id: mutant.id,
                path: source.path.clone(),
                line: mutant.line,
                original: mutant.original.to_owned(),
                replacement: mutant.replacement,
                result,
            });
        }
    }
    let killed = report
        .mutants
        .iter()
        .filter(|mutant| matches!(mutant.result.outcome, Outcome::Killed))
        .count();
    let survived = report
        .mutants
        .iter()
        .filter(|mutant| matches!(mutant.result.outcome, Outcome::Survived))
        .count();
    let uncovered = report
        .mutants
        .iter()
        .filter(|mutant| matches!(mutant.result.outcome, Outcome::NoCoverage))
        .count();
    let mut summary = format!(
        "Mutants: {}. Killed: {killed}. Survived: {survived}.",
        report.mutants.len()
    );
    if sources.coverage_path.is_some() {
        summary.push_str(&format!(" No coverage: {uncovered}."));
    }
    host.send(Request::Log { text: summary })?;
    Ok(report)
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut host = Host {
        input: BufReader::new(io::stdin()),
        output: io::stdout(),
    };
    let report = run(&mut host)?;
    host.send(Request::Finish { report: &report })?;
    Ok(())
}
