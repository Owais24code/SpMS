using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Catalog;

public sealed record ServiceInput(string? Code, string? Name, string? CatalogType, string? CategoryCode, string? ShortDescription,
    int? DurationMinutes, int? PreBufferMinutes, int? PostBufferMinutes, long? BasePriceMinor, string? CurrencyCode, string? TaxCode,
    short? CapacityMaximum, string? RequiredResourceType, string[]? RequiredCapabilities, string[]? RequiredLicenseTypeCodes,
    bool? RequiresIntake, string? IntakeFormCode, bool? DepositRequired, bool? OnlineBookable, short? MinimumGuestAge);

public sealed record OptionInput(string? OptionCode, string? OptionType, string? Label, long? PriceDeltaMinor, int? DurationDeltaMinutes, short? MaxQuantity);

public sealed record OfferingInput(long? PriceMinor, string? CurrencyCode, bool? OnlineBookable, int? RoomTurnoverMinutes,
    int? ProviderTransitionMinutes, string? Status);

public sealed record TaxRuleInput(string? TaxCode, string? Jurisdiction, decimal? Rate, bool? Inclusive, bool? PropertyOnly, string? EffectiveFrom);

public sealed record TransitionInput(string? To, string? Reason);

public sealed record OfferingView(ServiceRow Service, PropertyServiceRow? Offering);

/// <summary>
/// The tenant's menu (§53.2 Catalog) and what each property offers from it.
///
/// A service is drafted, activated, withdrawn and retired; nothing is
/// deleted, because an appointment names the service it booked. Changing a
/// price or duration never reaches existing bookings — each appointment froze
/// what it booked. A property offers a service with its own price and buffers
/// or not at all (property_service).
/// </summary>
public sealed partial class CatalogService(SpmsDbContext db, MasterData master, IUnitOfWork uow)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")] private static partial Regex CodeRx();
    [GeneratedRegex("^[A-Z]{3}$")] private static partial Regex CurrencyRx();
    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,19}$")] private static partial Regex TaxCodeRx();

    private static readonly Dictionary<string, string[]> ServiceMoves = new()
    {
        [ServiceStatuses.Draft] = [ServiceStatuses.Active, ServiceStatuses.Retired],
        [ServiceStatuses.Active] = [ServiceStatuses.Inactive, ServiceStatuses.Retired],
        [ServiceStatuses.Inactive] = [ServiceStatuses.Active, ServiceStatuses.Retired],
        [ServiceStatuses.Retired] = [],
    };

    public Task<List<ServiceRow>> ServicesAsync(string? status, CancellationToken ct) =>
        db.Set<ServiceRow>().AsNoTracking().Where(s => status == null || s.Status == status).OrderBy(s => s.Name).ToListAsync(ct);

    public Task<ServiceRow?> ServiceAsync(Guid id, CancellationToken ct) =>
        db.Set<ServiceRow>().AsNoTracking().SingleOrDefaultAsync(s => s.ServiceId == id, ct);

    public static string? Validate(ServiceInput i, bool creating)
    {
        if (creating && (i.Code is null || i.Name is null || i.DurationMinutes is null)) return "code, name and durationMinutes are required.";
        if (i.Code is not null && !CodeRx().IsMatch(i.Code)) return "code is 2–40 lowercase letters, digits and hyphens.";
        if (i.Name is not null && (i.Name.Trim().Length is 0 or > 120)) return "name is 1–120 characters.";
        if (i.DurationMinutes is { } d && (d <= 0 || d > 600 || d % 5 != 0)) return "durationMinutes is a positive multiple of 5, at most 600.";
        if (i.PreBufferMinutes is < 0 or > 120 || i.PostBufferMinutes is < 0 or > 120) return "Buffers are 0–120 minutes.";
        if (i.BasePriceMinor is < 0) return "basePriceMinor cannot be negative.";
        if (i.CurrencyCode is not null && !CurrencyRx().IsMatch(i.CurrencyCode)) return "currencyCode is ISO 4217 (USD).";
        if (i.TaxCode is { Length: > 0 } t && !TaxCodeRx().IsMatch(t)) return "taxCode is an uppercase code (SPA).";
        if (i.CatalogType is not null and not ("Service" or "AddOn" or "Package")) return "catalogType is Service, AddOn or Package.";
        if (i.RequiredResourceType is not null and not ("TreatmentRoom" or "WetRoom" or "CoupleRoom" or "Chair"))
            return "requiredResourceType is TreatmentRoom, WetRoom, CoupleRoom or Chair.";
        return null;
    }

    private static void Apply(ServiceRow s, ServiceInput i)
    {
        if (i.Code is not null) s.Code = i.Code;
        if (i.Name is not null) s.Name = i.Name.Trim();
        if (i.CatalogType is not null) s.CatalogType = i.CatalogType;
        if (i.CategoryCode is not null) s.CategoryCode = i.CategoryCode.Length == 0 ? null : i.CategoryCode;
        if (i.ShortDescription is not null) s.ShortDescription = i.ShortDescription.Length == 0 ? null : i.ShortDescription;
        if (i.DurationMinutes is { } d) s.DurationMinutes = d;
        if (i.PreBufferMinutes is { } pre) s.PreBufferMinutes = pre;
        if (i.PostBufferMinutes is { } post) s.PostBufferMinutes = post;
        if (i.BasePriceMinor is { } p) s.BasePriceMinor = p;
        if (i.CurrencyCode is not null) s.CurrencyCode = i.CurrencyCode;
        if (i.TaxCode is not null) s.TaxCode = i.TaxCode.Length == 0 ? null : i.TaxCode;
        if (i.CapacityMaximum is { } c) s.CapacityMaximum = c;
        if (i.RequiredResourceType is not null) s.RequiredResourceType = i.RequiredResourceType.Length == 0 ? null : i.RequiredResourceType;
        if (i.RequiredCapabilities is not null) s.RequiredCapabilities = i.RequiredCapabilities;
        if (i.RequiredLicenseTypeCodes is not null) s.RequiredLicenseTypeCodes = i.RequiredLicenseTypeCodes;
        if (i.RequiresIntake is { } ri) s.RequiresIntake = ri;
        if (i.IntakeFormCode is not null) s.IntakeFormCode = i.IntakeFormCode.Length == 0 ? null : i.IntakeFormCode;
        if (i.DepositRequired is { } dr) s.DepositRequired = dr;
        if (i.OnlineBookable is { } ob) s.OnlineBookable = ob;
        if (i.MinimumGuestAge is { } age) s.MinimumGuestAge = age;
    }

    public Task<Edit<ServiceRow>> CreateAsync(ServiceInput i, string defaultCurrency, CancellationToken ct)
    {
        var row = new ServiceRow { ServiceId = Uuid7.New(), Status = ServiceStatuses.Draft, CurrencyCode = defaultCurrency };
        Apply(row, i);
        return master.CreateAsync(row, "catalog.service.create", "service", s => s.ServiceId, ct);
    }

    public Task<Edit<ServiceRow>> UpdateAsync(Guid id, int version, ServiceInput i, CancellationToken ct) =>
        master.ChangeAsync<ServiceRow>(s => s.ServiceId == id, version, "catalog.service.update", "service", s => s.ServiceId, s =>
        {
            if (s.Status == ServiceStatuses.Retired) return "A retired service is not changed.";
            Apply(s, i);
            return null;
        }, ct);

    public Task<Edit<ServiceRow>> TransitionAsync(Guid id, int version, string to, string? reason, CancellationToken ct) =>
        master.ChangeAsync<ServiceRow>(s => s.ServiceId == id, version, "catalog.service.transition", "service", s => s.ServiceId, s =>
        {
            if (!ServiceMoves.TryGetValue(s.Status, out var next) || !next.Contains(to)) return $"A {s.Status} service cannot become {to}.";
            s.Status = to;
            return null;
        }, ct, reason);

    /* -------------------------------- options -------------------------------- */

    public Task<List<ServiceOptionRuleRow>> OptionsAsync(Guid serviceId, CancellationToken ct) =>
        db.Set<ServiceOptionRuleRow>().AsNoTracking().Where(o => o.ServiceId == serviceId).OrderBy(o => o.Label).ToListAsync(ct);

    public Task<Edit<ServiceOptionRuleRow>> AddOptionAsync(Guid serviceId, OptionInput i, CancellationToken ct) =>
        master.CreateAsync(new ServiceOptionRuleRow
        {
            ServiceOptionRuleId = Uuid7.New(), ServiceId = serviceId, OptionCode = i.OptionCode!, OptionType = i.OptionType!, Label = i.Label!.Trim(),
            PriceDeltaMinor = i.PriceDeltaMinor ?? 0, DurationDeltaMinutes = i.DurationDeltaMinutes ?? 0, MaxQuantity = i.MaxQuantity ?? 1,
        }, "catalog.option.create", "service_option_rule", o => o.ServiceOptionRuleId, ct,
            async () => await db.Set<ServiceRow>().AnyAsync(s => s.ServiceId == serviceId, ct) ? null : (string?)"No such service.");

    public Task<Edit<ServiceOptionRuleRow>> RetireOptionAsync(Guid optionId, int version, CancellationToken ct) =>
        master.ChangeAsync<ServiceOptionRuleRow>(o => o.ServiceOptionRuleId == optionId, version, "catalog.option.retire", "service_option_rule",
            o => o.ServiceOptionRuleId, o =>
            {
                if (o.Status == ServiceOptionRuleStatuses.Retired) return "Already retired.";
                o.Status = ServiceOptionRuleStatuses.Retired;
                return null;
            }, ct);

    /* ------------------------------ the offering ----------------------------- */

    /// <summary>Every service in the catalogue with this property's terms for it, if it offers it.</summary>
    public async Task<List<OfferingView>> OfferingAsync(CancellationToken ct)
    {
        var property = db.Scope.RequireProperty();
        var services = await db.Set<ServiceRow>().AsNoTracking().Where(s => s.Status != ServiceStatuses.Retired).OrderBy(s => s.Name).ToListAsync(ct);
        var offers = await db.Set<PropertyServiceRow>().AsNoTracking().Where(p => p.PropertyId == property).ToListAsync(ct);
        return services.Select(s => new OfferingView(s, offers.FirstOrDefault(o => o.ServiceId == s.ServiceId))).ToList();
    }

    /// <summary>Offers a service here (or changes the terms). The first call creates the row; later calls need its version.</summary>
    public async Task<Edit<PropertyServiceRow>> OfferAsync(Guid serviceId, int? version, OfferingInput i, CancellationToken ct)
    {
        if ((i.PriceMinor is null) != (i.CurrencyCode is null) && i.PriceMinor is not null) return Edit<PropertyServiceRow>.Refused(EditOutcome.Invalid, "A property price names its currency.");
        if (i.Status is not null and not ("Active" or "Inactive")) return Edit<PropertyServiceRow>.Refused(EditOutcome.Invalid, "status is Active or Inactive.");
        await using var tx = await uow.BeginAsync(ct);
        var result = await OfferInTransactionAsync(serviceId, version, i, ct);
        if (result.Outcome == EditOutcome.Ok) await tx.CommitAsync(ct);
        return result;
    }

    private async Task<Edit<PropertyServiceRow>> OfferInTransactionAsync(Guid serviceId, int? version, OfferingInput i, CancellationToken ct)
    {
        var property = db.Scope.RequireProperty();
        var existing = await db.Set<PropertyServiceRow>().AsNoTracking().Where(p => p.PropertyId == property && p.ServiceId == serviceId)
            .Select(p => (Guid?)p.PropertyServiceId).SingleOrDefaultAsync(ct);
        if (existing is null)
            return await master.CreateAsync(new PropertyServiceRow
            {
                PropertyServiceId = Uuid7.New(), PropertyId = property, ServiceId = serviceId, PriceMinor = i.PriceMinor, CurrencyCode = i.CurrencyCode,
                OnlineBookable = i.OnlineBookable, RoomTurnoverMinutes = i.RoomTurnoverMinutes, ProviderTransitionMinutes = i.ProviderTransitionMinutes,
                Status = i.Status ?? PropertyServiceStatuses.Active,
            }, "catalog.offering.create", "property_service", p => p.PropertyServiceId, ct,
                async () => await db.Set<ServiceRow>().AnyAsync(s => s.ServiceId == serviceId && s.Status != ServiceStatuses.Retired, ct)
                    ? null : (string?)"No such service, or it is retired.");
        if (version is null) return Edit<PropertyServiceRow>.Refused(EditOutcome.Invalid, "This property already offers the service: send If-Match to change its terms.");
        return await master.ChangeAsync<PropertyServiceRow>(p => p.PropertyServiceId == existing, version.Value, "catalog.offering.update", "property_service",
            p => p.PropertyServiceId, p =>
            {
                p.PriceMinor = i.PriceMinor; p.CurrencyCode = i.CurrencyCode;
                p.OnlineBookable = i.OnlineBookable; p.RoomTurnoverMinutes = i.RoomTurnoverMinutes; p.ProviderTransitionMinutes = i.ProviderTransitionMinutes;
                if (i.Status is not null) p.Status = i.Status;
                return null;
            }, ct);
    }

    /* --------------------------------- tax ---------------------------------- */

    public Task<List<TaxRuleRow>> TaxRulesAsync(CancellationToken ct) =>
        db.Set<TaxRuleRow>().AsNoTracking().OrderBy(t => t.TaxCode).ThenByDescending(t => t.EffectiveFrom).ToListAsync(ct);

    /// <summary>A new rate is a new row from its effective date; the previous rate for the same code and scope ends there.</summary>
    public async Task<Edit<TaxRuleRow>> AddTaxRuleAsync(TaxRuleInput i, DateTimeOffset from, CancellationToken ct)
    {
        if (i.TaxCode is null || !TaxCodeRx().IsMatch(i.TaxCode)) return Edit<TaxRuleRow>.Refused(EditOutcome.Invalid, "taxCode is an uppercase code (SPA).");
        if (i.Rate is not (>= 0m and < 1m)) return Edit<TaxRuleRow>.Refused(EditOutcome.Invalid, "rate is a fraction: 0.08875 for 8.875%.");
        if (string.IsNullOrWhiteSpace(i.Jurisdiction)) return Edit<TaxRuleRow>.Refused(EditOutcome.Invalid, "jurisdiction is required.");
        await using var tx = await uow.BeginAsync(ct);
        Guid? property = i.PropertyOnly == true ? db.Scope.RequireProperty() : null;
        var previous = await db.Set<TaxRuleRow>().Where(t => t.TaxCode == i.TaxCode && t.PropertyId == property && t.Status == TaxRuleStatuses.Active
                                                          && t.EffectiveTo == null && t.EffectiveFrom < from).ToListAsync(ct);
        foreach (var p in previous) p.EffectiveTo = from;
        var added = await master.CreateAsync(new TaxRuleRow
        {
            TaxRuleId = Uuid7.New(), PropertyId = property, TaxCode = i.TaxCode, Jurisdiction = i.Jurisdiction!.Trim(), Rate = i.Rate!.Value,
            Inclusive = i.Inclusive ?? false, EffectiveFrom = from, Status = TaxRuleStatuses.Active,
        }, "catalog.tax_rule.create", "tax_rule", t => t.TaxRuleId, ct);
        if (added.Outcome == EditOutcome.Ok) await tx.CommitAsync(ct);
        return added;
    }
}

public static class CatalogEndpoints
{
    private static object Service(ServiceRow s) => new
    {
        serviceId = s.ServiceId, s.Code, s.Name, s.CatalogType, s.CategoryCode, s.ShortDescription, s.DurationMinutes, s.PreBufferMinutes,
        s.PostBufferMinutes, s.BasePriceMinor, currencyCode = s.CurrencyCode.Trim(), s.TaxCode, s.CapacityMaximum, s.RequiredResourceType,
        s.RequiredCapabilities, s.RequiredLicenseTypeCodes, s.RequiresIntake, s.IntakeFormCode, s.DepositRequired, s.OnlineBookable,
        s.MinimumGuestAge, s.Status, rowVersion = s.Version, eTag = $"\"{s.Version}\"",
    };

    private static object Option(ServiceOptionRuleRow o) => new
    {
        optionId = o.ServiceOptionRuleId, o.ServiceId, o.OptionCode, o.OptionType, o.Label, o.PriceDeltaMinor, o.DurationDeltaMinutes,
        o.MaxQuantity, o.Status, rowVersion = o.Version, eTag = $"\"{o.Version}\"",
    };

    private static object Offering(PropertyServiceRow p) => new
    {
        propertyServiceId = p.PropertyServiceId, p.ServiceId, p.PriceMinor, currencyCode = p.CurrencyCode?.Trim(), p.OnlineBookable,
        p.RoomTurnoverMinutes, p.ProviderTransitionMinutes, p.Status, rowVersion = p.Version, eTag = $"\"{p.Version}\"",
    };

    private static object Tax(TaxRuleRow t) => new
    {
        taxRuleId = t.TaxRuleId, t.PropertyId, t.TaxCode, t.Jurisdiction, t.Rate, t.Inclusive, t.Status,
        effectiveFrom = t.EffectiveFrom.ToUniversalTime().ToString("O"), effectiveTo = t.EffectiveTo?.ToUniversalTime().ToString("O"),
        rowVersion = t.Version, eTag = $"\"{t.Version}\"",
    };

    private static Task<IResult?> Catalogue(RequestContext ctx, IAccessDecider access, CancellationToken ct) =>
        WebApi.GateAsync(ctx, access, SpaScopes.Write, "can_manage_catalog", Fga.Tenant(ctx.TenantId), ct);

    private static Task<IResult?> Offer(RequestContext ctx, IAccessDecider access, CancellationToken ct) =>
        WebApi.GateAsync(ctx, access, SpaScopes.Write, "can_manage_offering", Fga.Property(ctx.PropertyId), ct);

    public static IEndpointRouteBuilder MapCatalog(this IEndpointRouteBuilder app)
    {
        app.MapGet("/catalog/services", async (HttpContext http, CatalogService catalog, string? status, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await catalog.ServicesAsync(status, ct)).Select(Service), Json.Options);
        });

        app.MapGet("/catalog/services/{id:guid}", async (HttpContext http, CatalogService catalog, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return await catalog.ServiceAsync(id, ct) is { } s ? EditResults.Ok(http, s, Service) : WebApi.NotFound(ctx);
        });

        app.MapPost("/catalog/services", async (HttpContext http, CatalogService catalog, IAccessDecider access, SpmsDbContext db, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Catalogue(ctx, access, ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<ServiceInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (CatalogService.Validate(i, creating: true) is { } bad) return WebApi.Invalid(ctx, bad);
            var currency = i.CurrencyCode ?? await db.Set<Spms.Modules.Core.Data.PropertyRow>().Where(p => p.PropertyId == ctx.PropertyId)
                .Select(p => p.CurrencyCode).SingleOrDefaultAsync(ct) ?? "USD";
            return (await catalog.CreateAsync(i, currency.Trim(), ct)).ToHttp(http, ctx, Service, 201);
        });

        app.MapPatch("/catalog/services/{id:guid}", async (HttpContext http, CatalogService catalog, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Catalogue(ctx, access, ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<ServiceInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (CatalogService.Validate(i, creating: false) is { } bad) return WebApi.Invalid(ctx, bad);
            return (await catalog.UpdateAsync(id, version, i, ct)).ToHttp(http, ctx, Service);
        });

        app.MapPost("/catalog/services/{id:guid}/transitions", async (HttpContext http, CatalogService catalog, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Catalogue(ctx, access, ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var (i, fail) = await WebApi.BodyAsync<TransitionInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.To is null) return WebApi.Invalid(ctx, "to is required: Active, Inactive or Retired.");
            return (await catalog.TransitionAsync(id, version, i.To, i.Reason, ct)).ToHttp(http, ctx, Service);
        });

        app.MapGet("/catalog/services/{id:guid}/options", async (HttpContext http, CatalogService catalog, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await catalog.OptionsAsync(id, ct)).Select(Option), Json.Options);
        });

        app.MapPost("/catalog/services/{id:guid}/options", async (HttpContext http, CatalogService catalog, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Catalogue(ctx, access, ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<OptionInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.OptionCode) || string.IsNullOrWhiteSpace(i.Label)
                || i.OptionType is not ("AddOn" or "Enhancement" or "DurationExtension" or "Preference"))
                return WebApi.Invalid(ctx, "optionCode, label and optionType (AddOn, Enhancement, DurationExtension, Preference) are required.");
            return (await catalog.AddOptionAsync(id, i, ct)).ToHttp(http, ctx, Option, 201);
        });

        app.MapPost("/catalog/options/{id:guid}/retire", async (HttpContext http, CatalogService catalog, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Catalogue(ctx, access, ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            return (await catalog.RetireOptionAsync(id, version, ct)).ToHttp(http, ctx, Option);
        });

        app.MapGet("/catalog/offering", async (HttpContext http, CatalogService catalog, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await catalog.OfferingAsync(ct)).Select(v => new
            {
                service = Service(v.Service), offering = v.Offering is null ? null : Offering(v.Offering),
                offered = v.Offering?.Status == PropertyServiceStatuses.Active,
                effectivePriceMinor = v.Offering?.PriceMinor ?? v.Service.BasePriceMinor,
            }), Json.Options);
        });

        app.MapPut("/catalog/offering/{serviceId:guid}", async (HttpContext http, CatalogService catalog, IAccessDecider access, Guid serviceId, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Offer(ctx, access, ct) is { } refused) return refused;
            int? version = null;
            if (http.Request.Headers.IfMatch.Count > 0)
            {
                if (Guard.RequireIfMatch(http, ctx, out var v) is { } noMatch) return noMatch;
                version = v;
            }
            var (i, fail) = await WebApi.BodyAsync<OfferingInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.PriceMinor is < 0) return WebApi.Invalid(ctx, "priceMinor cannot be negative.");
            return (await catalog.OfferAsync(serviceId, version, i, ct)).ToHttp(http, ctx, Offering, version is null ? 201 : 200);
        });

        app.MapGet("/catalog/tax-rules", async (HttpContext http, CatalogService catalog, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
            return Results.Json((await catalog.TaxRulesAsync(ct)).Select(Tax), Json.Options);
        });

        app.MapPost("/catalog/tax-rules", async (HttpContext http, CatalogService catalog, IAccessDecider access, IClock clock, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Catalogue(ctx, access, ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<TaxRuleInput>(http, ctx, ct);
            if (i is null) return fail!;
            var from = clock.UtcNow;
            if (i.EffectiveFrom is not null && !Guard.TryParseInstant(i.EffectiveFrom, out from)) return WebApi.Invalid(ctx, "effectiveFrom is ISO 8601.");
            return (await catalog.AddTaxRuleAsync(i, from, ct)).ToHttp(http, ctx, Tax, 201);
        });

        return app;
    }
}
