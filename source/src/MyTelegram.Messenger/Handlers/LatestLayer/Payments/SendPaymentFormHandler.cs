using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarsTransactions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Send compiled payment form
/// Possible errors
/// Code Type Description
/// 400 FORM_UNSUPPORTED Please update your client.
/// 400 INVOICE_INVALID The specified invoice is invalid.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PAYMENT_CREDENTIALS_INVALID The specified payment credentials are invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 TMP_PASSWORD_INVALID The passed tmp_password is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.sendPaymentForm"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User вњ”] [Bot вњ–] [Anonymous вњ”]
/// </remarks>
internal sealed class SendPaymentFormHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestSendPaymentForm, MyTelegram.Schema.Payments.IPaymentResult>
{
    private const string BalanceCollectionName = "eventflow-userstarsbalancereadmodel";

    protected override async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestSendPaymentForm obj)
    {
        if (obj.Credentials == null)
        {
            RpcErrors.RpcErrors400.PaymentCredentialsInvalid.ThrowRpcError();
        }

        if (obj.Credentials is TInputPaymentCredentialsSaved savedCredentials && savedCredentials.TmpPassword.IsEmpty)
        {
            RpcErrors.RpcErrors400.TmpPasswordInvalid.ThrowRpcError();
        }

        if (obj.FormId == 0)
        {
            RpcErrors.RpcErrors400.InvoiceInvalid.ThrowRpcError();
        }

        if (obj.Invoice is TInputInvoiceStars starsInvoice)
        {
            return await HandleStarsInvoicePaymentAsync(input, starsInvoice);
        }

        RpcErrors.RpcErrors400.InvoiceInvalid.ThrowRpcError();
        throw new RpcException(new RpcError(400, "INVOICE_INVALID"));
    }

    private async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleStarsInvoicePaymentAsync(
        IRequestInput input,
        TInputInvoiceStars starsInvoice)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        switch (starsInvoice.Purpose)
        {
            case TInputStorePaymentStarsTopup topupPurpose:
                ValidateStarsPaymentInputs(topupPurpose.Stars, topupPurpose.Amount, topupPurpose.Currency);
                var updatedSelfBalance = await AddStarsBalanceAsync(input.UserId, topupPurpose.Stars);
                var topupTx = StarsTransactionStore.CreateTransactionDocument(
                    input.UserId,
                    topupPurpose.Stars,
                    now,
                    (int)PeerType.User,
                    input.UserId,
                    title: "Stars Topup",
                    description: $"Card topup of {topupPurpose.Stars} Stars");
                await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(topupTx);
                return CreatePaymentResult(now, updatedSelfBalance);

            case TInputStorePaymentStarsGift giftPurpose:
                ValidateStarsPaymentInputs(giftPurpose.Stars, giftPurpose.Amount, giftPurpose.Currency);
                var recipientPeer = peerHelper.GetPeer(giftPurpose.UserId, input.UserId);
                if (recipientPeer.PeerType != PeerType.User && recipientPeer.PeerType != PeerType.Self)
                {
                    RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
                }

                var recipientUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(recipientPeer.PeerId));
                if (recipientUser == null)
                {
                    RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
                }

                var updatedRecipientBalance = await AddStarsBalanceAsync(recipientPeer.PeerId, giftPurpose.Stars);
                var giftTx = StarsTransactionStore.CreateTransactionDocument(
                    recipientPeer.PeerId,
                    giftPurpose.Stars,
                    now,
                    (int)PeerType.User,
                    input.UserId,
                    title: "Stars Gift",
                    description: $"Gift from user {input.UserId}");
                await StarsTransactionStore.GetCollection(mongoDatabase).InsertOneAsync(giftTx);

                return recipientPeer.PeerId == input.UserId
                    ? CreatePaymentResult(now, updatedRecipientBalance)
                    : CreatePaymentResult(now, null);
        }

        RpcErrors.RpcErrors400.PurposeInvalid.ThrowRpcError();
        throw new RpcException(new RpcError(400, "PURPOSE_INVALID"));
    }

    private async Task<long> AddStarsBalanceAsync(long userId, long amount)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>(BalanceCollectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var current = await collection.Find(filter).FirstOrDefaultAsync();

        var currentBalance = 0L;
        if (current != null && current.Contains("Balance"))
        {
            currentBalance = current["Balance"].IsInt64 ? current["Balance"].AsInt64 : current["Balance"].AsInt32;
        }

        var newBalance = currentBalance + amount;

        if (current == null)
        {
            await collection.InsertOneAsync(new BsonDocument
            {
                { "UserId", userId },
                { "Balance", newBalance },
                { "LastUpdated", DateTime.UtcNow }
            });
        }
        else
        {
            var update = Builders<BsonDocument>.Update
                .Set("Balance", newBalance)
                .Set("LastUpdated", DateTime.UtcNow);
            await collection.UpdateOneAsync(filter, update);
        }

        return newBalance;
    }

    private static void ValidateStarsPaymentInputs(long stars, long amount, string? currency)
    {
        if (stars <= 0 || amount <= 0 || string.IsNullOrWhiteSpace(currency))
        {
            RpcErrors.RpcErrors400.PurposeInvalid.ThrowRpcError();
        }
    }

    private static MyTelegram.Schema.Payments.IPaymentResult CreatePaymentResult(int now, long? updatedBalance)
    {
        var updates = new TVector<IUpdate>();
        if (updatedBalance.HasValue)
        {
            updates.Add(new TUpdateStarsBalance
            {
                Balance = new TStarsAmount { Amount = updatedBalance.Value }
            });
        }

        return new MyTelegram.Schema.Payments.TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = updates,
                Users = [],
                Chats = [],
                Date = now,
                Seq = 0
            }
        };
    }
}
