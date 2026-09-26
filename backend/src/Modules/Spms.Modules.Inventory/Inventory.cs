using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Inventory.Data;
using Spms.Modules.Resources.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Inventory;

public sealed record ItemInput(string? ItemCode, string? ItemName, string? ItemKind, string? BaseUom, string? CategoryCode, string? BrandName,
    decimal? ReorderPoint, decimal? MaximumStock, int? LeadTimeDays, string? Status);
public sealed record VariantInput(string? VariantCode, string? Barcode, string? SizeCode, string? ColorCode, long? SellPriceMinor, string? CurrencyCode,
    string? TaxCode, bool? InventoryEnabled, string? Status);
public sealed record MovementInput(Guid? VariantId, Guid? LocationId, string? StockState, string? MovementType, decimal? Quantity, decimal? UnitCostMinor, string? ReasonCode);
public sealed record TransferInput(Guid? VariantId, Guid? FromLocationId, Guid? ToLocationId, string? StockState, decimal? Quantity);
public sealed record StateChangeInput(Guid? VariantId, Guid? LocationId, string? FromState, string? ToState, decimal? Quantity, string? ReasonCode);
public sealed record CountInput(Guid? VariantId, Guid? LocationId, string? StockState);
public sealed record CountRecordInput(decimal? Quantity, string? ReasonCode);
public sealed record LaundryLine(Guid? VariantId, decimal? Quantity, decimal? Lost);
public sealed record LaundryInput(Guid? DispatchLocationId, Guid? ReturnLocationId, string? ExpectedReturnUtc, string? SupplierReference, List<LaundryLine>? Lines);
public sealed record SupplyInput(Guid? ServiceId, Guid? VariantId, decimal? Quantity, decimal? WastePercent, bool? IsReturnable);

/// <summary>One signed movement at one grain (variant, location, state). Lots are not tracked in R1.</summary>
public sealed record Movement(Guid VariantId, Guid LocationId, string StockState, string MovementType, decimal Quantity,
    string? ReasonCode = null, decimal? UnitCostMinor = null, string? SourceType = null, Guid? SourceId = null);

public sealed record BalanceView(InventoryLocationBalanceRow Balance, string ItemName, string VariantCode, string LocationName, decimal ReorderPoint);

/// <summary>
/// Stock (§Inventory operating and data guide). Every movement is a ledger
/// entry, never updated; the balance is its projection, changed in the same
/// transaction under a row lock so two tills cannot both sell the last robe.
/// Stock never goes below zero by an ordinary movement — only what really
/// happened in a treatment (consumption) or a count may show a shortfall.
/// A retried request is recognised by its idempotency key and not posted twice.
/// </summary>
public sealed class InventoryService(SpmsDbContext db, MasterData master, IUnitOfWork uow, IClock clock)
{
    public static readonly string[] States = ["Saleable", "Clean", "Soiled", "InLaundry", "Damaged", "Quarantine"];
    public static readonly string[] ManualMovements = ["Receipt", "Issue", "Return", "Adjustment"];
    private static readonly string[] MayGoShort = ["Consumption", "CountVariance"];

    public enum Outcome { Ok, NotFound, Invalid, Insufficient, Illegal, StaleVersion, SameCounter }

    public sealed record Posted(Outcome Outcome, IReadOnlyList<InventoryLedgerEntryRow> Entries, string? Detail = null);

    /* ------------------------------ items ------------------------------ */

    public async Task<List<(InventoryItemRow Item, List<InventoryItemVariantRow> Variants)>> ItemsAsync(CancellationToken ct)
    {
        var items = await db.Set<InventoryItemRow>().AsNoTracking().OrderBy(i => i.ItemName).ToListAsync(ct);
        var variants = await db.Set<InventoryItemVariantRow>().AsNoTracking().ToListAsync(ct);
        return items.Select(i => (i, variants.Where(v => v.InventoryItemId == i.InventoryItemId).OrderBy(v => v.VariantCode).ToList())).ToList();
    }

    public Task<Edit<InventoryItemRow>> AddItemAsync(ItemInput i, CancellationToken ct) =>
        master.CreateAsync(new InventoryItemRow
        {
            InventoryItemId = Uuid7.New(), ItemCode = i.ItemCode!.Trim(), ItemName = i.ItemName!.Trim(), ItemKind = i.ItemKind!, BaseUom = i.BaseUom ?? "ea",
            CategoryCode = i.CategoryCode, BrandName = i.BrandName, ReorderPoint = i.ReorderPoint ?? 0, MaximumStock = i.MaximumStock, LeadTimeDays = i.LeadTimeDays ?? 0,
        }, "inventory.item.create", "inventory_item", x => x.InventoryItemId, ct);

    public Task<Edit<InventoryItemRow>> UpdateItemAsync(Guid id, int version, ItemInput i, CancellationToken ct) =>
        master.ChangeAsync<InventoryItemRow>(x => x.InventoryItemId == id, version, "inventory.item.update", "inventory_item", x => x.InventoryItemId, x =>
        {
            if (i.ItemName is not null) x.ItemName = i.ItemName.Trim();
            if (i.ItemKind is not null) x.ItemKind = i.ItemKind;
            if (i.CategoryCode is not null) x.CategoryCode = i.CategoryCode.Length == 0 ? null : i.CategoryCode;
            if (i.BrandName is not null) x.BrandName = i.BrandName.Length == 0 ? null : i.BrandName;
            if (i.ReorderPoint is { } r) x.ReorderPoint = r;
            if (i.MaximumStock is { } m) x.MaximumStock = m;
            if (i.LeadTimeDays is { } l) x.LeadTimeDays = l;
            if (i.Status is not null) x.Status = i.Status;
            return null;
        }, ct);

    public Task<Edit<InventoryItemVariantRow>> AddVariantAsync(Guid itemId, VariantInput i, CancellationToken ct) =>
        master.CreateAsync(new InventoryItemVariantRow
        {
            InventoryItemVariantId = Uuid7.New(), InventoryItemId = itemId, VariantCode = i.VariantCode!.Trim(), Barcode = i.Barcode, SizeCode = i.SizeCode,
            ColorCode = i.ColorCode, SellPriceMinor = i.SellPriceMinor, CurrencyCode = i.CurrencyCode, TaxCode = i.TaxCode, InventoryEnabled = i.InventoryEnabled ?? true,
        }, "inventory.variant.create", "inventory_item_variant", v => v.InventoryItemVariantId, ct,
            async () => await db.Set<InventoryItemRow>().AnyAsync(x => x.InventoryItemId == itemId, ct) ? null : (string?)"No such item.");

    public Task<Edit<InventoryItemVariantRow>> UpdateVariantAsync(Guid id, int version, VariantInput i, CancellationToken ct) =>
        master.ChangeAsync<InventoryItemVariantRow>(v => v.InventoryItemVariantId == id, version, "inventory.variant.update", "inventory_item_variant",
            v => v.InventoryItemVariantId, v =>
            {
                if (i.Barcode is not null) v.Barcode = i.Barcode.Length == 0 ? null : i.Barcode;
                if (i.SizeCode is not null) v.SizeCode = i.SizeCode;
                if (i.ColorCode is not null) v.ColorCode = i.ColorCode;
                if (i.SellPriceMinor is not null || i.CurrencyCode is not null) { v.SellPriceMinor = i.SellPriceMinor; v.CurrencyCode = i.CurrencyCode; }
                if (i.TaxCode is not null) v.TaxCode = i.TaxCode.Length == 0 ? null : i.TaxCode;
                if (i.InventoryEnabled is { } e) v.InventoryEnabled = e;
                if (i.Status is not null) v.Status = i.Status;
                return null;
            }, ct);

    /* ----------------------------- balances ----------------------------- */

    public async Task<List<BalanceView>> BalancesAsync(Guid? locationId, Guid? variantId, bool lowOnly, CancellationToken ct)
    {
        var rows = await (from b in db.Set<InventoryLocationBalanceRow>().AsNoTracking()
                          where (locationId == null || b.LocationId == locationId) && (variantId == null || b.InventoryItemVariantId == variantId)
                          join v in db.Set<InventoryItemVariantRow>() on b.InventoryItemVariantId equals v.InventoryItemVariantId
                          join i in db.Set<InventoryItemRow>() on v.InventoryItemId equals i.InventoryItemId
                          join l in db.Set<LocationRow>() on b.LocationId equals l.LocationId
                          orderby i.ItemName, v.VariantCode, l.LocationName, b.StockState
                          select new { b, i.ItemName, v.VariantCode, l.LocationName, i.ReorderPoint }).ToListAsync(ct);
        var views = rows.Select(r => new BalanceView(r.b, r.ItemName, r.VariantCode, r.LocationName, r.ReorderPoint)).ToList();
        if (!lowOnly) return views;
        // Low means the usable stock of a variant across the property is at or under its reorder point.
        var usable = views.Where(v => v.Balance.StockState is "Saleable" or "Clean").GroupBy(v => v.Balance.InventoryItemVariantId)
            .Where(g => g.Sum(x => x.Balance.OnHand) <= g.First().ReorderPoint).Select(g => g.Key).ToHashSet();
        return views.Where(v => usable.Contains(v.Balance.InventoryItemVariantId) && v.Balance.StockState is "Saleable" or "Clean").ToList();
    }

    public Task<List<InventoryLedgerEntryRow>> LedgerAsync(Guid? variantId, Guid? locationId, int limit, CancellationToken ct) =>
        db.Set<InventoryLedgerEntryRow>().AsNoTracking()
            .Where(e => (variantId == null || e.InventoryItemVariantId == variantId) && (locationId == null || e.LocationId == locationId))
            .OrderByDescending(e => e.OccurredAt).Take(limit).ToListAsync(ct);

    /* ----------------------------- posting ----------------------------- */

    /// <summary>
    /// Posts movements atomically. Each grain's balance row is locked, so the
    /// check that stock suffices and the change are one step. The key makes a
    /// retried request a replay: its entries are returned, nothing is posted.
    /// </summary>
    public async Task<Posted> PostAsync(IReadOnlyList<Movement> moves, string key, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var result = await PostInTransactionAsync(moves, key, ct);
        if (result.Outcome == Outcome.Ok) await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<Posted> PostInTransactionAsync(IReadOnlyList<Movement> moves, string key, CancellationToken ct)
    {
        var prior = await db.Set<InventoryLedgerEntryRow>().AsNoTracking().Where(e => e.IdempotencyKey.StartsWith(key + "#")).ToListAsync(ct);
        if (prior.Count > 0) return new(Outcome.Ok, prior);

        var property = db.Scope.RequireProperty();
        var variants = moves.Select(m => m.VariantId).Distinct().ToList();
        var locations = moves.Select(m => m.LocationId).Distinct().ToList();
        if (await db.Set<InventoryItemVariantRow>().CountAsync(v => variants.Contains(v.InventoryItemVariantId), ct) != variants.Count)
            return new(Outcome.Invalid, [], "An item variant does not exist.");
        if (await db.Set<LocationRow>().CountAsync(l => locations.Contains(l.LocationId) && l.Status == "Active", ct) != locations.Count)
            return new(Outcome.Invalid, [], "A location does not exist at this property.");

        var now = clock.UtcNow;
        var entries = new List<InventoryLedgerEntryRow>();
        var n = 0;
        foreach (var m in moves)
        {
            var balance = await db.Set<InventoryLocationBalanceRow>()
                .FromSqlInterpolated($"""
                    SELECT * FROM inventory.inventory_location_balance
                     WHERE inventory_item_variant_id = {m.VariantId} AND location_id = {m.LocationId}
                       AND lot_number IS NULL AND stock_state = {m.StockState}
                       FOR UPDATE
                    """).SingleOrDefaultAsync(ct);
            var onHand = balance?.OnHand ?? 0;
            if (onHand + m.Quantity < 0 && !MayGoShort.Contains(m.MovementType))
            {
                db.ChangeTracker.Clear();
                return new(Outcome.Insufficient, [], $"Only {onHand:0.##} {m.StockState} on hand there.");
            }
            if (balance is null)
                db.Add(new InventoryLocationBalanceRow
                {
                    InventoryLocationBalanceId = Uuid7.New(), PropertyId = property, InventoryItemVariantId = m.VariantId, LocationId = m.LocationId,
                    StockState = m.StockState, OnHand = m.Quantity, UnitCostMinor = m.UnitCostMinor ?? 0,
                });
            else
            {
                balance.OnHand += m.Quantity;
                if (m.MovementType == "Receipt" && m.UnitCostMinor is { } cost && balance.OnHand > 0)
                    balance.UnitCostMinor = Math.Round(((onHand * balance.UnitCostMinor) + (m.Quantity * cost)) / balance.OnHand, 6); // moving average
            }
            var unit = m.UnitCostMinor ?? balance?.UnitCostMinor;
            var entry = new InventoryLedgerEntryRow
            {
                EntryId = Uuid7.New(), PropertyId = property, InventoryItemVariantId = m.VariantId, LocationId = m.LocationId, StockState = m.StockState,
                MovementType = m.MovementType, Quantity = m.Quantity, UnitCostMinor = unit, ValueDeltaMinor = unit is { } u ? u * m.Quantity : null,
                ReasonCode = m.ReasonCode, SourceDocumentType = m.SourceType, SourceDocumentId = m.SourceId, IdempotencyKey = $"{key}#{n++}", OccurredAt = now,
            };
            db.Add(entry);
            entries.Add(entry);
            // Each grain is saved as it is posted, so a second move on the same grain in this batch finds the row.
            await db.SaveChangesAsync(ct);
        }
        await master.AuditAsync(new AuditEntry("inventory.post", "inventory_ledger_entry", entries[0].EntryId.ToString(),
            AfterData: moves.Select(m => new { m.VariantId, m.LocationId, m.StockState, m.MovementType, m.Quantity, m.ReasonCode })), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        return new(Outcome.Ok, entries);
    }

    public Task<Posted> MoveAsync(MovementInput i, string key, CancellationToken ct)
    {
        var sign = i.MovementType is "Issue" ? -1 : 1;
        var qty = i.MovementType == "Adjustment" ? i.Quantity!.Value : Math.Abs(i.Quantity!.Value) * sign;
        return PostAsync([new Movement(i.VariantId!.Value, i.LocationId!.Value, i.StockState ?? "Saleable", i.MovementType!, qty, i.ReasonCode, i.UnitCostMinor, i.MovementType == "Receipt" ? null : null)], key, ct);
    }

    public Task<Posted> TransferAsync(TransferInput i, string key, CancellationToken ct)
    {
        var doc = Uuid7.New();
        var state = i.StockState ?? "Saleable";
        return PostAsync([
            new Movement(i.VariantId!.Value, i.FromLocationId!.Value, state, "TransferOut", -i.Quantity!.Value, SourceType: "Transfer", SourceId: doc),
            new Movement(i.VariantId!.Value, i.ToLocationId!.Value, state, "TransferIn", i.Quantity!.Value, SourceType: "Transfer", SourceId: doc),
        ], key, ct);
    }

    public Task<Posted> ChangeStateAsync(StateChangeInput i, string key, CancellationToken ct) =>
        PostAsync([
            new Movement(i.VariantId!.Value, i.LocationId!.Value, i.FromState!, "StateChange", -i.Quantity!.Value, i.ReasonCode),
            new Movement(i.VariantId!.Value, i.LocationId!.Value, i.ToState!, "StateChange", i.Quantity!.Value, i.ReasonCode),
        ], key, ct);

    /* ----------------------------- counts ----------------------------- */

    public Task<List<StockCountRow>> CountsAsync(bool openOnly, CancellationToken ct) =>
        db.Set<StockCountRow>().AsNoTracking().Where(c => !openOnly || c.Status == "Open" || c.Status == "Counted" || c.Status == "Recounted")
            .OrderByDescending(c => c.StockCountId).Take(200).ToListAsync(ct);

    public async Task<Edit<StockCountRow>> OpenCountAsync(CountInput i, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var state = i.StockState ?? "Saleable";
        var expected = await db.Set<InventoryLocationBalanceRow>().AsNoTracking()
            .Where(b => b.InventoryItemVariantId == i.VariantId && b.LocationId == i.LocationId && b.LotNumber == null && b.StockState == state)
            .Select(b => (decimal?)b.OnHand).SingleOrDefaultAsync(ct) ?? 0;
        var opened = await master.CreateAsync(new StockCountRow
        {
            StockCountId = Uuid7.New(), PropertyId = db.Scope.RequireProperty(), InventoryItemVariantId = i.VariantId!.Value, LocationId = i.LocationId!.Value,
            StockState = state, ExpectedQuantity = expected, Status = "Open",
        }, "inventory.count.open", "stock_count", c => c.StockCountId, ct);
        if (opened.Outcome == EditOutcome.Ok) await tx.CommitAsync(ct);
        return opened;
    }

    public Task<Edit<StockCountRow>> RecordCountAsync(Guid id, int version, decimal quantity, bool recount, string? reason, CancellationToken ct) =>
        master.ChangeAsync<StockCountRow>(c => c.StockCountId == id, version, recount ? "inventory.count.recount" : "inventory.count.record", "stock_count",
            c => c.StockCountId, c =>
            {
                if (!recount && c.Status != "Open") return "This count has already been recorded; recount it instead.";
                if (recount && c.Status is not ("Counted" or "Recounted")) return "Only a recorded count can be recounted.";
                if (recount) { c.RecountQuantity = quantity; c.Status = "Recounted"; }
                else { c.ObservedQuantity = quantity; c.Status = "Counted"; }
                c.CountedBy = db.Scope.PrincipalId;
                c.CountedAt = clock.UtcNow;
                c.ReasonCode = reason ?? c.ReasonCode;
                return null;
            }, ct, reason);

    /// <summary>A second person approves the count; its variance posts to the ledger and the count is Posted.</summary>
    public async Task<(Outcome Outcome, StockCountRow? Row, string? Detail)> ApproveCountAsync(Guid id, int version, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var c = await db.Set<StockCountRow>().SingleOrDefaultAsync(x => x.StockCountId == id, ct);
        if (c is null) return (Outcome.NotFound, null, null);
        if (c.Version != version) { db.ChangeTracker.Clear(); return (Outcome.StaleVersion, c, null); }
        if (c.Status is not ("Counted" or "Recounted")) { db.ChangeTracker.Clear(); return (Outcome.Illegal, c, "Only a recorded count is approved."); }
        if (c.CountedBy is { } counter && counter == db.Scope.PrincipalId)
        { db.ChangeTracker.Clear(); return (Outcome.SameCounter, c, "The person who counted cannot approve the count."); }
        var observed = c.RecountQuantity ?? c.ObservedQuantity ?? 0;
        // The variance is against the stock now, not when the count was opened: movements in between are not lost.
        var current = await db.Set<InventoryLocationBalanceRow>().AsNoTracking()
            .Where(b => b.InventoryItemVariantId == c.InventoryItemVariantId && b.LocationId == c.LocationId && b.LotNumber == null && b.StockState == c.StockState)
            .Select(b => (decimal?)b.OnHand).SingleOrDefaultAsync(ct) ?? 0;
        c.ApprovedBy = db.Scope.PrincipalId;
        c.ApprovedAt = clock.UtcNow;
        c.Status = "Posted";
        await db.SaveChangesAsync(ct);
        var variance = observed - current;
        if (variance != 0)
        {
            var posted = await PostInTransactionAsync([new Movement(c.InventoryItemVariantId, c.LocationId, c.StockState, "CountVariance", variance,
                c.ReasonCode ?? "Count", SourceType: "StockCount", SourceId: c.StockCountId)], $"count:{c.StockCountId:N}", ct);
            if (posted.Outcome != Outcome.Ok) return (posted.Outcome, null, posted.Detail);
        }
        await master.AuditAsync(new AuditEntry("inventory.count.approve", "stock_count", c.StockCountId.ToString(), c.Version,
            FromStatus: "Counted", ToStatus: "Posted", AfterData: new { observed, current, variance }), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        var posted_ = await db.Set<StockCountRow>().AsNoTracking().SingleAsync(x => x.StockCountId == id, ct);
        await tx.CommitAsync(ct);
        return (Outcome.Ok, posted_, null);
    }

    /* ----------------------------- laundry ----------------------------- */

    public Task<List<LaundryBatchRow>> LaundryAsync(CancellationToken ct) =>
        db.Set<LaundryBatchRow>().AsNoTracking().OrderByDescending(b => b.LaundryBatchId).Take(100).ToListAsync(ct);

    /// <summary>Soiled linen leaves: Soiled out at the dispatch location, InLaundry in (the stock is still ours, just away).</summary>
    public async Task<(Outcome Outcome, LaundryBatchRow? Batch, string? Detail)> DispatchAsync(LaundryInput i, string key, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var existing = await db.Set<InventoryLedgerEntryRow>().AsNoTracking().Where(e => e.IdempotencyKey.StartsWith(key + "#"))
            .Select(e => e.SourceDocumentId).FirstOrDefaultAsync(ct);
        if (existing is { } done) return (Outcome.Ok, await db.Set<LaundryBatchRow>().AsNoTracking().SingleAsync(b => b.LaundryBatchId == done, ct), null);
        DateTimeOffset? expected = Guard.TryParseInstant(i.ExpectedReturnUtc, out var e) ? e : null;
        var batch = new LaundryBatchRow
        {
            LaundryBatchId = Uuid7.New(), PropertyId = db.Scope.RequireProperty(), DispatchLocationId = i.DispatchLocationId!.Value,
            ReturnLocationId = i.ReturnLocationId ?? i.DispatchLocationId!.Value, DispatchedAt = clock.UtcNow, ExpectedReturnAt = expected,
            SupplierReference = i.SupplierReference, Status = "Dispatched",
        };
        db.Add(batch);
        await db.SaveChangesAsync(ct);
        var moves = i.Lines!.SelectMany(l => new[]
        {
            new Movement(l.VariantId!.Value, batch.DispatchLocationId, "Soiled", "LaundryOut", -l.Quantity!.Value, SourceType: "LaundryBatch", SourceId: batch.LaundryBatchId),
            new Movement(l.VariantId!.Value, batch.DispatchLocationId, "InLaundry", "LaundryOut", l.Quantity!.Value, SourceType: "LaundryBatch", SourceId: batch.LaundryBatchId),
        }).ToList();
        var posted = await PostInTransactionAsync(moves, key, ct);
        if (posted.Outcome != Outcome.Ok) return (posted.Outcome, null, posted.Detail);
        await master.AuditAsync(new AuditEntry("inventory.laundry.dispatch", "laundry_batch", batch.LaundryBatchId.ToString(), 1, ToStatus: "Dispatched"), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return (Outcome.Ok, batch, null);
    }

    /// <summary>Clean linen returns to the return location; what did not come back is a LaundryLoss.</summary>
    public async Task<(Outcome Outcome, LaundryBatchRow? Batch, string? Detail)> ReceiveAsync(Guid id, int version, List<LaundryLine> lines, string key, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var batch = await db.Set<LaundryBatchRow>().SingleOrDefaultAsync(b => b.LaundryBatchId == id, ct);
        if (batch is null) return (Outcome.NotFound, null, null);
        if (batch.Version != version) { db.ChangeTracker.Clear(); return (Outcome.StaleVersion, batch, null); }
        if (batch.Status is not ("Dispatched" or "PartiallyReceived")) { db.ChangeTracker.Clear(); return (Outcome.Illegal, batch, $"A {batch.Status} batch receives nothing."); }
        var away = await db.Set<InventoryLedgerEntryRow>().AsNoTracking()
            .Where(e => e.SourceDocumentType == "LaundryBatch" && e.SourceDocumentId == id && e.StockState == "InLaundry")
            .GroupBy(e => e.InventoryItemVariantId).Select(g => new { g.Key, Qty = g.Sum(x => x.Quantity) }).ToListAsync(ct);
        var moves = new List<Movement>();
        foreach (var l in lines)
        {
            var out_ = away.SingleOrDefault(a => a.Key == l.VariantId)?.Qty ?? 0;
            var back = l.Quantity ?? 0;
            var lost = l.Lost ?? 0;
            if (back + lost > out_) { db.ChangeTracker.Clear(); return (Outcome.Invalid, batch, $"More returned than is out ({out_:0.##})."); }
            if (back > 0)
            {
                moves.Add(new Movement(l.VariantId!.Value, batch.DispatchLocationId, "InLaundry", "LaundryIn", -back, SourceType: "LaundryBatch", SourceId: id));
                moves.Add(new Movement(l.VariantId!.Value, batch.ReturnLocationId, "Clean", "LaundryIn", back, SourceType: "LaundryBatch", SourceId: id));
            }
            if (lost > 0)
                moves.Add(new Movement(l.VariantId!.Value, batch.DispatchLocationId, "InLaundry", "LaundryLoss", -lost, "LostInLaundry", SourceType: "LaundryBatch", SourceId: id));
        }
        var remaining = away.Sum(a => a.Qty) - lines.Sum(l => (l.Quantity ?? 0) + (l.Lost ?? 0));
        batch.Status = remaining > 0 ? "PartiallyReceived" : "Received";
        batch.ReceivedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        if (moves.Count > 0)
        {
            var posted = await PostInTransactionAsync(moves, key, ct);
            if (posted.Outcome != Outcome.Ok) return (posted.Outcome, null, posted.Detail);
        }
        await master.AuditAsync(new AuditEntry("inventory.laundry.receive", "laundry_batch", id.ToString(), batch.Version, ToStatus: batch.Status), ct);
        await db.SaveChangesAsync(ct); // the audit and outbox rows added above belong to this transaction
        db.ChangeTracker.Clear();
        var received = await db.Set<LaundryBatchRow>().AsNoTracking().SingleAsync(b => b.LaundryBatchId == id, ct);
        await tx.CommitAsync(ct);
        return (Outcome.Ok, received, null);
    }

    /* ------------------------- service supplies ------------------------- */

    public Task<List<ServiceSupplyRow>> SuppliesAsync(Guid? serviceId, CancellationToken ct) =>
        db.Set<ServiceSupplyRow>().AsNoTracking().Where(s => serviceId == null || s.ServiceId == serviceId).ToListAsync(ct);

    public Task<Edit<ServiceSupplyRow>> AddSupplyAsync(SupplyInput i, CancellationToken ct) =>
        master.CreateAsync(new ServiceSupplyRow
        {
            ServiceSupplyId = Uuid7.New(), ServiceId = i.ServiceId!.Value, InventoryItemVariantId = i.VariantId!.Value, Quantity = i.Quantity!.Value,
            WastePercent = i.WastePercent ?? 0, IsReturnable = i.IsReturnable ?? false, EffectiveFrom = clock.UtcNow,
        }, "inventory.supply.create", "service_supply", s => s.ServiceSupplyId, ct, async () =>
            !await db.Set<ServiceRow>().AnyAsync(s => s.ServiceId == i.ServiceId, ct) ? "No such service."
            : !await db.Set<InventoryItemVariantRow>().AnyAsync(v => v.InventoryItemVariantId == i.VariantId, ct) ? "No such item variant." : null);

    public Task<Edit<ServiceSupplyRow>> RetireSupplyAsync(Guid id, int version, CancellationToken ct) =>
        master.ChangeAsync<ServiceSupplyRow>(s => s.ServiceSupplyId == id, version, "inventory.supply.retire", "service_supply", s => s.ServiceSupplyId, s =>
        {
            if (s.Status == "Retired") return "Already retired.";
            s.Status = "Retired";
            return null;
        }, ct);
}

/// <summary>
/// A completed treatment uses what its service's supplies say, in the
/// completion's own transaction: product is consumed, linen goes from Clean
/// to Soiled. It is taken from the room's own store when the room has one,
/// else the property's first storage location; with neither, nothing is
/// posted (a property that does not track stock is not refused a completion).
/// </summary>
public sealed class ConsumptionObserver(SpmsDbContext db, InventoryService inventory, Microsoft.Extensions.Logging.ILogger<ConsumptionObserver> log) : ISchedulingObserver
{
    public async Task TransitionedAsync(Appointment after, AppointmentStatus from, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        if (after.Status != AppointmentStatus.Completed || !Guid.TryParse(after.ServiceId, out var service) || !Guid.TryParse(after.AppointmentId, out var appointment)) return;
        var supplies = await db.Set<ServiceSupplyRow>().AsNoTracking().Where(s => s.ServiceId == service && s.Status == "Active").ToListAsync(ct);
        if (supplies.Count == 0) return;
        Guid? location = Guid.TryParse(after.RoomId, out var room)
            ? await db.Set<ResourceRow>().Where(r => r.ResourceId == room).Select(r => r.LocationId).SingleOrDefaultAsync(ct) : null;
        location ??= await db.Set<LocationRow>().Where(l => l.LocationType == "Storage" && l.Status == "Active")
            .OrderBy(l => l.LocationCode).Select(l => (Guid?)l.LocationId).FirstOrDefaultAsync(ct);
        if (location is not { } at) return;
        var moves = new List<Movement>();
        foreach (var s in supplies)
        {
            var qty = Math.Round(s.Quantity * (1 + s.WastePercent), 6);
            if (s.IsReturnable)
            {
                moves.Add(new Movement(s.InventoryItemVariantId, at, "Clean", "Consumption", -s.Quantity, "Treatment", SourceType: "Appointment", SourceId: appointment));
                moves.Add(new Movement(s.InventoryItemVariantId, at, "Soiled", "Consumption", s.Quantity, "Treatment", SourceType: "Appointment", SourceId: appointment));
            }
            else
                moves.Add(new Movement(s.InventoryItemVariantId, at, "Saleable", "Consumption", -qty, "Treatment", SourceType: "Appointment", SourceId: appointment));
        }
        // Its own savepoint: stock bookkeeping never fails the treatment it records.
        try
        {
            var posted = await inventory.PostAsync(moves, $"use:{appointment:N}", ct);
            if (posted.Outcome != InventoryService.Outcome.Ok)
                log.LogWarning("Supplies for appointment {Appointment} were not posted: {Detail}", appointment, posted.Detail);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException e)
        {
            log.LogWarning(e, "Supplies for appointment {Appointment} were not posted", appointment);
        }
    }
}

public static class InventoryEndpoints
{
    private static object Item(InventoryItemRow i, IEnumerable<InventoryItemVariantRow>? variants = null) => new
    {
        itemId = i.InventoryItemId, i.ItemCode, i.ItemName, i.ItemKind, i.BaseUom, i.CategoryCode, i.BrandName, i.ReorderPoint, i.MaximumStock,
        i.LeadTimeDays, i.Status, variants = variants?.Select(Variant), rowVersion = i.Version, eTag = $"\"{i.Version}\"",
    };

    private static object Variant(InventoryItemVariantRow v) => new
    {
        variantId = v.InventoryItemVariantId, itemId = v.InventoryItemId, v.VariantCode, v.Barcode, v.SizeCode, v.ColorCode, v.SellPriceMinor,
        currencyCode = v.CurrencyCode?.Trim(), v.TaxCode, v.InventoryEnabled, v.Status, rowVersion = v.Version, eTag = $"\"{v.Version}\"",
    };

    private static object Entry(InventoryLedgerEntryRow e) => new
    {
        entryId = e.EntryId, variantId = e.InventoryItemVariantId, e.LocationId, e.StockState, e.MovementType, e.Quantity, e.UnitCostMinor,
        e.ValueDeltaMinor, e.ReasonCode, e.SourceDocumentType, e.SourceDocumentId, occurredUtc = e.OccurredAt.ToUniversalTime().ToString("O"), recordedBy = (Guid?)null,
    };

    private static object Count(StockCountRow c) => new
    {
        stockCountId = c.StockCountId, variantId = c.InventoryItemVariantId, c.LocationId, c.StockState, c.ExpectedQuantity, c.ObservedQuantity,
        c.RecountQuantity, variance = (c.RecountQuantity ?? c.ObservedQuantity) is { } q ? q - c.ExpectedQuantity : (decimal?)null,
        c.CountedBy, c.ApprovedBy, c.ReasonCode, c.Status, rowVersion = c.Version, eTag = $"\"{c.Version}\"",
    };

    private static object Batch(LaundryBatchRow b) => new
    {
        laundryBatchId = b.LaundryBatchId, b.DispatchLocationId, b.ReturnLocationId, dispatchedUtc = b.DispatchedAt?.ToUniversalTime().ToString("O"),
        expectedReturnUtc = b.ExpectedReturnAt?.ToUniversalTime().ToString("O"), receivedUtc = b.ReceivedAt?.ToUniversalTime().ToString("O"),
        b.SupplierReference, b.Status, rowVersion = b.Version, eTag = $"\"{b.Version}\"",
    };

    private static object Supply(ServiceSupplyRow s) => new
    {
        serviceSupplyId = s.ServiceSupplyId, s.ServiceId, variantId = s.InventoryItemVariantId, s.Quantity, s.WastePercent, s.IsReturnable, s.Status,
        rowVersion = s.Version, eTag = $"\"{s.Version}\"",
    };

    private static Task<IResult?> Can(RequestContext ctx, IAccessDecider access, string relation, CancellationToken ct) =>
        WebApi.GateAsync(ctx, access, SpaScopes.Inventory, relation, Fga.Property(ctx.PropertyId), ct);

    private static string? Key(HttpContext http) =>
        http.Request.Headers["Idempotency-Key"].FirstOrDefault() is { Length: >= 8 and <= 200 } k ? k : null;

    private static IResult PostedResult(RequestContext ctx, InventoryService.Posted p) => p.Outcome switch
    {
        InventoryService.Outcome.Ok => Results.Json(new { entries = p.Entries.Select(Entry) }, Json.Options, statusCode: 201),
        InventoryService.Outcome.Insufficient => Problem.From(ApiError.HardConflict, ctx.CorrelationId, p.Detail),
        _ => WebApi.Invalid(ctx, p.Detail ?? "The movement was refused."),
    };

    private static IResult Tuple<T>(HttpContext http, RequestContext ctx, (InventoryService.Outcome Outcome, T? Row, string? Detail) r, Func<T, object> dto)
        where T : class, IVersioned => r.Outcome switch
    {
        InventoryService.Outcome.Ok => EditResults.Ok(http, r.Row!, dto),
        InventoryService.Outcome.NotFound => WebApi.NotFound(ctx),
        InventoryService.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, extensions: r.Row is null ? null : Problem.Ext("current", dto(r.Row))),
        InventoryService.Outcome.SameCounter => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, r.Detail),
        InventoryService.Outcome.Invalid => WebApi.Invalid(ctx, r.Detail ?? "Refused."),
        _ => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
    };

    private static bool Positive(decimal? q) => q is > 0 and < 1_000_000;

    public static IEndpointRouteBuilder MapInventory(this IEndpointRouteBuilder app)
    {
        app.MapGet("/inventory/items", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_inventory", ct) is { } refused) return refused;
            return Results.Json((await svc.ItemsAsync(ct)).Select(x => Item(x.Item, x.Variants)), Json.Options);
        });

        app.MapPost("/inventory/items", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_adjust_inventory", ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<ItemInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.ItemCode) || string.IsNullOrWhiteSpace(i.ItemName) || i.ItemKind is not ("Retail" or "Linen" or "Amenity" or "Consumable" or "Professional"))
                return WebApi.Invalid(ctx, "itemCode, itemName and itemKind (Retail, Linen, Amenity, Consumable, Professional) are required.");
            return (await svc.AddItemAsync(i, ct)).ToHttp(http, ctx, x => Item(x), 201);
        });

        app.MapPatch("/inventory/items/{id:guid}", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_adjust_inventory", ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<ItemInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.Status is not (null or "Active" or "Inactive" or "Retired")) return WebApi.Invalid(ctx, "status is Active, Inactive or Retired.");
            return (await svc.UpdateItemAsync(id, version, i, ct)).ToHttp(http, ctx, x => Item(x));
        });

        app.MapPost("/inventory/items/{id:guid}/variants", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_adjust_inventory", ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<VariantInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.VariantCode)) return WebApi.Invalid(ctx, "variantCode is required.");
            if ((i.SellPriceMinor is null) != (i.CurrencyCode is null)) return WebApi.Invalid(ctx, "A sell price names its currency.");
            return (await svc.AddVariantAsync(id, i, ct)).ToHttp(http, ctx, Variant, 201);
        });

        app.MapPatch("/inventory/variants/{id:guid}", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_adjust_inventory", ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<VariantInput>(http, ctx, ct);
            if (i is null) return fail!;
            return (await svc.UpdateVariantAsync(id, version, i, ct)).ToHttp(http, ctx, Variant);
        });

        app.MapGet("/inventory/balances", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid? locationId, Guid? variantId, bool? low, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_inventory", ct) is { } refused) return refused;
            return Results.Json((await svc.BalancesAsync(locationId, variantId, low == true, ct)).Select(b => new
            {
                variantId = b.Balance.InventoryItemVariantId, b.Balance.LocationId, b.LocationName, b.ItemName, b.VariantCode, b.Balance.StockState,
                b.Balance.OnHand, b.Balance.Allocated, available = b.Balance.OnHand - b.Balance.Allocated, b.Balance.UnitCostMinor, b.ReorderPoint,
            }), Json.Options);
        });

        app.MapGet("/inventory/ledger", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid? variantId, Guid? locationId, int? limit, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_inventory", ct) is { } refused) return refused;
            return Results.Json((await svc.LedgerAsync(variantId, locationId, Math.Clamp(limit ?? 100, 1, 500), ct)).Select(Entry), Json.Options);
        });

        app.MapPost("/inventory/movements", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_adjust_inventory", ct) is { } refused) return refused;
            if (Key(http) is not { } key) return WebApi.Invalid(ctx, "Idempotency-Key (8–200 characters) is required on a stock movement.");
            var (i, fail) = await WebApi.BodyAsync<MovementInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.VariantId is null || i.LocationId is null || !InventoryService.ManualMovements.Contains(i.MovementType))
                return WebApi.Invalid(ctx, "variantId, locationId and movementType (Receipt, Issue, Return, Adjustment) are required.");
            if (i.StockState is not null && !InventoryService.States.Contains(i.StockState)) return WebApi.Invalid(ctx, "Unknown stockState.");
            if (i.MovementType == "Adjustment" ? i.Quantity is null or 0 || string.IsNullOrWhiteSpace(i.ReasonCode) : !Positive(i.Quantity))
                return WebApi.Invalid(ctx, "quantity is positive (an adjustment is signed and needs a reasonCode).");
            return PostedResult(ctx, await svc.MoveAsync(i, $"mv:{key}", ct));
        });

        app.MapPost("/inventory/transfers", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_adjust_inventory", ct) is { } refused) return refused;
            if (Key(http) is not { } key) return WebApi.Invalid(ctx, "Idempotency-Key is required on a transfer.");
            var (i, fail) = await WebApi.BodyAsync<TransferInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.VariantId is null || i.FromLocationId is null || i.ToLocationId is null || i.FromLocationId == i.ToLocationId || !Positive(i.Quantity))
                return WebApi.Invalid(ctx, "variantId, two different locations and a positive quantity are required.");
            return PostedResult(ctx, await svc.TransferAsync(i, $"tr:{key}", ct));
        });

        app.MapPost("/inventory/state-changes", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_move_stock_state", ct) is { } refused) return refused;
            if (Key(http) is not { } key) return WebApi.Invalid(ctx, "Idempotency-Key is required.");
            var (i, fail) = await WebApi.BodyAsync<StateChangeInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.VariantId is null || i.LocationId is null || !InventoryService.States.Contains(i.FromState) || !InventoryService.States.Contains(i.ToState)
                || i.FromState == i.ToState || !Positive(i.Quantity))
                return WebApi.Invalid(ctx, "variantId, locationId, two different states and a positive quantity are required.");
            return PostedResult(ctx, await svc.ChangeStateAsync(i, $"st:{key}", ct));
        });

        app.MapGet("/inventory/counts", async (HttpContext http, InventoryService svc, IAccessDecider access, bool? open, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_inventory", ct) is { } refused) return refused;
            return Results.Json((await svc.CountsAsync(open == true, ct)).Select(Count), Json.Options);
        });

        app.MapPost("/inventory/counts", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_move_stock_state", ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<CountInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.VariantId is null || i.LocationId is null) return WebApi.Invalid(ctx, "variantId and locationId are required.");
            return (await svc.OpenCountAsync(i, ct)).ToHttp(http, ctx, Count, 201);
        });

        foreach (var (path, recount) in new[] { ("record", false), ("recount", true) })
            app.MapPost($"/inventory/counts/{{id:guid}}/{path}", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
            {
                var ctx = RequestContext.From(http);
                if (await Can(ctx, access, "can_move_stock_state", ct) is { } refused) return refused;
                if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
                var (i, fail) = await WebApi.BodyAsync<CountRecordInput>(http, ctx, ct);
                if (i is null) return fail!;
                if (i.Quantity is not (>= 0 and < 1_000_000)) return WebApi.Invalid(ctx, "quantity is what is there: zero or more.");
                return (await svc.RecordCountAsync(id, version, i.Quantity.Value, recount, i.ReasonCode, ct)).ToHttp(http, ctx, Count);
            });

        app.MapPost("/inventory/counts/{id:guid}/approve", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_approve_count", ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            return Tuple(http, ctx, await svc.ApproveCountAsync(id, version, ct), Count);
        });

        app.MapGet("/inventory/laundry", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_inventory", ct) is { } refused) return refused;
            return Results.Json((await svc.LaundryAsync(ct)).Select(Batch), Json.Options);
        });

        app.MapPost("/inventory/laundry", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_move_stock_state", ct) is { } refused) return refused;
            if (Key(http) is not { } key) return WebApi.Invalid(ctx, "Idempotency-Key is required on a laundry dispatch.");
            var (i, fail) = await WebApi.BodyAsync<LaundryInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.DispatchLocationId is null || i.Lines is not { Count: > 0 } || i.Lines.Any(l => l.VariantId is null || !Positive(l.Quantity)))
                return WebApi.Invalid(ctx, "dispatchLocationId and lines (variantId, positive quantity) are required.");
            var r = await svc.DispatchAsync(i, $"ld:{key}", ct);
            return r.Outcome == InventoryService.Outcome.Ok
                ? Results.Json(Batch(r.Batch!), Json.Options, statusCode: 201)
                : Tuple(http, ctx, r, Batch);
        });

        app.MapPost("/inventory/laundry/{id:guid}/receive", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_move_stock_state", ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<LaundryInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.Lines is not { Count: > 0 } || i.Lines.Any(l => l.VariantId is null || l.Quantity is < 0 || l.Lost is < 0))
                return WebApi.Invalid(ctx, "lines (variantId, quantity returned, lost) are required.");
            return Tuple(http, ctx, await svc.ReceiveAsync(id, version, i.Lines, $"lr:{id:N}:{version}", ct), Batch);
        });

        app.MapGet("/inventory/service-supplies", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid? serviceId, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_inventory", ct) is { } refused) return refused;
            return Results.Json((await svc.SuppliesAsync(serviceId, ct)).Select(Supply), Json.Options);
        });

        app.MapPost("/inventory/service-supplies", async (HttpContext http, InventoryService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_adjust_inventory", ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<SupplyInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.ServiceId is null || i.VariantId is null || !Positive(i.Quantity) || i.WastePercent is < 0 or >= 1)
                return WebApi.Invalid(ctx, "serviceId, variantId and a positive quantity are required; wastePercent is a fraction under 1.");
            return (await svc.AddSupplyAsync(i, ct)).ToHttp(http, ctx, Supply, 201);
        });

        app.MapPost("/inventory/service-supplies/{id:guid}/retire", async (HttpContext http, InventoryService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_adjust_inventory", ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            return (await svc.RetireSupplyAsync(id, version, ct)).ToHttp(http, ctx, Supply);
        });

        return app;
    }
}
