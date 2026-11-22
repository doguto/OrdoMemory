using System;
using System.Collections.Generic;
using System.Linq;

namespace OrdoMemory.Generator
{
    internal static class TemplateFunctions
    {
        public static string Render(Template template)
        {
            return string.Join("\n", template.ToLines());
        }

        public static string Each<T>(IEnumerable<T> items, Func<T, string> render, string separator = "\n")
        {
            return string.Join(separator, items.Select(render));
        }

        public static string If(bool condition, string text)
        {
            return condition ? text : "";
        }
    }
}
