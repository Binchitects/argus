using Llm.Api.Chat.Tools;

namespace Llm.Tests;

public sealed class CalculatorTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("2^3^2", "512")]
    [InlineData("-2^2", "-4")]
    [InlineData("2^-1", "0.5")]
    [InlineData("(-2)^2", "4")]
    [InlineData("10 % 4", "2")]
    [InlineData("1_250 * 0.18", "225")]
    [InlineData("sqrt(16) + abs(-3)", "7")]
    [InlineData("round(2 / 3, 4)", "0.6667")]
    [InlineData("max(1, 7, 3) - min(4; 2)", "5")]
    [InlineData("log(1000) + ln(e) + log(8, 2)", "7")]
    [InlineData("round(sin(pi / 2))", "1")]
    [InlineData("1e3 / 4", "250")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("12 × 3 ÷ 4", "9")]
    [InlineData("123456789 * 987654321", "121932631112635269")]
    [InlineData("2^64", "18446744073709551616")]
    [InlineData("0.1 * 3", "0.3")]
    public void Arithmetic_comes_out_right(string expression, string expected) =>
        Assert.Equal(expected, Calculator.Format(Calculator.Evaluate(expression)));

    [Theory]
    [InlineData("1 / 0", "Division by zero")]
    [InlineData("sqrt(-1)", "not a finite number")]
    [InlineData("10^40", "beyond")]
    [InlineData("2 +* 3", "Unexpected")]
    [InlineData("(1 + 2", "not closed")]
    [InlineData("System.IO.File(1)", "Unknown name 'System'")]
    [InlineData("sqrt 4", "needs brackets")]
    [InlineData("sqrt(1, 2)", "takes one number")]
    [InlineData("2 3", "Unexpected '3'")]
    [InlineData("", "ends too early")]
    public void Anything_else_is_refused_with_the_reason(string expression, string reason) =>
        Assert.Contains(reason, Assert.Throws<CalculatorException>(() => Calculator.Evaluate(expression)).Message, StringComparison.Ordinal);

    [Fact]
    public void Deep_nesting_and_long_input_are_refused_not_crashed_on()
    {
        Assert.Throws<CalculatorException>(() => Calculator.Evaluate(new string('(', 200) + "1" + new string(')', 200)));
        Assert.Throws<CalculatorException>(() => Calculator.Evaluate(new string('-', 400) + "1"));
        Assert.Throws<CalculatorException>(() => Calculator.Evaluate(string.Join("+", Enumerable.Repeat("1", 300))));
    }

    [Fact]
    public void Numbers_read_as_written_every_digit_and_no_trailing_zeros() =>
        Assert.Equal(["1000000", "100000000000000000000", "0.000123", "0.0000000015", "2.5"],
            new[] { 1e6m, 1e20m, 0.000123m, 0.0000000015m, 2.500m }.Select(Calculator.Format));
}
