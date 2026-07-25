using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Allow a user to send us messages without paying if <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a> are enabled.
/// Possible errors
/// Code Type Description
/// 400 PARENT_PEER_INVALID The specified <code>parent_peer</code> is invalid.
/// 400 UNSUPPORTED <code>require_payment</code> cannot be <em>set</em> by users, only by monoforums: users must instead use the <a href="https://corefork.telegram.org/constructor/inputPrivacyKeyNoPaidMessages">inputPrivacyKeyNoPaidMessages</a> privacy setting to remove a previously added exemption.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.toggleNoPaidMessagesException"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleNoPaidMessagesExceptionHandler(
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestToggleNoPaidMessagesException, IBool>
{
    private const string CollectionName = "paid_message_exceptions";

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestToggleNoPaidMessagesException obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.UserId);
        var targetPeer = peerHelper.GetPeer(obj.UserId, input.UserId);
        if (targetPeer.PeerType != PeerType.User)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        Peer? parentPeer = null;
        if (obj.ParentPeer != null)
        {
            await accessHashHelper.CheckAccessHashAsync(input, obj.ParentPeer);
            parentPeer = peerHelper.GetPeer(obj.ParentPeer, input.UserId);
        }
        else if (obj.RequirePayment)
        {
            RpcErrors.RpcErrors400.Unsupported.ThrowRpcError();
        }

        var collection = mongoDatabase.GetCollection<PaidMessageException>(CollectionName);
        var scopeType = parentPeer?.PeerType;
        var scopePeerId = parentPeer?.PeerId;
        var filter = Builders<PaidMessageException>.Filter.And(
            Builders<PaidMessageException>.Filter.Eq(x => x.ScopeType, scopeType),
            Builders<PaidMessageException>.Filter.Eq(x => x.ScopePeerId, scopePeerId),
            Builders<PaidMessageException>.Filter.Eq(x => x.OwnerUserId, input.UserId),
            Builders<PaidMessageException>.Filter.Eq(x => x.TargetUserId, targetPeer.PeerId)
        );

        // require_payment=true => remove exemption; otherwise add exemption
        if (obj.RequirePayment)
        {
            await collection.DeleteOneAsync(filter);
            return new TBoolTrue();
        }

        var update = Builders<PaidMessageException>.Update
            .Set(x => x.ScopeType, scopeType)
            .Set(x => x.ScopePeerId, scopePeerId)
            .Set(x => x.OwnerUserId, input.UserId)
            .Set(x => x.TargetUserId, targetPeer.PeerId)
            .Set(x => x.UpdatedAt, DateTime.UtcNow.ToTimestamp());
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }

    private sealed class PaidMessageException
    {
        public PeerType? ScopeType { get; set; }
        public long? ScopePeerId { get; set; }
        public long OwnerUserId { get; set; }
        public long TargetUserId { get; set; }
        public int UpdatedAt { get; set; }
    }
}
