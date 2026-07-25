namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get the number of stars we have received from the specified user thanks to <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a>; the received amount will be equal to the sent amount multiplied by <a href="https://corefork.telegram.org/api/config#stars-paid-message-commission-permille">stars_paid_message_commission_permille</a> divided by 1000.
/// Possible errors
/// Code Type Description
/// 400 PARENT_PEER_INVALID The specified <code>parent_peer</code> is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getPaidMessagesRevenue"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPaidMessagesRevenueHandler(
    IPaidMessagesAppService paidMessagesAppService,
    IAccessHashHelper accessHashHelper,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetPaidMessagesRevenue, MyTelegram.Schema.Account.IPaidMessagesRevenue>
{
    protected override async Task<MyTelegram.Schema.Account.IPaidMessagesRevenue> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetPaidMessagesRevenue obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.UserId);
        var payerPeer = peerHelper.GetPeer(obj.UserId, input.UserId);
        if (payerPeer.PeerType != PeerType.User)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        Peer? parentPeer = null;
        if (obj.ParentPeer != null)
        {
            await accessHashHelper.CheckAccessHashAsync(input, obj.ParentPeer);
            parentPeer = peerHelper.GetPeer(obj.ParentPeer, input.UserId);
        }

        var stars = await paidMessagesAppService.GetPaidMessagesRevenueAsync(input.UserId, payerPeer.PeerId, parentPeer);
        return new TPaidMessagesRevenue { StarsAmount = stars };
    }
}
