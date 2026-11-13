using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OrdoMemory.Generator
{
    sealed class FieldModel
    {
        public string Name;
        public string Type;
        public string ParameterName;
    }

    sealed class KeyModel
    {
        public FieldModel Field;
        public bool Hash;
        public bool Unique;

        // 索引に渡すキーの型と、フィールドの値からの変換式の書式 ({0} に値の式が入る).
        public string KeyType;
        public string KeyTraits;
        public string Convert;
        public bool KeyIsReference;

        public string IndexField => "__" + char.ToLowerInvariant(Field.Name[0]) + Field.Name.Substring(1) + "Index";
    }

    sealed class TableModel
    {
        public string Namespace;
        public string RowName;
        public string RowFullName;
        public string TableName;
        public string Accessibility;
        public string PkType;
        public string PkTraits;
        public int SlotBits;
        public bool GenerateConstructor;
        public List<FieldModel> Fields = new List<FieldModel>();
        public List<KeyModel> Keys = new List<KeyModel>();

        public static TableModel Create(INamedTypeSymbol type, AttributeData tableAttr, INamedTypeSymbol keyAttrType, GeneratorExecutionContext context)
        {
            var location = type.Locations.FirstOrDefault();
            var ok = true;

            if (type.ContainingType != null || type.IsGenericType)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedDeclaration, location, type.Name));
                return null;
            }

            var isPartial = type.DeclaringSyntaxReferences.All(r =>
                r.GetSyntax() is StructDeclarationSyntax s && s.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!type.IsReadOnly || !isPartial)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MustBeReadOnlyPartial, location, type.Name));
                return null;
            }

            var model = new TableModel
            {
                Namespace = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                RowName = type.Name,
                RowFullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TableName = type.Name + "Table",
                Accessibility = type.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public ? "public" : "internal",
            };

            var pk64 = false;
            var slotBits = 0;
            foreach (var arg in tableAttr.NamedArguments)
            {
                switch (arg.Key)
                {
                    case "TableName":
                        if (arg.Value.Value is string name && name.Length > 0) model.TableName = name;
                        break;
                    case "Pk":
                        pk64 = arg.Value.Value is int pk && pk == 1;
                        break;
                    case "SlotBits":
                        slotBits = arg.Value.Value is int bits ? bits : 0;
                        break;
                }
            }

            model.PkType = pk64 ? "ulong" : "uint";
            model.PkTraits = pk64 ? "global::OrdoMemory.UInt64Pk" : "global::OrdoMemory.UInt32Pk";

            // 世代は uint で持つため 32bit 以下. 世代とスロットはどちらも 1bit 以上.
            var minBits = pk64 ? 32 : 1;
            var maxBits = pk64 ? 63 : 31;
            if (slotBits == 0) slotBits = pk64 ? 32 : 22;
            if (slotBits < minBits || slotBits > maxBits)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.InvalidSlotBits, location, slotBits, model.PkType, $"{minBits}..{maxBits}"));
                ok = false;
            }

            model.SlotBits = slotBits;

            foreach (var member in type.GetMembers())
            {
                if (!(member is IFieldSymbol field) || field.IsStatic || field.IsConst) continue;

                if (field.AssociatedSymbol is IPropertySymbol property)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.AutoPropertyNotSupported, property.Locations.FirstOrDefault(), property.Name));
                    ok = false;
                    continue;
                }

                if (field.IsImplicitlyDeclared) continue;

                if (field.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public && field.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Internal)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.InaccessibleField, field.Locations.FirstOrDefault(), field.Name));
                    ok = false;
                    continue;
                }

                var fieldModel = new FieldModel
                {
                    Name = field.Name,
                    Type = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ParameterName = "@" + char.ToLowerInvariant(field.Name[0]) + field.Name.Substring(1),
                };
                // Update の第 1 引数 pk との衝突を避ける.
                if (fieldModel.ParameterName == "@pk") fieldModel.ParameterName = "@pkValue";
                model.Fields.Add(fieldModel);

                var keyAttr = field.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, keyAttrType));
                if (keyAttr == null) continue;

                var key = new KeyModel { Field = fieldModel, Unique = true };
                if (keyAttr.ConstructorArguments.Length > 0 && keyAttr.ConstructorArguments[0].Value is int kind) key.Hash = kind == 1;
                foreach (var arg in keyAttr.NamedArguments)
                {
                    if (arg.Key == "Unique" && arg.Value.Value is bool unique) key.Unique = unique;
                }

                if (!TryMapKeyType(field.Type, key))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedKeyType, field.Locations.FirstOrDefault(), field.Name, field.Type.ToDisplayString()));
                    ok = false;
                    continue;
                }

                model.Keys.Add(key);
            }

            if (model.Fields.Count == 0 && ok)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoFields, location, type.Name));
                ok = false;
            }

            if (!ok) return null;

            model.GenerateConstructor = !type.InstanceConstructors.Any(c =>
                c.Parameters.Length == model.Fields.Count &&
                c.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .SequenceEqual(model.Fields.Select(f => f.Type)));
            return model;
        }

        // 整数型は int / long / ulong にまとめ、Ordered 索引で正規化キーを PK と詰められるようにする.
        static bool TryMapKeyType(ITypeSymbol type, KeyModel key)
        {
            var special = type.SpecialType;
            var cast = true;
            if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
            {
                special = named.EnumUnderlyingType.SpecialType;
            }
            else if (special == SpecialType.System_Boolean)
            {
                Set(key, "int", "Int32Key", "({0} ? 1 : 0)");
                return true;
            }
            else
            {
                cast = false;
            }

            switch (special)
            {
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Char:
                case SpecialType.System_Int32:
                    Set(key, "int", "Int32Key", special == SpecialType.System_Int32 && !cast ? "{0}" : "(int){0}");
                    return true;
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                    Set(key, "long", "Int64Key", special == SpecialType.System_Int64 && !cast ? "{0}" : "(long){0}");
                    return true;
                case SpecialType.System_UInt64:
                    Set(key, "ulong", "UInt64Key", cast ? "(ulong){0}" : "{0}");
                    return true;
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                    Set(key, "double", "DoubleKey", "global::OrdoMemory.KeyConvert.Double({0})");
                    return true;
                case SpecialType.System_String:
                    Set(key, "string", "StringKey", "{0}");
                    key.KeyIsReference = true;
                    return true;
                default:
                    return false;
            }
        }

        static void Set(KeyModel key, string keyType, string traits, string convert)
        {
            key.KeyType = keyType;
            key.KeyTraits = "global::OrdoMemory." + traits;
            key.Convert = convert;
        }
    }
}
