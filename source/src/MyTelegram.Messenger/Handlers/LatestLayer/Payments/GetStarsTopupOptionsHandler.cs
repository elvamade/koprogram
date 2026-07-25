namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain a list of <a href="https://corefork.telegram.org/api/stars#buying-or-gifting-stars">Telegram Stars topup options »</a> as <a href="https://corefork.telegram.org/constructor/starsTopupOption">starsTopupOption</a> constructors.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsTopupOptions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsTopupOptionsHandler : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTopupOptions, TVector<MyTelegram.Schema.IStarsTopupOption>>
{
    protected override Task<TVector<MyTelegram.Schema.IStarsTopupOption>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTopupOptions obj)
    {
        var options = new TVector<MyTelegram.Schema.IStarsTopupOption>
        {
            CreateOption(100, 199, "stars_100"),
            CreateOption(250, 499, "stars_250"),
            CreateOption(500, 999, "stars_500"),
            CreateOption(1000, 1999, "stars_1000"),
            CreateOption(2500, 4999, "stars_2500", extended: true),
            CreateOption(5000, 9999, "stars_5000", extended: true)
        };

        return Task.FromResult(options);
    }

    private static TStarsTopupOption CreateOption(long stars, long amount, string storeProduct, bool extended = false)
    {
        return new TStarsTopupOption
        {
            Extended = extended,
            Stars = stars,
            StoreProduct = storeProduct,
            Currency = "USD",
            Amount = amount
        };
    }
}
