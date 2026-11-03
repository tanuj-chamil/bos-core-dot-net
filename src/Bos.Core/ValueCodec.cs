using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bos.Contracts;

namespace Bos.Core;

// Schema constraints and index ordering share these exact, non-floating-point codecs.
internal static class ValueCodec
{
    private const RegexOptions SafeRegexOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    internal static bool TryMetadataInt(JsonNode? node, out int result)
    {
        result = 0;
        if (node is not JsonValue value || node.GetValueKind() != JsonValueKind.Number) return false;
        return value.TryGetValue<int>(out result)
            || int.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }

    internal static string String(JsonNode value)
    {
        if (value.GetValueKind() != JsonValueKind.String) throw Invalid("Expected a string.");
        return CanonicalJson.Text(value.GetValue<string>());
    }

    internal static BigInteger Integer(string text)
    {
        if (text.Length > 16384 || !Regex.IsMatch(text, "\\A[+-]?[0-9]+\\z", RegexOptions.CultureInvariant))
            throw Invalid("Expected a base-10 integer string.");
        var value = BigInteger.Parse(text, CultureInfo.InvariantCulture);
        if (value < long.MinValue || value > long.MaxValue) throw Invalid("Integer exceeds signed 64-bit range.");
        return value;
    }

    internal static (BigInteger Value, int Scale) Decimal(string text)
    {
        if (text.Length > 16384 || !Regex.IsMatch(text, "\\A[+-]?[0-9]+(?:\\.[0-9]+)?\\z", RegexOptions.CultureInvariant))
            throw Invalid("Expected a base-10 decimal string without an exponent.");
        var dot = text.IndexOf('.');
        var scale = dot < 0 ? 0 : text.Length - dot - 1;
        if (scale > 12) throw Invalid("Decimal has more than 12 fractional digits.");
        var value = BigInteger.Parse(text.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return (value, scale);
    }

    internal static BigInteger Rescale(BigInteger value, int from, int to)
    {
        if (from > to) throw Invalid("Excess fractional digits; explicit rounding is required.");
        return value * BigInteger.Pow(10, to - from);
    }

    internal static string Format(BigInteger value, int scale)
    {
        var digits = BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        if (scale > 0) digits = digits.Insert(digits.Length - scale, ".");
        return value.Sign < 0 ? "-" + digits : digits;
    }

    internal static Regex Pattern(string pattern)
    {
        if (pattern.EnumerateRunes().Count() > 256) throw Invalid("Pattern exceeds 256 characters.");
        try { return new Regex(pattern, SafeRegexOptions, RegexTimeout); }
        catch (ArgumentException) { throw Invalid("Invalid or unsupported regular expression."); }
        catch (NotSupportedException) { throw Invalid("Invalid or unsupported regular expression."); }
    }

    internal static bool Matches(Regex pattern, string value)
    {
        try { return pattern.IsMatch(value); }
        catch (RegexMatchTimeoutException) { throw Invalid("Regular expression match timed out."); }
    }

    internal static string StringFormat(string format, string text)
    {
        switch (format)
        {
            case "email":
                if (text.Length > 254 || !Regex.IsMatch(text, "\\A[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?=.{1,253}\\z)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+\\z", RegexOptions.CultureInvariant))
                    throw Invalid("Expected an email address.");
                return text;
            case "phone":
                if (!Regex.IsMatch(text, "\\A\\+[1-9][0-9]{1,14}\\z", RegexOptions.CultureInvariant)) throw Invalid("Expected an E.164 phone number.");
                return text;
            case "url":
                if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 || url.Host.Length == 0)
                    throw Invalid("Expected an absolute HTTP/HTTPS URL without credentials.");
                return text;
            case "uuid":
                if (!Guid.TryParseExact(text, "D", out var uuid)) throw Invalid("Expected a UUID.");
                return uuid.ToString("D", CultureInfo.InvariantCulture);
            case "uri":
                if (!Uri.TryCreate(text, UriKind.Absolute, out _)) throw Invalid("Expected an absolute URI.");
                return text;
            case "slug":
                if (!Regex.IsMatch(text, "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)) throw Invalid("Expected a lowercase slug.");
                return text;
            case "ipAddress":
                if (!IPAddress.TryParse(text, out _)) throw Invalid("Expected an IP address.");
                return text;
            case "hostname":
                if (!Hostname(text)) throw Invalid("Expected a hostname.");
                return text;
            case "countryCode":
                if (!Regex.IsMatch(text, "\\A[A-Za-z]{2}\\z", RegexOptions.CultureInvariant)) throw Invalid("Expected an ISO alpha-2 country code.");
                return text.ToUpperInvariant();
            case "currencyCode":
                if (!Regex.IsMatch(text, "\\A[A-Za-z]{3}\\z", RegexOptions.CultureInvariant)) throw Invalid("Expected an ISO alpha-3 currency code.");
                return text.ToUpperInvariant();
            case "locale":
                if (!Regex.IsMatch(text, "\\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*\\z", RegexOptions.CultureInvariant)) throw Invalid("Expected a BCP47 locale.");
                try { _ = CultureInfo.GetCultureInfo(text); }
                catch (CultureNotFoundException) { throw Invalid("Expected a supported BCP47 locale."); }
                return text;
            case "timeZone":
                if (!Regex.IsMatch(text, "\\A[A-Za-z._+-]+(?:/[A-Za-z0-9._+-]+)*\\z", RegexOptions.CultureInvariant)) throw Invalid("Expected an IANA-style time-zone id.");
                try { _ = TimeZoneInfo.FindSystemTimeZoneById(text); }
                catch (TimeZoneNotFoundException) { throw Invalid("Expected an available IANA time zone."); }
                catch (InvalidTimeZoneException) { throw Invalid("Expected an available IANA time zone."); }
                return text;
            default: throw Invalid("Unknown string format.");
        }
    }

    internal static string Scalar(string kind, JsonNode value)
    {
        if (kind == "boolean")
        {
            if (value.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("Expected a boolean.");
            return value.GetValue<bool>() ? "true" : "false";
        }
        var text = String(value);
        switch (kind)
        {
            case "string":
            case "enum":
                if (Encoding.UTF8.GetByteCount(text) > 16 * 1024) throw Invalid("String exceeds 16 KiB UTF-8.");
                return text;
            case "integer": return Integer(text).ToString(CultureInfo.InvariantCulture);
            case "decimal":
            case "percentage":
                var number = Decimal(text);
                while (number.Scale > 0 && number.Value % 10 == 0) number = (number.Value / 10, number.Scale - 1);
                if (BigInteger.Abs(number.Value) >= BigInteger.Pow(10, 28)) throw Invalid("Decimal exceeds 28 digits.");
                return Format(number.Value, number.Scale);
            case "reference":
                try { return JsonArgs.Uuid(text); }
                catch (BosException) { throw Invalid("Expected a canonical lowercase UUID."); }
            case "userReference":
            case "attachment":
                if (!Guid.TryParseExact(text, "D", out var uuid)) throw Invalid("Expected a UUID.");
                return uuid.ToString("D", CultureInfo.InvariantCulture);
            case "date":
                if (!Regex.IsMatch(text, "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}\\z", RegexOptions.CultureInvariant)
                    || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid("Expected a Gregorian YYYY-MM-DD date.");
                return text;
            case "instant": return Instant(text);
            case "time": return Time(text);
            case "localDateTime": return LocalDateTime(text);
            case "offsetDateTime": return OffsetDateTime(text);
            case "duration": return Duration(text);
            case "calendarPeriod": return CalendarPeriod(text);
            default: throw Invalid("Unknown scalar kind.");
        }
    }

    private static bool Hostname(string text)
    {
        if (text.Length is < 1 or > 253 || text[^1] == '.') return false;
        foreach (var label in text.Split('.'))
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
        return true;
    }

    private static string Time(string text)
    {
        if (!Regex.IsMatch(text, "\\A[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,3})?\\z", RegexOptions.CultureInvariant)
            || !TimeOnly.TryParseExact(text, ["HH:mm:ss", "HH:mm:ss.f", "HH:mm:ss.ff", "HH:mm:ss.fff"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw Invalid("Expected an ISO time with seconds and at most milliseconds.");
        return time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private static string LocalDateTime(string text)
    {
        if (!Regex.IsMatch(text, "\\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,3})?\\z", RegexOptions.CultureInvariant)
            || !DateTime.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.f", "yyyy-MM-dd'T'HH:mm:ss.ff", "yyyy-MM-dd'T'HH:mm:ss.fff"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw Invalid("Expected a local ISO date-time with at most milliseconds.");
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private static string OffsetDateTime(string text)
    {
        var match = Regex.Match(text, "\\A(?<local>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,3})?)(?<zone>Z|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant);
        if (!match.Success || !DateTime.TryParseExact(match.Groups["local"].Value,
                ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.f", "yyyy-MM-dd'T'HH:mm:ss.ff", "yyyy-MM-dd'T'HH:mm:ss.fff"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw Invalid("Expected an ISO offset date-time with at most milliseconds.");
        var offset = Offset(match.Groups["zone"].Value);
        try { return new DateTimeOffset(local, offset).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture); }
        catch (ArgumentException) { throw Invalid("Invalid date-time offset."); }
    }

    private static string Instant(string text)
    {
        var match = Regex.Match(text, "\\A(?<local>[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,3})?)(?<zone>[Zz]|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant);
        if (!match.Success || !DateTime.TryParseExact(match.Groups["local"].Value.Replace('t', 'T'),
                ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.f", "yyyy-MM-dd'T'HH:mm:ss.ff", "yyyy-MM-dd'T'HH:mm:ss.fff"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw Invalid("Expected an RFC 3339 instant with offset and at most milliseconds.");
        var zone = match.Groups["zone"].Value;
        var minutes = 0;
        if (zone.Length > 1)
        {
            var hours = int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture);
            var remainder = int.Parse(zone.AsSpan(4, 2), CultureInfo.InvariantCulture);
            if (hours > 23 || remainder > 59) throw Invalid("Invalid instant offset.");
            minutes = (hours * 60 + remainder) * (zone[0] == '-' ? -1 : 1);
        }
        try { return local.AddMinutes(-minutes).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { throw Invalid("Instant is outside the supported Gregorian range."); }
    }

    private static TimeSpan Offset(string zone)
    {
        if (zone == "Z") return TimeSpan.Zero;
        var hours = int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture);
        var minutes = int.Parse(zone.AsSpan(4, 2), CultureInfo.InvariantCulture);
        if (hours > 14 || minutes > 59 || hours == 14 && minutes != 0) throw Invalid("Invalid date-time offset.");
        var offset = new TimeSpan(hours, minutes, 0);
        return zone[0] == '-' ? -offset : offset;
    }

    private static string Duration(string text)
    {
        var match = Regex.Match(text, "\\A(?<sign>-)?P(?:(?<days>[0-9]+)D)?(?:T(?:(?<hours>[0-9]+)H)?(?:(?<minutes>[0-9]+)M)?(?:(?<seconds>[0-9]+)(?:\\.(?<fraction>[0-9]{1,3}))?S)?)?\\z", RegexOptions.CultureInvariant);
        if (!match.Success || !match.Groups["days"].Success && !match.Groups["hours"].Success && !match.Groups["minutes"].Success && !match.Groups["seconds"].Success)
            throw Invalid("Expected a fixed ISO-8601 days/time duration.");
        if (text.Contains('T') && !match.Groups["hours"].Success && !match.Groups["minutes"].Success && !match.Groups["seconds"].Success)
            throw Invalid("Expected a fixed ISO-8601 days/time duration.");
        BigInteger Part(string name) => match.Groups[name].Success ? BigInteger.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : BigInteger.Zero;
        var fraction = match.Groups["fraction"].Success ? match.Groups["fraction"].Value.PadRight(3, '0') : "0";
        var milliseconds = (((Part("days") * 24 + Part("hours")) * 60 + Part("minutes")) * 60 + Part("seconds")) * 1000 + BigInteger.Parse(fraction, CultureInfo.InvariantCulture);
        if (match.Groups["sign"].Success) milliseconds = -milliseconds;
        return FormatDuration(milliseconds);
    }

    private static string FormatDuration(BigInteger milliseconds)
    {
        var sign = milliseconds.Sign < 0 ? "-" : "";
        milliseconds = BigInteger.Abs(milliseconds);
        var days = BigInteger.DivRem(milliseconds, 86_400_000, out milliseconds);
        var hours = BigInteger.DivRem(milliseconds, 3_600_000, out milliseconds);
        var minutes = BigInteger.DivRem(milliseconds, 60_000, out milliseconds);
        var seconds = BigInteger.DivRem(milliseconds, 1000, out var fraction);
        var result = sign + "P" + (days != 0 ? days.ToString(CultureInfo.InvariantCulture) + "D" : "");
        if (hours != 0 || minutes != 0 || seconds != 0 || fraction != 0 || days == 0)
        {
            result += "T";
            if (hours != 0) result += hours.ToString(CultureInfo.InvariantCulture) + "H";
            if (minutes != 0) result += minutes.ToString(CultureInfo.InvariantCulture) + "M";
            if (seconds != 0 || fraction != 0 || hours == 0 && minutes == 0)
            {
                result += seconds.ToString(CultureInfo.InvariantCulture);
                if (fraction != 0) result += "." + ((int)fraction).ToString("D3", CultureInfo.InvariantCulture).TrimEnd('0');
                result += "S";
            }
        }
        return result;
    }

    private static string CalendarPeriod(string text)
    {
        var match = Regex.Match(text, "\\A(?<sign>-)?P(?:(?<years>[0-9]+)Y)?(?:(?<months>[0-9]+)M)?(?:(?<days>[0-9]+)D)?\\z", RegexOptions.CultureInvariant);
        if (!match.Success || !match.Groups["years"].Success && !match.Groups["months"].Success && !match.Groups["days"].Success)
            throw Invalid("Expected an ISO-8601 years/months/days calendar period.");
        BigInteger Part(string name) => match.Groups[name].Success ? BigInteger.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : BigInteger.Zero;
        var years = Part("years"); var months = Part("months"); var days = Part("days");
        var sign = match.Groups["sign"].Success && (years != 0 || months != 0 || days != 0) ? "-" : "";
        var result = sign + "P";
        if (years != 0) result += years.ToString(CultureInfo.InvariantCulture) + "Y";
        if (months != 0) result += months.ToString(CultureInfo.InvariantCulture) + "M";
        if (days != 0 || years == 0 && months == 0) result += days.ToString(CultureInfo.InvariantCulture) + "D";
        return result;
    }

    internal static BosException Invalid(string message) => new("CONVERSION_FAILED", message);
}
