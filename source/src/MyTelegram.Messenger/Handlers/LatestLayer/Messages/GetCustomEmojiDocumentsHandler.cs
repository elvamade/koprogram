using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.BotVerifications;
using MongoDocumentReadModel = MyTelegram.ReadModel.MongoDB.DocumentReadModel;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetch <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickers »</a>.Returns a list of <a href="https://corefork.telegram.org/constructor/document">documents</a> with the animated custom emoji in TGS format, and a <a href="https://corefork.telegram.org/constructor/documentAttributeCustomEmoji">documentAttributeCustomEmoji</a> attribute with the original emoji and info about the emoji stickerset this custom emoji belongs to.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getCustomEmojiDocuments"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetCustomEmojiDocumentsHandler(
    IMongoDatabase mongoDatabase,
    ILayeredService<IDocumentConverter> documentLayeredService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetCustomEmojiDocuments, TVector<MyTelegram.Schema.IDocument>>
{
    protected override async Task<TVector<MyTelegram.Schema.IDocument>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetCustomEmojiDocuments obj)
    {
        if (obj.DocumentId == null || obj.DocumentId.Count == 0)
        {
            return [];
        }

        var uniqueIds = obj.DocumentId.Distinct().ToList();
        var collection = mongoDatabase.GetCollection<MongoDocumentReadModel>("eventflow-documentreadmodel");
        var filter = Builders<MongoDocumentReadModel>.Filter.In(p => p.DocumentId, uniqueIds);
        var documentReadModels = await collection.Find(filter).ToListAsync();

        if (documentReadModels.Count == 0)
        {
            return [];
        }

        var textColorMap = await GetVerificationTextColorMapAsync(uniqueIds);
        var result = new TVector<MyTelegram.Schema.IDocument>();
        foreach (var documentReadModel in documentReadModels)
        {
            var document = documentLayeredService.GetConverter(input.Layer).ToDocument(documentReadModel);
            ApplyTextColorOverride(document, textColorMap);
            result.Add(document);
        }

        return result;
    }

    private async Task<Dictionary<long, bool>> GetVerificationTextColorMapAsync(IReadOnlyCollection<long> documentIds)
    {
        var map = new Dictionary<long, bool>();
        if (documentIds.Count == 0)
        {
            return map;
        }

        var idSet = documentIds as HashSet<long> ?? documentIds.ToHashSet();
        var settingsCollection = BotVerificationStore.GetVerifierSettingsCollection(mongoDatabase);
        var filter = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.In("Icon", documentIds),
            Builders<BsonDocument>.Filter.In("StickerId", documentIds)
        );

        var settingsDocs = await settingsCollection.Find(filter).ToListAsync();
        foreach (var settingsDoc in settingsDocs)
        {
            var textColor = BotVerificationStore.GetTextColorFromVerifierSettings(settingsDoc);
            if (textColor == null)
            {
                continue;
            }

            var icon = BotVerificationStore.GetIconFromVerifierSettings(settingsDoc);
            if (icon == 0 || !idSet.Contains(icon))
            {
                continue;
            }

            map[icon] = textColor.Value;
        }

        return map;
    }

    private static void ApplyTextColorOverride(ILayeredDocument document, IReadOnlyDictionary<long, bool> textColorMap)
    {
        if (!textColorMap.TryGetValue(document.Id, out var textColor))
        {
            return;
        }

        foreach (var attribute in document.Attributes)
        {
            if (attribute is TDocumentAttributeCustomEmoji customEmoji)
            {
                customEmoji.TextColor = textColor;
            }
        }
    }
}
