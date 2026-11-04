using System.Globalization;

namespace Mulse.Modules.BuiltIn.Augment;

public sealed record DurableBatchSchedule(TimeOnly Cutoff, TimeZoneInfo TimeZone)
{
    public string? BatchTimeMetadataKey { get; init; }
    public string? BatchIntervalMetadataKey { get; init; }
    public string Key => BatchTimeMetadataKey is null ? $"Daily|{Cutoff.ToString("HH:mm", CultureInfo.InvariantCulture)}|{TimeZone.Id}"
        : $"Metadata|{BatchTimeMetadataKey.Length}:{BatchTimeMetadataKey}|{BatchIntervalMetadataKey!.Length}:{BatchIntervalMetadataKey}|{TimeZone.Id}";

    public static DurableBatchSchedule? Read(ModuleStepDefinition step)
    {
        var mode = ModuleSettingReader.GetOptional(step.Settings, "releaseMode") ?? "CountOnly";
        if (mode == "CountOnly") return null;
        if (mode is not ("CountOrDailyCutoff" or "CountOrMetadataDeadline"))
            throw new ArgumentException("Durable batch releaseMode must be CountOnly, CountOrDailyCutoff or CountOrMetadataDeadline.");
        var zoneId = ModuleSettingReader.GetRequired(step.Settings, "timeZoneId", DurableBatchAugmentModule.ModuleId);
        if (zoneId.Length > 256) throw new ArgumentException("Timed batches require an explicit timeZoneId of at most 256 characters.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException($"Batch timezone '{zoneId}' is unavailable or invalid on this host. Configure an explicit supported timezone.", ex); }
        if (mode == "CountOrMetadataDeadline")
        {
            var timeKey = ModuleSettingReader.GetRequired(step.Settings, "batchTimeMetadataKey", DurableBatchAugmentModule.ModuleId);
            var intervalKey = ModuleSettingReader.GetRequired(step.Settings, "batchIntervalMetadataKey", DurableBatchAugmentModule.ModuleId);
            if (timeKey.Length > 256 || intervalKey.Length > 256 || timeKey.Equals(intervalKey, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("BatchTime and BatchInterval require distinct metadata keys of at most 256 characters.");
            return new(default, zone) { BatchTimeMetadataKey = timeKey, BatchIntervalMetadataKey = intervalKey };
        }
        var cutoff = ReadTime(ModuleSettingReader.GetRequired(step.Settings, "dailyCutoff", DurableBatchAugmentModule.ModuleId));
        return new(cutoff, zone);
    }

    public void ValidateSavedTiming(FlowBatchCheckpointState state)
    {
        if (state.DeadlineUtc is null) throw new InvalidOperationException("Timed batch checkpoint is missing its persisted deadline; no data was changed.");
        if (BatchTimeMetadataKey is null) return;
        _ = ReadTime(state.OriginalBatchTime ?? throw new InvalidOperationException("Metadata-timed batch is missing its original BatchTime."));
        _ = ReadInterval(state.PolicyBatchInterval ?? throw new InvalidOperationException("Metadata-timed batch is missing its pinned BatchInterval."));
    }

    public (DateTimeOffset? DeadlineUtc, string? OriginalBatchTime, string? PolicyBatchInterval) ResolveDeadline(
        DateTimeOffset now, FlowBatchCheckpointState? existing, IReadOnlyList<IntegrationPayload> incoming, bool replay)
    {
        if (existing is not null) ValidateSavedTiming(existing);
        if (BatchTimeMetadataKey is null) return (existing?.DeadlineUtc ?? NextDeadline(now), null, null);
        if (replay)
        {
            if (existing is null) throw new InvalidOperationException("A batch replay requires persisted timing state.");
            return (existing.DeadlineUtc, existing.OriginalBatchTime, existing.PolicyBatchInterval);
        }
        if (incoming.Count == 0) throw new ArgumentException("Metadata timing requires at least one incoming payload.");
        var originalTime = existing?.OriginalBatchTime ?? RequiredMetadata(incoming[0], BatchTimeMetadataKey);
        var cutoff = ReadTime(originalTime);
        var intervalText = existing?.PolicyBatchInterval ?? RequiredMetadata(incoming[0], BatchIntervalMetadataKey!);
        var interval = ReadInterval(intervalText);
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZone).DateTime;
        var deadline = existing?.DeadlineUtc ?? (interval is { } duration ? ResolveLocal(localNow.Add(duration)) : NextDaily(now, cutoff));
        if (interval is null)
        {
            foreach (var payload in incoming)
            {
                var changedTime = RequiredMetadata(payload, BatchTimeMetadataKey);
                var changedCutoff = ReadTime(changedTime);
                // Match the source's comparison against the FIRST message, using the existing deadline's local date.
                if (changedTime != originalTime)
                    deadline = ResolveLocal(TimeZoneInfo.ConvertTime(deadline, TimeZone).Date.Add(changedCutoff.ToTimeSpan()));
            }
        }
        return (deadline, originalTime, intervalText);
    }

    public DateTimeOffset NextDeadline(DateTimeOffset now)
    {
        if (BatchTimeMetadataKey is not null) throw new InvalidOperationException("Metadata-driven timing requires incoming message facts and persisted batch state.");
        return NextDaily(now, Cutoff);
    }

    private DateTimeOffset NextDaily(DateTimeOffset now, TimeOnly cutoff)
    {
        var date = TimeZoneInfo.ConvertTime(now, TimeZone).Date;
        for (var day = 0; day < 3; day++)
        {
            var deadline = ResolveLocal(date.AddDays(day).Add(cutoff.ToTimeSpan()));
            if (deadline > now) return deadline;
        }
        throw new InvalidOperationException("Batch cutoff could not resolve a future deadline.");
    }

    private DateTimeOffset ResolveLocal(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(local)) local = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerMinute));
        var shiftedMinutes = 0;
        while (TimeZone.IsInvalidTime(local))
        {
            if (++shiftedMinutes > 1440) throw new InvalidOperationException("Batch cutoff could not resolve a valid local time within one day.");
            local = local.AddMinutes(1);
        }
        // A repeated clock time has one cutoff: its later occurrence. A skipped time advances to the first valid minute.
        var offset = TimeZone.IsAmbiguousTime(local) ? TimeZone.GetAmbiguousTimeOffsets(local).Min() : TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static TimeOnly ReadTime(string text)
        => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time
            : throw new ArgumentException("BatchTime/daily cutoffs require HH:mm (24-hour clock), without culture-dependent dates or seconds.");

    private static TimeSpan? ReadInterval(string text)
    {
        if (text == "NONE") return null;
        var parts = text.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            throw new ArgumentException("BatchInterval must be NONE or a positive integer followed by :D, :H or :M (maximum 366 days).");
        var minutes = parts[1] switch { "D" => (long)amount * 1440, "H" => (long)amount * 60, "M" => amount, _ => -1 };
        if (minutes is <= 0 or > 527040) throw new ArgumentException("BatchInterval requires units D, H or M and a duration no longer than 366 days.");
        return TimeSpan.FromMinutes(minutes);
    }

    private static string RequiredMetadata(IntegrationPayload payload, string key)
        => payload.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value
            : throw new ArgumentException($"Metadata-timed batching requires nonblank '{key}' on the message; no checkpoint was changed.");
}
