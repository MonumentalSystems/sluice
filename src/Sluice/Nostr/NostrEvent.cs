using System.Text.Json.Serialization;

namespace Sluice.Nostr;

/// <summary>A NIP-01 nostr event (the exact wire field names — used for both signing and JSON I/O).</summary>
public sealed class NostrEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("pubkey")] public string Pubkey { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("tags")] public List<string[]> Tags { get; set; } = new();
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("sig")] public string Sig { get; set; } = string.Empty;
}
