using Countflow.Modules.Inventory.Domain;
using Countflow.SharedKernel.Modules;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Persistence;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options)
    : DbContext(options), IModuleDbContext
{
    public const string SchemaName = "inventory";

    /// <summary>The ledger. Append-only; see <see cref="StockMovement"/>.</summary>
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    /// <summary>The projection of the ledger. Rebuildable, never authoritative.</summary>
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();

    /// <summary>
    /// Claims against the projection. Not part of the ledger and not derived from
    /// it — see <see cref="StockReservation"/> for why a promise is not a movement,
    /// and why it lives in this schema anyway.
    /// </summary>
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    public DbSet<Lot> Lots => Set<Lot>();

    public DbSet<LicensePlate> LicensePlates => Set<LicensePlate>();

    /// <summary>
    /// The second of the three guards on the append-only invariant: nothing can
    /// persist a modified or deleted ledger row through this context, whatever it
    /// asks for.
    /// <para>
    /// This throws rather than returning a <c>Result</c>. A domain failure is
    /// something a user can legitimately cause and be told about; an attempt to
    /// rewrite the ledger is a programming error, and the only useful response is
    /// a stack trace pointing at the line that tried.
    /// </para>
    /// <para>
    /// Overriding this overload is sufficient: the single-argument
    /// <c>SaveChangesAsync(CancellationToken)</c> delegates to it.
    /// </para>
    /// </summary>
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardLedgerIsAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardLedgerIsAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void GuardLedgerIsAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<StockMovement>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"The stock ledger is append-only (ADR 0001): attempted to {entry.State} " +
                    $"movement {entry.Entity.Id}. Corrections are compensating entries — " +
                    "post a Reversal instead.");
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(SchemaName);

        builder.Entity<StockMovement>(movement =>
        {
            movement.HasKey(m => m.Id);

            // Postgres identity, not an EF-side counter: replay depends on a total
            // order that holds across concurrent writers, and only the database
            // can hand those out. ALWAYS rather than BY DEFAULT so no insert can
            // supply its own sequence number.
            movement.Property(m => m.Sequence)
                .ValueGeneratedOnAdd()
                .UseIdentityAlwaysColumn();

            movement.HasIndex(m => m.Sequence).IsUnique();

            movement.Property(m => m.Quantity).HasPrecision(18, 4);
            movement.Property(m => m.IdempotencyKey).HasMaxLength(128).IsRequired();
            movement.Property(m => m.UserName).HasMaxLength(256).IsRequired();
            movement.Property(m => m.Notes).HasMaxLength(1000);

            // The idempotency guarantee, in one line. A retried command collides
            // here rather than posting a second entry, and the collision is what
            // the ledger uses to recognise the retry and return the first result.
            movement.HasIndex(m => m.IdempotencyKey).IsUnique();

            // A movement may be reversed once. A second compensating entry against
            // the same line would double-count the correction.
            movement.HasIndex(m => m.ReversesMovementId)
                .IsUnique()
                .HasFilter("\"ReversesMovementId\" IS NOT NULL");

            // Enquiry paths: stock card for an item, and everything that touched a
            // location. Both are read with a time bound, hence the composite.
            movement.HasIndex(m => new { m.ItemId, m.Sequence });
            movement.HasIndex(m => new { m.FromLocationId, m.Sequence });
            movement.HasIndex(m => new { m.ToLocationId, m.Sequence });
            movement.HasIndex(m => m.OccurredAt);
            movement.HasIndex(m => m.LotId);
            movement.HasIndex(m => m.TaskId);

            movement.ToTable(table =>
            {
                // Direction is carried by the from/to pair. Allowing a negative
                // quantity as well would let one movement be expressed two ways,
                // and every aggregate downstream would have to handle both.
                table.HasCheckConstraint(
                    "CK_StockMovements_PositiveQuantity",
                    "\"Quantity\" > 0");

                // A movement whose two sides are identical changes nothing but
                // still tells an auditor something happened.
                table.HasCheckConstraint(
                    "CK_StockMovements_DistinctHoldings",
                    """
                    NOT ("FromLocationId" = "ToLocationId"
                         AND "FromLicensePlateId" IS NOT DISTINCT FROM "ToLicensePlateId"
                         AND "FromStatus" = "ToStatus")
                    """);
            });
        });

        builder.Entity<StockBalance>(balance =>
        {
            balance.HasKey(b => b.Id);

            balance.Property(b => b.Quantity).HasPrecision(18, 4);

            // The balance key. AreNullsDistinct(false) maps to NULLS NOT
            // DISTINCT: without it Postgres treats each null lot and null licence
            // plate as unique, so the same shelf would grow a new balance row on
            // every posting and no ON CONFLICT would ever fire.
            balance.HasIndex(b => new
                {
                    b.ItemId,
                    b.LotId,
                    b.LocationId,
                    b.LicensePlateId,
                    b.Status
                })
                .IsUnique()
                .AreNullsDistinct(false);

            // Stock enquiry by location, and the allocation scan for an item.
            balance.HasIndex(b => new { b.LocationId, b.ItemId });
            balance.HasIndex(b => new { b.ItemId, b.Status });
            balance.HasIndex(b => b.LicensePlateId);

            balance.ToTable(table =>
                // The invariant that matters most, enforced by the database rather
                // than by the posting code: a physical location cannot hold
                // negative stock. Virtual counterparties can and must — they are
                // the contra accounts, and a warehouse that has received anything
                // owes its supplier account exactly that much.
                table.HasCheckConstraint(
                    "CK_StockBalances_NonNegativeUnlessCounterparty",
                    "\"Quantity\" >= 0 OR \"AllowsNegative\""));
        });

        builder.Entity<StockReservation>(reservation =>
        {
            reservation.HasKey(r => r.Id);

            reservation.Property(r => r.Quantity).HasPrecision(18, 4);

            // 256 rather than the 128 a movement's key gets, because this one sits
            // at the bottom of a derivation chain: a wave's key begets an order's,
            // which begets a line's, which begets one per holding. Four identifiers
            // deep is genuinely what makes a retried wave release idempotent at
            // every level, and truncating it to fit would break exactly that.
            reservation.Property(r => r.IdempotencyKey).HasMaxLength(256).IsRequired();
            reservation.Property(r => r.OwnerReference).HasMaxLength(128).IsRequired();
            reservation.Property(r => r.UserName).HasMaxLength(256).IsRequired();
            reservation.Property(r => r.ReleaseReason).HasMaxLength(500);

            // The one foreign key in this module, and it stays inside the schema:
            // a claim without the holding it stands against is meaningless, and
            // balance rows are never deleted, so it can never be the thing that
            // blocks a write. Restrict rather than cascade — a delete that took
            // live commitments with it is a delete that should have failed.
            reservation.HasOne(r => r.Balance!)
                .WithMany()
                .HasForeignKey(r => r.BalanceId)
                .OnDelete(DeleteBehavior.Restrict);

            // Exactly-once reserving. A retried order release collides here rather
            // than committing the same pallet to one order twice.
            reservation.HasIndex(r => r.IdempotencyKey).IsUnique();

            // The availability query: open claims against a holding, summed on
            // every allocation and on every page of the stock screen.
            reservation.HasIndex(r => new { r.BalanceId, r.State });

            // "What is this order still holding?" — the cancellation path, and the
            // repair an interrupted release runs before it allocates again.
            reservation.HasIndex(r => new { r.OwnerId, r.State });

            reservation.ToTable(table =>
            {
                table.HasCheckConstraint("CK_StockReservations_PositiveQuantity", "\"Quantity\" > 0");

                // A closed claim has to say when it closed, or the age of the
                // oldest open commitment — which is the number that tells a
                // supervisor an order is stuck — is computed from a column that is
                // sometimes null for no reason.
                table.HasCheckConstraint(
                    "CK_StockReservations_ClosedHasTimestamp",
                    $"\"State\" = {(int)ReservationState.Open} OR \"ClosedAt\" IS NOT NULL");
            });
        });

        builder.Entity<Lot>(lot =>
        {
            lot.HasKey(l => l.Id);
            lot.Property(l => l.LotNumber).HasMaxLength(64).IsRequired();
            lot.Property(l => l.SupplierLotNumber).HasMaxLength(64);

            // Lot numbers repeat across items constantly; they are unique only
            // within one.
            lot.HasIndex(l => new { l.ItemId, l.LotNumber }).IsUnique();
            lot.HasIndex(l => l.ExpiryDate);
        });

        builder.Entity<LicensePlate>(plate =>
        {
            plate.HasKey(p => p.Id);
            plate.Property(p => p.Sscc).HasMaxLength(18).IsFixedLength().IsRequired();
            plate.HasIndex(p => p.Sscc).IsUnique();

            plate.HasOne(p => p.Parent)
                .WithMany(p => p.Children)
                .HasForeignKey(p => p.ParentLicensePlateId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // No foreign keys to masterdata.Items or masterdata.Locations, by design.
        // A cross-schema FK is exactly the coupling ADR 0002 forbids: it would
        // make the two modules undeployable apart and let a MasterData migration
        // lock inventory tables. Referential integrity across the boundary is the
        // posting path's job — every command resolves both sides through the
        // published directory before it writes anything.
    }
}
