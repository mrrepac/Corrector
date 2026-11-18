using System.Globalization;

namespace Corrector;

internal static class Calculator
{
    static bool Space(char c) => c is ' ' or '\t' or '\u00A0' or '\u202F';
    static bool MathChar(char c) => char.IsAsciiDigit(c) || Space(c) || "+-*/%(),.".Contains(c);

    // Only inspect the suffix of the current line. Require a word boundary so an
    // identifier, a power, or an unsupported operator cannot lose its left operand.
    internal static string? Expression(string text)
    {
        if (!text.EndsWith('=')) return null;
        int end = text.Length - 1;
        int start = end;
        while (start > 0 && MathChar(text[start - 1])) start--;
        if (start == end || (start > 0 && !Space(text[start]) &&
            text[start - 1] is not (':' or '\n' or '\r' or '\u2028' or '\u2029'))) return null;
        string expression = text[start..end].Trim();
        return expression.Any(char.IsAsciiDigit) ? expression : null;
    }

    internal static string Evaluate(string expression)
    {
        try
        {
            var parser = new Parser(expression);
            decimal value = parser.Sum(0).Value;
            if (!parser.AtEnd) throw Invalid();
            string answer = value.ToString("0.############################", CultureInfo.InvariantCulture);
            return expression.Contains('.') && !expression.Contains(',') ? answer : answer.Replace('.', ',');
        }
        catch (DivideByZeroException) { throw new InvalidOperationException("На ноль делить нельзя."); }
        catch (OverflowException) { throw new InvalidOperationException("Число слишком велико для калькулятора."); }
    }

    static InvalidOperationException Invalid() => new("Проверь числа, скобки и знаки действий перед =.");

    // A percentage remains relative only until it participates in an operation.
    internal readonly record struct Operand(decimal Value, bool Percent = false);

    sealed class Parser(string input)
    {
        int pos;
        char Next
        {
            get
            {
                while (pos < input.Length && Space(input[pos])) pos++;
                return pos == input.Length ? '\0' : input[pos];
            }
        }
        internal bool AtEnd => Next == '\0' && pos == input.Length;

        internal Operand Sum(int depth)
        {
            var left = Product(depth);
            while (Next is '+' or '-')
            {
                char op = input[pos++];
                var right = Product(depth);
                decimal amount = right.Percent ? left.Value * right.Value : right.Value;
                left = new(op == '+' ? left.Value + amount : left.Value - amount);
            }
            return left;
        }

        Operand Product(int depth)
        {
            var left = Factor(depth);
            while (Next is '*' or '/')
            {
                char op = input[pos++];
                decimal right = Factor(depth).Value;
                left = new(op == '*' ? left.Value * right : left.Value / right);
            }
            return left;
        }

        Operand Factor(int depth)
        {
            bool negative = false;
            while (Next is '+' or '-')
                if (input[pos++] == '-') negative = !negative;

            Operand operand;
            if (Next == '(')
            {
                if (depth >= 64) throw Invalid();
                pos++;
                operand = Sum(depth + 1);
                if (Next != ')') throw Invalid();
                pos++;
            }
            else
            {
                int start = pos;
                while (pos < input.Length && char.IsAsciiDigit(input[pos])) pos++;
                if (pos < input.Length && input[pos] is '.' or ',')
                {
                    pos++;
                    while (pos < input.Length && char.IsAsciiDigit(input[pos])) pos++;
                }
                if (start == pos) throw Invalid();
                try { operand = new(decimal.Parse(input[start..pos].Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)); }
                catch (FormatException) { throw Invalid(); }
            }
            if (Next == '%')
            {
                pos++;
                operand = new(operand.Value / 100m, true);
            }
            return negative ? operand with { Value = -operand.Value } : operand;
        }
    }
}
