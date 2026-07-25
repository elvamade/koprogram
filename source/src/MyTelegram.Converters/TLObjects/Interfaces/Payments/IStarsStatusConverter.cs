// ReSharper disable All

using MyTelegram.Schema.Payments;

namespace MyTelegram.Converters.TLObjects.Payments;

public partial interface IStarsStatusConverter : ILayeredConverter
{
    IStarsStatus ToStarsStatus(bool ton);
    IStarsStatus ToStarsStatus(bool ton, long balance);
}
