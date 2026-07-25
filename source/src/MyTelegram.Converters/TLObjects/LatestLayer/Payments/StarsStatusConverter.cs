// ReSharper disable All

using MyTelegram.Schema.Payments;

namespace MyTelegram.Converters.TLObjects.Payments;

internal sealed class StarsStatusConverter : IStarsStatusConverter, ITransientDependency
{
    public int Layer => Layers.LayerLatest;

    public IStarsStatus ToStarsStatus(bool ton)
    {
        // Default balance - actual balance is fetched in GetStarsStatusHandler
        return ToStarsStatus(ton, 10000000);
    }

    public IStarsStatus ToStarsStatus(bool ton, long balance)
    {
        if (ton)
        {
            return new TStarsStatus
            {
                Balance = new TStarsTonAmount
                {
                    Amount = balance
                },
                Chats = [],
                History = [],
                Users = []
            };
        }

        return new TStarsStatus
        {
            Balance = new TStarsAmount
            {
                Amount = balance
            },
            Chats = [],
            History = [],
            Users = []
        };
    }
}
