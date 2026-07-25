using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Enable or disable <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a> in this <a href="https://corefork.telegram.org/api/channel">supergroup</a> or <a href="https://corefork.telegram.org/api/monoforum">monoforum</a>.Also used to <a href="https://corefork.telegram.org/api/monoforum">enable or disable monoforums aka direct messages in a channel</a>.Note that passing the ID of the monoforum itself to <code>channel</code> will return a <code>CHANNEL_MONOFORUM_UNSUPPORTED</code> error: pass the ID of the associated channel to edit the settings of the associated monoforum, instead.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_MONOFORUM_UNSUPPORTED <a href="https://corefork.telegram.org/api/channel#monoforums">Monoforums</a> do not support this feature.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 400 STARS_AMOUNT_INVALID The specified amount in stars is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.updatePaidMessagesPrice"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdatePaidMessagesPriceHandler(
    IChannelAppService channelAppService,
    IAccessHashHelper accessHashHelper,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestUpdatePaidMessagesPrice, MyTelegram.Schema.IUpdates>
{
    private const string CollectionName = "paid_message_channel_settings";

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestUpdatePaidMessagesPrice obj)
    {
        if (obj.SendPaidMessagesStars < 0)
        {
            RpcErrors.RpcErrors400.StarsAmountInvalid.ThrowRpcError();
        }

        if (obj.Channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var channelId = inputChannel.ChannelId;
        await accessHashHelper.CheckAccessHashAsync(input, channelId, inputChannel.AccessHash, AccessHashType.Channel);
        var channelReadModel = await channelAppService.GetAsync(channelId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        var collection = mongoDatabase.GetCollection<PaidMessageChannelSettings>(CollectionName);
        var filter = Builders<PaidMessageChannelSettings>.Filter.Eq(x => x.ChannelId, channelId);
        var update = Builders<PaidMessageChannelSettings>.Update
            .Set(x => x.ChannelId, channelId)
            .Set(x => x.SendPaidMessagesStars, obj.SendPaidMessagesStars)
            .Set(x => x.BroadcastMessagesAllowed, obj.BroadcastMessagesAllowed)
            .Set(x => x.UpdatedAt, DateTime.UtcNow.ToTimestamp());
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        return new TUpdates { Chats = [], Updates = [], Users = [] };
    }

    private sealed class PaidMessageChannelSettings
    {
        public long ChannelId { get; set; }
        public long SendPaidMessagesStars { get; set; }
        public bool BroadcastMessagesAllowed { get; set; }
        public int UpdatedAt { get; set; }
    }
}
