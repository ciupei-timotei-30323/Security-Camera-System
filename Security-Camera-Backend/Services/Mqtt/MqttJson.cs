using System.Text.Json;
using System.Text.Json.Serialization;

namespace Security_Camera_Backend.Services.Mqtt;

/// <summary>
/// Serializer settings for MQTT payloads. The ESP32 firmware and the dashboard both parse these
/// documents, so the contract is camelCase and must stay byte-compatible with the HTTP responses
/// produced by ASP.NET Core's default JSON options.
/// </summary>
public static class MqttJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
