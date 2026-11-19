using System.Globalization;
using System.Text;

namespace WakeMesh.Core;

/// <summary>
/// Japanese holiday calendar loaded from an external CSV stream.
/// Coverage bounds are explicit so years outside the file are not treated as "no holidays".
/// </summary>
public sealed class HolidayCalendar
{
    public required DateTimeOffset RetrievedAt { get; init; }
    public string? VersionLabel { get; init; }
    public required DateOnly CoverageStartInclusive { get; init; }
    public required DateOnly CoverageEndInclusive { get; init; }
    public required IReadOnlySet<DateOnly> Dates { get; init; }

    public bool Covers(DateOnly date) =>
        date >= CoverageStartInclusive && date <= CoverageEndInclusive;

    public bool Contains(DateOnly date) => Dates.Contains(date);

    /// <summary>
    /// Lookup used by alarm exclusion. Outside coverage is unknown — callers should prefer firing
    /// and surface a warning rather than treating the day as a non-holiday.
    /// </summary>
    public HolidayDateKind Classify(DateOnly date)
    {
        if (!Covers(date))
        {
            return HolidayDateKind.OutsideCoverage;
        }

        return Contains(date) ? HolidayDateKind.Holiday : HolidayDateKind.NonHoliday;
    }
}

public enum HolidayDateKind
{
    NonHoliday = 0,
    Holiday = 1,
    /// <summary>Date is outside the calendar's known coverage; do not infer "not a holiday".</summary>
    OutsideCoverage = 2
}

public sealed class HolidayCalendarParser
{
    private static readonly string ExpectedDateHeader = "国民の祝日・休日月日";
    private static readonly string ExpectedNameHeader = "国民の祝日・休日名称";
    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");

    static HolidayCalendarParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public HolidayCalendar Parse(
        Stream stream,
        DateTimeOffset retrievedAt,
        string? versionLabel = null,
        Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        encoding ??= Encoding.GetEncoding(932);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        var headerLine = reader.ReadLine()
            ?? throw new FormatException("Holiday CSV is empty.");
        var headerFields = ParseCsvLine(headerLine);
        if (headerFields.Count < 2
            || !string.Equals(headerFields[0].Trim(), ExpectedDateHeader, StringComparison.Ordinal)
            || !string.Equals(headerFields[1].Trim(), ExpectedNameHeader, StringComparison.Ordinal))
        {
            throw new FormatException(
                $"Holiday CSV header must be '{ExpectedDateHeader},{ExpectedNameHeader}'.");
        }

        var dates = new HashSet<DateOnly>();
        DateOnly? min = null;
        DateOnly? max = null;
        var lineNumber = 1;

        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseCsvLine(line);
            if (fields.Count == 0 || (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0])))
            {
                continue;
            }

            if (fields.Count < 2)
            {
                throw new FormatException($"Holiday CSV line {lineNumber} must contain date and name columns.");
            }

            var dateText = fields[0].Trim();
            if (!DateOnly.TryParseExact(
                    dateText,
                    ["yyyy/M/d", "yyyy/MM/dd", "yyyy/M/dd", "yyyy/MM/d"],
                    JapaneseCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                throw new FormatException($"Holiday CSV line {lineNumber} has invalid date '{dateText}'.");
            }

            if (!dates.Add(date))
            {
                throw new FormatException($"Holiday CSV line {lineNumber} duplicates date {date:yyyy-MM-dd}.");
            }

            min = min is null || date < min ? date : min;
            max = max is null || date > max ? date : max;
        }

        if (min is null || max is null)
        {
            throw new FormatException("Holiday CSV contains no data rows.");
        }

        return new HolidayCalendar
        {
            RetrievedAt = retrievedAt,
            VersionLabel = versionLabel,
            CoverageStartInclusive = min.Value,
            CoverageEndInclusive = max.Value,
            Dates = dates
        };
    }

    /// <summary>
    /// Minimal RFC4180-ish CSV field parser supporting quoted fields and doubled quotes.
    /// </summary>
    internal static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else
            {
                if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }
        }

        if (inQuotes)
        {
            throw new FormatException("Holiday CSV line has an unclosed quote.");
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
