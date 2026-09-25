using System.Globalization;

namespace Llm.Api.Chat.Tools;

public sealed class CalculatorException(string message) : Exception(message);

/// <summary>
/// Arithmetic for the model, which is bad at it: + - * / % ^, brackets, and a
/// few functions. A small parser of its own, never an evaluator of code, so an
/// expression can only ever be arithmetic. Decimal, not double: 28 significant
/// digits, so 123456789 * 987654321 is exact (a double gets its last digits
/// wrong). Functions like sqrt and sin go through double, to 15 digits.
/// </summary>
public static class Calculator
{
    private const int MaxLength = 500;
    private const int MaxDepth = 50;

    private static readonly Dictionary<string, Func<decimal[], decimal>> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = a => Real(a, Math.Sqrt), ["abs"] = a => One(a, Math.Abs),
        ["sin"] = a => Real(a, Math.Sin), ["cos"] = a => Real(a, Math.Cos), ["tan"] = a => Real(a, Math.Tan),
        ["asin"] = a => Real(a, Math.Asin), ["acos"] = a => Real(a, Math.Acos), ["atan"] = a => Real(a, Math.Atan),
        ["ln"] = a => Real(a, Math.Log), ["log2"] = a => Real(a, Math.Log2), ["exp"] = a => Real(a, Math.Exp),
        ["log"] = a => a.Length == 2 ? Decimal(Math.Log((double)a[0], (double)a[1])) : Real(a, Math.Log10),
        ["floor"] = a => One(a, Math.Floor), ["ceil"] = a => One(a, Math.Ceiling),
        ["round"] = a => a.Length == 2 ? Math.Round(a[0], (int)Math.Clamp(a[1], 0, 28), MidpointRounding.AwayFromZero) : One(a, x => Math.Round(x, MidpointRounding.AwayFromZero)),
        ["min"] = a => a.Length > 0 ? a.Min() : throw new CalculatorException("min needs at least one number."),
        ["max"] = a => a.Length > 0 ? a.Max() : throw new CalculatorException("max needs at least one number."),
    };

    private static readonly Dictionary<string, decimal> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = 3.1415926535897932384626433833m, ["e"] = 2.7182818284590452353602874714m,
    };

    private static decimal One(decimal[] a, Func<decimal, decimal> f) =>
        a.Length == 1 ? f(a[0]) : throw new CalculatorException("That function takes one number.");

    /// <summary>A function only double has (sqrt, sin...): 15 significant digits.</summary>
    private static decimal Real(decimal[] a, Func<double, double> f) =>
        a.Length == 1 ? Decimal(f((double)a[0])) : throw new CalculatorException("That function takes one number.");

    private static decimal Decimal(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 7.9e28
            ? (decimal)value
            : throw new CalculatorException("The result is not a finite number within ±7.9e28 (for example, the square root of a negative number).");

    public static decimal Evaluate(string expression)
    {
        if (expression.Length > MaxLength)
        {
            throw new CalculatorException($"The expression is longer than {MaxLength} characters.");
        }
        var p = new Parser(expression);
        try
        {
            var value = p.Sum(0);
            p.SkipSpace();
            return p.AtEnd ? value : throw new CalculatorException($"Unexpected '{p.Current}' at position {p.Position + 1}.");
        }
        catch (DivideByZeroException)
        {
            throw new CalculatorException("Division by zero.");
        }
        catch (OverflowException)
        {
            throw new CalculatorException("A number went beyond ±7.9e28, the largest this calculator keeps exactly.");
        }
    }

    /// <summary>The value as a person would write it: every digit, no trailing zeros, no exponent.</summary>
    public static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private sealed class Parser(string text)
    {
        private int _pos;

        public int Position => _pos;
        public bool AtEnd => _pos >= text.Length;
        public char Current => text[_pos];

        public void SkipSpace()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
            {
                _pos++;
            }
        }

        private bool Take(char c)
        {
            SkipSpace();
            if (!AtEnd && Current == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        public decimal Sum(int depth)
        {
            var value = Product(depth);
            while (true)
            {
                if (Take('+'))
                {
                    value += Product(depth);
                }
                else if (Take('-'))
                {
                    value -= Product(depth);
                }
                else
                {
                    return value;
                }
            }
        }

        private decimal Product(int depth)
        {
            var value = Unary(depth);
            while (true)
            {
                if (Take('*') || Take('×'))
                {
                    value *= Unary(depth);
                }
                else if (Take('/') || Take('÷'))
                {
                    value /= Unary(depth);
                }
                else if (Take('%'))
                {
                    value %= Unary(depth);
                }
                else
                {
                    return value;
                }
            }
        }

        /// <summary>A sign applies to the whole power, as in mathematics: -2^2 = -4.</summary>
        private decimal Unary(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new CalculatorException("The expression is nested too deeply.");
            }
            if (Take('-'))
            {
                return -Unary(depth + 1);
            }
            if (Take('+'))
            {
                return Unary(depth + 1);
            }
            return Power(depth);
        }

        /// <summary>Right-associative (2^3^2 = 2^9), and an exponent may have a sign (2^-1).</summary>
        private decimal Power(int depth)
        {
            var value = Atom(depth);
            return Take('^') ? Pow(value, Unary(depth + 1)) : value;
        }

        /// <summary>Exact for whole exponents (by squaring); through double otherwise.</summary>
        private static decimal Pow(decimal x, decimal n)
        {
            if (n != decimal.Truncate(n) || Math.Abs(n) > 1000)
            {
                return Decimal(Math.Pow((double)x, (double)n));
            }
            var (result, factor, k) = (1m, x, (long)Math.Abs(n));
            for (; k > 0; k >>= 1)
            {
                if ((k & 1) == 1)
                {
                    result *= factor;
                }
                if (k > 1)
                {
                    factor *= factor;
                }
            }
            return n < 0 ? 1m / result : result;
        }

        private decimal Atom(int depth)
        {
            SkipSpace();
            if (Take('('))
            {
                var inner = Sum(depth + 1);
                if (!Take(')'))
                {
                    throw new CalculatorException("A bracket is not closed.");
                }
                return inner;
            }
            if (AtEnd)
            {
                throw new CalculatorException("The expression ends too early.");
            }
            if (char.IsDigit(Current) || Current == '.')
            {
                var start = _pos;
                while (!AtEnd && (char.IsDigit(Current) || Current is '.' or '_'))
                {
                    _pos++;
                }
                if (!AtEnd && Current is 'e' or 'E' && _pos + 1 < text.Length && (char.IsDigit(text[_pos + 1]) || text[_pos + 1] is '-' or '+'))
                {
                    _pos += 2;
                    while (!AtEnd && char.IsDigit(Current))
                    {
                        _pos++;
                    }
                }
                // Digits can be grouped with underscores: 1_000_000. (A comma separates a function's arguments.)
                var number = text[start.._pos].Replace("_", "", StringComparison.Ordinal);
                return decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : throw new CalculatorException($"'{text[start.._pos]}' is not a number (or beyond ±7.9e28).");
            }
            if (char.IsLetter(Current))
            {
                var start = _pos;
                while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_'))
                {
                    _pos++;
                }
                var name = text[start.._pos];
                if (Constants.TryGetValue(name, out var constant))
                {
                    return constant;
                }
                if (!Functions.TryGetValue(name, out var f))
                {
                    throw new CalculatorException($"Unknown name '{name}'. Functions: {string.Join(", ", Functions.Keys)}; constants: pi, e.");
                }
                if (!Take('('))
                {
                    throw new CalculatorException($"{name} needs brackets, e.g. {name}(2).");
                }
                var args = new List<decimal>();
                if (!Take(')'))
                {
                    do
                    {
                        args.Add(Sum(depth + 1));
                    }
                    while (Take(';') || Take(','));
                    if (!Take(')'))
                    {
                        throw new CalculatorException($"{name}( is not closed.");
                    }
                }
                return f([.. args]);
            }
            throw new CalculatorException($"Unexpected '{Current}' at position {_pos + 1}.");
        }
    }
}
