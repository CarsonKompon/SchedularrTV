namespace SchedularrTV.Core.Query;

internal enum TokenKind
{
    End,
    Identifier,
    String,
    Colon,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Comma,
    Star,
    Operator
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position);

/// <summary>Hand-rolled lexer; avoids a parser-generator dependency for a grammar this small.</summary>
internal static class QueryLexer
{
    public static List<Token> Tokenize(string input)
    {
        List<Token> tokens = [];
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new Token(TokenKind.LParen, "(", i++));
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RParen, ")", i++));
                    continue;
                case '[':
                    tokens.Add(new Token(TokenKind.LBracket, "[", i++));
                    continue;
                case ']':
                    tokens.Add(new Token(TokenKind.RBracket, "]", i++));
                    continue;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", i++));
                    continue;
                case ':':
                    tokens.Add(new Token(TokenKind.Colon, ":", i++));
                    continue;
                case '*':
                    tokens.Add(new Token(TokenKind.Star, "*", i++));
                    continue;
                case '\'':
                case '"':
                    tokens.Add(ReadString(input, ref i));
                    continue;
            }

            if (TryReadOperator(input, ref i, out Token op))
            {
                tokens.Add(op);
                continue;
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '.' or '-')
            {
                int start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] is '_' or '.' or '-'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, input[start..i], start));
                continue;
            }

            throw new QueryParseException($"Unexpected character '{c}' at position {i}.");
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, input.Length));
        return tokens;
    }

    private static bool TryReadOperator(string input, ref int i, out Token token)
    {
        ReadOnlySpan<char> span = input.AsSpan(i);

        foreach (string candidate in TwoCharOperators)
        {
            if (span.StartsWith(candidate, StringComparison.Ordinal))
            {
                token = new Token(TokenKind.Operator, candidate, i);
                i += candidate.Length;
                return true;
            }
        }

        if (span.Length > 0 && (span[0] is '>' or '<' or '!'))
        {
            token = new Token(TokenKind.Operator, span[0].ToString(), i);
            i++;
            return true;
        }

        token = default;
        return false;
    }

    private static readonly string[] TwoCharOperators = ["==", "!=", ">=", "<=", "&&", "||"];

    private static Token ReadString(string input, ref int i)
    {
        char quote = input[i];
        int start = i;
        i++;

        var sb = new System.Text.StringBuilder();
        while (i < input.Length && input[i] != quote)
        {
            // Only quote/backslash are unescaped; everything else keeps its backslash so regex
            // literals such as MATCHES '\d+pm' survive tokenisation intact.
            if (input[i] == '\\' && i + 1 < input.Length && (input[i + 1] == quote || input[i + 1] == '\\'))
            {
                i++;
            }

            sb.Append(input[i]);
            i++;
        }

        if (i >= input.Length)
        {
            throw new QueryParseException($"Unterminated string starting at position {start}.");
        }

        i++; // closing quote
        return new Token(TokenKind.String, sb.ToString(), start);
    }
}

public sealed class QueryParseException(string message) : Exception(message);
