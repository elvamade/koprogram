namespace MyTelegram;

public record EmojiStatus(
    long DocumentId,
    int? Until = null,
    long? CollectibleId = null,
    string? CollectibleTitle = null,
    string? CollectibleSlug = null,
    long? CollectiblePatternDocumentId = null,
    int? CollectibleCenterColor = null,
    int? CollectibleEdgeColor = null,
    int? CollectiblePatternColor = null,
    int? CollectibleTextColor = null
)
{
    public bool IsCollectible => CollectibleId.HasValue;
}
