using System;
using System.Collections.Generic;
using System.Text;

namespace FolderCompareWPF
{
    public class DifferLike
    {
        public IEnumerable<string> Compare(string[] a, string[] b)
        {
            var cruncher = new SequenceMatcherLike();
            cruncher.SetSeqs(a, b);

            foreach (var (tag, alo, ahi, blo, bhi) in cruncher.GetOpcodes())
            {
                IEnumerable<string> g = tag switch
                {
                    "replace" => FancyReplace(a, alo, ahi, b, blo, bhi),
                    "delete" => Dump("-", a, alo, ahi),
                    "insert" => Dump("+", b, blo, bhi),
                    "equal" => Dump(" ", a, alo, ahi),
                    _ => throw new InvalidOperationException("unknown tag " + tag)
                };
                foreach (var line in g) yield return line;
            }
        }

        private IEnumerable<string> Dump(string tag, string[] x, int lo, int hi)
        {
            for (int i = lo; i < hi; i++)
                yield return $"{tag} {x[i]}";
        }

        private IEnumerable<string> PlainReplace(string[] a, int alo, int ahi, string[] b, int blo, int bhi)
        {
            IEnumerable<string> first, second;
            if (bhi - blo < ahi - alo)
            {
                first = Dump("+", b, blo, bhi);
                second = Dump("-", a, alo, ahi);
            }
            else
            {
                first = Dump("-", a, alo, ahi);
                second = Dump("+", b, blo, bhi);
            }
            foreach (var l in first) yield return l;
            foreach (var l in second) yield return l;
        }

        private IEnumerable<string> FancyReplace(string[] a, int alo, int ahi, string[] b, int blo, int bhi)
        {
            double bestRatio = 0.74, cutoff = 0.75;
            int bestI = -1, bestJ = -1;
            int? eqi = null, eqj = null;

            var cruncher = new SequenceMatcherLike();

            for (int j = blo; j < bhi; j++)
            {
                string bj = b[j];
                cruncher.SetSeq2Char(bj);
                for (int i = alo; i < ahi; i++)
                {
                    string ai = a[i];
                    if (ai == bj)
                    {
                        if (eqi == null) { eqi = i; eqj = j; }
                        continue;
                    }
                    cruncher.SetSeq1Char(ai);
                    if (cruncher.RealQuickRatio() > bestRatio &&
                        cruncher.QuickRatio() > bestRatio &&
                        cruncher.Ratio() > bestRatio)
                    {
                        bestRatio = cruncher.Ratio();
                        bestI = i; bestJ = j;
                    }
                }
            }

            if (bestRatio < cutoff)
            {
                if (!eqi.HasValue || !eqj.HasValue)
                {
                    foreach (var l in PlainReplace(a, alo, ahi, b, blo, bhi))
                        yield return l;
                    yield break;
                }
                bestI = eqi.Value; bestJ = eqj.Value; bestRatio = 1.0;
            }
            else
            {
                eqi = null;
            }

            foreach (var l in FancyHelper(a, alo, bestI, b, blo, bestJ))
                yield return l;

            string aelt = a[bestI], belt = b[bestJ];
            if (eqi == null)
            {
                var atags = new StringBuilder();
                var btags = new StringBuilder();
                cruncher.SetSeqsChar(aelt, belt);
                foreach (var (tag, ai1, ai2, bj1, bj2) in cruncher.GetOpcodes())
                {
                    int la = ai2 - ai1, lb = bj2 - bj1;
                    switch (tag)
                    {
                        case "replace":
                            atags.Append('^', la);
                            btags.Append('^', lb);
                            break;
                        case "delete":
                            atags.Append('-', la);
                            break;
                        case "insert":
                            btags.Append('+', lb);
                            break;
                        case "equal":
                            atags.Append(' ', la);
                            btags.Append(' ', lb);
                            break;
                    }
                }
                foreach (var l in QFormat(aelt, belt, atags.ToString(), btags.ToString()))
                    yield return l;
            }
            else
            {
                yield return "  " + aelt;
            }

            foreach (var l in FancyHelper(a, bestI + 1, ahi, b, bestJ + 1, bhi))
                yield return l;
        }

        private IEnumerable<string> FancyHelper(string[] a, int alo, int ahi, string[] b, int blo, int bhi)
        {
            if (alo < ahi)
            {
                if (blo < bhi)
                {
                    foreach (var l in FancyReplace(a, alo, ahi, b, blo, bhi))
                        yield return l;
                }
                else
                {
                    foreach (var l in Dump("-", a, alo, ahi))
                        yield return l;
                }
            }
            else if (blo < bhi)
            {
                foreach (var l in Dump("+", b, blo, bhi))
                    yield return l;
            }
        }

        private static int CountLeading(string s, char ch)
        {
            int i = 0;
            while (i < s.Length && s[i] == ch) i++;
            return i;
        }

        private IEnumerable<string> QFormat(string aline, string bline, string atags, string btags)
        {
            int common = Math.Min(CountLeading(aline, '\t'), CountLeading(bline, '\t'));
            common = Math.Min(common, CountLeading(atags.Substring(0, Math.Min(common, atags.Length)), ' '));
            common = Math.Min(common, CountLeading(btags.Substring(0, Math.Min(common, btags.Length)), ' '));

            string aTagsTrim = atags.Substring(Math.Min(common, atags.Length)).TrimEnd();
            string bTagsTrim = btags.Substring(Math.Min(common, btags.Length)).TrimEnd();

            yield return "- " + aline;
            if (aTagsTrim.Length > 0)
                yield return "? " + new string('\t', common) + aTagsTrim;

            yield return "+ " + bline;
            if (bTagsTrim.Length > 0)
                yield return "? " + new string('\t', common) + bTagsTrim;
        }
    }
}
