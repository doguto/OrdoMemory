using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using OrdoMemory.Generator.Emitting;
using OrdoMemory.Generator.Models;

namespace OrdoMemory.Generator
{
    [Generator]
    public sealed class OrdoTableGenerator : ISourceGenerator
    {
        const string TableAttributeName = "OrdoMemory.OrdoTableAttribute";
        const string PrimaryKeyAttributeName = "OrdoMemory.PrimaryKeyAttribute";
        const string KeyAttributeName = "OrdoMemory.SecondaryKeyAttribute";

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new Receiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (!(context.SyntaxReceiver is Receiver receiver)) return;

            var compilation = context.Compilation;
            var tableAttributeType = compilation.GetTypeByMetadataName(TableAttributeName);
            var primaryKeyAttributeType = compilation.GetTypeByMetadataName(PrimaryKeyAttributeName);
            var keyAttributeType = compilation.GetTypeByMetadataName(KeyAttributeName);
            if (tableAttributeType == null || primaryKeyAttributeType == null || keyAttributeType == null) return;

            var done = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var hints = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in receiver.Candidates)
            {
                var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
                if (!(semanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is INamedTypeSymbol type)) continue;

                var tableAttribute = type.GetAttributes().FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, tableAttributeType));
                if (tableAttribute == null || !done.Add(type)) continue;

                var model = TableModel.Create(type, tableAttribute, primaryKeyAttributeType, keyAttributeType, context);
                if (model == null) continue;

                var hint = HintNameOf(type, hints);
                context.AddSource(hint, SourceText.From(TableEmitter.Emit(model), Encoding.UTF8));
            }
        }

        static string HintNameOf(INamedTypeSymbol type, HashSet<string> used)
        {
            var name = type.ToDisplayString().Replace("_", "__").Replace('.', '_');
            var hint = name + ".OrdoTable.g.cs";
            for (var i = 2; !used.Add(hint); i++)
            {
                hint = name + "." + i + ".OrdoTable.g.cs";
            }

            return hint;
        }

        sealed class Receiver : ISyntaxReceiver
        {
            public readonly List<StructDeclarationSyntax> Candidates = new();

            public void OnVisitSyntaxNode(SyntaxNode node)
            {
                if (node is StructDeclarationSyntax structDeclaration && structDeclaration.AttributeLists.Count > 0) Candidates.Add(structDeclaration);
            }
        }
    }
}
