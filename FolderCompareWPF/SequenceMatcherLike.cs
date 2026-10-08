using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderCompareWPF
{
    public class SequenceMatcherLike
    {
        private string[] _a = Array.Empty<string>();
        private string[] _b = Array.Empty<string>();
        private Dictionary<string, List<int>> _b2j = new();
        private readonly bool _autojunk;

        public SequenceMatcherLike(bool autojunk = true)
        {
            _autojunk = autojunk;
        }

        public void SetSeqs(string[] a, string[] b)
        {
            SetSeq1(a);
            SetSeq2(b);
        }

        public void SetSeq1(string[] a) => _a = a;

        public void SetSeq2(string[] b)
        {
            _b = b;
            ChainB();
        }

        private void ChainB()
        {
            _b2j = new Dictionary<string, List<int>>();
            for (int i = 0; i < _b.Length; i++)
            {
                if (!_b2j.TryGetValue(_b[i], out var list))
                {
                    list = new List<int>();
                    _b2j[_b[i]] = list;
                }
                list.Add(i);
            }

            if (_autojunk && _b.Length >= 200)
            {
                int threshold = _b.Length / 100 + 1;
                var popular = _b2j.Where(kv => kv.Value.Count > threshold)
                                   .Select(kv => kv.Key).ToList();
                foreach (var elt in popular)
                    _b2j.Remove(elt);
            }
        }

        public (int, int, int) FindLongestMatch(int alo, int ahi, int blo, int bhi)
        {
            int besti = alo, bestj = blo, bestsize = 0;
            var j2len = new Dictionary<int, int>();

            for (int i = alo; i < ahi; i++)
            {
                var newj2len = new Dictionary<int, int>();
                if (_b2j.TryGetValue(_a[i], out var jIndices))
                {
                    foreach (var j in jIndices)
                    {
                        if (j < blo) continue;
                        if (j >= bhi) break;
                        int k = (j2len.TryGetValue(j - 1, out var v) ? v : 0) + 1;
                        newj2len[j] = k;
                        if (k > bestsize)
                        {
                            besti = i - k + 1;
                            bestj = j - k + 1;
                            bestsize = k;
                        }
                    }
                }
                j2len = newj2len;
            }

            while (besti > alo && bestj > blo && _a[besti - 1] == _b[bestj - 1])
            {
                besti--; bestj--; bestsize++;
            }
            while (besti + bestsize < ahi && bestj + bestsize < bhi &&
                   _a[besti + bestsize] == _b[bestj + bestsize])
            {
                bestsize++;
            }

            return (besti, bestj, bestsize);
        }

        public List<(int, int, int)> GetMatchingBlocks()
        {
            int la = _a.Length, lb = _b.Length;
            var queue = new Queue<(int, int, int, int)>();
            queue.Enqueue((0, la, 0, lb));
            var matchingBlocks = new List<(int, int, int)>();

            while (queue.Count > 0)
            {
                var (alo, ahi, blo, bhi) = queue.Dequeue();
                var (i, j, k) = FindLongestMatch(alo, ahi, blo, bhi);
                if (k > 0)
                {
                    matchingBlocks.Add((i, j, k));
                    if (alo < i && blo < j)
                        queue.Enqueue((alo, i, blo, j));
                    if (i + k < ahi && j + k < bhi)
                        queue.Enqueue((i + k, ahi, j + k, bhi));
                }
            }

            matchingBlocks.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : x.Item2.CompareTo(y.Item2));

            var result = new List<(int, int, int)>();
            int i1 = 0, j1 = 0, k1 = 0;
            foreach (var (i2, j2, k2) in matchingBlocks)
            {
                if (i1 + k1 == i2 && j1 + k1 == j2)
                {
                    k1 += k2;
                }
                else
                {
                    if (k1 > 0) result.Add((i1, j1, k1));
                    i1 = i2; j1 = j2; k1 = k2;
                }
            }
            if (k1 > 0) result.Add((i1, j1, k1));
            result.Add((la, lb, 0));
            return result;
        }

        public List<(string Tag, int Ai1, int Ai2, int Bj1, int Bj2)> GetOpcodes()
        {
            int i = 0, j = 0;
            var answer = new List<(string, int, int, int, int)>();
            foreach (var (ai, bj, size) in GetMatchingBlocks())
            {
                string tag = "";
                if (i < ai && j < bj) tag = "replace";
                else if (i < ai) tag = "delete";
                else if (j < bj) tag = "insert";

                if (tag != "")
                    answer.Add((tag, i, ai, j, bj));

                i = ai + size; j = bj + size;

                if (size > 0)
                    answer.Add(("equal", ai, i, bj, j));
            }
            return answer;
        }

        // 문자 단위 비교(줄 하나를 문자 시퀀스로 취급)
        public void SetSeqsChar(string a, string b)
        {
            SetSeq1Char(a);
            SetSeq2Char(b);
        }
        public void SetSeq1Char(string a) => _a = a.Select(c => c.ToString()).ToArray();
        public void SetSeq2Char(string b) => SetSeq2(b.Select(c => c.ToString()).ToArray());

        public double Ratio()
        {
            int matches = GetMatchingBlocks().Sum(m => m.Item3);
            int total = _a.Length + _b.Length;
            return total == 0 ? 1.0 : 2.0 * matches / total;
        }

        public double QuickRatio()
        {
            var fullCount = new Dictionary<string, int>();
            foreach (var elt in _b)
                fullCount[elt] = fullCount.TryGetValue(elt, out var c) ? c + 1 : 1;

            var avail = new Dictionary<string, int>();
            int matches = 0;
            foreach (var elt in _a)
            {
                int numb;
                if (avail.TryGetValue(elt, out var av)) numb = av;
                else numb = fullCount.TryGetValue(elt, out var fc) ? fc : 0;
                avail[elt] = numb - 1;
                if (numb > 0) matches++;
            }
            int total = _a.Length + _b.Length;
            return total == 0 ? 1.0 : 2.0 * matches / total;
        }

        public double RealQuickRatio()
        {
            int la = _a.Length, lb = _b.Length;
            int total = la + lb;
            return total == 0 ? 1.0 : 2.0 * Math.Min(la, lb) / total;
        }
    }
}