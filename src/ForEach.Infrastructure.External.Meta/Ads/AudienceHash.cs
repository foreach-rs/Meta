using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ForEach.Infrastructure.External.Meta.Ads;

/// <summary>
/// Turning an address or a telephone number into what Meta will accept for a Custom Audience.
///
/// Pure, and separated from the client that uploads it, because this is the half that can be wrong
/// without anybody noticing: a badly normalised value hashes to something Meta simply fails to
/// match, and the audience comes back smaller than it should with no error anywhere. The rules are
/// Meta's, not ours — lower case, no spaces, digits only for a number, and a country code that is
/// present but not prefixed.
///
/// The hash is not a security measure here and should not be read as one. It is a format: SHA-256
/// of the normalised value, hex, lower case. What protects the customer is that only people who
/// gave marketing consent are ever put through it.
/// </summary>
public static partial class AudienceHash
{
    /// <summary>
    /// An e-mail address, or null when there is nothing worth sending.
    ///
    /// Lower-cased and trimmed and nothing else: Meta does no further normalisation, and stripping
    /// dots or plus-tags — as some providers treat them — would hash to a value their side never
    /// produced.
    /// </summary>
    public static string? Email(string? address) =>
        string.IsNullOrWhiteSpace(address)
            ? null
            : Sha256(address.Trim().ToLowerInvariant());

    /// <summary>
    /// A telephone number in Meta's shape: digits only, country code included, no plus and no
    /// leading zeros from a national format.
    /// </summary>
    /// <param name="number">
    /// The number as somebody typed it, in any format; anything that is not a digit is dropped.
    /// Null, or no digits at all, gives null rather than a hash of nothing.
    /// </param>
    /// <param name="defaultCountryCode">
    /// Prefixed when the number is written nationally — "381" here, with or without a leading
    /// plus. A Serbian number typed as 060/123-4567 is 381601234567 to Meta, and uploading it as
    /// 0601234567 matches nobody.
    /// </param>
    public static string? Phone(string? number, string defaultCountryCode)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;

        var trimmed = number.TrimStart();

        // "+381 (0)60 1234567": the "(0)" is a trunk zero shown for national dialling and is not
        // dialled from abroad, so it goes before anything else is read.
        trimmed = TrunkZeroInBrackets().Replace(trimmed, "");

        var digits = new string([.. trimmed.Where(char.IsAsciiDigit)]);

        if (digits.Length == 0) return null;

        var international = trimmed.StartsWith('+');

        if (!international && digits.StartsWith("00", StringComparison.Ordinal))
        {
            // "00" is the international call prefix: 00381… is +381….
            digits = digits.TrimStart('0');
            international = true;
        }

        if (!international)
        {
            var country = new string([.. (defaultCountryCode ?? "").Where(char.IsAsciiDigit)]);

            if (country.Length > 0)
            {
                // A national number: drop the trunk zero and ALWAYS put the country code in
                // front. Not skipped when the national number happens to start with the same
                // digits — German 0491 123456 is 49 491 123456, and 49 491… is not a duplicate.
                digits = country + digits.TrimStart('0');
            }
        }

        return digits.Length == 0 ? null : Sha256(digits);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\(\s*0\s*\)")]
    private static partial System.Text.RegularExpressions.Regex TrunkZeroInBrackets();

    private static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        var builder = new StringBuilder(hash.Length * 2);

        foreach (var b in hash) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));

        return builder.ToString();
    }
}
