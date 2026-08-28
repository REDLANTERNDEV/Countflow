using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Countflow.Modules.Identity.Domain;
using Countflow.Modules.MasterData.Domain;
using Countflow.Modules.MasterData.Persistence;
using Countflow.Modules.MasterData.Persistence.Seeding;
using Countflow.SharedKernel.Gs1;
using Countflow.SharedKernel.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Countflow.IntegrationTests;

/// <summary>
/// The pallet label end to end: a blind receipt arrives with no supplier licence
/// plate, the warehouse mints one on its own GS1 prefix, and the label that comes
/// out carries a barcode a scanner can split back into the fields the ledger holds.
/// <para>
/// The class exists to prove that sentence. Everything else here defends the
/// properties that make it worth anything: that a template is refused before it can
/// print rather than at a rack, that a mixed pallet says "mixed" instead of naming
/// one of its SKUs, that a second print is recorded as a second print, and that a
/// handheld's retry is not one.
/// </para>
/// </summary>
public sealed class LabelsLoopTests(CountflowApiFixture fixture)
    : IClassFixture<CountflowApiFixture>, IAsyncLifetime
{
    private const string Secret = "Countflow#2026";

    private static object Credentials(string identifier, string secret) =>
        new { identifier, password = secret };

    private HttpClient _admin = null!;
    private HttpClient _supervisor = null!;
    private HttpClient _operator = null!;
    private string _warehouseCode = string.Empty;
    private string _dock = string.Empty;
    private int _counter;

    public async Task InitializeAsync()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

            if (!await masterData.Warehouses.AnyAsync())
            {
                var layout = new WarehouseLayoutBuilder().Build();
                masterData.Warehouses.Add(layout.Warehouse);
                masterData.Areas.AddRange(layout.Areas);
                masterData.Zones.AddRange(layout.Zones);
                masterData.Locations.AddRange(layout.Locations);
                await masterData.SaveChangesAsync();
            }

            _warehouseCode = await masterData.Warehouses.Select(w => w.Code).FirstAsync();

            _dock = await masterData.Locations
                .Where(l => l.Type == LocationType.ReceivingDock)
                .OrderBy(l => l.Code)
                .Select(l => l.Code)
                .FirstAsync();

            await EnsureUsersAsync(scope.ServiceProvider);
        }

        _admin = await SignInAsync("labels-admin");
        _supervisor = await SignInAsync("labels-supervisor");
        _operator = await SignInAsync("labels-operator");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------- the loop

    [Fact]
    public async Task A_blind_receipt_mints_a_licence_plate_and_the_label_scans_back_to_what_the_ledger_holds()
    {
        var sku = await NewItemAsync(caseGtin: "09501101530003", caseSize: 12);

        // 1. Nothing is supplied. This is the blind receipt the whole module exists
        //    for: a pallet with no readable identity until this call gives it one.
        var label = await Read<PrintedLabel>(await PostAsync(
            _operator, "/api/v1/labels/prints", new { kind = "Pallet", warehouseCode = _warehouseCode }));

        Assert.True(Sscc.IsValid(label.Sscc), $"{label.Sscc} is not a valid SSCC");
        Assert.False(label.IsReprint);
        Assert.NotNull(label.PrintId);
        Assert.Equal("Default pallet label", label.TemplateName);
        Assert.Equal(1, label.TemplateVersion);

        // 2. The label came off the seeded template and is real ZPL.
        Assert.StartsWith("^XA", label.Zpl, StringComparison.Ordinal);
        Assert.EndsWith("^XZ", label.Zpl, StringComparison.Ordinal);

        // 3. The plate exists now, before any stock is posted against it. Printing
        //    the label is what made it real.
        var beforeStock = await GetAsync<List<PrintRow>>($"/api/v1/labels/prints?sscc={label.Sscc}");
        Assert.Single(beforeStock);

        // 4. Goods land on it.
        await PostAsync(_operator, "/api/v1/inbound/receipts", new
        {
            sku,
            quantity = 48m,
            toLocationCode = _dock,
            sscc = label.Sscc,
            lotNumber = "LOT-A",
            expiryDate = "2027-01-31"
        });

        // 5. And the label now says what is on it — including a barcode that a
        //    scanner splits back into the same batch, expiry and case count.
        var filled = await Read<PrintedLabel>(await PostAsync(
            _operator, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode, sscc = label.Sscc }));

        Assert.True(filled.IsReprint, "a second label for one pallet is a reprint and is recorded as one");

        var scanned = Gs1Parser.Parse(BarcodeData(filled.Zpl));

        Assert.True(scanned.IsSuccess);
        Assert.Equal(label.Sscc, scanned.Value.Sscc);
        Assert.Equal("LOT-A", scanned.Value.LotNumber);
        Assert.Equal(new DateOnly(2027, 1, 31), scanned.Value.ExpiryDate);

        // AI 02 is the GTIN of the contained trade items — the case's, never the
        // loose unit's — and AI 37 counts those, so 48 base units of a 12-pack is
        // four cases and not forty-eight.
        Assert.Equal("09501101530003", scanned.Value.Find("02"));
        Assert.Equal(4, scanned.Value.Quantity);
    }

    // ------------------------------------------------- what a label may claim

    /// <summary>
    /// The property the binding contract exists to hold: a label never asserts
    /// something the ledger does not. A pallet with two SKUs on it has no batch
    /// number and no SKU, and it says "mixed" rather than naming the first thing
    /// it found.
    /// </summary>
    [Fact]
    public async Task A_mixed_pallet_says_mixed_rather_than_naming_one_of_its_skus()
    {
        var first = await NewItemAsync();
        var second = await NewItemAsync();

        var sscc = NewSscc();

        foreach (var sku in new[] { first, second })
        {
            await PostAsync(_operator, "/api/v1/inbound/receipts", new
            {
                sku,
                quantity = 10m,
                toLocationCode = _dock,
                sscc
            });
        }

        var label = await Read<PrintedLabel>(await PostAsync(
            _operator, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode, sscc }));

        Assert.DoesNotContain(first, label.Zpl, StringComparison.Ordinal);
        Assert.DoesNotContain(second, label.Zpl, StringComparison.Ordinal);

        // And the barcode carries the identity alone — no content block it cannot
        // honestly fill in.
        var scanned = Gs1Parser.Parse(BarcodeData(label.Zpl));

        Assert.True(scanned.IsSuccess);
        Assert.Equal(sscc, scanned.Value.Sscc);
        Assert.Null(scanned.Value.LotNumber);
        Assert.Null(scanned.Value.Find("02"));
    }

    /// <summary>
    /// A pallet labelled and not yet built is the ordinary state of a label between
    /// the printer and the pallet, and it has to render rather than refuse.
    /// </summary>
    [Fact]
    public async Task An_unbuilt_pallet_still_prints()
    {
        var label = await Read<PrintedLabel>(await PostAsync(
            _operator, "/api/v1/labels/prints", new { kind = "Pallet", warehouseCode = _warehouseCode }));

        Assert.Contains("^XA", label.Zpl, StringComparison.Ordinal);
        Assert.Equal("00" + label.Sscc, BarcodeData(label.Zpl));
    }

    // ------------------------------------------------------------- the mint

    /// <summary>
    /// A serial is never reissued, which is the strictest rule in this codebase and
    /// the reason the mint draws from a sequence rather than from a MAX over a
    /// table. Two prints, two plates, always.
    /// </summary>
    [Fact]
    public async Task Every_minted_licence_plate_is_distinct()
    {
        var minted = new List<string>();

        for (var index = 0; index < 8; index++)
        {
            var label = await Read<PrintedLabel>(await PostAsync(
                _operator, "/api/v1/labels/prints", new { kind = "Pallet", warehouseCode = _warehouseCode }));

            minted.Add(label.Sscc);
        }

        Assert.Equal(minted.Count, minted.Distinct(StringComparer.Ordinal).Count());
        Assert.All(minted, sscc => Assert.True(Sscc.IsValid(sscc)));
    }

    /// <summary>
    /// A site with no GS1 prefix is refused rather than given eighteen
    /// valid-looking digits belonging to another company. A conflict, not a bad
    /// request: the caller cannot fix it by sending something else.
    /// </summary>
    [Fact]
    public async Task A_site_with_no_company_prefix_cannot_mint()
    {
        var code = await NewWarehouseWithoutPrefixAsync();

        var response = await PostAsync(
            _operator, "/api/v1/labels/prints", new { kind = "Pallet", warehouseCode = code });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("masterdata.no_company_prefix", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // -------------------------------------------------------- the print record

    /// <summary>
    /// A handheld's outbox retries. A retry that recorded a second print would
    /// report a reprint that never happened — which is exactly the fact this table
    /// exists to be trusted about.
    /// </summary>
    [Fact]
    public async Task A_retried_print_is_the_same_print_and_not_a_reprint()
    {
        var key = Guid.CreateVersion7().ToString();
        var sscc = NewSscc();

        var first = await Read<PrintedLabel>(await PostAsync(
            _operator, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode, sscc }, key));

        var retry = await Read<PrintedLabel>(await PostAsync(
            _operator, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode, sscc }, key));

        Assert.Equal(first.PrintId, retry.PrintId);
        Assert.False(retry.IsReprint);

        Assert.Single(await GetAsync<List<PrintRow>>($"/api/v1/labels/prints?sscc={sscc}"));
    }

    [Fact]
    public async Task A_print_without_an_idempotency_key_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/labels/prints")
        {
            Content = JsonContent.Create(new { kind = "Pallet", warehouseCode = _warehouseCode })
        };

        var response = await _operator.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The audit question this table exists for: which pallets were labelled twice.
    /// </summary>
    [Fact]
    public async Task A_reprint_is_recorded_with_who_made_it()
    {
        var sscc = NewSscc();

        await PostAsync(_operator, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode, sscc });

        await PostAsync(_supervisor, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode, sscc });

        var prints = await GetAsync<List<PrintRow>>($"/api/v1/labels/prints?sscc={sscc}");

        Assert.Equal(2, prints.Count);
        Assert.Contains(prints, print => print is { IsReprint: true, PrintedBy: "labels-supervisor" });
        Assert.Contains(prints, print => print is { IsReprint: false, PrintedBy: "labels-operator" });

        var reprints = await GetAsync<List<PrintRow>>($"/api/v1/labels/prints?sscc={sscc}&reprintsOnly=true");

        Assert.Single(reprints);
    }

    /// <summary>
    /// Printing is not a stock movement. Nothing moved — a stocktake would find
    /// exactly what it found before — so the ledger has nothing to say about it.
    /// The same test ADR 0001 implies for a reservation and a no-variance count.
    /// </summary>
    [Fact]
    public async Task Printing_posts_nothing_to_the_ledger()
    {
        var before = await GetAsync<MovementPage>("/api/v1/inventory/movements?pageSize=1");

        await PostAsync(_operator, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode });

        var after = await GetAsync<MovementPage>("/api/v1/inventory/movements?pageSize=1");

        Assert.Equal(before.Total, after.Total);
    }

    // ---------------------------------------------------- the template lifecycle

    /// <summary>
    /// The whole promise of holding a template as data: a layout change is a record
    /// and a promotion, not a deploy — and the version that produced a label is
    /// still readable afterwards.
    /// </summary>
    [Fact]
    public async Task A_layout_change_is_a_promotion_and_the_older_version_is_still_named_by_its_prints()
    {
        var sscc = NewSscc();

        var beforeChange = await Read<PrintedLabel>(await PostAsync(
            _operator, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode, sscc }));

        var seededVersion = beforeChange.TemplateVersion;

        var draft = await Read<TemplateBody>(await PostAsync(_supervisor, "/api/v1/labels/templates", new
        {
            kind = "Pallet",
            name = "Pallet label with a customer caption",
            cloneFromTemplateId = beforeChange.TemplateId
        }));

        Assert.Equal("Draft", draft.Status);
        Assert.Equal(0, draft.Version);
        Assert.NotEmpty(draft.Elements);

        // Add a caption the seeded layout does not have.
        var elements = draft.Elements
            .Select(element => new
            {
                type = element.Type,
                order = element.Order,
                x = element.X,
                y = element.Y,
                rotation = element.Rotation,
                fieldCode = element.FieldCode,
                literal = element.Literal,
                fontHeight = element.FontHeight,
                fontWidth = element.FontWidth,
                symbology = element.Symbology,
                barcodeHeight = element.BarcodeHeight,
                showInterpretation = element.ShowInterpretation,
                widthDots = element.WidthDots,
                heightDots = element.HeightDots,
                thicknessDots = element.ThicknessDots
            })
            .Cast<object>()
            .Append(new
            {
                type = "Text",
                order = 99,
                x = 30,
                y = 960,
                rotation = "N",
                fieldCode = (string?)null,
                literal = "HANDLE WITH CARE",
                fontHeight = (int?)28,
                fontWidth = (int?)null,
                symbology = (string?)null,
                barcodeHeight = (int?)null,
                showInterpretation = true,
                widthDots = (int?)null,
                heightDots = (int?)null,
                thicknessDots = (int?)null
            })
            .ToList();

        var replaced = await _supervisor.PutAsJsonAsync(
            $"/api/v1/labels/templates/{draft.Id}", new { elements });

        Assert.True(replaced.IsSuccessStatusCode, await replaced.Content.ReadAsStringAsync());

        var promoted = await Read<TemplateBody>(await PostAsync(
            _admin, $"/api/v1/labels/templates/{draft.Id}/promote", new { }));

        Assert.Equal("Active", promoted.Status);
        Assert.Equal(seededVersion + 1, promoted.Version);

        // The floor is printing the new layout.
        var afterChange = await Read<PrintedLabel>(await PostAsync(
            _operator, "/api/v1/labels/prints",
            new { kind = "Pallet", warehouseCode = _warehouseCode, sscc = NewSscc() }));

        Assert.Equal(promoted.Version, afterChange.TemplateVersion);
        Assert.Contains("HANDLE WITH CARE", afterChange.Zpl, StringComparison.Ordinal);

        // And the label printed before the change still names the version that made
        // it. This is what the stamp is for.
        var older = (await GetAsync<List<PrintRow>>($"/api/v1/labels/prints?sscc={sscc}")).Single();

        Assert.Equal(seededVersion, older.TemplateVersion);
        Assert.NotEqual(promoted.Version, older.TemplateVersion);
    }

    /// <summary>
    /// A template is checked by rendering it, at the moment somebody presses save
    /// rather than at a rack with a pallet waiting. This is the module's whole
    /// safety story, and it is the same one <c>RuleVocabulary.Builds</c> tells.
    /// </summary>
    [Fact]
    public async Task A_template_referencing_a_field_that_does_not_exist_is_refused_at_save_time()
    {
        var response = await PostAsync(_supervisor, "/api/v1/labels/templates", new
        {
            kind = "Pallet",
            name = "Broken",
            widthDots = 812,
            heightDots = 1218,
            elements = new[]
            {
                new { type = "Text", order = 0, x = 30, y = 30, fieldCode = "pallet.customer_slogan", fontHeight = 30 }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("labels.unknown_field", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_active_template_cannot_be_edited()
    {
        var active = (await GetAsync<List<TemplateBody>>("/api/v1/labels/templates?kind=Pallet&status=Active")).Single();

        var response = await _supervisor.PutAsJsonAsync(
            $"/api/v1/labels/templates/{active.Id}",
            new { elements = new[] { new { type = "Text", order = 0, x = 10, y = 10, literal = "X", fontHeight = 20 } } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("labels.template_not_editable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Only an admin puts a layout on the floor, matching rule-set promotion. A
    /// supervisor authors; an admin decides what the building prints.
    /// </summary>
    [Fact]
    public async Task Promotion_is_admin_only()
    {
        var draft = await Read<TemplateBody>(await PostAsync(_supervisor, "/api/v1/labels/templates", new
        {
            kind = "Pallet",
            name = "Supervisor's draft",
            widthDots = 812,
            heightDots = 1218,
            elements = new[]
            {
                new { type = "Text", order = 0, x = 30, y = 30, fieldCode = "pallet.sscc", fontHeight = 30 }
            }
        }));

        var refused = await PostAsync(_supervisor, $"/api/v1/labels/templates/{draft.Id}/promote", new { });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// A preview may name a draft — that is most of its value — and it records
    /// nothing and mints nothing. Looking at a label is not making one.
    /// </summary>
    [Fact]
    public async Task A_preview_renders_a_draft_and_records_nothing()
    {
        var sscc = NewSscc();

        var draft = await Read<TemplateBody>(await PostAsync(_supervisor, "/api/v1/labels/templates", new
        {
            kind = "Pallet",
            name = "Draft being looked at",
            widthDots = 812,
            heightDots = 1218,
            elements = new[]
            {
                new { type = "Text", order = 0, x = 30, y = 30, literal = "PREVIEW ONLY", fontHeight = 30 }
            }
        }));

        var preview = await Read<PrintedLabel>(await PostAsync(_supervisor, "/api/v1/labels/renderings", new
        {
            kind = "Pallet",
            warehouseCode = _warehouseCode,
            sscc,
            templateId = draft.Id
        }));

        Assert.Contains("PREVIEW ONLY", preview.Zpl, StringComparison.Ordinal);
        Assert.Null(preview.PrintId);

        Assert.Empty(await GetAsync<List<PrintRow>>($"/api/v1/labels/prints?sscc={sscc}"));
    }

    [Fact]
    public async Task An_operator_may_print_but_not_preview()
    {
        var refused = await PostAsync(_operator, "/api/v1/labels/renderings", new
        {
            kind = "Pallet",
            warehouseCode = _warehouseCode,
            sscc = NewSscc()
        });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// The published seam between data and code: everything here is a template's to
    /// arrange, and anything not here needs a change to the server.
    /// </summary>
    [Fact]
    public async Task The_vocabulary_says_what_a_template_may_reference()
    {
        var kinds = await GetAsync<List<KindBody>>("/api/v1/labels/kinds");

        var pallet = Assert.Single(kinds, kind => kind.Kind == "Pallet");

        Assert.Contains(pallet.Fields, field => field.Code == "pallet.sscc" && field.IsAlwaysPresent);
        Assert.Contains(pallet.Fields, field => field.Code == "pallet.lot_number" && !field.IsAlwaysPresent);
        Assert.DoesNotContain(pallet.Fields, field => field.Code == "pallet.customer_slogan");
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Pulls the barcode's data out of the emitted ZPL and undoes the hex escaping,
    /// so the test can hand it to the parser exactly as a scanner would.
    /// </summary>
    private static string BarcodeData(string zpl)
    {
        var barcode = zpl.IndexOf("^BC", StringComparison.Ordinal);

        Assert.True(barcode >= 0, "the label carries no linear barcode");

        var start = zpl.IndexOf("^FD", barcode, StringComparison.Ordinal) + 3;
        var end = zpl.IndexOf("^FS", start, StringComparison.Ordinal);

        return zpl[start..end]
            .Replace(">8", Gs1Parser.GroupSeparator.ToString(), StringComparison.Ordinal)
            .Replace("_5E", "^", StringComparison.Ordinal)
            .Replace("_7E", "~", StringComparison.Ordinal)
            .Replace("_5C", "\\", StringComparison.Ordinal)
            .Replace("_5F", "_", StringComparison.Ordinal);
    }

    private async Task<string> NewItemAsync(
        string? caseGtin = null,
        int caseSize = 0,
        [CallerMemberName] string caller = "")
    {
        var sku = $"LBL-{Interlocked.Increment(ref _counter):D3}-{caller[..Math.Min(caller.Length, 24)]}";

        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var item = new Item
        {
            Sku = sku,
            Description = $"Label test item for {caller}",
            BaseUom = "EA",
            TemperatureClass = TemperatureClass.Ambient,
            AbcClass = AbcClass.B,
            IsLotTracked = true,
            IsExpiryTracked = true
        };

        item.PackConfigurations.Add(new PackConfiguration
        {
            ItemId = item.Id,
            Level = PackagingLevel.Each,
            BaseUnitQuantity = 1,
            LengthMm = 100m,
            WidthMm = 100m,
            HeightMm = 100m,
            GrossWeightKg = 0.5m,
            IsPickUnit = true
        });

        if (caseGtin is not null)
        {
            // The rung that matters for a logistics label: AI 02 is the GTIN of the
            // contained *trade items*, and AI 37 counts those rather than base units.
            item.PackConfigurations.Add(new PackConfiguration
            {
                ItemId = item.Id,
                Level = PackagingLevel.Case,
                BaseUnitQuantity = caseSize,
                Gtin = caseGtin,
                IsReceivingUnit = true
            });
        }

        masterData.Items.Add(item);
        await masterData.SaveChangesAsync();

        return sku;
    }

    private async Task<string> NewWarehouseWithoutPrefixAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var masterData = scope.ServiceProvider.GetRequiredService<MasterDataDbContext>();

        var code = $"WHX{Interlocked.Increment(ref _counter):D2}";

        masterData.Warehouses.Add(new Warehouse
        {
            Code = code,
            Name = "Site with no GS1 prefix",
            Gs1CompanyPrefix = null
        });

        await masterData.SaveChangesAsync();

        return code;
    }

    private static string NewSscc() =>
        Sscc.FromPayload($"3{Random.Shared.NextInt64(0, 9_999_999_999_999_999L):D16}");

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        object body,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.CreateVersion7().ToString());

        return await client.SendAsync(request);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _supervisor.GetAsync(url);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpClient> SignInAsync(string userName)
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(userName, Secret));
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);

        return client;
    }

    private async Task EnsureUsersAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<CountflowRole>>();
        var users = services.GetRequiredService<UserManager<CountflowUser>>();

        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new CountflowRole(role));
            }
        }

        await EnsureAsync("labels-admin", "LBL-ADM", Roles.Admin);
        await EnsureAsync("labels-supervisor", "LBL-SUP", Roles.Supervisor);
        await EnsureAsync("labels-operator", "LBL-OP1", Roles.Operator);

        async Task EnsureAsync(string userName, string operatorCode, string role)
        {
            if (await users.FindByNameAsync(userName) is not null)
            {
                return;
            }

            var user = new CountflowUser
            {
                UserName = userName,
                Email = $"{userName}@countflow.test",
                DisplayName = userName,
                OperatorCode = operatorCode
            };

            await users.CreateAsync(user, Secret);
            await users.AddToRoleAsync(user, role);
        }
    }

    // ----------------------------------------------------------- wire shapes

    private sealed record LoginResponse(string AccessToken);

    private sealed record PrintedLabel(
        string Zpl,
        string Kind,
        string Sscc,
        Guid TemplateId,
        string TemplateName,
        int TemplateVersion,
        bool IsReprint,
        Guid? PrintId,
        DateTimeOffset PrintedAt);

    private sealed record PrintRow(
        Guid Id,
        string Kind,
        string Sscc,
        Guid TemplateId,
        string TemplateName,
        int TemplateVersion,
        bool IsReprint,
        string PayloadHash,
        DateTimeOffset PrintedAt,
        string PrintedBy);

    private sealed record ElementBody(
        Guid Id,
        string Type,
        int Order,
        int X,
        int Y,
        string Rotation,
        string? FieldCode,
        string? Literal,
        int? FontHeight,
        int? FontWidth,
        string? Symbology,
        int? BarcodeHeight,
        bool ShowInterpretation,
        int? WidthDots,
        int? HeightDots,
        int? ThicknessDots);

    private sealed record TemplateBody(
        Guid Id,
        string Kind,
        string Name,
        int Version,
        string Status,
        Guid? WarehouseId,
        int WidthDots,
        int HeightDots,
        int? Darkness,
        string? Notes,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ActivatedAt,
        IReadOnlyList<ElementBody> Elements);

    private sealed record FieldBody(string Code, string Title, bool IsAlwaysPresent);

    private sealed record KindBody(string Kind, IReadOnlyList<FieldBody> Fields);

    private sealed record MovementPage(int Total);
}
