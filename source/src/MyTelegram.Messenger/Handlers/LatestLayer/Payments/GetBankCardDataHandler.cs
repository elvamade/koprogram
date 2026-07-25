namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Get info about a credit card
/// Possible errors
/// Code Type Description
/// 400 BANK_CARD_NUMBER_INVALID The specified card number is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getBankCardData"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetBankCardDataHandler : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetBankCardData, MyTelegram.Schema.Payments.IBankCardData>
{
    protected override Task<MyTelegram.Schema.Payments.IBankCardData> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetBankCardData obj)
    {
        var normalizedNumber = NormalizeCardNumber(obj.Number);
        if (string.IsNullOrWhiteSpace(normalizedNumber) ||
            normalizedNumber.Length is < 12 or > 19 ||
            !normalizedNumber.All(char.IsDigit) ||
            !IsLuhnValid(normalizedNumber))
        {
            RpcErrors.RpcErrors400.BankCardNumberInvalid.ThrowRpcError();
        }

        var cardTitle = GetCardTitle(normalizedNumber);
        var openUrls = new TVector<IBankCardOpenUrl>
        {
            new TBankCardOpenUrl
            {
                Name = "Stripe Test Cards",
                Url = "https://stripe.com/docs/testing"
            }
        };

        MyTelegram.Schema.Payments.IBankCardData result = new MyTelegram.Schema.Payments.TBankCardData
        {
            Title = cardTitle,
            OpenUrls = openUrls
        };

        return Task.FromResult(result);
    }

    private static string NormalizeCardNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return string.Empty;
        }

        return new string(number.Where(char.IsDigit).ToArray());
    }

    private static string GetCardTitle(string number)
    {
        if (number.StartsWith("4", StringComparison.Ordinal))
        {
            return "Visa";
        }

        if (number.StartsWith("34", StringComparison.Ordinal) || number.StartsWith("37", StringComparison.Ordinal))
        {
            return "American Express";
        }

        if (number.StartsWith("5", StringComparison.Ordinal))
        {
            return "Mastercard";
        }

        return "Bank Card";
    }

    private static bool IsLuhnValid(string cardNumber)
    {
        var sum = 0;
        var shouldDouble = false;
        for (var i = cardNumber.Length - 1; i >= 0; i--)
        {
            var digit = cardNumber[i] - '0';
            if (shouldDouble)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            shouldDouble = !shouldDouble;
        }

        return sum % 10 == 0;
    }
}
