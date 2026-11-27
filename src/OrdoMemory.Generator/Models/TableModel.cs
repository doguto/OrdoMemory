using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OrdoMemory.Generator.Models
{
    sealed class TableModel
    {
        public TableModel(
            string namespaceName,
            string rowName,
            string rowFullName,
            string tableName,
            string accessibility,
            bool usesUInt64PrimaryKey,
            int slotBits,
            string primaryKeyField,
            IReadOnlyList<FieldModel> fields,
            IReadOnlyList<KeyModel> keys,
            bool generateConstructor,
            bool hasMutableField
        )
        {
            NamespaceName = namespaceName;
            RowName = rowName;
            RowFullName = rowFullName;
            TableName = tableName;
            Accessibility = accessibility;
            PrimaryKeyType = PrimaryKeyTypeOf(usesUInt64PrimaryKey);
            PrimaryKeyTraits = usesUInt64PrimaryKey ? "global::OrdoMemory.UInt64PrimaryKey" : "global::OrdoMemory.UInt32PrimaryKey";
            SlotBits = slotBits;
            PrimaryKeyField = primaryKeyField;
            Fields = fields;
            Keys = keys;
            GenerateConstructor = generateConstructor;
            HasMutableField = hasMutableField;
        }

        public string NamespaceName { get; }
        public string RowName { get; }
        public string RowFullName { get; }
        public string TableName { get; }
        public string Accessibility { get; }
        public string PrimaryKeyType { get; }
        public string PrimaryKeyTraits { get; }
        public int SlotBits { get; }
        public string PrimaryKeyField { get; }
        public IReadOnlyList<FieldModel> Fields { get; }
        public IReadOnlyList<KeyModel> Keys { get; }
        public bool GenerateConstructor { get; }
        public bool HasMutableField { get; }

        static string PrimaryKeyTypeOf(bool usesUInt64PrimaryKey) => usesUInt64PrimaryKey ? "ulong" : "uint";

        public static TableModel Create(INamedTypeSymbol type, AttributeData tableAttribute, INamedTypeSymbol primaryKeyAttributeType, INamedTypeSymbol keyAttributeType, GeneratorExecutionContext context)
        {
            var location = type.Locations.FirstOrDefault();

            // ネスト型と Generics 型を Row にすることを禁止する
            if (type.ContainingType != null || type.IsGenericType)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedDeclaration, location, type.Name));
                return null;
            }

            // partial であることを確認する. readonly struct でなくてもよい (キー以外のフィールドを UpdateRef で書き換えられるようにするため)
            var isPartial = type.DeclaringSyntaxReferences.All(reference =>
                reference.GetSyntax() is StructDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
            );
            if (!isPartial)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MustBePartial, location, type.Name));
                return null;
            }

            // readonly struct でない場合、readonly でないメンバーを ref readonly / in 越しに呼ぶと隠れたコピーが起きるため警告する
            if (!type.IsReadOnly) ReportNonReadOnlyMembers(type, context);

            var isValid = true;
            var tableName = type.Name + "Table";
            var usesUInt64PrimaryKey = false;
            var slotBits = 0;
            foreach (var argument in tableAttribute.NamedArguments)
            {
                switch (argument.Key)
                {
                    case "TableName":
                        if (argument.Value.Value is string name && name.Length > 0) tableName = name;
                        break;
                    case "PrimaryKey":
                        usesUInt64PrimaryKey = argument.Value.Value is int primaryKeyKind && primaryKeyKind == 1;
                        break;
                    case "SlotBits":
                        slotBits = argument.Value.Value is int bits ? bits : 0;
                        break;
                }
            }

            var minimumSlotBits = usesUInt64PrimaryKey ? 32 : 1;
            var maximumSlotBits = usesUInt64PrimaryKey ? 63 : 31;
            if (slotBits == 0) slotBits = usesUInt64PrimaryKey ? 32 : 22;
            if (slotBits < minimumSlotBits || slotBits > maximumSlotBits)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.InvalidSlotBits, location, slotBits, PrimaryKeyTypeOf(usesUInt64PrimaryKey), $"{minimumSlotBits}..{maximumSlotBits}"));
                isValid = false;
            }

            string primaryKeyField = null;
            var primaryKeyCount = 0;
            var hasMutableField = false;
            var fields = new List<FieldModel>();
            var keys = new List<KeyModel>();
            foreach (var member in type.GetMembers())
            {
                if (!(member is IFieldSymbol field) || field.IsStatic || field.IsConst) continue;

                if (field.AssociatedSymbol is IPropertySymbol property)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AutoPropertyNotSupported, property.Locations.FirstOrDefault(), property.Name));
                    isValid = false;
                    continue;
                }

                if (field.IsImplicitlyDeclared) continue;

                if (field.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public && field.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Internal)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.InaccessibleField, field.Locations.FirstOrDefault(), field.Name));
                    isValid = false;
                    continue;
                }

                var keyAttribute = field.GetAttributes().FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, keyAttributeType));

                var isPrimaryKey = field.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, primaryKeyAttributeType));

                // PK と SecondaryKey は索引・スロット表と食い違わないよう、Update 以外で書き換えられないようにする
                if ((isPrimaryKey || keyAttribute != null) && !field.IsReadOnly)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.KeyFieldMustBeReadOnly, field.Locations.FirstOrDefault(), field.Name));
                    isValid = false;
                }

                if (!field.IsReadOnly) hasMutableField = true;

                if (isPrimaryKey)
                {
                    primaryKeyCount++;
                    primaryKeyField = field.Name;

                    var primaryKeySpecialType = usesUInt64PrimaryKey ? SpecialType.System_UInt64 : SpecialType.System_UInt32;
                    if (field.Type.SpecialType != primaryKeySpecialType)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.PrimaryKeyType, field.Locations.FirstOrDefault(), field.Name, PrimaryKeyTypeOf(usesUInt64PrimaryKey)));
                        isValid = false;
                    }

                    if (keyAttribute != null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.PrimaryKeyIsSecondaryKey, field.Locations.FirstOrDefault(), field.Name));
                        isValid = false;
                    }

                    continue;
                }

                var fieldModel = new FieldModel(field.Name, field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                fields.Add(fieldModel);

                if (keyAttribute == null) continue;

                var hash = keyAttribute.ConstructorArguments.Length > 0 && keyAttribute.ConstructorArguments[0].Value is int indexKind && indexKind == 1;
                var unique = true;
                foreach (var argument in keyAttribute.NamedArguments)
                {
                    if (argument.Key == "Unique" && argument.Value.Value is bool value) unique = value;
                }

                if (!TryMapKeyType(field.Type, out var mapping))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedKeyType, field.Locations.FirstOrDefault(), field.Name, field.Type.ToDisplayString()));
                    isValid = false;
                    continue;
                }

                keys.Add(new KeyModel(fieldModel, hash, unique, mapping));
            }

            if (primaryKeyCount != 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.PrimaryKeyCount, location, type.Name));
                isValid = false;
            }

            if (fields.Count == 0 && isValid)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoFields, location, type.Name));
                isValid = false;
            }

            if (!isValid) return null;

            var generateConstructor = !type.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == fields.Count &&
                constructor.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                           .SequenceEqual(fields.Select(fieldModel => fieldModel.Type)));

            return new TableModel(
                type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                type.Name,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                tableName,
                type.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public ? "public" : "internal",
                usesUInt64PrimaryKey,
                slotBits,
                primaryKeyField,
                fields,
                keys,
                generateConstructor,
                hasMutableField);
        }

        static void ReportNonReadOnlyMembers(INamedTypeSymbol type, GeneratorExecutionContext context)
        {
            foreach (var member in type.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared) continue;

                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary && !method.IsReadOnly:
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MemberShouldBeReadOnly, method.Locations.FirstOrDefault(), type.Name, method.Name));
                        break;
                    case IPropertySymbol property when property.GetMethod != null && !property.GetMethod.IsReadOnly:
                        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MemberShouldBeReadOnly, property.Locations.FirstOrDefault(), type.Name, property.Name));
                        break;
                }
            }
        }

        static bool TryMapKeyType(ITypeSymbol type, out KeyTypeMapping mapping)
        {
            var specialType = type.SpecialType;
            var needsCast = true;
            if (type is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Enum)
            {
                specialType = namedType.EnumUnderlyingType.SpecialType;
            }
            else if (specialType == SpecialType.System_Boolean)
            {
                mapping = new KeyTypeMapping("int", "Int32Key", "({0} ? 1 : 0)");
                return true;
            }
            else
            {
                needsCast = false;
            }

            switch (specialType)
            {
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Char:
                case SpecialType.System_Int32:
                    mapping = new KeyTypeMapping("int", "Int32Key", specialType == SpecialType.System_Int32 && !needsCast ? "{0}" : "(int){0}");
                    return true;
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                    mapping = new KeyTypeMapping("long", "Int64Key", specialType == SpecialType.System_Int64 && !needsCast ? "{0}" : "(long){0}");
                    return true;
                case SpecialType.System_UInt64:
                    mapping = new KeyTypeMapping("ulong", "UInt64Key", needsCast ? "(ulong){0}" : "{0}");
                    return true;
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                    mapping = new KeyTypeMapping("double", "DoubleKey", "global::OrdoMemory.KeyConvert.Double({0})");
                    return true;
                case SpecialType.System_String:
                    mapping = new KeyTypeMapping("string", "StringKey", "{0}", isReference: true);
                    return true;
                default:
                    mapping = default;
                    return false;
            }
        }
    }
}
