using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace OrdoMemory.Generator
{
    // Unity が対応する Microsoft.CodeAnalysis 3.8 で動かすため、IIncrementalGenerator ではなく ISourceGenerator を使う.
    [Generator]
    public sealed class RuntimeTableGenerator : ISourceGenerator
    {
        const string TableAttributeName = "OrdoMemory.RuntimeTableAttribute";
        const string KeyAttributeName = "OrdoMemory.SecondaryKeyAttribute";

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new Receiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (!(context.SyntaxReceiver is Receiver receiver)) return;

            var compilation = context.Compilation;
            var tableAttr = compilation.GetTypeByMetadataName(TableAttributeName);
            var keyAttr = compilation.GetTypeByMetadataName(KeyAttributeName);
            if (tableAttr == null || keyAttr == null) return;

            var done = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var decl in receiver.Candidates)
            {
                var semantic = compilation.GetSemanticModel(decl.SyntaxTree);
                if (!(semantic.GetDeclaredSymbol(decl, context.CancellationToken) is INamedTypeSymbol type)) continue;

                var attr = type.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, tableAttr));
                if (attr == null || !done.Add(type)) continue;

                var model = TableModel.Create(type, attr, keyAttr, context);
                if (model == null) continue;

                var hint = type.ToDisplayString().Replace('.', '_') + ".RuntimeTable.g.cs";
                context.AddSource(hint, SourceText.From(TableEmitter.Emit(model), Encoding.UTF8));
            }
        }

        sealed class Receiver : ISyntaxReceiver
        {
            public readonly List<StructDeclarationSyntax> Candidates = new List<StructDeclarationSyntax>();

            public void OnVisitSyntaxNode(SyntaxNode node)
            {
                if (node is StructDeclarationSyntax s && s.AttributeLists.Count > 0) Candidates.Add(s);
            }
        }
    }
}
