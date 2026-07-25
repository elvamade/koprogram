using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Converters.Responses.Interfaces.Payments;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.StarsTransactions;
using MyTelegram.Schema;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Fetch <a href="https://corefork.telegram.org/api/stars#balance-and-transaction-history">Telegram Stars transactions</a>.The <code>inbound</code> and <code>outbound</code> flags are mutually exclusive: if none of the two are set, both incoming and outgoing transactions are fetched.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SUBSCRIPTION_ID_INVALID The specified subscription_id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsTransactions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsTransactionsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService,
    ILayeredService<IStarsStatusResponseConverter> starsStatusLayeredService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTransactions, MyTelegram.Schema.Payments.IStarsStatus>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTransactions obj)
    {
        if (obj.SubscriptionId != null)
        {
            RpcErrors.RpcErrors400.SubscriptionIdInvalid.ThrowRpcError();
        }

        var userId = input.UserId;
        if (obj.Peer is not TInputPeerSelf)
        {
            var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
            userId = peer.PeerId;
        }

        var collection = StarsTransactionStore.GetCollection(mongoDatabase);
        var filter = Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId);

        if (obj.Inbound && !obj.Outbound)
        {
            filter &= Builders<BsonDocument>.Filter.Gt("Amount", 0);
        }
        else if (obj.Outbound && !obj.Inbound)
        {
            filter &= Builders<BsonDocument>.Filter.Lt("Amount", 0);
        }

        if (!string.IsNullOrEmpty(obj.Offset) && TryParseOffset(obj.Offset, out var offsetDate, out var offsetId))
        {
            var dateFilter = obj.Ascending
                ? Builders<BsonDocument>.Filter.Gt("Date", offsetDate)
                : Builders<BsonDocument>.Filter.Lt("Date", offsetDate);

            var tieFilter = obj.Ascending
                ? Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("Date", offsetDate),
                    Builders<BsonDocument>.Filter.Gt("_id", offsetId))
                : Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("Date", offsetDate),
                    Builders<BsonDocument>.Filter.Lt("_id", offsetId));

            filter &= Builders<BsonDocument>.Filter.Or(dateFilter, tieFilter);
        }

        var limit = obj.Limit <= 0 ? 20 : Math.Min(obj.Limit, 100);
        var sort = obj.Ascending
            ? Builders<BsonDocument>.Sort.Ascending("Date").Ascending("_id")
            : Builders<BsonDocument>.Sort.Descending("Date").Descending("_id");

        var docs = await collection.Find(filter).Sort(sort).Limit(limit + 1).ToListAsync();
        var hasMore = docs.Count > limit;
        if (hasMore)
        {
            docs = docs.Take(limit).ToList();
        }

        string? nextOffset = null;
        if (hasMore && docs.Count > 0)
        {
            var last = docs[^1];
            nextOffset = $"{StarsTransactionStore.GetInt(last, "Date")}:{last["_id"].AsObjectId}";
        }

        var balance = await StarsTransactionQueryHelper.GetBalanceAsync(mongoDatabase, userId);
        var (history, users) = await StarsTransactionQueryHelper.BuildHistoryAsync(mongoDatabase, userConverterService, input, docs, obj.Ton);

        var status = new TStarsStatus
        {
            Balance = obj.Ton ? new TStarsTonAmount { Amount = balance } : new TStarsAmount { Amount = balance },
            History = history,
            NextOffset = nextOffset,
            Chats = [],
            Users = users
        };

        return starsStatusLayeredService.GetConverter(input.Layer).ToLayeredData(status);
    }

    private static bool TryParseOffset(string offset, out int date, out ObjectId objectId)
    {
        date = 0;
        objectId = ObjectId.Empty;
        var parts = offset.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], out date) && ObjectId.TryParse(parts[1], out objectId);
    }
}
