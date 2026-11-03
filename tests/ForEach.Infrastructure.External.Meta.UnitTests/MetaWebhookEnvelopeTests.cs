using ForEach.Infrastructure.External.Meta.Webhooks;

namespace ForEach.Infrastructure.External.Meta.UnitTests;

/// <summary>
/// Filing an incoming batch under the product that sent it, and under what it is about.
///
/// One webhook carries Messenger, Instagram, WhatsApp and lead submissions, and this is the only
/// place that separates them. A payload filed wrongly is read by a parser looking for a tree that
/// is not there — no messages, no error, and from a screen indistinguishable from a customer who
/// never wrote.
/// </summary>
public class MetaWebhookEnvelopeTests
{
    [Theory]
    [InlineData("page", MetaChannel.Messenger)]
    [InlineData("instagram", MetaChannel.Instagram)]
    [InlineData("whatsapp_business_account", MetaChannel.WhatsApp)]
    public void The_object_field_names_the_channel(string envelope, MetaChannel expected)
    {
        var info = MetaWebhookEnvelope.Classify($$"""{"object":"{{envelope}}","entry":[]}""");

        Assert.Equal(MetaWebhookKind.Messaging, info.Kind);
        Assert.Equal(expected, info.Channel);
    }

    [Fact]
    public void A_form_submission_is_not_read_as_a_conversation()
    {
        // The one that used to go missing. A lead arrives under `object: "page"` exactly like a
        // Messenger message, and only the changed field tells them apart — so before this, every
        // submission went to a parser that walks a different tree and found nothing to store.
        var info = MetaWebhookEnvelope.Classify(
            """
            {"object":"page","entry":[{"id":"1122","time":1755800000,
              "changes":[{"field":"leadgen","value":{"leadgen_id":"9001","form_id":"77",
                "page_id":"1122","created_time":1755800000}}]}]}
            """);

        Assert.Equal(MetaWebhookKind.LeadGen, info.Kind);

        // No channel, because a lead is not a conversation. Naming one would invite somebody to
        // open a thread for a person who has not written to us.
        Assert.Null(info.Channel);
        Assert.Equal(["1122"], info.EntryIds);
    }

    [Fact]
    public void A_batch_carrying_both_is_treated_as_the_lead_it_also_carries()
    {
        // Meta may put a message and a form submission in one call. The lead is the half that
        // would otherwise vanish, so it decides — and the raw payload is stored whole either way,
        // which is what makes this recoverable rather than a coin toss.
        var info = MetaWebhookEnvelope.Classify(
            """
            {"object":"page","entry":[
              {"id":"1122","messaging":[{"sender":{"id":"5"},"message":{"mid":"m1","text":"zdravo"}}]},
              {"id":"1122","changes":[{"field":"leadgen","value":{"leadgen_id":"9001"}}]}]}
            """);

        Assert.Equal(MetaWebhookKind.LeadGen, info.Kind);
    }

    [Fact]
    public void A_changed_field_nobody_reads_is_still_a_page_batch()
    {
        // Subscribing to one field subscribes to its neighbours' noise. `feed` is not a lead and
        // not a message; it is stored and never parsed, which is the correct amount of attention.
        var info = MetaWebhookEnvelope.Classify(
            """{"object":"page","entry":[{"id":"1122","changes":[{"field":"feed","value":{}}]}]}""");

        Assert.Equal(MetaWebhookKind.Messaging, info.Kind);
        Assert.Equal(MetaChannel.Messenger, info.Channel);
    }

    [Fact]
    public void An_envelope_nobody_recognises_goes_to_Messenger()
    {
        // Deliberately the harmless end: Messenger's parser reads a shape it does not understand as
        // no messages. Guessing WhatsApp would hand it to a parser that walks a different tree, and
        // the row would still be stored under a channel it did not come from.
        Assert.Equal(MetaChannel.Messenger, MetaWebhookEnvelope.Classify("""{"object":"threads"}""").Channel);
        Assert.Equal(MetaChannel.Messenger, MetaWebhookEnvelope.Classify("""{"entry":[]}""").Channel);
    }

    [Fact]
    public void A_payload_that_is_not_json_is_still_classified_rather_than_refused()
    {
        // The row is evidence. Somebody asking why nothing arrived is better served by an
        // unreadable payload with an error stamped on it than by silence.
        var broken = MetaWebhookEnvelope.Classify("<html>nope</html>");

        Assert.Equal(MetaWebhookKind.Messaging, broken.Kind);
        Assert.Equal(MetaChannel.Messenger, broken.Channel);
        Assert.Empty(broken.EntryIds);

        Assert.Equal(MetaChannel.Messenger, MetaWebhookEnvelope.Classify(string.Empty).Channel);
    }

    [Fact]
    public void The_entry_ids_come_back_for_whoever_has_to_route_on_them()
    {
        // A relay serving several installs picks the tenant from these without parsing the batch
        // properly. Page id and WABA id both live here, which is why the field is not called
        // PageIds.
        var info = MetaWebhookEnvelope.Classify(
            """{"object":"whatsapp_business_account","entry":[{"id":"waba-1"},{"id":"waba-2"}]}""");

        Assert.Equal(["waba-1", "waba-2"], info.EntryIds);
    }
}
