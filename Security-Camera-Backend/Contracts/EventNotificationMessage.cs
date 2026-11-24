namespace Security_Camera_Backend.Contracts;

/// <summary>
/// Non-retained MQTT event summary published to <c>systems/{systemId}/events</c> for every
/// ingested detection. Deliberately small: dashboards only need to know that something arrived so
/// they can refresh the events table, and the terminal only needs the suspect flag.
/// </summary>
public sealed record EventNotificationMessage(
    int EventId,
    DateTimeOffset Timestamp,
    bool IsSuspect,
    bool HasFace);
