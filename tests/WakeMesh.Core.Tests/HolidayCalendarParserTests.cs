using System.Text;
using WakeMesh.Core;

namespace WakeMesh.Core.Tests;

public sealed class HolidayCalendarParserTests
{
    private static readonly DateTimeOffset RetrievedAt =
        new(2026, 11, 14, 3, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Parse_reads_cp932_header_and_slash_dates()
    {
        // Fictitious miniature fixture shaped like Cabinet Office CSV (not official data).
        var csv = """
            国民の祝日・休日月日,国民の祝日・休日名称
            2024/1/1,元日
            2024/2/11,建国記念の日
            """;
        using var stream = OpenCp932(csv);

        var calendar = new HolidayCalendarParser().Parse(
            stream,
            retrievedAt: RetrievedAt,
            versionLabel: "fixture-v1");

        Assert.Equal(RetrievedAt, calendar.RetrievedAt);
        Assert.Equal("fixture-v1", calendar.VersionLabel);
        Assert.Equal(new DateOnly(2024, 1, 1), calendar.CoverageStartInclusive);
        Assert.Equal(new DateOnly(2024, 2, 11), calendar.CoverageEndInclusive);
        Assert.True(calendar.Contains(new DateOnly(2024, 1, 1)));
        Assert.True(calendar.Contains(new DateOnly(2024, 2, 11)));
        Assert.False(calendar.Contains(new DateOnly(2024, 1, 2)));
    }

    [Fact]
    public void Parse_supports_quoted_fields_with_commas_and_escaped_quotes()
    {
        var csv =
            "国民の祝日・休日月日,国民の祝日・休日名称\n" +
            "2024/5/3,\"憲法記念日\"\n" +
            "2024/5/4,\"みどりの日, メモ\"\n" +
            "2024/5/5,\"祝日 \"\"特別\"\"\"\n";
        using var stream = OpenCp932(csv);

        var calendar = new HolidayCalendarParser().Parse(stream, RetrievedAt, "q");

        Assert.Equal(3, calendar.Dates.Count);
        Assert.True(calendar.Contains(new DateOnly(2024, 5, 5)));
    }

    [Fact]
    public void Parse_throws_on_duplicate_dates()
    {
        var csv = """
            国民の祝日・休日月日,国民の祝日・休日名称
            2024/1/1,元日
            2024/1/1,重複
            """;
        using var stream = OpenCp932(csv);

        var ex = Assert.Throws<FormatException>(() =>
            new HolidayCalendarParser().Parse(stream, RetrievedAt));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_throws_on_invalid_date()
    {
        var csv = """
            国民の祝日・休日月日,国民の祝日・休日名称
            2024-01-01,元日
            """;
        using var stream = OpenCp932(csv);

        var ex = Assert.Throws<FormatException>(() =>
            new HolidayCalendarParser().Parse(stream, RetrievedAt));
        Assert.Contains("invalid date", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_throws_on_bad_header()
    {
        var csv = """
            date,name
            2024/1/1,元日
            """;
        using var stream = OpenCp932(csv);

        Assert.Throws<FormatException>(() =>
            new HolidayCalendarParser().Parse(stream, RetrievedAt));
    }

    [Fact]
    public void Classify_marks_outside_coverage_distinct_from_non_holiday()
    {
        var csv = """
            国民の祝日・休日月日,国民の祝日・休日名称
            2024/1/1,元日
            2024/1/8,成人の日
            """;
        using var stream = OpenCp932(csv);
        var calendar = new HolidayCalendarParser().Parse(stream, RetrievedAt, "cov");

        Assert.Equal(HolidayDateKind.Holiday, calendar.Classify(new DateOnly(2024, 1, 1)));
        Assert.Equal(HolidayDateKind.NonHoliday, calendar.Classify(new DateOnly(2024, 1, 2)));
        Assert.Equal(HolidayDateKind.OutsideCoverage, calendar.Classify(new DateOnly(2025, 1, 1)));
        Assert.Equal(HolidayDateKind.OutsideCoverage, calendar.Classify(new DateOnly(2023, 12, 31)));
    }

    private static MemoryStream OpenCp932(string utf16Text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(932).GetBytes(utf16Text.Replace("\n", "\r\n", StringComparison.Ordinal));
        return new MemoryStream(bytes);
    }
}
