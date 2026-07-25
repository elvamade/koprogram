using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.BotVerifications;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Verify a user or chat <a href="https://corefork.telegram.org/api/bots/verification">on behalf of an organization »</a>.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 403 BOT_VERIFIER_FORBIDDEN This bot cannot assign <a href="https://corefork.telegram.org/api/bots/verification">verification icons</a>.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.setCustomVerification"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetCustomVerificationHandler(
    IPeerHelper peerHelper,
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IMongoDatabase mongoDatabase,
    IReadModelCacheHelper<IUserReadModel> userReadModelCacheHelper,
    IReadModelCacheHelper<IChannelReadModel> channelReadModelCacheHelper) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetCustomVerification, IBool>
{
    private const int BotVerificationDescriptionLimit = 70;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestSetCustomVerification obj)
    {
        var caller = await userAppService.GetAsync(input.UserId);
        if (caller == null)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var callerIsBot = caller.Bot;
        long verifierBotId;

        if (callerIsBot)
        {
            if (obj.Bot != null)
            {
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            }

            verifierBotId = input.UserId;
        }
        else
        {
            if (obj.Bot == null)
            {
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            }

            var botPeer = peerHelper.GetPeer(obj.Bot!, input.UserId);
            if (botPeer.PeerType != PeerType.User || !peerHelper.IsBotUser(botPeer.PeerId))
            {
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            }

            var botUser = await userAppService.GetAsync(botPeer.PeerId);
            if (botUser == null || !botUser.Bot)
            {
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            }

            verifierBotId = botPeer.PeerId;
        }

        var verifierSettings = await BotVerificationStore.GetVerifierSettingsAsync(mongoDatabase, verifierBotId);
        if (verifierSettings == null)
        {
            RpcErrors.RpcErrors403.BotVerifierForbidden.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        if (peer!.PeerType == PeerType.Self)
        {
            peer = new Peer(PeerType.User, input.UserId);
        }

        if (peer.PeerType is not (PeerType.User or PeerType.Channel))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        if (peer.PeerType == PeerType.User)
        {
            var targetUser = await userAppService.GetAsync(peer.PeerId);
            if (targetUser == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }
        else
        {
            var targetChannel = await channelAppService.GetAsync(peer.PeerId);
            if (targetChannel == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }

        string? customDescription = string.IsNullOrWhiteSpace(obj.CustomDescription)
            ? null
            : obj.CustomDescription.Trim();

        if (customDescription != null)
        {
            var byteCount = Encoding.UTF8.GetByteCount(customDescription);
            if (byteCount > BotVerificationDescriptionLimit)
            {
                RpcErrors.RpcErrors400.InputTextTooLong.ThrowRpcError();
            }
        }

        var icon = BotVerificationStore.GetIconFromVerifierSettings(verifierSettings!);
        var verifierSettingsCollection = BotVerificationStore.GetVerifierSettingsCollection(mongoDatabase);
        var verifierFilter = Builders<BsonDocument>.Filter.Eq("BotId", verifierBotId);
        var arrayField = peer.PeerType == PeerType.User ? "UserIds" : "ChannelIds";
        var updates = new List<UpdateDefinition<BsonDocument>>();

        if (obj.Enabled)
        {
            updates.Add(Builders<BsonDocument>.Update.AddToSet(arrayField, peer.PeerId));

            var canModifyCustomDescription = BotVerificationStore.GetBool(verifierSettings!, "CanModifyCustomDescription");
            if (customDescription != null && canModifyCustomDescription)
            {
                updates.Add(Builders<BsonDocument>.Update.Set("CustomDescription", customDescription));
            }

            if (updates.Count > 0)
            {
                await verifierSettingsCollection.UpdateOneAsync(verifierFilter, Builders<BsonDocument>.Update.Combine(updates));
            }

            await UpdateReadModelIconAsync(peer, icon);
        }
        else
        {
            updates.Add(Builders<BsonDocument>.Update.Pull(arrayField, peer.PeerId));

            if (updates.Count > 0)
            {
                await verifierSettingsCollection.UpdateOneAsync(verifierFilter, Builders<BsonDocument>.Update.Combine(updates));
            }

            var remainingSettings = await BotVerificationStore.GetVerifierSettingsForPeerAsync(mongoDatabase, peer.PeerType, peer.PeerId, verifierBotId);
            long? remainingIcon = remainingSettings == null ? (long?)null : BotVerificationStore.GetIconFromVerifierSettings(remainingSettings);
            await UpdateReadModelIconAsync(peer, remainingIcon);
        }

        return new TBoolTrue();
    }

    private async Task UpdateReadModelIconAsync(Peer peer, long? icon)
    {
        if (peer.PeerType == PeerType.User)
        {
            var collection = BotVerificationStore.GetUserReadModelCollection(mongoDatabase);
            var filter = Builders<BsonDocument>.Filter.Eq("UserId", peer.PeerId);
            var update = icon.HasValue && icon.Value > 0
                ? Builders<BsonDocument>.Update.Set("BotVerificationIcon", icon.Value)
                : Builders<BsonDocument>.Update.Unset("BotVerificationIcon");
            await collection.UpdateOneAsync(filter, update);
            userReadModelCacheHelper.Remove(UserId.Create(peer.PeerId).Value);
        }
        else if (peer.PeerType == PeerType.Channel)
        {
            var collection = BotVerificationStore.GetChannelReadModelCollection(mongoDatabase);
            var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId);
            var update = icon.HasValue && icon.Value > 0
                ? Builders<BsonDocument>.Update.Set("BotVerificationIcon", icon.Value)
                : Builders<BsonDocument>.Update.Unset("BotVerificationIcon");
            await collection.UpdateOneAsync(filter, update);
            channelReadModelCacheHelper.Remove(ChannelId.Create(peer.PeerId).Value);
        }
    }
}
