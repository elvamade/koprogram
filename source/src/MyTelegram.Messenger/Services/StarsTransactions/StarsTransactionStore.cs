using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.StarsTransactions;

internal static class StarsTransactionStore
{
    internal const string CollectionName = "eventflow-starstransactionreadmodel";

    internal static IMongoCollection<BsonDocument> GetCollection(IMongoDatabase database)
    {
        return database.GetCollection<BsonDocument>(CollectionName);
    }

    internal static BsonDocument CreateTransactionDocument(
        long ownerUserId,
        long amount,
        int date,
        int peerType,
        long peerId,
        long? giftId = null,
        string? title = null,
        string? description = null,
        bool gift = false,
        bool stargiftUpgrade = false,
        bool stargiftPrepaidUpgrade = false,
        bool stargiftResale = false,
        bool stargiftAuctionBid = false,
        bool offer = false,
        int? paidMessages = null,
        bool refund = false,
        bool pending = false,
        bool failed = false)
    {
        var txId = $"stx_{ownerUserId}_{date}_{Random.Shared.NextInt64():x16}";

        var doc = new BsonDocument
        {
            { "TransactionId", txId },
            { "OwnerUserId", ownerUserId },
            { "Date", date },
            { "Amount", amount },
            { "Nanos", 0 },
            { "PeerType", peerType },
            { "PeerId", peerId },
            { "Gift", gift },
            { "StargiftUpgrade", stargiftUpgrade },
            { "StargiftPrepaidUpgrade", stargiftPrepaidUpgrade },
            { "StargiftResale", stargiftResale },
            { "StargiftAuctionBid", stargiftAuctionBid },
            { "Offer", offer },
            { "Refund", refund },
            { "Pending", pending },
            { "Failed", failed }
        };

        if (giftId.HasValue)
        {
            doc.Add("GiftId", giftId.Value);
        }

        if (!string.IsNullOrEmpty(title))
        {
            doc.Add("Title", title);
        }

        if (!string.IsNullOrEmpty(description))
        {
            doc.Add("Description", description);
        }

        if (paidMessages.HasValue)
        {
            doc.Add("PaidMessages", paidMessages.Value);
        }

        return doc;
    }

    internal static TStarsTransaction ToStarsTransaction(
        BsonDocument doc,
        bool ton,
        IStarGift? stargift)
    {
        var amountValue = GetLong(doc, "Amount");
        var nanosValue = GetInt(doc, "Nanos");
        IStarsAmount amount;
        if (ton)
        {
            amount = new TStarsTonAmount { Amount = amountValue };
        }
        else
        {
            amount = new TStarsAmount { Amount = amountValue, Nanos = nanosValue };
        }

        var tx = new TStarsTransaction
        {
            Id = doc.GetValue("TransactionId", "").AsString,
            Amount = amount,
            Date = GetInt(doc, "Date"),
            Peer = BuildPeer(GetInt(doc, "PeerType"), GetLong(doc, "PeerId")),
            Gift = doc.GetValue("Gift", false).AsBoolean,
            StargiftUpgrade = doc.GetValue("StargiftUpgrade", false).AsBoolean,
            StargiftPrepaidUpgrade = doc.GetValue("StargiftPrepaidUpgrade", false).AsBoolean,
            StargiftResale = doc.GetValue("StargiftResale", false).AsBoolean,
            StargiftAuctionBid = doc.GetValue("StargiftAuctionBid", false).AsBoolean,
            Offer = doc.GetValue("Offer", false).AsBoolean,
            Refund = doc.GetValue("Refund", false).AsBoolean,
            Pending = doc.GetValue("Pending", false).AsBoolean,
            Failed = doc.GetValue("Failed", false).AsBoolean
        };

        if (doc.Contains("Title") && !doc["Title"].IsBsonNull)
        {
            tx.Title = doc["Title"].AsString;
        }

        if (doc.Contains("Description") && !doc["Description"].IsBsonNull)
        {
            tx.Description = doc["Description"].AsString;
        }

        if (stargift != null)
        {
            tx.Stargift = stargift;
        }

        if (doc.Contains("PaidMessages") && !doc["PaidMessages"].IsBsonNull)
        {
            tx.PaidMessages = doc["PaidMessages"].IsInt32 ? doc["PaidMessages"].AsInt32 : (int)doc["PaidMessages"].AsInt64;
        }

        return tx;
    }

    internal static IStarsTransactionPeer BuildPeer(int peerType, long peerId)
    {
        if (peerId <= 0)
        {
            return new TStarsTransactionPeerUnsupported();
        }

        return peerType switch
        {
            (int)PeerType.User => new TStarsTransactionPeer { Peer = new TPeerUser { UserId = peerId } },
            (int)PeerType.Chat => new TStarsTransactionPeer { Peer = new TPeerChat { ChatId = peerId } },
            (int)PeerType.Channel => new TStarsTransactionPeer { Peer = new TPeerChannel { ChannelId = peerId } },
            _ => new TStarsTransactionPeerUnsupported()
        };
    }

    internal static long GetLong(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt64 ? doc[field].AsInt64 : doc[field].AsInt32;
    }

    internal static int GetInt(BsonDocument doc, string field)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull) return 0;
        return doc[field].IsInt32 ? doc[field].AsInt32 : (int)doc[field].AsInt64;
    }
}
