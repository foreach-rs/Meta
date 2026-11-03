using System.Security.Cryptography;
using System.Text;
using ForEach.Infrastructure.External.Meta.Webhooks;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// The only thing standing between a public URL and somebody writing into a customer's history.
///
/// The webhook cannot require a login — Meta has no account here — so this signature IS the access
/// control. And because the route takes a batch, one accepted forgery is as many messages as the
/// forger cares to send, each of which will be read as something the customer said.
/// </summary>
public class MetaWebhookSignatureTests
{
    private const string Secret = "tajna-aplikacije";

    [Fact]
    public void A_genuine_call_is_accepted()
    {
        var body = Body("""{"object":"page","entry":[]}""");

        Assert.True(MetaWebhookSignature.IsValid(Sign(body), body, Secret));
    }

    [Fact]
    public void A_body_changed_by_one_byte_is_refused()
    {
        // The case the whole check exists for: a genuine header replayed over a payload somebody
        // edited. Anything less than byte-for-byte and a forger only needs one real call to copy.
        var header = Sign(Body("""{"object":"page","entry":[]}"""));

        Assert.False(MetaWebhookSignature.IsValid(header, Body("""{"object":"page","entry":[1]}"""), Secret));
    }

    [Fact]
    public void Another_secret_does_not_open_it()
    {
        var body = Body("""{"object":"page"}""");

        var header = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("pogrešna-tajna"), body));

        Assert.False(MetaWebhookSignature.IsValid(header, body, Secret));
    }

    [Fact]
    public void A_missing_or_malformed_header_is_refused_rather_than_thrown_at()
    {
        // Rubbish arrives on a public URL constantly. Each of these must be a plain refusal: an
        // exception would be a 500, and a 500 tells whoever sent it that their guess was
        // interesting.
        var body = Body("""{"object":"page"}""");

        Assert.False(MetaWebhookSignature.IsValid(null, body, Secret));
        Assert.False(MetaWebhookSignature.IsValid("", body, Secret));
        Assert.False(MetaWebhookSignature.IsValid("sha256=", body, Secret));
        Assert.False(MetaWebhookSignature.IsValid("sha256=nije-heksadecimalno", body, Secret));
        Assert.False(MetaWebhookSignature.IsValid("sha1=abcdef", body, Secret));
        Assert.False(MetaWebhookSignature.IsValid(Sign(body)[..40], body, Secret));
    }

    [Fact]
    public void With_no_secret_configured_nothing_is_accepted()
    {
        // An install that has not been set up must refuse everything rather than accept anything:
        // an empty secret is not a secret that matches an empty signature.
        var body = Body("""{"object":"page"}""");

        Assert.False(MetaWebhookSignature.IsValid(Sign(body), body, string.Empty));
        Assert.False(MetaWebhookSignature.IsValid("sha256=" + new string('0', 64), body, string.Empty));
    }

    [Fact]
    public void The_verify_token_has_to_match_exactly()
    {
        Assert.True(MetaWebhookSignature.IsVerifyTokenValid("token-123", "token-123"));
        Assert.False(MetaWebhookSignature.IsVerifyTokenValid("token-124", "token-123"));

        // Not a prefix, not a case-insensitive match, and never a blank one — whoever knows this
        // string can point our webhook at their own app.
        Assert.False(MetaWebhookSignature.IsVerifyTokenValid("token-12", "token-123"));
        Assert.False(MetaWebhookSignature.IsVerifyTokenValid("TOKEN-123", "token-123"));
        Assert.False(MetaWebhookSignature.IsVerifyTokenValid(null, "token-123"));
        Assert.False(MetaWebhookSignature.IsVerifyTokenValid("", string.Empty));
    }

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    private static string Sign(byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));
}
