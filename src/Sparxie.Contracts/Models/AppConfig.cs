using System.Text.Json.Serialization;

namespace Sparxie.Contracts.Models;

/// <summary>config.json 根契约，schemaVersion 固定为 1。</summary>
public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? SelectedProfileId { get; set; }

    public List<GameProfile> Profiles { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Profiles.Count == 0;
}
