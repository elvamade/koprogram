using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;

/// <summary>
/// Get info about an unsupported deep link, see <a href="https://corefork.telegram.org/api/links#unsupported-links">here for more info »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getDeepLinkInfo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class GetDeepLinkInfoHandler(IMongoDatabase mongoDatabase) 
    : RpcResultObjectHandler<MyTelegram.Schema.Help.RequestGetDeepLinkInfo, MyTelegram.Schema.Help.IDeepLinkInfo>
{
    protected override async Task<MyTelegram.Schema.Help.IDeepLinkInfo> HandleCoreAsync(
        IRequestInput input, 
        MyTelegram.Schema.Help.RequestGetDeepLinkInfo obj)
    {
        // Parse the path to determine link type
        // Examples: "nft/abc123", "giftcode/xyz", "premium_offer"
        var path = obj.Path?.Trim('/') ?? "";
        
        // Handle collectible gift links: tg://nft?slug=<slug> -> path = "nft"
        if (path.StartsWith("nft", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleNftLinkAsync(path);
        }
        
        // Handle gift code links: tg://giftcode?slug=<slug>
        if (path.StartsWith("giftcode", StringComparison.OrdinalIgnoreCase))
        {
            return new TDeepLinkInfo
            {
                Message = "🎁 This is a Telegram Premium gift code. Open it in the app to redeem your gift!"
            };
        }
        
        // Handle premium offer links
        if (path.StartsWith("premium_offer", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("premium_multigift", StringComparison.OrdinalIgnoreCase))
        {
            return new TDeepLinkInfo
            {
                Message = "⭐ Telegram Premium\n\nGet access to exclusive features, faster downloads, and more!"
            };
        }
        
        // Handle stars links
        if (path.StartsWith("stars", StringComparison.OrdinalIgnoreCase))
        {
            return new TDeepLinkInfo
            {
                Message = "⭐ Telegram Stars\n\nUse Stars to unlock digital goods and services."
            };
        }
        
        // Handle invoice links
        if (path.StartsWith("invoice", StringComparison.OrdinalIgnoreCase))
        {
            return new TDeepLinkInfo
            {
                Message = "💳 Payment Invoice\n\nThis link contains a payment invoice."
            };
        }
        
        // For unknown/unsupported links, return empty or a generic message
        return new TDeepLinkInfoEmpty();
    }
    
    private async Task<IDeepLinkInfo> HandleNftLinkAsync(string path)
    {
        // Try to extract slug from path if present (e.g., "nft/abc123")
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return new TDeepLinkInfo
            {
                Message = "🎁 Collectible Gift\n\nThis is a unique collectible gift on Telegram."
            };
        }
        
        var slug = parts[1];
        
        // Try to find the gift in database
        var savedGiftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-savedstargiftreadmodel");
        var giftsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftreadmodel");
        
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Slug", slug),
            Builders<BsonDocument>.Filter.Eq("Upgraded", true)
        );
        var savedGiftDoc = await savedGiftsCollection.Find(filter).FirstOrDefaultAsync();
        
        if (savedGiftDoc == null)
        {
            return new TDeepLinkInfo
            {
                Message = "🎁 Collectible Gift\n\nThis collectible gift was not found or is no longer available."
            };
        }
        
        var giftId = GetLong(savedGiftDoc, "GiftId");
        var giftDoc = await giftsCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).FirstOrDefaultAsync();
        
        var title = GetNullableString(giftDoc, "Title") ?? "Collectible Gift";
        var giftNum = GetNullableInt(savedGiftDoc, "GiftNum") ?? 1;
        var availabilityTotal = GetNullableInt(giftDoc, "AvailabilityTotal") ?? 0;
        
        var message = $"🎁 {title} #{giftNum}";
        if (availabilityTotal > 0)
        {
            message += $" of {availabilityTotal}";
        }
        message += "\n\nThis is a unique collectible gift on Telegram. Open the link to view details.";
        
        return new TDeepLinkInfo
        {
            Message = message
        };
    }
    
    private static long GetLong(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }
    
    private static int? GetNullableInt(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }
    
    private static string? GetNullableString(BsonDocument? doc, string field)
    {
        if (doc == null || !doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].AsString;
    }
}