using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Get info about <a href="https://corefork.telegram.org/api/channel">channels/supergroups</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.getChannels"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetChannelsHandler(
    IChatConverterService chatConverterService,
    IQueryProcessor queryProcessor,
    IAccessHashHelper accessHashHelper,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetChannels, IChats>
{
    protected override async Task<IChats> HandleCoreAsync(IRequestInput input, RequestGetChannels obj)
    {
        var channelIds = new List<long>();
        foreach (var inputChannel in obj.Id)
        {
            if (inputChannel is TInputChannel tInputChannel)
            {
                channelIds.Add(tInputChannel.ChannelId);
                await accessHashHelper.CheckAccessHashAsync(input, tInputChannel.ChannelId, tInputChannel.AccessHash, AccessHashType.Channel);
            }
        }

        if (channelIds.Count > 0)
        {
            var channelMemberReadModels = await queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIds));
            var channels = await chatConverterService.GetChannelListAsync(input, channelIds, channelMemberReadModels, layer: input.Layer);
            var settingsCollection = mongoDatabase.GetCollection<BsonDocument>("paid_message_channel_settings");
            var settingsDocs = await settingsCollection.Find(Builders<BsonDocument>.Filter.In("ChannelId", channelIds)).ToListAsync();
            var settings = settingsDocs.ToDictionary(
                k => k["ChannelId"].IsInt64 ? k["ChannelId"].AsInt64 : k["ChannelId"].AsInt32,
                v => v.GetValue("SendPaidMessagesStars", BsonNull.Value));
            foreach (var channel in channels)
            {
                if (channel is TChannel tChannel &&
                    settings.TryGetValue(tChannel.Id, out var starsValue) &&
                    !starsValue.IsBsonNull)
                {
                    var stars = starsValue.IsInt64 ? starsValue.AsInt64 : starsValue.AsInt32;
                    tChannel.SendPaidMessagesStars = stars > 0 ? stars : null;
                }
            }

            return new TChats
            {
                Chats = [..channels]
            };
        }

        RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        throw new NotImplementedException();
    }
}
