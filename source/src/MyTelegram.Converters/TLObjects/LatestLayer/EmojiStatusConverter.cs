namespace MyTelegram.Converters.TLObjects.LatestLayer;

public class EmojiStatusConverter : IEmojiStatusConverter, ITransientDependency
{
    public IEmojiStatus? ToEmojiStatus(EmojiStatus? emojiStatus)
    {
        if (emojiStatus == null)
        {
            return null;
        }

        if (emojiStatus.IsCollectible)
        {
            return new TEmojiStatusCollectible
            {
                CollectibleId = emojiStatus.CollectibleId!.Value,
                DocumentId = emojiStatus.DocumentId,
                Title = emojiStatus.CollectibleTitle ?? string.Empty,
                Slug = emojiStatus.CollectibleSlug ?? string.Empty,
                PatternDocumentId = emojiStatus.CollectiblePatternDocumentId ?? 0,
                CenterColor = emojiStatus.CollectibleCenterColor ?? 0,
                EdgeColor = emojiStatus.CollectibleEdgeColor ?? 0,
                PatternColor = emojiStatus.CollectiblePatternColor ?? 0,
                TextColor = emojiStatus.CollectibleTextColor ?? 0,
                Until = emojiStatus.Until
            };
        }

        return new TEmojiStatus
        {
            DocumentId = emojiStatus.DocumentId,
            Until = emojiStatus.Until
        };
    }

    public int Layer => Layers.LayerLatest;
}
