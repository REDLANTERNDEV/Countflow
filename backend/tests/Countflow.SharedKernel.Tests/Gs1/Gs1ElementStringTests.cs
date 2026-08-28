using Countflow.SharedKernel.Gs1;

namespace Countflow.SharedKernel.Tests.Gs1;

/// <summary>
/// The builder's job is to produce a string the parser splits back into the fields
/// it was given. Most of these therefore assert a <b>round trip</b> rather than a
/// literal — a test that asserted the exact characters would pass just as happily
/// on a string no scanner can read.
/// </summary>
public sealed class Gs1ElementStringTests
{
    private static string ValidSscc => Sscc.FromPayload("30512345000000001");

    [Fact]
    public void Encodes_a_lone_sscc_with_no_separator()
    {
        var built = Gs1ElementString.Build([("00", ValidSscc)]);

        Assert.True(built.IsSuccess);

        var encoded = built.Value.Encode(Gs1ElementString.AsciiGroupSeparator);

        Assert.Equal("00" + ValidSscc, encoded);
        Assert.DoesNotContain(Gs1ElementString.AsciiGroupSeparator, encoded, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one that matters. A predefined-length AI must not be followed by a
    /// separator and a variable-length one must be, and getting either backwards
    /// produces a symbol that still scans and decodes into the wrong fields.
    /// </summary>
    [Fact]
    public void Separates_only_after_variable_length_elements()
    {
        var built = Gs1ElementString.Build(
        [
            ("00", ValidSscc),
            ("17", "270131"),
            ("10", "BATCH-9"),
            ("37", "48")
        ]);

        Assert.True(built.IsSuccess);

        var encoded = built.Value.Encode(Gs1ElementString.AsciiGroupSeparator);

        // 00 and 17 are predefined length, so neither is separated. 10 is variable
        // and is followed by something, so it is. 37 is variable and last, so the
        // end of the symbol terminates it.
        Assert.Equal(
            $"00{ValidSscc}17270131" + "10BATCH-9" + Gs1ElementString.AsciiGroupSeparator + "3748",
            encoded);
    }

    [Fact]
    public void Round_trips_through_the_parser()
    {
        var built = Gs1ElementString.Build(
        [
            ("00", ValidSscc),
            ("02", "09501101530003"),
            ("17", "270131"),
            ("10", "LOT-77"),
            ("37", "48")
        ]);

        Assert.True(built.IsSuccess);

        var parsed = Gs1Parser.Parse(built.Value.Encode(Gs1ElementString.AsciiGroupSeparator));

        Assert.True(parsed.IsSuccess);
        Assert.Equal(ValidSscc, parsed.Value.Sscc);
        Assert.Equal("09501101530003", parsed.Value.Find("02"));
        Assert.Equal("LOT-77", parsed.Value.LotNumber);
        Assert.Equal(new DateOnly(2027, 1, 31), parsed.Value.ExpiryDate);
        Assert.Equal(48, parsed.Value.Quantity);
    }

    /// <summary>
    /// The batch number is where a real supplier puts anything it likes, and it is
    /// the element most likely to be followed by another. If the separator went
    /// missing here the expiry would be read out of the end of the lot number.
    /// </summary>
    [Fact]
    public void A_numeric_batch_number_still_terminates()
    {
        var built = Gs1ElementString.Build([("10", "123456"), ("21", "SER-1")]);

        Assert.True(built.IsSuccess);

        var parsed = Gs1Parser.Parse(built.Value.Encode(Gs1ElementString.AsciiGroupSeparator));

        Assert.True(parsed.IsSuccess);
        Assert.Equal("123456", parsed.Value.LotNumber);
        Assert.Equal("SER-1", parsed.Value.SerialNumber);
    }

    [Fact]
    public void Zpl_encoding_uses_the_printer_s_own_fnc1()
    {
        var built = Gs1ElementString.Build([("10", "LOT-1"), ("37", "6")]);

        Assert.True(built.IsSuccess);
        Assert.Equal("10LOT-1>8376", built.Value.Encode(Gs1ElementString.ZplFunction1));
    }

    [Fact]
    public void Human_readable_form_brackets_every_identifier()
    {
        var built = Gs1ElementString.Build([("00", ValidSscc), ("10", "LOT-1")]);

        Assert.True(built.IsSuccess);
        Assert.Equal($"(00){ValidSscc}(10)LOT-1", built.Value.HumanReadable());
    }

    /// <summary>
    /// A short SSCC would swallow the head of whatever came after it, because
    /// nothing terminates a predefined-length element. This is the check that
    /// cannot be a warning.
    /// </summary>
    [Fact]
    public void Refuses_a_predefined_length_element_of_the_wrong_length()
    {
        var built = Gs1ElementString.Build([("00", "12345")]);

        Assert.True(built.IsFailure);
        Assert.Equal("gs1.wrong_fixed_length", built.Error.Code);
    }

    [Fact]
    public void Refuses_a_non_numeric_value_for_a_numeric_identifier()
    {
        var built = Gs1ElementString.Build([("37", "many")]);

        Assert.True(built.IsFailure);
        Assert.Equal("gs1.non_numeric", built.Error.Code);
    }

    [Fact]
    public void Refuses_an_unknown_identifier()
    {
        var built = Gs1ElementString.Build([("99", "1")]);

        Assert.True(built.IsFailure);
        Assert.Equal("gs1.unknown_ai", built.Error.Code);
    }

    [Fact]
    public void Refuses_an_identifier_with_no_value_rather_than_dropping_it()
    {
        var built = Gs1ElementString.Build([("00", ValidSscc), ("10", "")]);

        Assert.True(built.IsFailure);
        Assert.Equal("gs1.empty_value", built.Error.Code);
    }

    [Fact]
    public void Refuses_a_value_longer_than_the_identifier_holds()
    {
        var built = Gs1ElementString.Build([("10", new string('X', 21))]);

        Assert.True(built.IsFailure);
        Assert.Equal("gs1.value_too_long", built.Error.Code);
    }

    [Fact]
    public void Refuses_the_same_identifier_twice()
    {
        var built = Gs1ElementString.Build([("10", "A"), ("10", "B")]);

        Assert.True(built.IsFailure);
        Assert.Equal("gs1.duplicate_ai", built.Error.Code);
    }

    [Fact]
    public void Refuses_an_empty_element_string()
    {
        Assert.True(Gs1ElementString.Build([]).IsFailure);
    }
}
