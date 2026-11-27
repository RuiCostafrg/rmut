use sha2::{Digest, Sha256};
use std::borrow::Cow;
use std::fmt::Write as _;
use std::io;
use std::ops::Range;
use tree_sitter::{Node, Parser};

pub struct Mutation<'a> {
    pub id: String,
    pub line: usize,
    pub original: &'a str,
    pub replacement: Cow<'a, str>,
    pub offset: usize,
    start: usize,
    end: usize,
    switch: Option<Range<usize>>,
}

impl Mutation<'_> {
    pub fn apply(&self, source: &str) -> String {
        let rest = &source[self.end..];
        let needs_space = match self.replacement.as_ref() {
            "+" | "-" => rest.starts_with(self.replacement.as_ref()),
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

pub fn instrument(source: &str, mutations: &[Mutation<'_>], selector: &str) -> Option<String> {
    let mut end = 0;
    let mut instrumented = String::new();
    // Discovery keeps all alternatives for one operator adjacent. Emit one
    // conditional chain for its expression so only the selected alternative runs.
    let switchable: Vec<_> = mutations
        .iter()
        .filter(|mutant| mutant.can_switch())
        .collect();
    for alternatives in switchable.chunk_by(|a, b| a.start == b.start) {
        let Some(range) = &alternatives[0].switch else {
            continue;
        };
        instrumented.push_str(&source[end..range.start]);
        instrumented.push('(');
        for mutant in alternatives {
            let left = &source[range.start..mutant.start];
            let right = &source[mutant.end..range.end];
            write!(
                instrumented,
                "(global::System.Environment.GetEnvironmentVariable(\"{selector}\") == \"{}\") ? ({left}{}{right}) : ",
                mutant.id, mutant.replacement
            )
            .expect("writing to a String cannot fail");
        }
        instrumented.push('(');
        instrumented.push_str(&source[range.clone()]);
        instrumented.push_str("))");
        end = range.end;
    }
    if instrumented.is_empty() {
        return None;
    }
    instrumented.push_str(&source[end..]);
    Some(instrumented)
}

impl Mutation<'_> {
    pub fn can_switch(&self) -> bool {
        self.switch.is_some()
    }
}

fn replacements(operator: &str) -> &'static [&'static str] {
    match operator {
        "+" => &["-"],
        "-" => &["+"],
        "*" => &["/"],
        "%" | "/" => &["*"],
        "==" => &["!="],
        "!=" => &["=="],
        "<" => &[">", "<="],
        "<=" => &[">", "<"],
        ">" => &["<", ">="],
        ">=" => &["<", ">"],
        "&&" => &["||"],
        "||" => &["&&"],
        _ => &[],
    }
}

pub struct Discovery<'a> {
    pub mutations: Vec<Mutation<'a>>,
    pub can_instrument: bool,
}

pub fn discover<'a>(
    parser: &mut Parser,
    path: &str,
    source: &'a str,
    stryker: bool,
) -> io::Result<Discovery<'a>> {
    let tree = parser
        .parse(source, None)
        .ok_or_else(|| io::Error::other("C# parsing failed"))?;
    if tree.root_node().has_error() {
        return Err(io::Error::other(
            "C# syntax is unsupported by the Rust parser",
        ));
    }
    let mut cursor = tree.walk();
    let mut edits = Vec::new();
    let mut can_instrument = true;
    let mut excluded_end = 0;
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
        ) && node.child_by_field_name("name").is_some_and(|name| {
            let name = &source[name.byte_range()];
            name.trim_start_matches('@') == "Environment" || name.contains('\\')
        }) {
            can_instrument = false;
        }
        if stryker && node.start_byte() >= excluded_end && skip_stryker_subtree(node, source) {
            excluded_end = node.end_byte();
        }
        let included = node.start_byte() >= excluded_end;
        if included && node.kind() == "binary_expression" {
            if let Some(operator) = node
                .child_by_field_name("operator")
                .filter(|op| !op.is_missing())
            {
                let token = &source[operator.byte_range()];
                let alternatives = if stryker {
                    match token {
                        "&" => &["|"][..],
                        "|" => &["&"][..],
                        "+" if ["left", "right"]
                            .into_iter()
                            .any(|side| node.child_by_field_name(side).is_some_and(is_string)) =>
                        {
                            &[]
                        }
                        _ => replacements(token),
                    }
                } else {
                    replacements(token)
                };
                if !alternatives.is_empty() {
                    let switch = switch_range(node, source);
                    for &replacement in alternatives {
                        edits.push(Edit {
                            range: operator.byte_range(),
                            replacement: Cow::Borrowed(replacement),
                            switch: switch.clone(),
                        });
                    }
                }
            }
        }
        if stryker && included {
            stryker_edits(node, source, &mut edits);
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

    edits.sort_by_key(|edit| edit.range.start);
    let mut offset = 0;
    let mut line = 1;
    let mut previous = 0;
    let mutations = edits
        .into_iter()
        .map(
            |Edit {
                 range,
                 replacement,
                 switch,
             }| {
                let original = &source[range.clone()];
                let prefix = &source[previous..range.start];
                offset += prefix.encode_utf16().count();
                line += prefix
                    .matches(['\r', '\n', '\u{85}', '\u{2028}', '\u{2029}'])
                    .count()
                    - prefix.matches("\r\n").count();
                previous = range.start;
                let identity = format!("{offset}:{original}:{replacement}:{path}");
                Mutation {
                    id: format!("{:X}", Sha256::digest(identity.as_bytes())),
                    line,
                    original,
                    replacement,
                    offset,
                    start: range.start,
                    end: range.end,
                    switch,
                }
            },
        )
        .collect();
    Ok(Discovery {
        mutations,
        can_instrument,
    })
}

struct Edit<'a> {
    range: Range<usize>,
    replacement: Cow<'a, str>,
    switch: Option<Range<usize>>,
}

fn skip_stryker_subtree(node: Node<'_>, source: &str) -> bool {
    match node.kind() {
        "attribute_list" | "parameter_list" | "enum_member_declaration" | "using_directive" => true,
        "field_declaration" | "local_declaration_statement" => {
            let mut cursor = node.walk();
            let is_const = node
                .named_children(&mut cursor)
                .any(|child| child.kind() == "modifier" && &source[child.byte_range()] == "const");
            is_const
        }
        _ => false,
    }
}

fn is_string(node: Node<'_>) -> bool {
    matches!(
        node.kind(),
        "string_literal"
            | "verbatim_string_literal"
            | "raw_string_literal"
            | "interpolated_string_expression"
    )
}

fn first_syntax_child(node: Node<'_>) -> Option<Node<'_>> {
    let mut cursor = node.walk();
    let child = node
        .named_children(&mut cursor)
        .find(|child| !child.is_extra());
    child
}

fn ancestors(node: Node<'_>) -> impl Iterator<Item = Node<'_>> {
    std::iter::successors(node.parent(), Node::parent)
}

fn contains_syntax(node: Node<'_>, kinds: &[&str]) -> bool {
    if kinds.contains(&node.kind()) {
        return true;
    }
    if matches!(
        node.kind(),
        "lambda_expression" | "anonymous_method_expression"
    ) {
        return false;
    }
    let mut cursor = node.walk();
    let found = node
        .children(&mut cursor)
        .any(|child| contains_syntax(child, kinds));
    found
}

fn removable_statement(node: Node<'_>) -> bool {
    match node.kind() {
        "expression_statement" => !contains_syntax(node, &["assignment_expression", "out"]),
        "yield_statement" => true,
        "return_statement" if first_syntax_child(node).is_some() => false,
        "return_statement"
            if ancestors(node)
                .find(|parent| parent.kind() == "if_statement")
                .and_then(|parent| parent.child_by_field_name("condition"))
                .is_some_and(|condition| contains_syntax(condition, &["declaration_pattern"])) =>
        {
            false
        }
        "return_statement" | "break_statement" | "continue_statement" | "goto_statement"
        | "throw_statement" => !ancestors(node).any(|parent| parent.kind() == "switch_section"),
        _ => false,
    }
}

fn removable_block(node: Node<'_>, source: &str) -> bool {
    if first_syntax_child(node).is_none()
        || node.parent().is_some_and(|parent| {
            parent.kind() == "while_statement"
                && parent
                    .child_by_field_name("condition")
                    .is_some_and(|condition| &source[condition.byte_range()] == "true")
        })
    {
        return false;
    }
    for parent in ancestors(node) {
        match parent.kind() {
            "local_function_statement" => break,
            "switch_section" => return false,
            "constructor_declaration"
                if parent
                    .parent()
                    .and_then(|list| list.parent())
                    .is_some_and(|owner| owner.kind() == "struct_declaration") =>
            {
                let mut cursor = node.walk();
                return !node.named_children(&mut cursor).any(|child| {
                    child.kind() == "expression_statement"
                        && first_syntax_child(child)
                            .is_some_and(|expression| expression.kind() == "assignment_expression")
                });
            }
            _ => {}
        }
    }
    true
}

fn negatable_condition(mut node: Node<'_>) -> bool {
    if !node.parent().is_some_and(|parent| {
        matches!(parent.kind(), "if_statement" | "while_statement")
            && parent.child_by_field_name("condition") == Some(node)
    }) {
        return false;
    }
    if node.kind() == "is_pattern_expression" {
        // The grammar can absorb the remainder of a condition into a
        // constant pattern, e.g. "x is null && flag". Roslyn treats that
        // as a binary condition. Parenthesized pattern expressions stop
        // the check because their operators are genuinely inside the pattern.
        let Some(mut pattern) = node.child_by_field_name("pattern") else {
            return false;
        };
        while matches!(pattern.kind(), "negated_pattern" | "constant_pattern") {
            let Some(inner) = first_syntax_child(pattern) else {
                return false;
            };
            pattern = inner;
        }
        return pattern.kind() == "binary_expression"
            && pattern
                .child_by_field_name("operator")
                .is_some_and(|operator| matches!(operator.kind(), "&&" | "||"));
    }
    while node.kind() == "parenthesized_expression" {
        let Some(inner) = first_syntax_child(node) else {
            return false;
        };
        node = inner;
    }
    !matches!(
        (
            node.kind(),
            node.child_by_field_name("operator")
                .or_else(|| node.child(0))
                .map(|token| token.kind())
        ),
        ("binary_expression", Some("==" | "!=")) | ("prefix_unary_expression", Some("!"))
    )
}

fn stryker_edits<'a>(node: Node<'_>, source: &'a str, edits: &mut Vec<Edit<'a>>) {
    let text = |node: Node<'_>| &source[node.byte_range()];
    let mut add = |range, replacement| {
        edits.push(Edit {
            range,
            replacement,
            switch: None,
        })
    };
    if negatable_condition(node) {
        add(node.byte_range(), Cow::Owned(format!("!({})", text(node))));
    }

    let replacement = match node.kind() {
        "block" if removable_block(node, source) => "{}",
        "boolean_literal" => match text(node) {
            "true" => "false",
            _ => "true",
        },
        "string_literal" | "verbatim_string_literal" | "raw_string_literal" => {
            let literal = text(node);
            if literal.ends_with("u8")
                || literal.ends_with("U8")
                || node
                    .parent()
                    .and_then(|parent| parent.parent())
                    .filter(|parent| parent.kind() == "argument_list")
                    .and_then(|list| list.parent())
                    .filter(|expression| expression.kind() == "object_creation_expression")
                    .and_then(|creation| creation.child_by_field_name("type"))
                    .is_some_and(|kind| {
                        matches!(
                            text(kind),
                            "Regex"
                                | "System.Text.RegularExpressions.Regex"
                                | "Guid"
                                | "System.Guid"
                        )
                    })
            {
                return;
            }
            if matches!(literal, "\"\"" | "@\"\"")
                || node.kind() == "raw_string_literal" && literal.trim_matches('"').is_empty()
            {
                "\"Stryker was here!\""
            } else {
                "\"\""
            }
        }
        "interpolated_string_expression" if node.child_count() > 3 => "$\"\"",
        "is_pattern_expression" => {
            let Some(pattern) = node.child_by_field_name("pattern") else {
                return;
            };
            let prefix = &source[node.start_byte()..pattern.start_byte()];
            let replacement = if pattern.kind() == "negated_pattern" {
                let Some(inner) = first_syntax_child(pattern) else {
                    return;
                };
                format!("{prefix}{}", text(inner))
            } else {
                format!("{prefix}not {}", text(pattern))
            };
            add(node.byte_range(), Cow::Owned(replacement));
            return;
        }
        "prefix_unary_expression" | "postfix_unary_expression" => {
            let Some(operand) = first_syntax_child(node) else {
                return;
            };
            let operator = if node.kind() == "prefix_unary_expression" {
                node.child(0)
            } else {
                node.children(&mut node.walk()).last()
            };
            let Some(operator) = operator else {
                return;
            };
            let replacement = match operator.kind() {
                "!" | "~" if node.kind() == "prefix_unary_expression" => {
                    add(node.byte_range(), Cow::Borrowed(text(operand)));
                    return;
                }
                "-" => "+",
                "+" => "-",
                "++" => "--",
                "--" => "++",
                _ => return,
            };
            add(operator.byte_range(), Cow::Borrowed(replacement));
            return;
        }
        _ if removable_statement(node) => ";",
        _ => return,
    };
    add(node.byte_range(), Cow::Borrowed(replacement));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stryker_profile_discovers_broad_edits_outside_attributes_and_defaults() {
        let source = r#"
            [Example("😀")]
            class Sample
            {
                private const bool Constant = true;
                bool Check(string? input, bool enabled = true)
                {
                    if (input is null || enabled)
                        System.GC.KeepAlive("value");
                    return !enabled;
                }
            }
        "#;
        let mut parser = Parser::new();
        parser
            .set_language(&tree_sitter_c_sharp::LANGUAGE.into())
            .unwrap();

        let binary = discover(&mut parser, "Sample.cs", source, false).unwrap();
        assert_eq!(binary.mutations.len(), 1);
        assert_eq!(binary.mutations[0].original, "||");

        let expanded = discover(&mut parser, "Sample.cs", source, true).unwrap();
        let replacements: Vec<_> = expanded
            .mutations
            .iter()
            .map(|mutation| mutation.replacement.as_ref())
            .collect();
        assert_eq!(
            replacements,
            [
                "{}",
                "!(input is null || enabled)",
                "input is not null || enabled",
                "&&",
                ";",
                "\"\"",
                "enabled"
            ]
        );
        let condition = &expanded.mutations[1];
        let offset = source.find("input is null").unwrap();
        assert_eq!(condition.offset, source[..offset].encode_utf16().count());
        assert!(condition
            .apply(source)
            .contains("if (!(input is null || enabled))"));
        assert!(instrument(source, &expanded.mutations, "MUTANT").is_none());
    }
}
