using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OrdoMemory.Generator
{
    sealed class FieldModel
    {
        public string name;
        public string type;
        public string parameterName;
    }

    sealed class KeyModel
    {
        public FieldModel field;
        public bool hash;
        public bool unique;

        public string keyType;
        public string keyTraits;
        public string convert;
        public bool keyIsReference;

        public string IndexField => "__" + char.ToLowerInvariant(@field.name[0]) + @field.name.Substring(1) + "Index";
    }

    sealed class TableModel
    {
        public string namespaceName;
        public string rowName;
        public string rowFullName;
        public string tableName;
        public string accessibility;
        public string primaryKeyType;
        public string primaryKeyTraits;
        public int slotBits;
        public bool generateConstructor;
        public readonly List<FieldModel> fields = new();
        public readonly List<KeyModel> keys = new();

        public static TableModel Create(INamedTypeSymbol type, AttributeData tableAttribute, INamedTypeSymbol keyAttributeType, GeneratorExecutionContext context)
        {
            var location = type.Locations.FirstOrDefault();
            var isValid = true;

            if (type.ContainingType != null || type.IsGenericType)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedDeclaration, location, type.Name));
                return null;
            }

            var isPartial = type.DeclaringSyntaxReferences.All(reference =>
                reference.GetSyntax() is StructDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
            );
            if (!type.IsReadOnly || !isPartial)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MustBeReadOnlyPartial, location, type.Name));
                return null;
            }

            var model = new TableModel
            {
                namespaceName = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                rowName = type.Name,
                rowFullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                tableName = type.Name + "Table",
                accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal"
            };

            var usesUInt64PrimaryKey = false;
            var slotBits = 0;
            foreach (var argument in tableAttribute.NamedArguments)
            {
                switch (argument.Key)
                {
                    case "TableName":
                        if (argument.Value.Value is string name && name.Length > 0) model.tableName = name;
                        break;
                    case "PrimaryKey":
                        usesUInt64PrimaryKey = argument.Value.Value is int primaryKeyKind && primaryKeyKind == 1;
                        break;
                    case "SlotBits":
                        slotBits = argument.Value.Value is int bits ? bits : 0;
                        break;
                }
            }

            model.primaryKeyType = usesUInt64PrimaryKey ? "ulong" : "uint";
            model.primaryKeyTraits = usesUInt64PrimaryKey ? "global::OrdoMemory.UInt64PrimaryKey" : "global::OrdoMemory.UInt32PrimaryKey";

            var minimumSlotBits = usesUInt64PrimaryKey ? 32 : 1;
            var maximumSlotBits = usesUInt64PrimaryKey ? 63 : 31;
            if (slotBits == 0) slotBits = usesUInt64PrimaryKey ? 32 : 22;
            if (slotBits < minimumSlotBits || slotBits > maximumSlotBits)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.InvalidSlotBits, location, slotBits, model.primaryKeyType, $"{minimumSlotBits}..{maximumSlotBits}"));
                isValid = false;
            }

            model.slotBits = slotBits;

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

                if (field.DeclaredAccessibility != Accessibility.Public && field.DeclaredAccessibility != Accessibility.Internal)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.InaccessibleField, field.Locations.FirstOrDefault(), field.Name));
                    isValid = false;
                    continue;
                }

                var fieldModel = new FieldModel
                {
                    name = field.Name,
                    type = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    parameterName = "@" + char.ToLowerInvariant(field.Name[0]) + field.Name.Substring(1)
                };
                // 生成する Update の引数 primaryKey と衝突させない
                if (fieldModel.parameterName == "@primaryKey") fieldModel.parameterName = "@primaryKeyValue";
                model.fields.Add(fieldModel);

                var keyAttribute = field.GetAttributes().FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, keyAttributeType));
                if (keyAttribute == null) continue;

                var key = new KeyModel { field = fieldModel, unique = true };
                if (keyAttribute.ConstructorArguments.Length > 0 && keyAttribute.ConstructorArguments[0].Value is int indexKind) key.hash = indexKind == 1;
                foreach (var argument in keyAttribute.NamedArguments)
                {
                    if (argument.Key == "Unique" && argument.Value.Value is bool unique) key.unique = unique;
                }

                if (!TryMapKeyType(field.Type, key))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedKeyType, field.Locations.FirstOrDefault(), field.Name, field.Type.ToDisplayString()));
                    isValid = false;
                    continue;
                }

                model.keys.Add(key);
            }

            if (model.fields.Count == 0 && isValid)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoFields, location, type.Name));
                isValid = false;
            }

            if (!isValid) return null;

            model.generateConstructor = !type.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == model.fields.Count &&
                constructor.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                           .SequenceEqual(model.fields.Select(fieldModel => fieldModel.type)));
            return model;
        }

        static bool TryMapKeyType(ITypeSymbol type, KeyModel key)
        {
            var specialType = type.SpecialType;
            var needsCast = true;
            if (type is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Enum)
            {
                specialType = namedType.EnumUnderlyingType.SpecialType;
            }
            else if (specialType == SpecialType.System_Boolean)
            {
                SetKeyMapping(key, "int", "Int32Key", "({0} ? 1 : 0)");
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
                    SetKeyMapping(key, "int", "Int32Key", specialType == SpecialType.System_Int32 && !needsCast ? "{0}" : "(int){0}");
                    return true;
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                    SetKeyMapping(key, "long", "Int64Key", specialType == SpecialType.System_Int64 && !needsCast ? "{0}" : "(long){0}");
                    return true;
                case SpecialType.System_UInt64:
                    SetKeyMapping(key, "ulong", "UInt64Key", needsCast ? "(ulong){0}" : "{0}");
                    return true;
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                    SetKeyMapping(key, "double", "DoubleKey", "global::OrdoMemory.KeyConvert.Double({0})");
                    return true;
                case SpecialType.System_String:
                    SetKeyMapping(key, "string", "StringKey", "{0}");
                    key.keyIsReference = true;
                    return true;
                default:
                    return false;
            }
        }

        static void SetKeyMapping(KeyModel key, string keyType, string traits, string convert)
        {
            key.keyType = keyType;
            key.keyTraits = "global::OrdoMemory." + traits;
            key.convert = convert;
        }
    }
}
