using System.Globalization;

namespace Mulse.Modules.BuiltIn.Augment;

public sealed record DurableBatchSchedule(TimeOnly Cutoff, TimeZoneInfo TimeZone)
{
    public string Key => $"Daily|{Cutoff.ToString("HH:mm", CultureInfo.InvariantCulture)}|{TimeZone.Id}";

    public static DurableBatchSchedule? Read(ModuleStepDefinition step)
    {
        var mode = ModuleSettingReader.GetOptional(step.Settings, "releaseMode") ?? "CountOnly";
        if (mode == "CountOnly") return null;
        if (mode != "CountOrDailyCutoff") throw new ArgumentException("Durable batch releaseMode must be CountOnly or CountOrDailyCutoff.");
        var cutoff = ModuleSettingReader.GetRequired(step.Settings, "dailyCutoff", DurableBatchAugmentModule.ModuleId);
        var zoneId = ModuleSettingReader.GetRequired(step.Settings, "timeZoneId", DurableBatchAugmentModule.ModuleId);
        if (!TimeOnly.TryParseExact(cutoff, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || zoneId.Length > 256)
            throw new ArgumentException("Daily batch cutoffs require HH:mm (24-hour clock) and an explicit timeZoneId of at most 256 characters.");
        try { return new(time, TimeZoneInfo.FindSystemTimeZoneById(zoneId)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException($"Batch timezone '{zoneId}' is unavailable or invalid on this host. Configure an explicit supported timezone.", ex); }
    }

    public DateTimeOffset NextDeadline(DateTimeOffset now)
    {
        var date = TimeZoneInfo.ConvertTime(now, TimeZone).Date;
        for (var day = 0; day < 3; day++)
        {
            var local = DateTime.SpecifyKind(date.AddDays(day).Add(Cutoff.ToTimeSpan()), DateTimeKind.Unspecified);
            var shiftedMinutes = 0;
            while (TimeZone.IsInvalidTime(local))
            {
                if (++shiftedMinutes > 1440) throw new InvalidOperationException("Batch cutoff could not resolve a valid local time within one day.");
                local = local.AddMinutes(1);
            }
            // A repeated clock time has one cutoff: its later occurrence. A skipped time advances to the first valid minute.
            var offset = TimeZone.IsAmbiguousTime(local) ? TimeZone.GetAmbiguousTimeOffsets(local).Min() : TimeZone.GetUtcOffset(local);
            var deadline = new DateTimeOffset(local, offset).ToUniversalTime();
            if (deadline > now) return deadline;
        }
        throw new InvalidOperationException("Batch cutoff could not resolve a future deadline.");
    }
}
