using System;
using System.Linq;
using WakeMesh.Core;
using Xunit;

namespace WakeMesh.Core.Tests;

/// <summary>
/// JPC-APP: the bundled JpCalendar data must be verifiable, strictly bounded and fail closed
/// (no data) when the package content does not match the pinned digest.
/// </summary>
public sealed class BundledHolidayCalendarTests
{
    [Fact]
    public void BundledData_MatchesPinnedCountEndpointsAndDigest()
    {
        Assert.True(BundledHolidayCalendar.TryGet(out var calendar));
        Assert.NotNull(calendar);

        Assert.Equal(1067, calendar!.Dates.Count);
        Assert.Equal(new DateOnly(1955, 1, 1), calendar.CoverageStartInclusive);
        Assert.Equal(new DateOnly(2027, 11, 23), calendar.CoverageEndInclusive);
        Assert.Equal(new DateOnly(1955, 1, 1), calendar.Dates.Min());
        Assert.Equal(new DateOnly(2027, 11, 23), calendar.Dates.Max());

        var digest = BundledHolidayCalendar.ComputeSha256(
            BundledHolidayCalendar.BuildCanonicalCsv(calendar.Dates));
        Assert.Equal(BundledHolidayCalendar.ExpectedSha256, digest);
    }

    [Fact]
    public void BundledData_UsesStableVerificationDate_NotUtcNow()
    {
        Assert.True(BundledHolidayCalendar.TryGet(out var calendar));

        Assert.Equal(
            new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.FromHours(9)),
            calendar!.RetrievedAt);
        Assert.Contains("JpCalendar 2.0.0", calendar.VersionLabel);
    }

    [Theory]
    [InlineData("2026-11-19", HolidayDateKind.NonHoliday)]
    [InlineData("2026-11-23", HolidayDateKind.Holiday)]
    [InlineData("2026-05-06", HolidayDateKind.Holiday)] // substitute holiday
    [InlineData("2026-09-22", HolidayDateKind.Holiday)] // citizen's holiday
    [InlineData("2027-11-23", HolidayDateKind.Holiday)]
    [InlineData("1955-01-01", HolidayDateKind.Holiday)]
    [InlineData("1954-12-31", HolidayDateKind.OutsideCoverage)]
    [InlineData("2027-11-24", HolidayDateKind.OutsideCoverage)]
    [InlineData("2028-01-10", HolidayDateKind.OutsideCoverage)]
    public void Classify_IsStrictInsideTheBundledCoverage(string isoDate, HolidayDateKind expected)
    {
        Assert.True(BundledHolidayCalendar.TryGet(out var calendar));
        var date = DateOnly.ParseExact(isoDate, "yyyy-MM-dd");

        Assert.Equal(expected, calendar!.Classify(date));
    }

    [Fact]
    public void LazyInitialisation_YieldsTheSameVerifiedInstancePerProcess()
    {
        Assert.True(BundledHolidayCalendar.TryGet(out var first));
        Assert.True(BundledHolidayCalendar.TryGet(out var second));

        Assert.Same(first, second);
        Assert.Same(BundledHolidayCalendar.Current.Calendar, first);
    }

    [Fact]
    public void Build_RejectsDigestMismatch_WhenCountAndEndpointsStillMatch()
    {
        Assert.True(BundledHolidayCalendar.TryGet(out var real));

        // Keep the count and the coverage endpoints identical, but swap one holiday for a
        // neighbouring ordinary day: only the digest can detect this tampering.
        var holiday = new DateOnly(2026, 11, 23);
        var ordinary = new DateOnly(2026, 11, 24);
        bool TamperedProbe(DateOnly date) =>
            date == holiday ? false : date == ordinary || real!.Dates.Contains(date);

        var result = BundledHolidayCalendar.Build(
            TamperedProbe,
            BundledHolidayCalendar.CoverageStartInclusive,
            BundledHolidayCalendar.CoverageEndInclusive,
            BundledHolidayCalendar.ExpectedSha256,
            BundledHolidayCalendar.ExpectedCount);

        Assert.False(result.HasData);
        Assert.Null(result.Calendar);
        Assert.Contains("sha256", result.FailureReason);
        Assert.Contains("祝日もアラーム対象", result.FailureReason);
    }

    [Fact]
    public void Build_RejectsWrongCount_AndWrongEndpoints()
    {
        var holidays = new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 12) };

        var countFailure = BundledHolidayCalendar.Build(
            holidays.Contains,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 12),
            BundledHolidayCalendar.ExpectedSha256,
            expectedCount: 3);
        Assert.False(countFailure.HasData);
        Assert.Contains("日付数", countFailure.FailureReason);

        var endpointFailure = BundledHolidayCalendar.Build(
            holidays.Contains,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 13),
            BundledHolidayCalendar.ExpectedSha256,
            expectedCount: 2);
        Assert.False(endpointFailure.HasData);
        Assert.Contains("カバレッジ端", endpointFailure.FailureReason);
    }

    [Fact]
    public void Build_RejectsExceptions_InsteadOfReturningPartialData()
    {
        var result = BundledHolidayCalendar.Build(
            _ => throw new InvalidOperationException("broken package"),
            BundledHolidayCalendar.CoverageStartInclusive,
            BundledHolidayCalendar.CoverageEndInclusive,
            BundledHolidayCalendar.ExpectedSha256,
            BundledHolidayCalendar.ExpectedCount);

        Assert.False(result.HasData);
        Assert.Null(result.Calendar);
        Assert.Contains("broken package", result.FailureReason);
        Assert.Contains("祝日もアラーム対象", result.FailureReason);
    }

    [Fact]
    public void CanonicalCsv_IsSortedIsoDatesWithTrailingLf()
    {
        var csv = BundledHolidayCalendar.BuildCanonicalCsv(new[]
        {
            new DateOnly(2026, 1, 12),
            new DateOnly(2025, 12, 31),
            new DateOnly(2026, 1, 1)
        });

        Assert.Equal("2025-12-31\n2026-01-01\n2026-01-12\n", csv.Replace("\r\n", "\n"));
        Assert.EndsWith("\n", csv);
    }
}
