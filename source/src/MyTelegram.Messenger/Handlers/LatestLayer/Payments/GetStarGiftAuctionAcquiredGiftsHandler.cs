using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Get gifts acquired from a star gift auction.
/// <para><c>See <a href="" /> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ] [Bot ] [Anonymous ]
/// </remarks>
internal sealed class GetStarGiftAuctionAcquiredGiftsHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarGiftAuctionAcquiredGifts, MyTelegram.Schema.Payments.IStarGiftAuctionAcquiredGifts>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Payments.IStarGiftAuctionAcquiredGifts> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Payments.RequestGetStarGiftAuctionAcquiredGifts obj)
    {
        var giftId = obj.GiftId;

        // Get acquired gifts from database
        var acquiredCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftauctionacquiredreadmodel");
        var acquiredDocs = await acquiredCollection.Find(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId)
        ).SortByDescending(d => d["Date"]).ToListAsync();

        if (acquiredDocs.Count == 0)
        {
            acquiredDocs = await BuildFallbackAcquiredDocsAsync(giftId);
        }

        var gifts = new TVector<MyTelegram.Schema.IStarGiftAuctionAcquiredGift>();
        var userIds = new HashSet<long>();

        foreach (var doc in acquiredDocs)
        {
            var peerId = GetLong(doc, "PeerId");
            var peerType = GetNullableString(doc, "PeerType") ?? "user";

            MyTelegram.Schema.IPeer peer = peerType switch
            {
                "channel" => new TPeerChannel { ChannelId = peerId },
                "chat" => new TPeerChat { ChatId = peerId },
                _ => new TPeerUser { UserId = peerId }
            };

            if (peerType == "user")
            {
                userIds.Add(peerId);
            }

            var acquiredGift = new TStarGiftAuctionAcquiredGift
            {
                NameHidden = doc.GetValue("NameHidden", false).AsBoolean,
                Peer = peer,
                Date = GetNullableInt(doc, "Date") ?? 0,
                BidAmount = GetLong(doc, "BidAmount"),
                Round = GetNullableInt(doc, "Round") ?? 1,
                Pos = GetNullableInt(doc, "Pos") ?? 1,
                Message = GetTextWithEntities(doc),
                GiftNum = GetNullableInt(doc, "GiftNum")
            };

            gifts.Add(acquiredGift);
        }

        // Get users via converter service (handles privacy properly)
        var users = new TVector<MyTelegram.Schema.IUser>();
        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), true, true, input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        return new TStarGiftAuctionAcquiredGifts
        {
            Gifts = gifts,
            Users = users,
            Chats = []
        };
    }

    private static MyTelegram.Schema.ITextWithEntities? GetTextWithEntities(BsonDocument doc)
    {
        var messageText = GetNullableString(doc, "MessageText") ?? GetNullableString(doc, "Message");
        if (string.IsNullOrEmpty(messageText))
        {
            return null;
        }

        return new TTextWithEntities
        {
            Text = messageText,
            Entities = []
        };
    }

    private async Task<List<BsonDocument>> BuildFallbackAcquiredDocsAsync(long giftId)
    {
        var bidsCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stargiftauctionbidreadmodel");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("GiftId", giftId),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Eq("Won", true),
                Builders<BsonDocument>.Filter.Gt("AcquiredCount", 0)
            )
        );

        var bidDocs = await bidsCollection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("BidAmount").Descending("BidDate"))
            .ToListAsync();

        var fallbackDocs = new List<BsonDocument>();
        var pos = 1;
        foreach (var bid in bidDocs)
        {
            var doc = new BsonDocument
            {
                { "PeerId", GetLong(bid, "UserId") },
                { "PeerType", "user" },
                { "NameHidden", bid.GetValue("HideName", false).AsBoolean },
                { "Date", GetNullableInt(bid, "BidDate") ?? 0 },
                { "BidAmount", GetLong(bid, "BidAmount") },
                { "Round", GetNullableInt(bid, "Round") ?? 1 },
                { "Pos", pos },
                { "GiftNum", bid.Contains("GiftNum") && !bid["GiftNum"].IsBsonNull ? bid["GiftNum"] : BsonNull.Value },
                { "Message", GetNullableString(bid, "Message") ?? (BsonValue)BsonNull.Value }
            };
            fallbackDocs.Add(doc);
            pos++;
        }

        return fallbackDocs;
    }

    private static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        var value = doc[field];
        return value.IsInt64 ? value.AsInt64 : value.AsInt32;
    }

    private static int? GetNullableInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }

    private static string? GetNullableString(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return null;
        return doc[field].AsString;
    }
}

