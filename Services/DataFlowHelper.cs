using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMerger.Services
{
    /// <summary>
    /// Syntax-only data flow analysis for ExtractMethod.
    /// Determines which variables from the outer scope are used in a selection (parameters)
    /// and which are assigned in the selection and used after it (return value).
    /// </summary>
    public static class DataFlowHelper
    {
        public static DataFlowInfo Analyze(string sourceCode, int startLine, int endLine)
        {
            var result = new DataFlowInfo();

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode);
                var root = tree.GetCompilationUnitRoot();

                // Find the method containing the selection
                var containingMethod = FindContainingMethod(root, startLine);
                if (containingMethod == null)
                {
                    result.FallbackReason = "Selection is not inside a method body.";
                    return result;
                }

                // Get the line span of the containing method's body
                var methodBody = containingMethod.Body;
                if (methodBody == null)
                {
                    // Expression-bodied method
                    result.FallbackReason = "Method uses expression body, not a block body.";
                    return result;
                }

                // Collect method parameters
                var methodParams = containingMethod.ParameterList.Parameters
                    .Select(p => new DeclaredVariable
                    {
                        Name = p.Identifier.Text,
                        Type = p.Type?.ToString() ?? "object",
                        Line = GetLineNumber(p, tree)
                    })
                    .ToList();

                // Collect all local declarations in the method body
                var allLocals = CollectLocalDeclarations(methodBody, tree);

                // Locals declared BEFORE the selection
                var localsBefore = allLocals.Where(l => l.Line < startLine).ToList();

                // Locals declared INSIDE the selection
                var localsInside = allLocals.Where(l => l.Line >= startLine && l.Line <= endLine).ToList();
                var insideNames = new HashSet<string>(localsInside.Select(l => l.Name));

                // All variables available from outer scope = method params + locals declared before selection
                var outerScope = new Dictionary<string, DeclaredVariable>(StringComparer.Ordinal);
                foreach (var p in methodParams)
                    outerScope[p.Name] = p;
                foreach (var l in localsBefore)
                    outerScope[l.Name] = l;

                // Find identifiers USED inside the selection
                var selectionNodes = GetNodesInLineRange(methodBody, tree, startLine, endLine);
                var identifiersInSelection = selectionNodes
                    .SelectMany(n => n.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    .Select(id => id.Identifier.Text)
                    .ToHashSet(StringComparer.Ordinal);

                // Parameters = outer scope variables referenced in the selection
                // Exclude variables also declared inside the selection (shadowing)
                var parameters = new List<(string Type, string Name)>();
                foreach (var id in identifiersInSelection)
                {
                    if (insideNames.Contains(id))
                        continue;
                    if (outerScope.TryGetValue(id, out var decl))
                        parameters.Add((decl.Type, decl.Name));
                }

                // Deduplicate (same variable referenced multiple times)
                result.Parameters = parameters.Distinct().ToList();

                // Find variables ASSIGNED inside the selection
                var assignedInside = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in selectionNodes)
                {
                    foreach (var assignment in node.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
                    {
                        if (assignment.Left is IdentifierNameSyntax leftId)
                            assignedInside.Add(leftId.Identifier.Text);
                    }

                    // Also catch increment/decrement (i++, --count)
                    foreach (var postfix in node.DescendantNodesAndSelf().OfType<PostfixUnaryExpressionSyntax>())
                    {
                        if (postfix.Operand is IdentifierNameSyntax opId)
                            assignedInside.Add(opId.Identifier.Text);
                    }
                    foreach (var prefix in node.DescendantNodesAndSelf().OfType<PrefixUnaryExpressionSyntax>())
                    {
                        if (prefix.Operand is IdentifierNameSyntax opId)
                            assignedInside.Add(opId.Identifier.Text);
                    }
                }

                // Also treat local declarations inside the selection as "assigned"
                foreach (var local in localsInside)
                    assignedInside.Add(local.Name);

                // Find identifiers used AFTER the selection
                var nodesAfter = GetNodesInLineRange(methodBody, tree, endLine + 1, int.MaxValue);
                var identifiersAfter = nodesAfter
                    .SelectMany(n => n.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    .Select(id => id.Identifier.Text)
                    .ToHashSet(StringComparer.Ordinal);

                // Return value = assigned inside AND used after, from outer scope or declared inside
                var returnCandidates = assignedInside
                    .Where(name => identifiersAfter.Contains(name))
                    .ToList();

                if (returnCandidates.Count == 1)
                {
                    var name = returnCandidates[0];
                    // Find its type: check locals inside first, then outer scope
                    var localDecl = localsInside.FirstOrDefault(l => l.Name == name);
                    var type = localDecl?.Type ?? (outerScope.TryGetValue(name, out var outer) ? outer.Type : "var");
                    result.ReturnValue = (type, name);

                    // If it's from outer scope and assigned inside, it's both a parameter and return value.
                    // Remove it from parameters since we'll return it instead.
                    result.Parameters = result.Parameters.Where(p => p.Name != name).ToList();
                }
                else if (returnCandidates.Count > 1)
                {
                    result.FallbackReason = $"Multiple variables assigned inside and used after selection: {string.Join(", ", returnCandidates)}. Cannot infer a single return value.";
                    // Still keep the parameters - they're useful even if return type falls back to void
                }

                result.AnalysisSucceeded = true;
            }
            catch (Exception ex)
            {
                result.FallbackReason = $"Analysis error: {ex.Message}";
            }

            return result;
        }

        private static MethodDeclarationSyntax? FindContainingMethod(CompilationUnitSyntax root, int startLine)
        {
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var span = method.GetLocation().GetLineSpan();
                int methodStart = span.StartLinePosition.Line + 1;
                int methodEnd = span.EndLinePosition.Line + 1;

                if (startLine >= methodStart && startLine <= methodEnd)
                    return method;
            }
            return null;
        }

        private static List<DeclaredVariable> CollectLocalDeclarations(BlockSyntax body, SyntaxTree tree)
        {
            var locals = new List<DeclaredVariable>();

            foreach (var decl in body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            {
                var type = decl.Declaration.Type.ToString();
                foreach (var variable in decl.Declaration.Variables)
                {
                    locals.Add(new DeclaredVariable
                    {
                        Name = variable.Identifier.Text,
                        Type = type,
                        Line = GetLineNumber(variable, tree)
                    });
                }
            }

            // Also collect variables from for/foreach/using statements
            foreach (var forEach in body.DescendantNodes().OfType<ForEachStatementSyntax>())
            {
                locals.Add(new DeclaredVariable
                {
                    Name = forEach.Identifier.Text,
                    Type = forEach.Type.ToString(),
                    Line = GetLineNumber(forEach, tree)
                });
            }

            return locals;
        }

        private static List<SyntaxNode> GetNodesInLineRange(BlockSyntax body, SyntaxTree tree, int startLine, int endLine)
        {
            var nodes = new List<SyntaxNode>();
            foreach (var statement in body.Statements)
            {
                var span = statement.GetLocation().GetLineSpan();
                int stmtStart = span.StartLinePosition.Line + 1;
                int stmtEnd = span.EndLinePosition.Line + 1;

                if (stmtEnd >= startLine && stmtStart <= endLine)
                    nodes.Add(statement);
            }
            return nodes;
        }

        private static int GetLineNumber(SyntaxNode node, SyntaxTree tree)
        {
            return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }

        private static int GetLineNumber(SyntaxToken token, SyntaxTree tree)
        {
            return token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }

        private class DeclaredVariable
        {
            public string Name { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public int Line { get; set; }
        }
    }

    public class DataFlowInfo
    {
        public bool AnalysisSucceeded { get; set; }
        public List<(string Type, string Name)> Parameters { get; set; } = new();
        public (string Type, string Name)? ReturnValue { get; set; }
        public string? FallbackReason { get; set; }
    }
}
