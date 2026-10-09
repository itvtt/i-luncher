using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace FolderCompareWPF
{
    public static class DiffDocumentBuilder
    {
        public const double DiffLineHeight = 20.0;

        public static void Build(List<MainWindow.DiffRow> rows,
    out FlowDocument docA, out FlowDocument docB,
    out string lineNumsA, out string lineNumsB,
    out List<Paragraph?> paragraphsA, out List<Paragraph?> paragraphsB)
        {
            docA = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.WhiteSmoke
            };
            docB = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.WhiteSmoke
            };

            paragraphsA = new List<Paragraph?>();
            paragraphsB = new List<Paragraph?>();

            var lnA = new StringBuilder();
            var lnB = new StringBuilder();

            for (int idx = 0; idx < rows.Count; idx++)
            {
                var row = rows[idx];

                Paragraph? paraA = null;
                Paragraph? paraB = null;

                // 비교 정렬용 빈 행은 편집 문서에 넣지 않는다. 그래야 저장할 때
                // 반대쪽 파일에만 존재하는 행이 실제 빈 줄로 기록되지 않는다.
                if (row.LineNoA.HasValue)
                {
                    paraA = CreateParagraph(GetBackground(row.BackgroundTypeA ?? row.Type), row.DisplayPiecesA);
                    docA.Blocks.Add(paraA);
                    if (lnA.Length > 0) lnA.Append('\n');
                    lnA.Append(row.LineNoA.Value);
                }

                if (row.LineNoB.HasValue)
                {
                    paraB = CreateParagraph(GetBackground(row.BackgroundTypeB ?? row.Type), row.DisplayPiecesB);
                    docB.Blocks.Add(paraB);
                    if (lnB.Length > 0) lnB.Append('\n');
                    lnB.Append(row.LineNoB.Value);
                }

                paragraphsA.Add(paraA);
                paragraphsB.Add(paraB);
            }

            // 빈 파일도 RichTextBox에서 바로 입력할 수 있도록 문단 하나를 둔다.
            if (docA.Blocks.Count == 0) docA.Blocks.Add(new Paragraph { Margin = new Thickness(0), LineHeight = DiffLineHeight });
            if (docB.Blocks.Count == 0) docB.Blocks.Add(new Paragraph { Margin = new Thickness(0), LineHeight = DiffLineHeight });

            lineNumsA = lnA.ToString();
            lineNumsB = lnB.ToString();
            SetMinimumWidth(docA, rows.Where(r => r.LineNoA.HasValue).Select(r => r.A));
            SetMinimumWidth(docB, rows.Where(r => r.LineNoB.HasValue).Select(r => r.B));
        }

        // Keep one file line on one visual line, so line numbers and markers cannot drift.
        internal static void SetMinimumWidth(FlowDocument document, IEnumerable<string> lines)
        {
            double width = 0;
            var typeface = new System.Windows.Media.Typeface(document.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            foreach (string line in lines)
            {
                if (line.Length == 0) continue;
                var formatted = new System.Windows.Media.FormattedText(line.Replace("\t", "        "),
                    System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                    typeface, document.FontSize, System.Windows.Media.Brushes.White, 1.0);
                width = Math.Max(width, formatted.WidthIncludingTrailingWhitespace);
            }
            document.MinPageWidth = Math.Max(1, width + 32);
        }

        private static Paragraph CreateParagraph(System.Windows.Media.Brush? background, List<MainWindow.CharPiece> pieces)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = DiffLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Background = background
            };
            AppendPieces(paragraph, pieces);
            return paragraph;
        }

        private static void AppendPieces(Paragraph para, List<MainWindow.CharPiece> pieces)
        {
            if (pieces.Count == 0)
            {
                para.Inlines.Add(new Run(""));
                return;
            }

            foreach (var piece in pieces)
            {
                var run = new Run(piece.Text);
                if (piece.Kind == "removed")
                    run.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x83, 0x32, 0x42));
                else if (piece.Kind == "added")
                    run.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x21, 0x65, 0x48));

                para.Inlines.Add(run);
            }
        }

        private static System.Windows.Media.Brush? GetBackground(DiffType type)
        {
            return type switch
            {
                DiffType.Added => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x13, 0x35, 0x29)),
                DiffType.Removed => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3F, 0x20, 0x29)),
                DiffType.Modified => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3B, 0x32, 0x19)),
                _ => null
            };
        }
    }
}
