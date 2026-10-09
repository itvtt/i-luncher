using System.Text.RegularExpressions;

namespace FolderCompareWPF;

internal static class WhitespaceDiffBuilder
{
    // Compare a whitespace-free stream, retaining original lines for display and saving.
    public static List<MainWindow.DiffRow> Build(string[] a, string[] b)
    {
        var (tokensA, linesA) = Tokenize(a);
        var (tokensB, linesB) = Tokenize(b);
        var changedA = new HashSet<int>();
        var changedB = new HashSet<int>();
        var matcher = new SequenceMatcherLike();
        matcher.SetSeqs(tokensA, tokensB);
        foreach (var (tag, a1, a2, b1, b2) in matcher.GetOpcodes())
        {
            if (tag == "equal") continue;
            for (int i = a1; i < a2; i++) changedA.UnionWith(linesA[i]);
            for (int i = b1; i < b2; i++) changedB.UnionWith(linesB[i]);
        }
        return Enumerable.Range(0, Math.Max(a.Length, b.Length)).Select(i =>
        {
            bool ca = changedA.Contains(i), cb = changedB.Contains(i);
            return new MainWindow.DiffRow
            {
                LineNoA = i < a.Length ? i + 1 : null, LineNoB = i < b.Length ? i + 1 : null,
                A = i < a.Length ? a[i] : "", B = i < b.Length ? b[i] : "",
                Type = ca && cb ? DiffType.Modified : ca ? DiffType.Removed : cb ? DiffType.Added : DiffType.Same,
                BackgroundTypeA = ca ? DiffType.Removed : DiffType.Same,
                BackgroundTypeB = cb ? DiffType.Added : DiffType.Same
            };
        }).ToList();
    }

    private static (string[] Tokens, List<int[]> Lines) Tokenize(string[] lines)
    {
        var text = new System.Text.StringBuilder();
        var lineMap = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            foreach (char ch in lines[i])
                if (!char.IsWhiteSpace(ch)) { text.Append(ch); lineMap.Add(i); }
        var matches = Regex.Matches(text.ToString(), @"\w+|[^\w]");
        return (matches.Select(m => m.Value).ToArray(),
            matches.Select(m => lineMap.GetRange(m.Index, m.Length).Distinct().ToArray()).ToList());
    }
}
