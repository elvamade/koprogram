using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.StarsTransactions;
using MyTelegram.Schema;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain info about <a href="https://corefork.telegram.org/api/stars#balance-and-transaction-history">Telegram Star transactions »</a> using specific transaction IDs.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 TRANSACTION_ID_INVALID The specified transaction ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsTransactionsByID"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsTransactionsByIDHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTransactionsByID, MyTelegram.Schema.Payments.IStarsStatus>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTransactionsByID obj)
    {
        var userId = input.UserId;
        if (obj.Peer is not TInputPeerSelf)
        {
            var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
            userId = peer.PeerId;
        }

        var ids = obj.Id
            .OfType<TInputStarsTransaction>()
            .Select(x => x.Id)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            RpcErrors.RpcErrors400.TransactionIdInvalid.ThrowRpcError();
        }

        var collection = StarsTransactionStore.GetCollection(mongoDatabase);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerUserId", userId),
            Builders<BsonDocument>.Filter.In("TransactionId", ids)
        );

        var docs = await collection.Find(filter).ToListAsync();
        if (docs.Count == 0)
        {
            RpcErrors.RpcErrors400.TransactionIdInvalid.ThrowRpcError();
        }

        var balance = await StarsTransactionQueryHelper.GetBalanceAsync(mongoDatabase, userId);
        var (history, users) = await StarsTransactionQueryHelper.BuildHistoryAsync(mongoDatabase, userConverterService, input, docs, obj.Ton);

        return new TStarsStatus
        {
            Balance = obj.Ton ? new TStarsTonAmount { Amount = balance } : new TStarsAmount { Amount = balance },
            History = history,
            Chats = [],
            Users = users
        };
    }
}
