using Microsoft.CodeAnalysis;

namespace OrdoMemory.Generator
{
    internal static class Diagnostics
    {
        const string Category = "OrdoMemory";

        public static readonly DiagnosticDescriptor MustBeReadOnlyPartial = new(
            "OM001", "OrdoTable must be a readonly partial struct",
            "'{0}' must be declared as 'readonly partial struct'",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor UnsupportedDeclaration = new(
            "OM002", "Unsupported OrdoTable declaration",
            "'{0}' must be a non-generic top-level struct",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor UnsupportedKeyType = new(
            "OM003", "Unsupported SecondaryKey type",
            "SecondaryKey '{0}' has unsupported type '{1}'. Supported: integral types, enum, bool, char, float, double, string.",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor InvalidSlotBits = new(
            "OM004", "Invalid SlotBits",
            "SlotBits {0} is invalid for {1}. Valid range: {2}.",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor AutoPropertyNotSupported = new(
            "OM005", "Auto-property is not supported",
            "'{0}' is an auto-property. Use a readonly field instead.",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor InaccessibleField = new(
            "OM006", "Field is not accessible from the table",
            "Field '{0}' must be public or internal",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor NoFields = new(
            "OM007", "OrdoTable has no fields",
            "'{0}' must have at least one instance field",
            Category, DiagnosticSeverity.Error, true);
    }
}
