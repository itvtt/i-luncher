using System;
using System.Linq;
using System.Text;

namespace FolderCompareWPF
{
    internal enum FileContentComparisonKind
    {
        Identical,
        TextDifferent,
        BinaryDifferent
    }

    internal sealed class FileContentComparison
    {
        public FileContentComparisonKind Kind { get; }

        private FileContentComparison(FileContentComparisonKind kind)
        {
            Kind = kind;
        }

        public static FileContentComparison Identical() =>
            new(FileContentComparisonKind.Identical);

        public static FileContentComparison TextDifferent() =>
            new(FileContentComparisonKind.TextDifferent);

        public static FileContentComparison BinaryDifferent() =>
            new(FileContentComparisonKind.BinaryDifferent);
    }

    internal sealed class DecodedTextFile
    {
        public string NormalizedText { get; private set; }
        public Encoding Encoding { get; }
        public string NewLine { get; }

        public DecodedTextFile(string normalizedText, Encoding encoding, string newLine)
        {
            NormalizedText = normalizedText;
            Encoding = encoding;
            NewLine = newLine;
        }

        public byte[] Encode(string normalizedText)
        {
            string normalized = FileContentComparer.NormalizeLineEndings(normalizedText);
            string text = NewLine == "\n" ? normalized : normalized.Replace("\n", NewLine);
            byte[] preamble = Encoding.GetPreamble();
            byte[] content = Encoding.GetBytes(text);
            byte[] result = new byte[preamble.Length + content.Length];
            Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            Buffer.BlockCopy(content, 0, result, preamble.Length, content.Length);
            return result;
        }

        public void SetNormalizedText(string text) =>
            NormalizedText = FileContentComparer.NormalizeLineEndings(text);
    }

    /// <summary>
    /// 파일의 실질적인 내용을 비교한다.
    /// 텍스트 파일은 BOM, 인코딩(UTF 계열/CP949), 줄바꿈 형식 차이를 내용 변경으로 보지 않는다.
    /// </summary>
    internal static class FileContentComparer
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        static FileContentComparer()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static FileContentComparison Compare(ReadOnlySpan<byte> bytesA, ReadOnlySpan<byte> bytesB, bool ignoreWhitespace = false)
        {
            if (bytesA.SequenceEqual(bytesB))
                return FileContentComparison.Identical();

            if (!TryReadText(bytesA, out DecodedTextFile textA) ||
                !TryReadText(bytesB, out DecodedTextFile textB))
            {
                return FileContentComparison.BinaryDifferent();
            }

            string normalizedA = textA.NormalizedText;
            string normalizedB = textB.NormalizedText;
            if (ignoreWhitespace)
            {
                return EqualsIgnoringWhitespace(normalizedA, normalizedB)
                    ? FileContentComparison.Identical() : FileContentComparison.TextDifferent();
            }

            if (string.Equals(normalizedA, normalizedB, StringComparison.Ordinal))
                return FileContentComparison.Identical();

            return FileContentComparison.TextDifferent();
        }

        internal static string RemoveWhitespace(string text) => new(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

        private static bool EqualsIgnoringWhitespace(string a, string b)
        {
            int i = 0, j = 0;
            while (true)
            {
                while (i < a.Length && char.IsWhiteSpace(a[i])) i++;
                while (j < b.Length && char.IsWhiteSpace(b[j])) j++;
                if (i == a.Length || j == b.Length) return i == a.Length && j == b.Length;
                if (a[i++] != b[j++]) return false;
            }
        }

        public static bool TryReadText(ReadOnlySpan<byte> bytes, out DecodedTextFile file)
        {
            file = null!;
            if (!TryDecodeText(bytes, out string text, out Encoding encoding))
                return false;

            file = new DecodedTextFile(
                NormalizeLineEndings(text),
                encoding,
                DetectNewLine(text));
            return true;
        }

        private static bool TryDecodeText(ReadOnlySpan<byte> bytes, out string text, out Encoding encoding)
        {
            text = string.Empty;
            encoding = StrictUtf8;

            if (bytes.Length == 0)
                return true;

            if (TryDecodeBomText(bytes, out text, out encoding))
                return IsLikelyText(text);

            // BOM 없는 UTF-16 텍스트는 한쪽 바이트 위치에 NUL이 집중되는 패턴으로 판별한다.
            if (TryDecodeBomlessUtf16(bytes, out text, out encoding))
                return IsLikelyText(text);

            // 일반 텍스트에서 NUL은 사용되지 않는다. 이 검사를 UTF-16 판별 뒤에 해야 한다.
            if (bytes.Contains((byte)0))
                return false;

            if (TryDecode(bytes, 0, bytes.Length, StrictUtf8, out text) && IsLikelyText(text))
            {
                encoding = StrictUtf8;
                return true;
            }

            Encoding cp949 = Encoding.GetEncoding(
                949,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);

            if (TryDecode(bytes, 0, bytes.Length, cp949, out text) && IsLikelyText(text))
            {
                encoding = cp949;
                return true;
            }

            return false;
        }

        private static bool TryDecodeBomText(ReadOnlySpan<byte> bytes, out string text, out Encoding encoding)
        {
            text = string.Empty;
            encoding = StrictUtf8;

            // UTF-32 BOM은 UTF-16 BOM과 앞부분이 겹치므로 먼저 검사한다.
            if (bytes.StartsWith<byte>([0xFF, 0xFE, 0x00, 0x00]))
            {
                encoding = new UTF32Encoding(false, true, true);
                return TryDecode(bytes, 4, bytes.Length - 4, encoding, out text);
            }

            if (bytes.StartsWith<byte>([0x00, 0x00, 0xFE, 0xFF]))
            {
                encoding = new UTF32Encoding(true, true, true);
                return TryDecode(bytes, 4, bytes.Length - 4, encoding, out text);
            }

            if (bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]))
            {
                encoding = new UTF8Encoding(true, true);
                return TryDecode(bytes, 3, bytes.Length - 3, encoding, out text);
            }

            if (bytes.StartsWith<byte>([0xFF, 0xFE]))
            {
                encoding = new UnicodeEncoding(false, true, true);
                return TryDecode(bytes, 2, bytes.Length - 2, encoding, out text);
            }

            if (bytes.StartsWith<byte>([0xFE, 0xFF]))
            {
                encoding = new UnicodeEncoding(true, true, true);
                return TryDecode(bytes, 2, bytes.Length - 2, encoding, out text);
            }

            return false;
        }

        private static bool TryDecodeBomlessUtf16(ReadOnlySpan<byte> bytes, out string text, out Encoding encoding)
        {
            text = string.Empty;
            encoding = StrictUtf8;
            if (bytes.Length < 4 || bytes.Length % 2 != 0)
                return false;

            int pairCount = bytes.Length / 2;
            int evenNulls = 0;
            int oddNulls = 0;

            for (int i = 0; i < bytes.Length; i += 2)
            {
                if (bytes[i] == 0) evenNulls++;
                if (bytes[i + 1] == 0) oddNulls++;
            }

            // ASCII 비중이 있는 일반적인 소스/설정 파일을 보수적으로 인식한다.
            bool looksLittleEndian = oddNulls * 10 >= pairCount * 3 && evenNulls * 10 <= pairCount * 2;
            bool looksBigEndian = evenNulls * 10 >= pairCount * 3 && oddNulls * 10 <= pairCount * 2;

            if (looksLittleEndian)
            {
                encoding = new UnicodeEncoding(false, false, true);
                return TryDecode(bytes, 0, bytes.Length, encoding, out text);
            }

            if (looksBigEndian)
            {
                encoding = new UnicodeEncoding(true, false, true);
                return TryDecode(bytes, 0, bytes.Length, encoding, out text);
            }

            return false;
        }

        private static bool TryDecode(
            ReadOnlySpan<byte> bytes,
            int index,
            int count,
            Encoding encoding,
            out string text)
        {
            try
            {
                text = encoding.GetString(bytes.Slice(index, count));
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                return false;
            }
        }

        private static bool IsLikelyText(string text)
        {
            foreach (char ch in text)
            {
                if (ch == '\uFFFD' || ch == '\0')
                    return false;

                if (char.IsControl(ch) && ch != '\r' && ch != '\n' && ch != '\t' && ch != '\f')
                    return false;
            }

            return true;
        }

        internal static string NormalizeLineEndings(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n');

        private static string DetectNewLine(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                    return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
                if (text[i] == '\n')
                    return "\n";
            }

            return Environment.NewLine;
        }

    }
}
