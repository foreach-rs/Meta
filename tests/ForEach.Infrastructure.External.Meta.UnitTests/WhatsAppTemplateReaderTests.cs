using ForEach.Infrastructure.External.Meta.WhatsApp;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// Reading Meta's template list.
///
/// The parameter count is the part with a wrong answer that looks right. A send whose count
/// disagrees with the approved body is refused outright, with an error about parameters that names
/// no component — so counting a header's placeholders, or counting {{1}} twice because the wording
/// repeats it, breaks every send of that template and points the investigation somewhere else.
/// </summary>
public class WhatsAppTemplateReaderTests
{
    [Fact]
    public void The_body_component_is_the_one_that_is_read()
    {
        var template = Assert.Single(WhatsAppTemplateReader.Read(
            """{"data":[{"name":"reorder_reminder","language":"sr","status":"APPROVED","category":"UTILITY","components":[{"type":"HEADER","text":"Zdravo {{1}}"},{"type":"BODY","text":"Poštovani {{1}}, prošlo je dosta vremena od porudžbine {{2}}."},{"type":"FOOTER","text":"Pošaljite STOP za odjavu"}]}]}"""));

        Assert.Equal("reorder_reminder", template.Name);
        Assert.Equal("sr", template.Language);
        Assert.Equal(WhatsAppTemplateStatus.Approved, template.Status);
        Assert.Equal("UTILITY", template.Category);

        // The body's wording, not the header's — and two parameters, not the three a count across
        // every component would give.
        Assert.StartsWith("Poštovani", template.BodyText!, StringComparison.Ordinal);
        Assert.Equal(2, template.ParameterCount);
    }

    [Fact]
    public void A_placeholder_used_twice_is_still_one_parameter()
    {
        // The API takes one value per distinct placeholder. Counting occurrences sends two, and
        // the send is refused for a mismatch nothing on screen explains.
        var template = Assert.Single(WhatsAppTemplateReader.Read(
            """{"data":[{"name":"t","language":"sr","status":"APPROVED","components":[{"type":"BODY","text":"Poštovani {{1}}, hvala {{1}} na poverenju."}]}]}"""));

        Assert.Equal(1, template.ParameterCount);
    }

    [Fact]
    public void A_template_with_no_placeholders_takes_none()
    {
        var template = Assert.Single(WhatsAppTemplateReader.Read(
            """{"data":[{"name":"t","language":"sr","status":"APPROVED","components":[{"type":"BODY","text":"Zatvoreni smo do ponedeljka."}]}]}"""));

        Assert.Equal(0, template.ParameterCount);
    }

    [Theory]
    [InlineData("APPROVED", WhatsAppTemplateStatus.Approved)]
    [InlineData("PENDING", WhatsAppTemplateStatus.Pending)]
    [InlineData("REJECTED", WhatsAppTemplateStatus.Rejected)]
    [InlineData("PAUSED", WhatsAppTemplateStatus.Unknown)]
    [InlineData("nesto_novo", WhatsAppTemplateStatus.Unknown)]
    public void A_status_Meta_invents_is_not_an_approval(string status, WhatsAppTemplateStatus expected)
    {
        // Unknown is treated as "not approved" everywhere downstream. Guessing Approved for a
        // status nobody has heard of would send on a template Meta has paused.
        var template = Assert.Single(WhatsAppTemplateReader.Read(
            $$"""{"data":[{"name":"t","language":"sr","status":"{{status}}","components":[]}]}"""));

        Assert.Equal(expected, template.Status);
    }

    [Fact]
    public void A_template_with_no_name_is_skipped_rather_than_stored_empty()
    {
        // Nothing could send it, and an empty key would collide with the next nameless one.
        Assert.Empty(WhatsAppTemplateReader.Read(
            """{"data":[{"language":"sr","status":"APPROVED","components":[]}]}"""));
    }

    [Fact]
    public void An_answer_that_is_not_the_expected_shape_yields_nothing_rather_than_throwing()
    {
        // The caller treats an empty list as "nothing to update", which leaves the stored list
        // alone — the same outcome as a failed call, and the safe one.
        Assert.Empty(WhatsAppTemplateReader.Read("<html>nope</html>"));
        Assert.Empty(WhatsAppTemplateReader.Read("""{"error":{"message":"nope"}}"""));
    }
}
