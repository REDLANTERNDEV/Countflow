using Countflow.SharedKernel.Gs1;

namespace Countflow.SharedKernel.Tests.Gs1;

public sealed class Gs1ParserTests
{
    private const char GS = Gs1Parser.GroupSeparator;

    [Fact]
    public void Parses_a_bare_gtin()
    {
        var result = Gs1Parser.Parse("0109501101530003");

        Assert.True(result.IsSuccess);
        Assert.Equal("09501101530003", result.Value.Gtin?.Value);
    }

    [Fact]
    public void Parses_an_sscc_pallet_licence_plate()
    {
        const string payload = "34012345000000001";
        var sscc = payload + Gtin.CalculateCheckDigit(payload);

        var result = Gs1Parser.Parse("00" + sscc);

        Assert.True(result.IsSuccess);
        Assert.Equal(sscc, result.Value.Sscc);
        Assert.True(result.Value.HasValidSscc);
    }

    [Fact]
    public void An_sscc_with_a_bad_check_digit_is_flagged()
    {
        const string payload = "34012345000000001";
        var wrongCheckDigit = Gtin.CalculateCheckDigit(payload) == '0' ? '1' : '0';

        var result = Gs1Parser.Parse("00" + payload + wrongCheckDigit);

        // The element still parses — it is 18 digits — but the licence plate is
        // not trustworthy, so putaway must not create an LPN from it.
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.HasValidSscc);
    }

    [Fact]
    public void Two_predefined_length_elements_need_no_separator()
    {
        // This is the case a naive parser gets wrong: AI 01 is predefined at 14
        // digits, so AI 17 starts immediately with nothing between them.
        var result = Gs1Parser.Parse("0109501101530003" + "17261231");

        Assert.True(result.IsSuccess);
        Assert.Equal("09501101530003", result.Value.Gtin?.Value);
        Assert.Equal(new DateOnly(2026, 12, 31), result.Value.ExpiryDate);
    }

    [Fact]
    public void A_variable_length_element_runs_to_the_separator()
    {
        var raw = "0109501101530003" + "10LOT-4711" + GS + "17270115";

        var result = Gs1Parser.Parse(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal("09501101530003", result.Value.Gtin?.Value);
        Assert.Equal("LOT-4711", result.Value.LotNumber);
        Assert.Equal(new DateOnly(2027, 1, 15), result.Value.ExpiryDate);
    }

    [Fact]
    public void A_variable_length_element_may_end_the_string_without_a_separator()
    {
        var result = Gs1Parser.Parse("010950110153000317270115" + "10LOT-4711");

        Assert.True(result.IsSuccess);
        Assert.Equal("LOT-4711", result.Value.LotNumber);
    }

    [Fact]
    public void A_missing_separator_makes_the_lot_swallow_what_follows()
    {
        // Per the specification a variable-length element must be terminated by
        // FNC1. When a supplier's label omits it the data is genuinely
        // ambiguous, and the parser reads it the only way it can. Pinned here
        // because the failure is silent on the floor: the receipt looks fine and
        // the expiry date is simply gone.
        var result = Gs1Parser.Parse("0109501101530003" + "10LOT-4711" + "17270115");

        Assert.True(result.IsSuccess);
        Assert.Equal("LOT-471117270115", result.Value.LotNumber);
        Assert.Null(result.Value.ExpiryDate);
    }

    [Fact]
    public void Parses_the_full_logistics_label_a_receiver_actually_scans()
    {
        var raw = "0109501101530003" + "3103000123" + "10LOT-4711" + GS + "17270115" + "37000024";

        var result = Gs1Parser.Parse(raw);

        Assert.True(result.IsSuccess);
        var scan = result.Value;

        Assert.Equal("09501101530003", scan.Gtin?.Value);
        Assert.Equal("LOT-4711", scan.LotNumber);
        Assert.Equal(new DateOnly(2027, 1, 15), scan.ExpiryDate);
        Assert.Equal(24, scan.Quantity);
        Assert.Equal(0.123m, scan.NetWeightKg);
    }

    [Theory]
    // The final digit of a measure AI is the implied decimal position.
    [InlineData("3100000012", 12)]
    [InlineData("3101000123", 12.3)]
    [InlineData("3102000123", 1.23)]
    [InlineData("3103000123", 0.123)]
    public void Measure_ais_apply_the_implied_decimal_point(string element, double expected)
    {
        var result = Gs1Parser.Parse("0109501101530003" + element);

        Assert.True(result.IsSuccess);
        Assert.Equal((decimal)expected, result.Value.NetWeightKg);
    }

    [Fact]
    public void A_day_of_zero_means_the_end_of_that_month()
    {
        // Suppliers use this for best-before dates; rejecting it would fail
        // perfectly valid labels.
        var result = Gs1Parser.Parse("0109501101530003" + "17270200");

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateOnly(2027, 2, 28), result.Value.ExpiryDate);
    }

    [Fact]
    public void Leap_years_are_handled_for_end_of_month_dates()
    {
        var result = Gs1Parser.Parse("0109501101530003" + "17280200");

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateOnly(2028, 2, 29), result.Value.ExpiryDate);
    }

    [Fact]
    public void Strips_the_symbology_identifier_a_scanner_prepends()
    {
        var result = Gs1Parser.Parse("]C1" + "0109501101530003");

        Assert.True(result.IsSuccess);
        Assert.Equal("09501101530003", result.Value.Gtin?.Value);
    }

    [Fact]
    public void Accepts_the_bracketed_human_readable_form()
    {
        // What appears in supplier specs and support tickets. Being able to
        // paste one straight into the scan-test panel is worth supporting.
        var result = Gs1Parser.Parse("(01)09501101530003(10)LOT-4711(17)270115");

        Assert.True(result.IsSuccess);
        Assert.Equal("09501101530003", result.Value.Gtin?.Value);
        Assert.Equal("LOT-4711", result.Value.LotNumber);
        Assert.Equal(new DateOnly(2027, 1, 15), result.Value.ExpiryDate);
    }

    [Fact]
    public void Parses_a_serial_number_for_serialised_stock()
    {
        var result = Gs1Parser.Parse("0109501101530003" + "21SN-000917");

        Assert.True(result.IsSuccess);
        Assert.Equal("SN-000917", result.Value.SerialNumber);
    }

    [Fact]
    public void Unknown_ais_pass_through_rather_than_failing_the_scan()
    {
        // A supplier adding an AI we have not catalogued should not make an
        // otherwise valid label unscannable.
        var result = Gs1Parser.Parse("0109501101530003" + "91SOMETHING");

        Assert.True(result.IsSuccess);
        Assert.Equal("09501101530003", result.Value.Gtin?.Value);
        Assert.Contains(result.Value.Elements, e => e.Ai == "91" && e.Value == "SOMETHING");
    }

    [Theory]
    [InlineData("", "gs1.empty")]
    [InlineData("   ", "gs1.empty")]
    [InlineData("0109501101", "gs1.truncated_value")]      // GTIN cut short
    [InlineData("01ABCDEFGHIJKLMN", "gs1.non_numeric")]    // GTIN must be digits
    [InlineData("0109501101530003" + "17279999", "gs1.invalid_date")]
    [InlineData("XX123", "gs1.invalid_ai")]
    [InlineData("0109501101530003" + "1", "gs1.truncated_ai")]
    public void Malformed_input_fails_with_a_specific_code(string raw, string expectedCode)
    {
        var result = Gs1Parser.Parse(raw);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    [Fact]
    public void An_invalid_gtin_check_digit_is_surfaced_as_a_missing_gtin()
    {
        // The element parses — it is 14 digits — but it is not a real GTIN, so
        // item lookup must not proceed on it.
        var result = Gs1Parser.Parse("0109501101530004");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Gtin);
        Assert.Equal("09501101530004", result.Value.Find("01"));
    }

    [Fact]
    public void Two_digit_years_resolve_into_the_current_window()
    {
        var thisYear = DateTime.UtcNow.Year;
        var shortYear = (thisYear % 100).ToString("D2");

        var result = Gs1Parser.Parse($"0109501101530003" + $"17{shortYear}0601");

        Assert.True(result.IsSuccess);
        Assert.Equal(thisYear, result.Value.ExpiryDate?.Year);
    }

    [Fact]
    public void Tolerates_a_trailing_separator()
    {
        var result = Gs1Parser.Parse("0109501101530003" + "10LOT-1" + GS);

        Assert.True(result.IsSuccess);
        Assert.Equal("LOT-1", result.Value.LotNumber);
    }
}
