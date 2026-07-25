using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Set an <a href="https://corefork.telegram.org/api/emoji-status">emoji status</a>
/// Possible errors
/// Code Type Description
/// 400 COLLECTIBLE_INVALID The specified collectible is invalid.
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateEmojiStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateEmojiStatusHandler(ICommandBus commandBus, IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateEmojiStatus, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateEmojiStatus obj)
    {
        var emojiStatus = await ParseEmojiStatusAsync(input.UserId, obj.EmojiStatus);
        var command = new UpdateEmojiStatusCommand(UserId.Create(input.UserId), input.ToRequestInfo(), emojiStatus);
        await commandBus.PublishAsync(command, CancellationToken.None);

        return null!;
    }

    private async Task<EmojiStatus?> ParseEmojiStatusAsync(long ownerUserId, IEmojiStatus emojiStatus)
    {
        switch (emojiStatus)
        {
            case TEmojiStatusEmpty:
                return null;

            case TEmojiStatus status:
                if (status.DocumentId <= 0)
                {
                    RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                }

                return new EmojiStatus(status.DocumentId, status.Until);

            case TInputEmojiStatusCollectible collectible:
                return await ParseCollectibleEmojiStatusAsync(ownerUserId, collectible);

            // Must not be passed to account.updateEmojiStatus.
            case TEmojiStatusCollectible:
                RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
                return null;

            default:
                RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                return null;
        }
    }

    private async Task<EmojiStatus> ParseCollectibleEmojiStatusAsync(long ownerUserId, TInputEmojiStatusCollectible collectible)
    {
        if (collectible.CollectibleId <= 0)
        {
            RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
        }

        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        var modelsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_models");
        var patternsCollection = mongoDatabase.GetCollection<BsonDocument>("stargift_upgrade_patterns");

        var savedGiftFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", ownerUserId),
            Builders<BsonDocument>.Filter.Eq("SavedId", collectible.CollectibleId),
            Builders<BsonDocument>.Filter.Eq("Upgraded", true),
            Builders<BsonDocument>.Filter.Ne("Converted", true),
            Builders<BsonDocument>.Filter.Ne("Refunded", true)
        );
        var savedGiftDoc = await savedGiftsCollection.Find(savedGiftFilter).FirstOrDefaultAsync();
        if (savedGiftDoc == null)
        {
            RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
        }

        var giftId = GetLong(savedGiftDoc!, "GiftId");
        if (giftId <= 0)
        {
            RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
        }

        var giftDoc = await giftsCollection.Find(Builders<BsonDocument>.Filter.Eq("GiftId", giftId)).FirstOrDefaultAsync();
        var title = GetNullableString(giftDoc, "Title") ?? "Collectible Gift";
        var slug = GetNullableString(savedGiftDoc, "Slug");
        if (string.IsNullOrWhiteSpace(slug))
        {
            RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
        }

        var modelName = GetNullableString(savedGiftDoc, "ModelName");
        long documentId = 0;
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var modelDoc = await modelsCollection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("Name", modelName)
                )).FirstOrDefaultAsync();
            documentId = GetLong(modelDoc, "DocumentId");
        }

        if (documentId <= 0)
        {
            documentId = GetLong(giftDoc, "StickerId");
        }

        if (documentId <= 0)
        {
            RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
        }

        var patternName = GetNullableString(savedGiftDoc, "PatternName");
        long patternDocumentId = 0;
        if (!string.IsNullOrWhiteSpace(patternName))
        {
            var patternDoc = await patternsCollection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
                    Builders<BsonDocument>.Filter.Eq("Name", patternName)
                )).FirstOrDefaultAsync();
            patternDocumentId = GetLong(patternDoc, "DocumentId");
        }

        return new EmojiStatus(
            documentId,
            collectible.Until,
            collectible.CollectibleId,
            title,
            slug,
            patternDocumentId,
            GetNullableInt(savedGiftDoc, "BackdropCenterColor"),
            GetNullableInt(savedGiftDoc, "BackdropEdgeColor"),
            GetNullableInt(savedGiftDoc, "BackdropPatternColor"),
            GetNullableInt(savedGiftDoc, "BackdropTextColor")
        );
    }

    private static long GetLong(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull)
        {
            return 0;
        }

        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    private static int? GetNullableInt(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static string? GetNullableString(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull)
        {
            return null;
        }

        return doc[field].AsString;
    }
}
