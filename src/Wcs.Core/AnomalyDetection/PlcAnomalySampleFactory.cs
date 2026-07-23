namespace Wcs.Core.AnomalyDetection;

using System.Globalization;
using Wcs.Core.EventBus.Events;

public static class PlcAnomalySampleFactory
{
    public static PlcAnomalySample FromRawSignal(RawSignalEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var timestampUtc = evt.OccurTime.Kind == DateTimeKind.Utc
            ? evt.OccurTime
            : evt.OccurTime.ToUniversalTime();

        bool? booleanValue = null;
        double? numericValue = null;
        if (bool.TryParse(evt.NewValue, out var parsedBoolean))
            booleanValue = parsedBoolean;
        else if (double.TryParse(
                     evt.NewValue,
                     NumberStyles.Float | NumberStyles.AllowThousands,
                     CultureInfo.InvariantCulture,
                     out var parsedNumeric))
            numericValue = parsedNumeric;

        return new PlcAnomalySample
        {
            EventId = evt.EventId,
            TimestampUtc = timestampUtc,
            PlcName = evt.PlcName,
            DbBlock = evt.DbBlock,
            DeviceId = ExtractDeviceId(evt.FieldName),
            SignalName = evt.FieldName,
            OldValue = evt.OldValue,
            NewValue = evt.NewValue,
            NumericValue = numericValue,
            BooleanValue = booleanValue,
            Source = evt.Source
        };
    }

    public static string ExtractDeviceId(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return "UNKNOWN";
        var underscore = fieldName.IndexOf('_');
        var dot = fieldName.IndexOf('.');
        var separator = underscore < 0
            ? dot
            : dot < 0
                ? underscore
                : Math.Min(underscore, dot);
        return separator <= 0 ? fieldName : fieldName[..separator];
    }
}
