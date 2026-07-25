using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Converters.TLObjects.Payments;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.StarsTransactions;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Get the current <a href="https://corefork.telegram.org/api/stars">Telegram Stars balance</a> of the current account (with peer=<a href="https://corefork.telegram.org/constructor/inputPeerSelf">inputPeerSelf</a>), or the stars balance of the bot specified in <code>peer</code>.
/// Possible errors
/// Code Type Description
/// 403 BOT_ACCESS_FORBIDDEN The specified method <em>can</em> be used over a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business connection</a> for some operations, but the specified query attempted an operation that is not allowed over a business connection.
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsStatusHandler(
    ILayeredService<IStarsStatusConverter> starsStatusLayeredService,
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsStatus, MyTelegram.Schema.Payments.IStarsStatus>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsStatus obj)
    {
        // Determine which user's balance to fetch
        long userId = input.UserId;
        if (obj.Peer is not TInputPeerSelf)
        {
            var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
            userId = peer.PeerId;
        }

        // Fetch balance from MongoDB
        var balanceCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userstarsbalancereadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var balanceDoc = await balanceCollection.Find(filter).FirstOrDefaultAsync();

        long balance = 0;
        if (balanceDoc != null && balanceDoc.Contains("Balance"))
        {
            balance = balanceDoc["Balance"].IsInt64 ? balanceDoc["Balance"].AsInt64 : balanceDoc["Balance"].AsInt32;
        }

        var collection = StarsTransactionStore.GetCollection(mongoDatabase);
        var txFilter = Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId);
        var docs = await collection.Find(txFilter)
            .Sort(Builders<BsonDocument>.Sort.Descending("Date").Descending("_id"))
            .Limit(5)
            .ToListAsync();

        var (history, users) = await StarsTransactionQueryHelper.BuildHistoryAsync(
            mongoDatabase,
            userConverterService,
            input,
            docs,
            obj.Ton);

        var status = starsStatusLayeredService.GetConverter(input.Layer).ToStarsStatus(obj.Ton, balance);
        if (status is TStarsStatus starsStatus)
        {
            starsStatus.History = history;
            starsStatus.Users = users;
            starsStatus.Chats = [];
        }

        return status;
    }
}
