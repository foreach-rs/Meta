using ForEach.Infrastructure.External.Meta;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// Meta's failure codes, sorted into the four things a host can actually do about them.
///
/// The table is the point of this file. Every row is a decision somebody made once — retry, tell a
/// person, fix the call, or show the customer's own refusal — and the codes are the only evidence
/// of which is which, so a row silently changing meaning is a message that stops being sent or a
/// token nobody is told about.
/// </summary>
public class MetaErrorTests
{
    [Theory]
    [InlineData(190, null)]      // token invalid or expired
    [InlineData(102, null)]      // session expired
    [InlineData(104, null)]      // no token at all
    public void A_token_problem_is_configuration(int code, int? subcode) =>
        Assert.Equal(MetaFailureKind.Configuration, Error(code, subcode).Kind);

    [Theory]
    [InlineData(10, 2018278)]    // outside the 24-hour window
    [InlineData(10, 2018065)]    // this person cannot be messaged
    [InlineData(551, null)]      // user unavailable
    [InlineData(131047, null)]   // re-engagement needs a template
    [InlineData(131026, null)]   // recipient unavailable
    [InlineData(131051, null)]   // unsupported message type
    [InlineData(131030, null)]   // not in the test allow-list
    public void A_refusal_by_policy_is_never_retried(int code, int? subcode)
    {
        var error = Error(code, subcode);

        Assert.Equal(MetaFailureKind.PolicyRefused, error.Kind);
        Assert.False(MetaResult<string>.Fail(error).IsWorthRetrying);
    }

    [Theory]
    [InlineData(132000)]
    [InlineData(132007)]
    [InlineData(132016)]
    [InlineData(100)]
    public void A_wrong_call_is_ours_to_fix(int code) =>
        Assert.Equal(MetaFailureKind.CallerMistake, Error(code, null).Kind);

    [Theory]
    [InlineData(4)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(613)]
    [InlineData(80007)]
    [InlineData(130429)]
    public void Rate_limiting_is_worth_waiting_out(int code)
    {
        var error = Error(code, null);

        Assert.Equal(MetaFailureKind.Transient, error.Kind);
        Assert.True(MetaResult<string>.Fail(error).IsWorthRetrying);
    }

    [Fact]
    public void A_code_nobody_has_written_a_row_for_is_retried()
    {
        // On purpose, and the same reasoning as the NBS library: a wasted retry costs one
        // scheduled run, while giving up on something that would have worked costs the message
        // until somebody notices it never arrived.
        Assert.Equal(MetaFailureKind.Transient, Error(999999, null).Kind);
    }

    [Fact]
    public void The_subcode_is_what_separates_two_tens()
    {
        // A plain 10 is a permission problem somebody has to fix; 10/2018278 is the messaging
        // window having shut, which is an answer rather than a fault. Sorting them together would
        // have somebody reconnecting an account that works.
        Assert.NotEqual(Error(10, null).Kind, Error(10, 2018278).Kind);
    }

    [Fact]
    public void The_error_reads_as_Metas_own_words()
    {
        var error = MetaError.TryRead(
            """
            {"error":{"message":"Message failed to send because more than 24 hours have passed",
            "type":"OAuthException","code":10,"error_subcode":2018278,"fbtrace_id":"Axb12"}}
            """);

        Assert.NotNull(error);
        Assert.Equal(10, error.Code);
        Assert.Equal(2018278, error.Subcode);
        Assert.Equal("Axb12", error.TraceId);

        // The trace id is useless to us and the first thing Meta support asks for; the message is
        // what an operator reads, so it survives verbatim into the failure text.
        Assert.Contains("24 hours", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_that_is_not_Metas_error_is_not_read_as_one()
    {
        // A gateway between us and Meta answering HTML, or a success body. Calling either "Meta 0"
        // would put words in Meta's mouth on a screen somebody reads to decide what to do next.
        Assert.Null(MetaError.TryRead("<html><body>502 Bad Gateway</body></html>"));
        Assert.Null(MetaError.TryRead("""{"messages":[{"id":"wamid.1"}]}"""));
        Assert.Null(MetaError.TryRead(""));
    }

    [Fact]
    public void A_transport_failure_is_not_an_error_from_Meta()
    {
        // The distinction the result type exists for: a timeout says nothing about our token, and
        // a host that treated it as one would tell somebody to reconnect a working account.
        var result = MetaResult<string>.Transport("The operation was canceled.");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.True(result.IsWorthRetrying);
        Assert.Equal("The operation was canceled.", result.FailureMessage);
    }

    private static MetaError Error(int code, int? subcode) =>
        new(code, subcode, "OAuthException", "…", null);
}
