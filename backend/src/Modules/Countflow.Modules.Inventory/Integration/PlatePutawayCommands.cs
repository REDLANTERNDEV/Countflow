using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Countflow.Modules.Inventory.Domain;
using Countflow.Modules.Inventory.Ledger;
using Countflow.Modules.Inventory.Persistence;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Countflow.Modules.Inventory.Integration;

/// <summary>
/// Serializes the root key, including competing instructions for different plates.
/// A row is inserted and settled in ONE Inventory transaction; no pending outcome
/// is committed. Once refused, even a delayed worker cannot turn the key into stock.
/// </summary>
public sealed class PlatePutawayCommands(InventoryDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal async Task<Result<PlatePutawayCommand>> PrepareAsync(PlatePutawayInstruction instruction, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A pallet command requires an Inventory transaction.");
        var contents = instruction.ExpectedContents?.OrderBy(line => line.ItemId).ThenBy(line => line.LotId)
            .ThenBy(line => line.Condition).ThenBy(line => line.Quantity)
            .Select(line => new { line.ItemId, line.LotId, line.Condition,
                Quantity = line.Quantity.ToString("G29", CultureInfo.InvariantCulture) }).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            instruction.LicensePlateId, instruction.FromLocationId, instruction.ToLocationId,
            instruction.TaskId, instruction.DecisionTraceId, instruction.Actor.UserId,
            instruction.Notes, ExpectedContents = contents
        }, Json))));
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory."PlatePutawayCommands"
                ("IdempotencyKey", "Fingerprint", "LicensePlateId", "WorkTaskId", "PreparedAt", "OutcomeJson")
            VALUES ({instruction.IdempotencyKey}, {fingerprint}, {instruction.LicensePlateId}, {instruction.TaskId}, {now}, NULL)
            ON CONFLICT ("IdempotencyKey") DO NOTHING
            """, ct);
        var entry = await db.PlatePutawayCommands.AsNoTracking().SingleAsync(
            command => command.IdempotencyKey == instruction.IdempotencyKey, ct);
        return entry.Fingerprint == fingerprint ? entry : Result.Failure<PlatePutawayCommand>(InventoryErrors.IdempotencyKeyReused);
    }

    internal static Result<IReadOnlyList<PostedMovement>>? Replay(PlatePutawayCommand entry)
    {
        if (entry.OutcomeJson is null) return null;
        var outcome = JsonSerializer.Deserialize<Outcome>(entry.OutcomeJson, Json)
            ?? throw new InvalidOperationException("A pallet command outcome is missing.");
        return outcome.Error is { } error ? Result.Failure<IReadOnlyList<PostedMovement>>(error)
            : Result.Success<IReadOnlyList<PostedMovement>>((outcome.Movements
                ?? throw new InvalidOperationException("A successful pallet command has no movements."))
                .Select(movement => movement with { WasAlreadyPosted = true }).ToArray());
    }

    internal async Task SettleAsync(PlatePutawayCommand entry, Result<IReadOnlyList<PostedMovement>> result, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(result.IsSuccess
            ? new Outcome(result.Value.ToArray(), null) : new Outcome(null, result.Error), Json);
        var updated = await db.PlatePutawayCommands.Where(command => command.IdempotencyKey == entry.IdempotencyKey
            && command.Fingerprint == entry.Fingerprint && command.OutcomeJson == null)
            .ExecuteUpdateAsync(update => update.SetProperty(command => command.OutcomeJson, json), ct);
        if (updated != 1) throw new InvalidOperationException("A pallet command was not settled exactly once.");
    }

    private sealed record Outcome(PostedMovement[]? Movements, Error? Error);
}
