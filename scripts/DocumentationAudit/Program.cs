using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

// Audit named routines using SDK compiler assemblies, without runtime dependencies.
var root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
var count = 0;
var missing = new List<string>();
foreach (var folder in new[] { "src", "tests" })
foreach (var path in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")))
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
    foreach (var node in tree.GetRoot().DescendantNodes())
    {
        var name = node switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            LocalFunctionStatementSyntax l => l.Identifier.Text,
            ClassDeclarationSyntax c when c.ParameterList != null => c.Identifier.Text,
            RecordDeclarationSyntax r when r.ParameterList != null => r.Identifier.Text,
            _ => null
        };
        if (name == null) continue;
        count++;
        var trivia = node.GetLeadingTrivia().ToFullString();
        var xml = Regex.Match(trivia, @"<summary>\s*(.*?)\s*</summary>", RegexOptions.Singleline);
        var local = Regex.Match(trivia, @"// Summary:\s*(\S.+)");
        if ((!xml.Success || string.IsNullOrWhiteSpace(xml.Groups[1].Value)) && !local.Success)
            missing.Add($"{Path.GetRelativePath(root, path)}:{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {name}");
    }
}
foreach (var item in missing) Console.Error.WriteLine(item);
Console.WriteLine($"{count} named routines/primary constructors; {missing.Count} missing summaries.");
return missing.Count == 0 ? 0 : 1;
