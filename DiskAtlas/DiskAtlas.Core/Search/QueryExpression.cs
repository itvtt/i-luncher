namespace DiskAtlas.Core;

/// <summary>
/// A parsed file-search expression. AND binds more tightly than OR; parentheses
/// can override precedence. Text without an explicit operator is one literal term.
/// </summary>
public sealed class QueryExpression
{
    public const int MaximumQueryLength = 4096;
    public const int MaximumTerms = 64;
    public const int MaximumNestingDepth = 32;

    private readonly ExpressionNode _root;
    private readonly string[] _terms;
    private readonly IReadOnlyList<string> _readOnlyTerms;

    private QueryExpression(string query, ExpressionNode root, string[] terms)
    {
        Query = query;
        _root = root;
        _terms = terms;
        _readOnlyTerms = Array.AsReadOnly(terms);
    }

    public string Query { get; }

    public IReadOnlyList<string> Terms => _readOnlyTerms;

    /// <summary>
    /// Parses a query. Standalone AND and OR keywords are case-insensitive.
    /// Quoted text is always treated as a literal term, including operator words.
    /// </summary>
    /// <exception cref="ArgumentException">The query syntax or size is invalid.</exception>
    public static QueryExpression Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw Invalid("검색어를 입력하세요.");
        if (query.Length > MaximumQueryLength)
            throw Invalid($"검색어는 {MaximumQueryLength:N0}자 이하여야 합니다.");

        return new Parser(query).Parse();
    }

    internal bool Evaluate(ReadOnlySpan<bool> matchedTerms) => _root.Evaluate(matchedTerms);

    internal bool MatchesText(string text, StringComparison comparison) =>
        _root.MatchesText(text, comparison, _terms);

    private static ArgumentException Invalid(string message) => new(message, "query");

    private abstract record ExpressionNode
    {
        public abstract bool Evaluate(ReadOnlySpan<bool> matchedTerms);
        public abstract bool MatchesText(string text, StringComparison comparison, string[] terms);
    }

    private sealed record TermNode(int Index) : ExpressionNode
    {
        public override bool Evaluate(ReadOnlySpan<bool> matchedTerms) => matchedTerms[Index];
        public override bool MatchesText(string text, StringComparison comparison, string[] terms) =>
            text.Contains(terms[Index], comparison);
    }

    private sealed record AndNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode
    {
        public override bool Evaluate(ReadOnlySpan<bool> matchedTerms) =>
            Left.Evaluate(matchedTerms) && Right.Evaluate(matchedTerms);
        public override bool MatchesText(string text, StringComparison comparison, string[] terms) =>
            Left.MatchesText(text, comparison, terms) && Right.MatchesText(text, comparison, terms);
    }

    private sealed record OrNode(ExpressionNode Left, ExpressionNode Right) : ExpressionNode
    {
        public override bool Evaluate(ReadOnlySpan<bool> matchedTerms) =>
            Left.Evaluate(matchedTerms) || Right.Evaluate(matchedTerms);
        public override bool MatchesText(string text, StringComparison comparison, string[] terms) =>
            Left.MatchesText(text, comparison, terms) || Right.MatchesText(text, comparison, terms);
    }

    private sealed class Parser
    {
        private readonly string _query;
        private readonly List<string> _terms = [];
        private int _position;
        private int _nestingDepth;
        private Token _current;

        public Parser(string query)
        {
            _query = query;
            _current = ReadToken();
        }

        public QueryExpression Parse()
        {
            ExpressionNode root = ParseOr();
            if (_current.Kind == TokenKind.RightParenthesis)
                throw Invalid("닫는 괄호 ')'에 대응하는 여는 괄호가 없습니다.");
            if (_current.Kind != TokenKind.End)
                throw MissingOperator();

            return new QueryExpression(_query, root, _terms.ToArray());
        }

        private ExpressionNode ParseOr()
        {
            ExpressionNode left = ParseAnd();
            while (_current.Kind == TokenKind.Or)
            {
                NextToken();
                EnsureOperand("OR");
                left = new OrNode(left, ParseAnd());
            }
            return left;
        }

        private ExpressionNode ParseAnd()
        {
            ExpressionNode left = ParsePrimary();
            while (_current.Kind == TokenKind.And)
            {
                NextToken();
                EnsureOperand("AND");
                left = new AndNode(left, ParsePrimary());
            }
            return left;
        }

        private ExpressionNode ParsePrimary()
        {
            if (_current.Kind == TokenKind.Term)
            {
                int index = AddTerm(_current.Text!);
                NextToken();
                return new TermNode(index);
            }

            if (_current.Kind == TokenKind.LeftParenthesis)
            {
                if (_nestingDepth == MaximumNestingDepth)
                    throw Invalid($"괄호는 {MaximumNestingDepth:N0}단계까지만 중첩할 수 있습니다.");

                _nestingDepth++;
                NextToken();
                if (_current.Kind == TokenKind.RightParenthesis)
                    throw Invalid("빈 괄호에는 검색어를 넣어야 합니다.");
                if (_current.Kind == TokenKind.End)
                    throw Invalid("여는 괄호 '('를 닫아 주세요.");

                ExpressionNode nested = ParseOr();
                if (_current.Kind != TokenKind.RightParenthesis)
                {
                    if (_current.Kind == TokenKind.End)
                        throw Invalid("여는 괄호 '('를 닫아 주세요.");
                    throw MissingOperator();
                }

                NextToken();
                _nestingDepth--;
                return nested;
            }

            if (_current.Kind == TokenKind.RightParenthesis)
                throw Invalid("닫는 괄호 ')' 앞에 검색어가 필요합니다.");
            if (_current.Kind is TokenKind.And or TokenKind.Or)
                throw Invalid($"{_current.Text} 연산자 앞에 검색어가 필요합니다.");

            throw Invalid("검색어가 필요합니다.");
        }

        private int AddTerm(string term)
        {
            if (_terms.Count == MaximumTerms)
                throw Invalid($"검색 항목은 {MaximumTerms:N0}개 이하여야 합니다.");

            _terms.Add(term);
            return _terms.Count - 1;
        }

        private void EnsureOperand(string operatorName)
        {
            if (_current.Kind is TokenKind.End or TokenKind.RightParenthesis)
                throw Invalid($"{operatorName} 연산자 뒤에 검색어가 필요합니다.");
            if (_current.Kind is TokenKind.And or TokenKind.Or)
                throw Invalid($"{operatorName} 연산자 뒤에 검색어가 필요합니다.");
        }

        private ArgumentException MissingOperator() =>
            Invalid($"'{_current.Text ?? CurrentSymbol()}' 앞에 AND 또는 OR 연산자가 필요합니다.");

        private string CurrentSymbol() => _current.Kind switch
        {
            TokenKind.LeftParenthesis => "(",
            TokenKind.RightParenthesis => ")",
            _ => "검색어"
        };

        private void NextToken() => _current = ReadToken();

        private Token ReadToken()
        {
            while (_position < _query.Length && char.IsWhiteSpace(_query[_position]))
                _position++;

            if (_position == _query.Length)
                return new Token(TokenKind.End);

            char value = _query[_position];
            if (value == '(')
            {
                _position++;
                return new Token(TokenKind.LeftParenthesis, "(");
            }
            if (value == ')')
            {
                _position++;
                return new Token(TokenKind.RightParenthesis, ")");
            }
            if (value == '"')
                return ReadQuotedTerm();
            if (IsOperatorAt(_position, "AND"))
            {
                _position += 3;
                return new Token(TokenKind.And, "AND");
            }
            if (IsOperatorAt(_position, "OR"))
            {
                _position += 2;
                return new Token(TokenKind.Or, "OR");
            }

            int start = _position;
            while (_position < _query.Length)
            {
                value = _query[_position];
                if (value is '(' or ')' or '"' ||
                    IsOperatorAt(_position, "AND") || IsOperatorAt(_position, "OR"))
                    break;
                _position++;
            }

            string term = _query[start.._position].Trim();
            if (term.Length == 0)
                throw Invalid("연산자 사이에 검색어가 필요합니다.");
            return new Token(TokenKind.Term, term);
        }

        private Token ReadQuotedTerm()
        {
            int quotePosition = _position++;
            int start = _position;
            while (_position < _query.Length && _query[_position] != '"')
                _position++;

            if (_position == _query.Length)
                throw Invalid($"{quotePosition + 1}번째 위치의 큰따옴표를 닫아 주세요.");

            string term = _query[start.._position];
            _position++;
            if (term.Length == 0)
                throw Invalid("큰따옴표 안에 검색어를 넣어야 합니다.");
            return new Token(TokenKind.Term, term);
        }

        private bool IsOperatorAt(int index, string operatorName)
        {
            if (index + operatorName.Length > _query.Length ||
                !_query.AsSpan(index, operatorName.Length).Equals(operatorName, StringComparison.OrdinalIgnoreCase))
                return false;

            bool boundaryBefore = index == 0 || IsBoundary(_query[index - 1]);
            int after = index + operatorName.Length;
            bool boundaryAfter = after == _query.Length || IsBoundary(_query[after]);
            return boundaryBefore && boundaryAfter;
        }

        private static bool IsBoundary(char value) => char.IsWhiteSpace(value) || value is '(' or ')';
    }

    private readonly record struct Token(TokenKind Kind, string? Text = null);

    private enum TokenKind
    {
        End,
        Term,
        And,
        Or,
        LeftParenthesis,
        RightParenthesis
    }
}
