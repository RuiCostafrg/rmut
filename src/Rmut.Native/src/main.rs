mod discovery;

use serde::{de::DeserializeOwned, Deserialize, Serialize};
use std::io::{self, BufRead, BufReader, Write};
use tree_sitter::Parser;

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
enum Outcome {
    Passed,
    TestFailed,
    Killed,
    Survived,
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
    switched_workers: usize,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Prepared {
    ready: bool,
    first_result: Option<RunResult>,
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
        first_mutant: Option<&'a str>,
    },
    EvaluateSwitched {
        ids: Vec<&'a str>,
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
    let mut first_mutant = None;
    let mut first_result = None;
    if let Some(selector) = sources
        .selector
        .filter(|_| candidates.iter().all(|file| file.can_instrument))
    {
        let files: Vec<_> = sources
            .paths
            .iter()
            .zip(&candidates)
            .enumerate()
            .filter_map(|(index, (source, file))| {
                discovery::instrument(&source.content, &file.mutations, &selector)
                    .map(|content| InstrumentedSource { index, content })
            })
            .collect();
        if !files.is_empty() {
            let first = if sources.switched_workers == 2 {
                candidates
                    .iter()
                    .flat_map(|file| &file.mutations)
                    .find(|mutant| mutant.can_switch())
                    .map(|mutant| mutant.id.as_str())
            } else {
                None
            };
            let prepared: Prepared = host.call(Request::Prepare {
                files,
                first_mutant: first,
            })?;
            switched = prepared.ready;
            if switched {
                first_mutant = first.map(str::to_owned);
                first_result = prepared.first_result;
            }
        }
    }
    let mut mutants = sources
        .paths
        .iter()
        .zip(candidates)
        .enumerate()
        .flat_map(|(index, (source, file))| {
            file.mutations
                .into_iter()
                .map(move |mutant| (index, source, mutant))
        })
        .peekable();
    while let Some(first) = mutants.next() {
        let is_first = first_mutant.as_deref() == Some(first.2.id.as_str());
        let next_is_first = mutants
            .peek()
            .is_some_and(|(_, _, mutant)| first_mutant.as_deref() == Some(mutant.id.as_str()));
        let second = if switched && sources.switched_workers == 2 && !is_first && !next_is_first {
            mutants.next()
        } else {
            None
        };
        let batch = [Some(first), second];
        let ids: Vec<_> = batch
            .iter()
            .flatten()
            .filter(|(_, _, mutant)| switched && !is_first && mutant.can_switch())
            .map(|(_, _, mutant)| mutant.id.as_str())
            .collect();
        let count = ids.len();
        let results: Vec<Option<RunResult>> = if ids.is_empty() {
            Vec::new()
        } else {
            host.call(Request::EvaluateSwitched { ids })?
        };
        if results.len() != count {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Unexpected switched result count",
            )
            .into());
        }
        let mut switched_results = results.into_iter();
        for (index, source, mutant) in batch.into_iter().flatten() {
            let switched_result = if first_mutant.as_deref() == Some(mutant.id.as_str()) {
                first_mutant = None;
                first_result.take()
            } else if switched && mutant.can_switch() {
                switched_results.next().flatten()
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
    host.send(Request::Log {
        text: format!(
            "Mutants: {}. Killed: {killed}. Survived: {survived}.",
            report.mutants.len()
        ),
    })?;
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
