using System.Text.Json.Serialization;

namespace Leaves;

// Mirrors config/config.jsonc.
public record LeavesUtilsConfig
{
    [JsonPropertyName("dataDump")]
    public required bool DataDump { get; set; }

    // Suite-wide web UI filter: hide profiles whose nickname starts with one of these prefixes (case-insensitive) from every mod's web UI picker.
    // Not required, so an older config without these keys still loads with the defaults.
    [JsonPropertyName("WebUI_FilterProfiles")]
    public bool WebUiFilterProfiles { get; set; } = true;

    [JsonPropertyName("WebUI_HiddenProfilePrefixes")]
    public List<string> WebUiHiddenProfilePrefixes { get; set; } = new() { "headless", "zz" };
}
