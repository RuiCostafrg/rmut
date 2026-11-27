use sha2::{Digest, Sha256};
use std::io;
use std::ops::Range;
use std::path::Path;
use tree_sitter::{Node, Parser};

pub struct Mutation<'a> {
    pub id: String,
    pub line: usize,
    pub original: &'a str,
    pub replacement: &'static str,
    start: usize,
    end: usize,
    switch: Option<Range<usize>>,
}

impl Mutation<'_> {
    pub fn apply(&self, source: &str) -> String {
        let rest = &source[self.end..];
        let needs_space = match self.replacement {
            "+" | "-" => rest.starts_with(self.replacement),
            "/" => rest.starts_with(['/', '*']),
            _ => false,
        };
        let space = if needs_space { " " } else { "" };
        format!(
            "{}{replacement}{space}{rest}",
            &source[..self.start],
            replacement = self.replacement
        )
    }
}

// Restrict switching to whole expression-bodied methods with built-in integer
// operands. Other contexts can depend on constant folding or contextual typing.
fn switch_range(node: Node<'_>, source: &str) -> Option<Range<usize>> {
    let arrow = node
        .parent()
        .filter(|parent| parent.kind() == "arrow_expression_clause")?;
    let method = arrow
        .parent()
        .filter(|parent| parent.kind() == "method_declaration")?;
    let text = |node: Node<'_>| &source[node.byte_range()];
    let expression = text(node);
    if expression.contains(['\r', '\n', '\u{85}', '\u{2028}', '\u{2029}'])
        || expression.contains("//")
        || expression.contains("/*")
        || !matches!(text(method.child_by_field_name("returns")?), "int" | "bool")
    {
        return None;
    }

    let parameters = method.child_by_field_name("parameters")?;
    let mut cursor = parameters.walk();
    let integers: Vec<_> = parameters
        .named_children(&mut cursor)
        .filter(|parameter| parameter.child_by_field_name("type").map(text) == Some("int"))
        .filter_map(|parameter| parameter.child_by_field_name("name").map(text))
        .collect();
    for side in ["left", "right"] {
        let operand = node.child_by_field_name(side)?;
        let value = text(operand);
        match operand.kind() {
            "identifier" if integers.contains(&value) => {}
            "integer_literal" if value.parse::<i32>().is_ok() => {}
            _ => return None,
        }
    }
    Some(node.byte_range())
}

pub fn instrument(
    source: &str,
    mutations: &[Mutation<'_>],
    selector: &str,
    coverage_path: Option<&str>,
) -> Option<String> {
    let mut end = source.len();
    let mut pieces = Vec::new();
    for mutant in mutations.iter().rev() {
        let Some(range) = &mutant.switch else {
            continue;
        };
        let original = &source[range.clone()];
        let left = &source[range.start..mutant.start];
        let right = &source[mutant.end..range.end];
        let selection = format!(
            "global::System.Environment.GetEnvironmentVariable(\"{selector}\") == \"{}\"",
            mutant.id
        );
        // The absolute path also reaches children that launch with a clean
        // environment. A failed write poisons the whole collection.
        let condition = if let Some(path) = coverage_path {
            let marker = |name: &str| {
                serde_json::to_string(&Path::new(path).join(name))
                    .expect("a UTF-8 path encodes as a JSON string")
                    .replace('\u{85}', "\\u0085")
                    .replace('\u{2028}', "\\u2028")
                    .replace('\u{2029}', "\\u2029")
            };
            let hit = marker(&mutant.id);
            let error = marker("probe-error");
            format!(
                concat!(
                    "(global::System.Environment.GetEnvironmentVariable(\"{}\") is null ",
                    "? ((global::System.Func<bool>)(() => {{ ",
                    "try {{ global::System.IO.Directory.CreateDirectory({}); }} catch {{ ",
                    "try {{ global::System.IO.Directory.CreateDirectory({}); }} catch {{ ",
                    "global::System.Environment.FailFast(\"Rmut coverage probe failed.\"); ",
                    "}} }} return false; }}))() : {})"
                ),
                selector, hit, error, selection
            )
        } else {
            selection
        };
        pieces.push(format!(
            "({condition} ? ({left}{}{right}) : ({original})){}",
            mutant.replacement,
            &source[range.end..end]
        ));
        end = range.start;
    }
    if pieces.is_empty() {
        return None;
    }
    let mut instrumented = source[..end].to_owned();
    instrumented.extend(pieces.into_iter().rev());
    Some(instrumented)
}

impl Mutation<'_> {
    pub fn can_switch(&self) -> bool {
        self.switch.is_some()
    }
}

fn replacement(operator: &str) -> Option<&'static str> {
    match operator {
        "+" => Some("-"),
        "-" => Some("+"),
        "*" => Some("/"),
        "%" => Some("*"),
        "/" => Some("*"),
        "==" => Some("!="),
        "!=" => Some("=="),
        "<" => Some("<="),
        "<=" => Some("<"),
        ">" => Some(">="),
        ">=" => Some(">"),
        "&&" => Some("||"),
        "||" => Some("&&"),
        _ => None,
    }
}

pub struct Discovery<'a> {
    pub mutations: Vec<Mutation<'a>>,
    pub can_instrument: bool,
    pub can_collect_coverage: bool,
}

pub fn discover<'a>(parser: &mut Parser, path: &str, source: &'a str) -> io::Result<Discovery<'a>> {
    let tree = parser
        .parse(source, None)
        .ok_or_else(|| io::Error::other("C# parsing failed"))?;
    if tree.root_node().has_error() {
        return Err(io::Error::other(
            "C# syntax is unsupported by the Rust parser",
        ));
    }
    let mut cursor = tree.walk();
    let mut operators = Vec::new();
    let mut can_instrument = true;
    let mut can_collect_coverage = true;
    'walk: loop {
        let node = cursor.node();
        if node.kind() == "preproc_if" || node.kind() == "#if" {
            return Err(io::Error::other("conditional compilation is unsupported"));
        }
        // A project-local type can shadow the framework selector even when
        // the compiler's type-conflict warning has been suppressed.
        if matches!(
            node.kind(),
            "class_declaration"
                | "struct_declaration"
                | "record_declaration"
                | "interface_declaration"
                | "enum_declaration"
                | "delegate_declaration"
        ) {
            if let Some(name) = node.child_by_field_name("name") {
                let name = source[name.byte_range()].trim_start_matches('@');
                can_instrument &= name != "Environment" && !name.contains('\\');
                can_collect_coverage &= !matches!(name, "Directory" | "Func");
            }
        }
        // Initializer ordering and module initializer aliases are outside the
        // coverage proof for this slice. Keep their entire batch on full-suite execution.
        if matches!(
            node.kind(),
            "constructor_declaration"
                | "field_declaration"
                | "property_declaration"
                | "event_field_declaration"
                | "method_declaration"
        ) {
            let mut children = node.walk();
            if node.children(&mut children).any(|child| {
                (node.kind() == "method_declaration" && child.kind() == "attribute_list")
                    || (child.kind() == "modifier"
                        && matches!(&source[child.byte_range()], "static" | "const")
                        && matches!(
                            node.kind(),
                            "constructor_declaration"
                                | "field_declaration"
                                | "property_declaration"
                                | "event_field_declaration"
                        ))
            }) {
                can_collect_coverage = false;
            }
        }
        if node.kind() == "binary_expression" {
            if let Some(operator) = node
                .child_by_field_name("operator")
                .filter(|op| !op.is_missing())
            {
                operators.push((operator.byte_range(), switch_range(node, source)));
            }
        }
        if cursor.goto_first_child() {
            continue;
        }
        loop {
            if cursor.goto_next_sibling() {
                break;
            }
            if !cursor.goto_parent() {
                break 'walk;
            }
        }
    }

    operators.sort_unstable_by_key(|(range, _)| range.start);
    let mut offset = 0;
    let mut line = 1;
    let mut previous = 0;
    let mutations = operators
        .into_iter()
        .filter_map(|(range, switch)| {
            let original = &source[range.clone()];
            let replacement = replacement(original)?;
            let prefix = &source[previous..range.start];
            offset += prefix.encode_utf16().count();
            line += prefix
                .matches(['\r', '\n', '\u{85}', '\u{2028}', '\u{2029}'])
                .count()
                - prefix.matches("\r\n").count();
            previous = range.start;
            let identity = format!("{offset}:{original}:{replacement}:{path}");
            let id = format!("{:X}", Sha256::digest(identity.as_bytes()));
            Some(Mutation {
                id,
                line,
                original,
                replacement,
                start: range.start,
                end: range.end,
                switch,
            })
        })
        .collect();
    Ok(Discovery {
        mutations,
        can_instrument,
        can_collect_coverage,
    })
}
