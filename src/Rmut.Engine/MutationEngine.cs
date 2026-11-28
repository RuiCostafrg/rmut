using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rmut.Engine;

public static class MutationEngine
{
    /// <summary>Discovers single binary operator mutations in source order.</summary>
    public static IEnumerable<Mutant> Discover(string path, string source)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(source);

        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        foreach (var token in tree.GetRoot().DescendantTokens())
        {
            if (token.IsMissing || token.Parent is not BinaryExpressionSyntax expression ||
                token != expression.OperatorToken)
            {
                continue;
            }

            string[] replacements = token.Kind() switch
            {
                SyntaxKind.PlusToken => ["-"],
                SyntaxKind.MinusToken => ["+"],
                SyntaxKind.AsteriskToken => ["/"],
                SyntaxKind.SlashToken => ["*"],
                SyntaxKind.PercentToken => ["*"],
                SyntaxKind.EqualsEqualsToken => ["!="],
                SyntaxKind.ExclamationEqualsToken => ["=="],
                SyntaxKind.LessThanToken => [">", "<="],
                SyntaxKind.LessThanEqualsToken => [">", "<"],
                SyntaxKind.GreaterThanToken => ["<", ">="],
                SyntaxKind.GreaterThanEqualsToken => ["<", ">"],
                SyntaxKind.AmpersandAmpersandToken => ["||"],
                SyntaxKind.BarBarToken => ["&&"],
                _ => []
            };

            if (replacements.Length == 0)
            {
                continue;
            }

            var span = token.Span;
            var line = tree.GetLineSpan(span).StartLinePosition.Line + 1;
            foreach (var replacement in replacements)
            {
                var identity = FormattableString.Invariant($"{span.Start}:{token.Text}:{replacement}:{path}");
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                var spelling = replacement;

                // Separate adjacent operators when the replacement could merge into
                // an increment, decrement, or comment opener.
                if (span.End < source.Length &&
                    ((replacement is "+" or "-" && source[span.End] == replacement[0]) ||
                     (replacement == "/" && source[span.End] is '/' or '*')))
                {
                    spelling += " ";
                }

                var mutatedSource = string.Concat(source.AsSpan(0, span.Start), spelling, source.AsSpan(span.End));
                yield return new Mutant(id, path, line, token.Text, replacement, mutatedSource);
            }
        }
    }
}
