using Spms.Api.Endpoints;
using Spms.Api.Infrastructure;
using Spms.Domain.Errors;
using Xunit;

namespace Spms.Tests.Domain;

/// <summary>
/// The API layer's own helpers.
///
/// These were entirely uncovered: the guards, the problem+json shaping, the
/// ETag handling and the paging clamp all lived in a project the test project
/// did not even reference, and the tenancy convention lives in that layer.
/// </summary>
public class GuardTests
{
    [Fact]
    public void If_Match_accepts_a_strong_and_a_weak_etag()
    {
        Assert.True(Guard.ETagMatches("\"3\"", "\"3\""));
        Assert.True(Guard.ETagMatches("W/\"3\"", "\"3\""));
        Assert.True(Guard.ETagMatches("\"1\", W/\"3\"", "\"3\""));
        Assert.False(Guard.ETagMatches("\"2\"", "\"3\""));
    }

    [Fact]
    public void If_Match_refuses_a_bare_wildcard_on_a_consequential_write()
    {
        // RFC 7232 reads "*" as "if the resource exists", but API-002 requires
        // the caller to assert the version they read. Honouring it made 412
        // unreachable for any client that always sent it.
        Assert.False(Guard.ETagMatches("*", "\"3\""));
        Assert.False(Guard.TryParseIfMatchVersion("*", out _));

        Assert.True(Guard.TryParseIfMatchVersion("W/\"7\"", out var v));
        Assert.Equal(7, v);
    }

    [Fact]
    public void An_offsetless_timestamp_is_read_as_utc_not_as_server_local()
    {
        // A plain TryParse assumes the SERVER's zone, so the same request
        // landed on different instants depending on where the process ran —
        // in a field whose own violation rule says iso8601_required.
        Assert.True(Guard.TryParseInstant("2026-06-15T10:00:00", out var naive));
        Assert.Equal(TimeSpan.Zero, naive.Offset);
        Assert.Equal(10, naive.Hour);

        Assert.True(Guard.TryParseInstant("2026-06-15T08:00:00+05:30", out var offset));
        Assert.Equal(TimeSpan.Zero, offset.Offset);
        Assert.Equal(2, offset.Hour);
        Assert.Equal(30, offset.Minute);
    }

    [Fact]
    public void The_sane_instant_window_includes_its_lower_bound()
    {
        // Every caller's message says "between 2000 and 2100", and a strict
        // comparison rejected 2000-01-01T00:00:00Z itself.
        Assert.True(Guard.IsSaneInstant(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(Guard.IsSaneInstant(new DateTimeOffset(2099, 12, 31, 23, 59, 0, TimeSpan.Zero)));
        Assert.False(Guard.IsSaneInstant(new DateTimeOffset(1999, 12, 31, 23, 59, 0, TimeSpan.Zero)));
        Assert.False(Guard.IsSaneInstant(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Paging_clamps_rather_than_honouring_an_absurd_request()
    {
        Assert.Equal((0, PageLimits.Default), Guard.Page(null, null));
        Assert.Equal((0, PageLimits.Max), Guard.Page(-5, 99999));
        Assert.Equal((10, 1), Guard.Page(10, 0));
    }
}

public class ProblemTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("correlation_id")]
    [InlineData("code")]
    [InlineData("detail")]
    public void A_problem_extension_cannot_shadow_a_reserved_member(string reserved)
    {
        // Extensions were copied straight into the body, so a call site
        // passing `status` replaced the HTTP status with an appointment status
        // string and any client reading body.status got "Cancelled".
        Assert.Throws<ArgumentException>(() => Problem.From(
            ApiError.ValidationFailed, "corr", extensions: Problem.Ext(reserved, "spoofed")));
    }

    [Fact]
    public void A_non_reserved_extension_is_accepted()
    {
        var r = Problem.From(ApiError.HardConflict, "corr", extensions: Problem.Ext("conflicts", Array.Empty<string>()));
        Assert.NotNull(r);
    }
}

public class LocalClockTests
{
    [Fact]
    public void An_unresolvable_time_zone_renders_a_marked_fallback_not_a_crash()
    {
        var instant = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);
        Assert.Null(LocalClock.Resolve("Nowhere/Invented"));
        // The reader must be able to tell UTC is not being presented as local.
        Assert.EndsWith("Z", LocalClock.Label(instant, "Nowhere/Invented"));
    }

    [Fact]
    public void Property_local_rendering_uses_the_propertys_zone()
    {
        var instant = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);
        if (LocalClock.Resolve("America/New_York") is null) return;   // no zone data on this host
        Assert.Equal("2026-06-15 09:00", LocalClock.Label(instant, "America/New_York"));
    }
}

/// <summary>
/// The business day.
///
/// /availability and /appointments?date= both answer a question about one
/// property day, and they used to disagree: the grid was built in the
/// property's zone while the list was built from UTC midnight, so a property
/// at a large positive offset lost its morning from the board while the grid
/// still showed it. The fix was one shared function; these are the cases that
/// would have caught the divergence.
/// </summary>
public class BusinessDayTests
{
    [Fact]
    public void A_day_starts_at_local_midnight_not_UTC_midnight()
    {
        // New York in June is UTC-4, so the day opens at 04:00Z.
        var start = LocalClock.DayStartUtc(new DateOnly(2026, 6, 15), "America/New_York");
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 4, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void A_positive_offset_property_opens_before_UTC_midnight()
    {
        // Tokyo is UTC+9 year round: the local day begins the PREVIOUS UTC day.
        // This is the case the old UTC-midnight window silently truncated.
        var start = LocalClock.DayStartUtc(new DateOnly(2026, 6, 15), "Asia/Tokyo");
        Assert.Equal(new DateTimeOffset(2026, 6, 14, 15, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void A_spring_forward_date_does_not_throw()
    {
        // Lord Howe and a handful of zones skip midnight itself. A 500 on one
        // day a year is not an acceptable way to discover that.
        var start = LocalClock.DayStartUtc(new DateOnly(2026, 9, 6), "Australia/Lord_Howe");
        Assert.True(start > DateTimeOffset.MinValue);
    }

    [Fact]
    public void An_unresolvable_zone_falls_back_to_UTC_rather_than_failing()
    {
        var start = LocalClock.DayStartUtc(new DateOnly(2026, 6, 15), "Mars/Olympus_Mons");
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void A_DST_day_is_twenty_three_or_twenty_five_hours_long()
    {
        // The window /appointments?date= builds is [dayStart, nextDayStart),
        // which is not always 24 hours — a fixed AddDays(1) would have
        // over- or under-covered the day the clocks change.
        var from = LocalClock.DayStartUtc(new DateOnly(2026, 11, 1), "America/New_York");
        var to = LocalClock.DayStartUtc(new DateOnly(2026, 11, 2), "America/New_York");
        Assert.Equal(TimeSpan.FromHours(25), to - from);
    }
}
