using System.Globalization;
using System.Text.RegularExpressions;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Sdk;

namespace Mulse.Billing;

public static class BillingFilename
{
    private static readonly Regex Token = new(@"\[(?:(?<date>DATE|DATETIME|UTCDATETIME|UTCTIME|TIME|DAY)\((?<format>[A-Za-z0-9_.,-]*)\)|(?<simple>GUID|TICKS|SECONDS))\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Unexpanded = new(@"\[(?:DATE|DATETIME|UTCDATETIME|UTCTIME|TIME|DAY|GUID|TICKS|SECONDS)(?:\b|\()", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    internal static readonly IReadOnlyList<ModuleSettingDescriptor> Settings =
    [
        new("timeZone", "Filename time zone", "Explicit time zone for local macros; UTC macros always use UTC. Configure the original server zone when migrating.", true, ModuleSettingInputKind.Text, "UTC"),
        new("culture", "Filename culture", "Invariant or an explicit .NET culture name for date formats. Configure the original server culture when migrating.", true, ModuleSettingInputKind.Text, "Invariant")
    ];

    public static string Expand(string template, DateTimeOffset instant, TimeZoneInfo timeZone, CultureInfo culture, Guid guid)
    {
        if (template is null || template.Length > 4096) throw new ArgumentException("Billing filename templates must not exceed 4096 characters.");
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(culture);
        var local = TimeZoneInfo.ConvertTime(instant, timeZone).DateTime;
        var utc = instant.UtcDateTime;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var length = template.Length;
        var result = Token.Replace(template, match =>
        {
            if (!replacements.TryGetValue(match.Value, out var value))
            {
                var format = match.Groups["format"].Value;
                var macro = match.Groups["date"].Success ? match.Groups["date"].Value : match.Groups["simple"].Value;
                value = macro switch
                {
                    "DATE" => local.Date.ToString(format, culture),
                    "DATETIME" or "TIME" => local.ToString(format, culture),
                    "UTCDATETIME" or "UTCTIME" => utc.ToString(format, culture),
                    "DAY" => Day(local.DayOfWeek, format),
                    "GUID" => guid.ToString("D"),
                    "TICKS" => local.Ticks.ToString(culture),
                    "SECONDS" => (local - new DateTime(2001, 1, 1)).TotalSeconds.ToString("0", culture),
                    _ => throw new ArgumentException("Unsupported Billing filename macro.")
                };
                replacements.Add(match.Value, value);
            }
            length = checked(length + value.Length - match.Length);
            if (length > 4096) throw new ArgumentException("Expanded Billing filename exceeds 4096 characters.");
            return value;
        });
        if (Unexpanded.IsMatch(result)) throw new ArgumentException("Billing filename contains an invalid or unsupported macro format; no unresolved token was accepted.");
        return result;
    }

    internal static string Expand(string template, FlowExecutionContext context, IReadOnlyDictionary<string, string> settings, string module)
    {
        var environment = ReadEnvironment(settings, module);
        return Expand(template, context.StartedAt, environment.Zone, environment.Culture, Guid.NewGuid());
    }

    internal static (TimeZoneInfo Zone, CultureInfo Culture) ReadEnvironment(IReadOnlyDictionary<string, string> settings, string module)
    {
        var zone = ModuleSettingReader.GetRequired(settings, "timeZone", module);
        var name = ModuleSettingReader.GetRequired(settings, "culture", module);
        return (TimeZoneInfo.FindSystemTimeZoneById(zone), name == "Invariant" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name));
    }

    private static string Day(DayOfWeek day, string format)
    {
        if (!int.TryParse(format, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new ArgumentException("DAY requires a nonnegative integer length; 0 or greater than 6 means the full English day name.");
        var name = day.ToString().ToUpperInvariant();
        return count == 0 || count > 6 ? name : name[..Math.Min(count, name.Length)];
    }
}
