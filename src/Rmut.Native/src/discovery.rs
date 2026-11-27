use sha2::{Digest, Sha256};
use std::io;
use tree_sitter::Parser;

pub struct Mutation<'a> {
    pub id: String,
    pub line: usize,
    pub original: &'a str,
    pub replacement: &'static str,
    start: usize,
    end: usize,
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

pub fn discover<'a>(
    parser: &mut Parser,
    path: &str,
    source: &'a str,
) -> io::Result<Vec<Mutation<'a>>> {
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
    'walk: loop {
        let node = cursor.node();
        if node.kind() == "preproc_if" || node.kind() == "#if" {
            return Err(io::Error::other("conditional compilation is unsupported"));
        }
        if node.kind() == "binary_expression" {
            if let Some(operator) = node
                .child_by_field_name("operator")
                .filter(|op| !op.is_missing())
            {
                operators.push(operator.byte_range());
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

    operators.sort_unstable_by_key(|range| range.start);
    let mut offset = 0;
    let mut line = 1;
    let mut previous = 0;
    Ok(operators
        .into_iter()
        .filter_map(|range| {
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
            })
        })
        .collect())
}
