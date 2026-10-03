namespace SchedularrTV.Core.Query;

/// <summary>
/// Recursive-descent parser for the media query language.
/// <code>
/// expr       := orExpr
/// orExpr     := andExpr (('OR' | '||') andExpr)*
/// andExpr    := unary (('AND' | '&amp;&amp;') unary)*        // juxtaposition also implies AND
/// unary      := ('NOT' | '!') unary | primary
/// primary    := '(' expr ')' | '*' | comparison
/// comparison := ident ':' value
///             | ident ('==' | '!=' | '>' | '>=' | '&lt;' | '&lt;=' | 'CONTAINS' | 'MATCHES') value
///             | ident 'IN' '[' value (',' value)* ']'
/// </code>
/// </summary>
public static class QueryParser
{
    public static QueryExpression Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return QueryExpression.All;
        }

        List<Token> tokens = QueryLexer.Tokenize(input);
        int position = 0;
        QueryExpression expression = ParseOr(tokens, ref position);

        if (tokens[position].Kind != TokenKind.End)
        {
            throw new QueryParseException($"Unexpected token '{tokens[position].Text}' at position {tokens[position].Position}.");
        }

        return expression;
    }

    public static bool TryParse(string? input, out QueryExpression expression, out string? error)
    {
        try
        {
            expression = Parse(input);
            error = null;
            return true;
        }
        catch (QueryParseException ex)
        {
            expression = QueryExpression.None;
            error = ex.Message;
            return false;
        }
    }

    private static QueryExpression ParseOr(List<Token> tokens, ref int i)
    {
        List<QueryExpression> operands = [ParseAnd(tokens, ref i)];

        while (IsKeyword(tokens[i], "OR") || tokens[i] is { Kind: TokenKind.Operator, Text: "||" })
        {
            i++;
            operands.Add(ParseAnd(tokens, ref i));
        }

        return QueryExpression.Or(operands);
    }

    private static QueryExpression ParseAnd(List<Token> tokens, ref int i)
    {
        List<QueryExpression> operands = [ParseUnary(tokens, ref i)];

        while (true)
        {
            if (IsKeyword(tokens[i], "AND") || tokens[i] is { Kind: TokenKind.Operator, Text: "&&" })
            {
                i++;
            }
            else if (!StartsPrimary(tokens[i]))
            {
                break;
            }

            operands.Add(ParseUnary(tokens, ref i));
        }

        return QueryExpression.And(operands);
    }

    private static QueryExpression ParseUnary(List<Token> tokens, ref int i)
    {
        if (IsKeyword(tokens[i], "NOT") || tokens[i] is { Kind: TokenKind.Operator, Text: "!" })
        {
            i++;
            return new NotExpression(ParseUnary(tokens, ref i));
        }

        return ParsePrimary(tokens, ref i);
    }

    private static QueryExpression ParsePrimary(List<Token> tokens, ref int i)
    {
        Token token = tokens[i];

        if (token.Kind == TokenKind.Star)
        {
            i++;
            return QueryExpression.All;
        }

        if (token.Kind == TokenKind.LParen)
        {
            i++;
            QueryExpression inner = ParseOr(tokens, ref i);
            Expect(tokens, ref i, TokenKind.RParen, ")");
            return inner;
        }

        if (token.Kind != TokenKind.Identifier)
        {
            throw new QueryParseException($"Expected a field name at position {token.Position} but found '{token.Text}'.");
        }

        string field = token.Text;
        i++;

        Token next = tokens[i];

        if (next.Kind == TokenKind.Colon)
        {
            i++;
            return new FieldExpression(field, ComparisonOperator.Match, [ReadValue(tokens, ref i)]);
        }

        if (IsKeyword(next, "IN"))
        {
            i++;
            Expect(tokens, ref i, TokenKind.LBracket, "[");
            List<string> values = [];
            while (tokens[i].Kind != TokenKind.RBracket)
            {
                values.Add(ReadValue(tokens, ref i));
                if (tokens[i].Kind == TokenKind.Comma)
                {
                    i++;
                }
            }

            Expect(tokens, ref i, TokenKind.RBracket, "]");
            return new FieldExpression(field, ComparisonOperator.In, values);
        }

        if (TryReadComparisonOperator(next, out ComparisonOperator op))
        {
            i++;
            return new FieldExpression(field, op, [ReadValue(tokens, ref i)]);
        }

        throw new QueryParseException($"Expected an operator after field '{field}' at position {next.Position}.");
    }

    private static bool TryReadComparisonOperator(Token token, out ComparisonOperator op)
    {
        if (token.Kind == TokenKind.Operator)
        {
            switch (token.Text)
            {
                case "==": op = ComparisonOperator.Equals; return true;
                case "!=": op = ComparisonOperator.NotEquals; return true;
                case ">": op = ComparisonOperator.GreaterThan; return true;
                case ">=": op = ComparisonOperator.GreaterThanOrEqual; return true;
                case "<": op = ComparisonOperator.LessThan; return true;
                case "<=": op = ComparisonOperator.LessThanOrEqual; return true;
            }
        }

        if (IsKeyword(token, "CONTAINS"))
        {
            op = ComparisonOperator.Contains;
            return true;
        }

        if (IsKeyword(token, "MATCHES"))
        {
            op = ComparisonOperator.Matches;
            return true;
        }

        op = default;
        return false;
    }

    private static string ReadValue(List<Token> tokens, ref int i)
    {
        Token token = tokens[i];
        if (token.Kind is TokenKind.String or TokenKind.Identifier)
        {
            i++;
            return token.Text;
        }

        throw new QueryParseException($"Expected a value at position {token.Position} but found '{token.Text}'.");
    }

    private static void Expect(List<Token> tokens, ref int i, TokenKind kind, string display)
    {
        if (tokens[i].Kind != kind)
        {
            throw new QueryParseException($"Expected '{display}' at position {tokens[i].Position}.");
        }

        i++;
    }

    private static readonly string[] ReservedWords = ["AND", "OR", "NOT", "IN", "CONTAINS", "MATCHES"];

    // Juxtaposition implies AND (e.g. `tag:"a" tag:"b"`), so infix keywords must never look like a new primary.
    private static bool StartsPrimary(Token token) =>
        token.Kind is TokenKind.LParen or TokenKind.Star
        || (token.Kind == TokenKind.Operator && token.Text == "!")
        || IsKeyword(token, "NOT")
        || (token.Kind == TokenKind.Identifier && !ReservedWords.Contains(token.Text, StringComparer.OrdinalIgnoreCase));

    private static bool IsKeyword(Token token, string keyword) =>
        token.Kind == TokenKind.Identifier && string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase);
}
