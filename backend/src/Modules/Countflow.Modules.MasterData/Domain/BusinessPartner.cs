namespace Countflow.Modules.MasterData.Domain;

[Flags]
public enum PartnerRole
{
    None = 0,
    Supplier = 1,
    Customer = 2,
    Carrier = 4
}

/// <summary>
/// An external party goods come from or go to. Flags rather than a single kind:
/// the same company is routinely both a supplier and a customer, and returns
/// make that common rather than exotic.
/// </summary>
public sealed class BusinessPartner
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Code { get; set; }

    public required string Name { get; set; }

    public required PartnerRole Roles { get; set; }

    /// <summary>
    /// GS1 Global Location Number. What EDI trading partners identify each other
    /// by, so it is worth carrying from the start even before EDI lands.
    /// </summary>
    public string? Gln { get; set; }

    public string? City { get; set; }

    public string? CountryCode { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
