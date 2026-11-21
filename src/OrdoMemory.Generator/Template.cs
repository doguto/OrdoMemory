using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace OrdoMemory.Generator
{
    // 複数行の埋め込みを穴の位置のインデントに揃え、空の埋め込みだけの行は出力しない
    [InterpolatedStringHandler]
    sealed class Template
    {
        readonly List<string> lines = new List<string>();
        readonly StringBuilder currentLine = new StringBuilder();
        bool dropCurrentLineIfBlank;

        public Template(int literalLength, int formattedCount)
        {
        }

        public void AppendLiteral(string value)
        {
            var segments = value.Split('\n');
            currentLine.Append(segments[0].TrimEnd('\r'));
            for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
            {
                EndLine();
                currentLine.Append(segments[segmentIndex].TrimEnd('\r'));
            }
        }

        public void AppendFormatted<T>(T value)
        {
            var text = value?.ToString() ?? "";
            if (text.Length == 0)
            {
                if (IsBlank(currentLine)) dropCurrentLineIfBlank = true;
                return;
            }

            var indentation = LeadingWhitespace(currentLine);
            var segments = text.Split('\n');
            currentLine.Append(segments[0].TrimEnd('\r'));
            for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
            {
                EndLine();
                currentLine.Append(indentation).Append(segments[segmentIndex].TrimEnd('\r'));
            }
        }

        // 省略された埋め込みの前後に余る空行を、連続・先頭末尾・ブロック直後/直前に限って畳む
        public List<string> ToLines()
        {
            EndLine();
            var result = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    if (result.Count == 0) continue;
                    var previousLine = result[result.Count - 1];
                    if (previousLine.Length == 0 || previousLine.EndsWith("{")) continue;
                }
                else if (line.TrimStart().StartsWith("}"))
                {
                    RemoveTrailingBlankLines(result);
                }

                result.Add(line);
            }

            RemoveTrailingBlankLines(result);
            return result;
        }

        void EndLine()
        {
            var isBlank = IsBlank(currentLine);
            if (!(isBlank && dropCurrentLineIfBlank)) lines.Add(isBlank ? "" : currentLine.ToString());
            currentLine.Clear();
            dropCurrentLineIfBlank = false;
        }

        static void RemoveTrailingBlankLines(List<string> target)
        {
            while (target.Count > 0 && target[target.Count - 1].Length == 0) target.RemoveAt(target.Count - 1);
        }

        static bool IsBlank(StringBuilder builder)
        {
            for (var position = 0; position < builder.Length; position++)
            {
                if (!char.IsWhiteSpace(builder[position])) return false;
            }

            return true;
        }

        static string LeadingWhitespace(StringBuilder builder)
        {
            var length = 0;
            while (length < builder.Length && char.IsWhiteSpace(builder[length])) length++;
            return builder.ToString(0, length);
        }
    }
}
