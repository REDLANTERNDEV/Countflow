using System.Globalization;
using Countflow.Modules.MasterData.Domain;
using Countflow.SharedKernel.Gs1;

namespace Countflow.IntegrationTests;

/// <summary>
/// Guards identifier handling against locale-sensitive casing.
/// <para>
/// Turkish has dotted and dotless i: under <c>tr-TR</c>, <c>"i".ToUpper()</c> is
/// <c>"İ"</c> and <c>"I".ToLower()</c> is <c>"ı"</c>. Location codes are
/// upper-case alphanumeric, so a culture-sensitive conversion anywhere on that
/// path corrupts identifiers <em>for Turkish users only</em> — the worst shape
/// of bug, because it never reproduces for whoever wrote the code.
/// </para>
/// <para>
/// Today the backend runs with <c>InvariantGlobalization</c>, which removes the
/// hazard at the platform level: <c>tr-TR</c> cannot even be constructed. These
/// tests assert that, and automatically become live comparisons the moment
/// somebody enables globalization — which is exactly when the guard is needed.
/// </para>
/// <para>No database fixture, so they stay fast.</para>
/// </summary>
public sealed class TurkishLocaleTests
{
    /// <summary>The Turkish culture, or null when the runtime is invariant-only.</summary>
    private static CultureInfo? TurkishCulture()
    {
        try
        {
            return new CultureInfo("tr-TR");
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static T UnderTurkish<T>(Func<T> action)
    {
        var turkish = TurkishCulture();
        if (turkish is null)
        {
            return action();
        }

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = turkish;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_runtime_is_invariant_or_the_hazard_is_real()
    {
        var turkish = TurkishCulture();

        if (turkish is null)
        {
            // Invariant mode. The dotted-i hazard cannot occur because no
            // culture other than invariant exists. This is the current state and
            // is deliberate — see the note on InvariantGlobalization in
            // Directory.Build.props.
            return;
        }

        // Globalization has been enabled. Confirm the hazard is genuinely
        // present, so the remaining tests in this file are doing real work
        // rather than passing vacuously.
        Assert.Equal("İ", "i".ToUpper(turkish));
        Assert.Equal("ı", "I".ToLower(turkish));
    }

    [Fact]
    public void Invariant_casing_ignores_the_ambient_culture()
    {
        // The property every identifier path depends on, asserted unconditionally
        // because it holds in either mode.
        Assert.Equal("I", UnderTurkish(() => "i".ToUpperInvariant()));
        Assert.Equal("i", UnderTurkish(() => "I".ToLowerInvariant()));
    }

    [Theory]
    [InlineData("WH1", "STOR", "AMB", "A07", "03", "02", "B")]
    // Segments deliberately containing both i-forms — the exact input that
    // breaks under a culture-sensitive conversion.
    [InlineData("WHI", "MAIN", "III", "I07", "01", "01", "I")]
    public void Location_codes_build_identically_under_any_culture(
        string warehouse, string area, string zone, string aisle, string bay, string level, string position)
    {
        var invariant = LocationCode.ForBin(warehouse, area, zone, aisle, bay, level, position);
        var turkish = UnderTurkish(() => LocationCode.ForBin(warehouse, area, zone, aisle, bay, level, position));

        Assert.Equal(invariant, turkish);
        Assert.DoesNotContain('İ', turkish);
        Assert.DoesNotContain('ı', turkish);
    }

    [Fact]
    public void Virtual_location_codes_are_culture_independent()
    {
        // ForVirtual upper-cases the enum name. "InTransit" contains an 'i', so
        // a culture-sensitive ToUpper would yield "INTRANSİT" in Turkish and the
        // ledger would fail to find its own counterparty location.
        foreach (var type in new[]
                 {
                     LocationType.Supplier,
                     LocationType.Customer,
                     LocationType.Adjustment,
                     LocationType.Scrap,
                     LocationType.InTransit,
                 })
        {
            var invariant = LocationCode.ForVirtual("WH1", type);
            var turkish = UnderTurkish(() => LocationCode.ForVirtual("WH1", type));

            Assert.Equal(invariant, turkish);
            Assert.DoesNotContain('İ', turkish);
            Assert.DoesNotContain('ı', turkish);
        }

        Assert.Equal("WH1-VIRT-INTRANSIT", LocationCode.ForVirtual("WH1", LocationType.InTransit));
    }

    [Fact]
    public void Segment_validation_agrees_across_cultures()
    {
        foreach (var segment in new[] { "A07", "WHI", "III", "ABC123", "i07" })
        {
            Assert.Equal(
                LocationCode.IsValidSegment(segment),
                UnderTurkish(() => LocationCode.IsValidSegment(segment)));
        }
    }

    [Fact]
    public void Gtin_parsing_is_culture_independent()
    {
        const string candidate = "09501101530003";

        Assert.True(Gtin.TryParse(candidate, out var invariant));

        var turkish = UnderTurkish(() =>
        {
            Gtin.TryParse(candidate, out var parsed);
            return parsed!.Value.Value;
        });

        Assert.Equal(invariant!.Value.Value, turkish);
    }

    [Fact]
    public void Gs1_parsing_is_culture_independent()
    {
        const string barcode = "010950110153000310LOT-4711";

        var invariant = Gs1Parser.Parse(barcode);
        var turkish = UnderTurkish(() => Gs1Parser.Parse(barcode));

        Assert.True(invariant.IsSuccess);
        Assert.True(turkish.IsSuccess);
        Assert.Equal(invariant.Value.Gtin?.Value, turkish.Value.Gtin?.Value);
        Assert.Equal(invariant.Value.LotNumber, turkish.Value.LotNumber);
    }

    [Fact]
    public void Gs1_measure_decimals_do_not_pick_up_a_Turkish_decimal_comma()
    {
        // AI 3103 means three implied decimal places. Turkish writes 0,123 —
        // parsing the payload with the ambient culture would misread it, so the
        // parser uses InvariantCulture explicitly.
        const string barcode = "0109501101530003" + "3103000123";

        var turkish = UnderTurkish(() => Gs1Parser.Parse(barcode));

        Assert.True(turkish.IsSuccess);
        Assert.Equal(0.123m, turkish.Value.NetWeightKg);
    }
}
