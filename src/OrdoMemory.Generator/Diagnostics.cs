using Microsoft.CodeAnalysis;

namespace OrdoMemory.Generator
{
    static class Diagnostics
    {
        const string Category = "OrdoMemory";

        public static readonly DiagnosticDescriptor MustBeReadOnlyPartial = new DiagnosticDescriptor(
            "OM001", "RuntimeTable must be a readonly partial struct",
            "'{0}' must be declared as 'readonly partial struct'",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor UnsupportedDeclaration = new DiagnosticDescriptor(
            "OM002", "Unsupported RuntimeTable declaration",
            "'{0}' must be a non-generic top-level struct",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor UnsupportedKeyType = new DiagnosticDescriptor(
            "OM003", "Unsupported SecondaryKey type",
            "SecondaryKey '{0}' has unsupported type '{1}'. Supported: integral types, enum, bool, char, float, double, string.",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor InvalidSlotBits = new DiagnosticDescriptor(
            "OM004", "Invalid SlotBits",
            "SlotBits {0} is invalid for {1}. Valid range: {2}.",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor AutoPropertyNotSupported = new DiagnosticDescriptor(
            "OM005", "Auto-property is not supported",
            "'{0}' is an auto-property. Use a readonly field instead.",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor InaccessibleField = new DiagnosticDescriptor(
            "OM006", "Field is not accessible from the table",
            "Field '{0}' must be public or internal",
            Category, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor NoFields = new DiagnosticDescriptor(
            "OM007", "RuntimeTable has no fields",
            "'{0}' must have at least one instance field",
            Category, DiagnosticSeverity.Error, true);
    }
}
