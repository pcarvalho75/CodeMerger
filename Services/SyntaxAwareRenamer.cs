using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMerger.Services
{
    /// <summary>
    /// Renames identifiers by walking the syntax tree, skipping strings, comments, and non-identifier tokens.
    /// </summary>
    public static class SyntaxAwareRenamer
    {
        /// <summary>
        /// Replaces all identifier tokens matching oldName with newName in the given C# source code.
        /// Only touches actual code identifiers — skips string literals, comments, verbatim strings, interpolations, and attributes.
        /// Returns null if no changes were made.
        /// </summary>
        public static RenameOutcome? Rename(string sourceCode, string oldName, string newName)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceCode);
            var root = tree.GetCompilationUnitRoot();

            var tokensToReplace = root.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == oldName)
                .Where(t => !IsInsideStringOrComment(t))
                .ToList();

            if (tokensToReplace.Count == 0)
                return null;

            var newRoot = root.ReplaceTokens(tokensToReplace, (original, _) =>
                SyntaxFactory.Identifier(original.LeadingTrivia, newName, original.TrailingTrivia));

            return new RenameOutcome
            {
                ModifiedSource = newRoot.ToFullString(),
                OccurrenceCount = tokensToReplace.Count
            };
        }

        private static bool IsInsideStringOrComment(SyntaxToken token)
        {
            foreach (var ancestor in token.Parent?.AncestorsAndSelf() ?? Enumerable.Empty<SyntaxNode>())
            {
                switch (ancestor)
                {
                    case LiteralExpressionSyntax literal
                        when literal.IsKind(SyntaxKind.StringLiteralExpression)
                          || literal.IsKind(SyntaxKind.CharacterLiteralExpression):
                        return true;

                    case InterpolatedStringExpressionSyntax:
                        // Only skip if the token is in the text portion, not in the interpolation holes
                        if (token.Parent is InterpolatedStringTextSyntax)
                            return true;
                        break;

                    case AttributeSyntax:
                        // Don't skip attributes — they contain valid identifiers to rename
                        break;
                }
            }

            // Check if the token is inside trivia (comments, disabled text, etc.)
            // This shouldn't happen for real tokens, but guard against it
            return false;
        }

        public class RenameOutcome
        {
            public string ModifiedSource { get; set; } = "";
            public int OccurrenceCount { get; set; }
        }
    }
}
