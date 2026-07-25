using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.BotVerifications;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Get full info about a <a href="https://corefork.telegram.org/api/channel#supergroups">supergroup</a>, <a href="https://corefork.telegram.org/api/channel#gigagroups">gigagroup</a> or <a href="https://corefork.telegram.org/api/channel#channels">channel</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHANNEL_PUBLIC_GROUP_NA channel/supergroup not available.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.getFullChannel"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetFullChannelHandler(IQueryProcessor queryProcessor, //ILayeredService<IChatConverter> layeredService,
IUserConverterService userConverterService, IChatConverterService chatConverterService, IAccessHashHelper accessHashHelper, IPhotoAppService photoAppService, ILogger<GetFullChannelHandler> logger, IChannelAppService channelAppService, IChannelAdminRightsChecker channelAdminRightsChecker, IMongoDatabase mongoDatabase) : RpcResultObjectHandler<RequestGetFullChannel, MyTelegram.Schema.Messages.IChatFull>
{
    protected override async Task<MyTelegram.Schema.Messages.IChatFull> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestGetFullChannel obj)
    {
        if (obj.Channel is TInputChannel inputChannel)
        {
            var channelId = inputChannel.ChannelId;
            await accessHashHelper.CheckAccessHashAsync(input, channelId, inputChannel.AccessHash, AccessHashType.Channel);
            var channelReadModel = await channelAppService.GetAsync(channelId);
            if (channelReadModel == null !)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            var channelFullReadModel = await channelAppService.GetChannelFullAsync(channelId);
            if (channelFullReadModel == null)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel!))
            {
                return null !;
            }

            var dialogReadModel = await queryProcessor.ProcessAsync(new GetDialogByIdQuery(DialogId.Create(input.UserId, PeerType.Channel, channelId).Value));
            if (dialogReadModel == null)
            {
                logger.LogWarning("Dialog not exists, userId: {UserId}, toPeer: {ToPeer}", input.UserId, new Peer(PeerType.Channel, channelId));
            }
            else
            {
                channelFullReadModel!.ReadInboxMaxId = dialogReadModel.ReadInboxMaxId;
                channelFullReadModel.ReadOutboxMaxId = dialogReadModel.ReadOutboxMaxId;
                var maxId = new[]
                {
                    dialogReadModel.ReadInboxMaxId,
                    dialogReadModel.ReadOutboxMaxId,
                    dialogReadModel.ChannelHistoryMinId
                }.Max();
                channelFullReadModel.UnreadCount = channelReadModel!.TopMessageId - maxId;
            }

            var channelMemberReadModel = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, input.UserId));
            var peerNotifySettings = await queryProcessor.ProcessAsync(new GetPeerNotifySettingsByIdQuery(PeerNotifySettingsId.Create(input.UserId, PeerType.Channel, channelId).Value));
            var photoReadModel = await photoAppService.GetAsync(channelReadModel!.PhotoId);
            IChatInviteReadModel? chatInviteReadModel = null;
            if (channelReadModel.AdminList.Any(p => p.UserId == input.UserId))
            {
                chatInviteReadModel = await queryProcessor.ProcessAsync(new GetPermanentChatInviteQuery(channelId, input.UserId));
            }

            var chatFull = chatConverterService.ToChannelFull(input, channelReadModel, photoReadModel, channelFullReadModel!, channelMemberReadModel, peerNotifySettings, chatInviteReadModel, input.Layer);
            var fullChat = chatFull.FullChat;
            if (fullChat is ILayeredChannelFull layeredChannelFull)
            {
                layeredChannelFull.ViewForumAsMessages = dialogReadModel?.ViewForumAsMessages ?? false;
                layeredChannelFull.ParticipantsHidden = channelReadModel.ParticipantsHidden;
                if (channelMemberReadModel == null || channelMemberReadModel.Left || channelMemberReadModel.Kicked)
                {
                    layeredChannelFull.CanViewParticipants = false;
                }

                // Set pending requests for channel admin
                await SetRecentRequestersAsync(input, layeredChannelFull, chatFull);
            }

            IChat? linkedChannel = null;
            if (channelFullReadModel!.LinkedChatId.HasValue)
            {
                var linkedChannelReadModel = await channelAppService.GetAsync(channelFullReadModel.LinkedChatId.Value);
                if (linkedChannelReadModel != null !)
                {
                    var linkedChannelPhotoReadModel = await photoAppService.GetAsync(linkedChannelReadModel.PhotoId);
                    var linkedChannelMemberReadModel = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(linkedChannelReadModel.ChannelId, input.UserId));
                    linkedChannel = chatConverterService.ToChannel(input, linkedChannelReadModel, linkedChannelPhotoReadModel, linkedChannelMemberReadModel, linkedChannelMemberReadModel == null || linkedChannelMemberReadModel.Left, input.Layer);
                //r.Chats.Add(linkedChannel);
                }
            }

            if (linkedChannel != null)
            {
                chatFull.Chats.Add(linkedChannel);
            }

            await SetBotVerificationAsync(channelId, chatFull);
            await SetPaidMessagesSettingsAsync(channelId, chatFull);
            return chatFull;
        }

        throw new NotImplementedException();
    }

    private async Task SetRecentRequestersAsync(IRequestInput input, ILayeredChannelFull layeredChannelFull, MyTelegram.Schema.Messages.IChatFull chatFull)
    {
        var channelId = layeredChannelFull.Id;
        if (await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.InviteUsers))
        {
            var pendingRequestsCount = await queryProcessor.ProcessAsync(new GetPendingRequestsCountQuery(channelId));
            if (pendingRequestsCount > 0)
            {
                layeredChannelFull.RequestsPending = pendingRequestsCount;
                var recentRequesters = await queryProcessor.ProcessAsync(new GetRecentRequestUserIdListQuery(channelId, 5));
                layeredChannelFull.RecentRequesters = [..recentRequesters];
                var users = await userConverterService.GetUserListAsync(input, [..recentRequesters], false, false, input.Layer);
                chatFull.Users = [..users];
            }
        }
    }

    private async Task SetBotVerificationAsync(long channelId, MyTelegram.Schema.Messages.IChatFull chatFull)
    {
        var botVerification = await BotVerificationStore.GetBotVerificationAsync(mongoDatabase, PeerType.Channel, channelId);
        if (botVerification == null)
        {
            return;
        }

        if (chatFull.FullChat is TChannelFull channelFull)
        {
            channelFull.BotVerification = botVerification;
        }

        var chat = chatFull.Chats.FirstOrDefault();
        if (chat is TChannel channel)
        {
            channel.BotVerificationIcon = botVerification.Icon;
        }
    }

    private async Task SetPaidMessagesSettingsAsync(long channelId, MyTelegram.Schema.Messages.IChatFull chatFull)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("paid_message_channel_settings");
        var settings = await collection.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();
        if (settings == null)
        {
            return;
        }

        var stars = settings.GetValue("SendPaidMessagesStars", BsonNull.Value);
        var starsValue = stars.IsBsonNull ? (long?)null : (stars.IsInt64 ? stars.AsInt64 : stars.AsInt32);
        if (!starsValue.HasValue || starsValue.Value <= 0)
        {
            return;
        }

        if (chatFull.FullChat is TChannelFull channelFull)
        {
            channelFull.SendPaidMessagesStars = starsValue.Value;
        }

        var chat = chatFull.Chats.FirstOrDefault();
        if (chat is TChannel channel)
        {
            channel.SendPaidMessagesStars = starsValue.Value;
        }
    }
}
