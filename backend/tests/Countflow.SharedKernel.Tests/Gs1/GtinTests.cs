using Countflow.SharedKernel.Gs1;

namespace Countflow.SharedKernel.Tests.Gs1;

public sealed class GtinTests
{
    [Theory]
    // The same product as EAN-13, UPC-A and GTIN-14 — all must normalise to one
    // 14-digit value, or a case scan and a retail scan look like different items.
    [InlineData("9501101530003", "09501101530003")]
    [InlineData("09501101530003", "09501101530003")]
    [InlineData("614141000036", "00614141000036")]
    public void Valid_gtins_normalise_to_fourteen_digits(string input, string expected)
    {
        Assert.True(Gtin.TryParse(input, out var gtin));
        Assert.Equal(expected, gtin!.Value.Value);
    }

    [Theory]
    [InlineData("9501101530004")]   // wrong check digit
    [InlineData("950110153000")]    // wrong length
    [InlineData("95011015300OO")]   // not numeric
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_gtins_are_rejected(string? input)
    {
        Assert.False(Gtin.TryParse(input, out _));
    }

    [Fact]
    public void Check_digit_calculation_round_trips()
    {
        const string payload = "0950110153000";

        var checkDigit = Gtin.CalculateCheckDigit(payload);

        Assert.Equal('3', checkDigit);
        Assert.True(Gtin.HasValidCheckDigit(payload + checkDigit));
    }

    [Fact]
    public void A_single_transposed_digit_fails_the_check()
    {
        // The modulo-10 scheme exists to catch exactly this, and it is the most
        // common keying error when a barcode is unreadable and gets typed in.
        Assert.True(Gtin.TryParse("09501101530003", out _));
        Assert.False(Gtin.TryParse("09501101532003", out _));
    }

    [Fact]
    public void Parse_throws_on_invalid_input()
    {
        Assert.Throws<FormatException>(() => Gtin.Parse("123"));
    }
}
