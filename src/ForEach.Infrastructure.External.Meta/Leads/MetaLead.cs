namespace ForEach.Infrastructure.External.Meta.Leads;

/// <summary>One answer as the person gave it, under the question's own name.</summary>
/// <param name="Name">
/// Meta's field name — `email`, `phone_number`, `full_name` for the standard questions, and
/// whatever the form's author typed for the rest. The host maps the standard ones onto its own
/// columns and keeps everything, because the custom questions are usually the point of the form.
/// </param>
/// <param name="Values">
/// A list, because a checkbox question answers with several. Nearly always one, and a host that
/// assumed one would lose the other answers on exactly the questions worth asking.
/// </param>
public sealed record MetaLeadField(string Name, IReadOnlyList<string> Values);

/// <summary>
/// One submitted form, fetched with the Page token.
/// </summary>
/// <param name="Id">Meta's `leadgen_id`: the key a host stores the lead under and de-duplicates on.</param>
/// <param name="CreatedAt">When the person pressed send, in UTC.</param>
/// <param name="FormId">The form these are the answers to. Null only when Meta omits it.</param>
/// <param name="AdId">The advertisement that led here. Empty for an organic lead.</param>
/// <param name="AdName">Its name as it stood when the lead was fetched. Names change; ids do not.</param>
/// <param name="AdSetId">The ad set the advertisement belongs to. Empty for an organic lead.</param>
/// <param name="AdSetName">Its name when fetched.</param>
/// <param name="CampaignId">
/// The campaign above the ad set, and what joins a lead to a day of <see cref="Ads.MetaAdInsight"/>.
/// Empty for an organic lead.
/// </param>
/// <param name="CampaignName">Its name when fetched.</param>
/// <param name="IsOrganic">
/// A form on the Page rather than an advertisement. The campaign fields are then empty and nothing
/// is missing — a host reporting "leads without a campaign" as a fault would be reporting this.
/// </param>
/// <param name="Platform">"fb" or "ig", in Meta's own words rather than translated.</param>
/// <param name="Fields">
/// Every answer on the form, in the form's order. <see cref="Value"/> reads one by name.
/// </param>
public sealed record MetaLead(
    string Id,
    DateTime CreatedAt,
    string? FormId,
    string? AdId,
    string? AdName,
    string? AdSetId,
    string? AdSetName,
    string? CampaignId,
    string? CampaignName,
    bool IsOrganic,
    string? Platform,
    IReadOnlyList<MetaLeadField> Fields)
{
    /// <summary>
    /// The first value of a field, by Meta's name for it, or null.
    ///
    /// Case-insensitive because form authors have been seen typing `Email` where the standard field
    /// is `email`, and a mapping that missed it would leave an address in the answers and none in
    /// the column anybody searches.
    /// </summary>
    public string? Value(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>
/// A form as Meta describes it, for the cache a host keeps so a list can say "Kesice — proleće"
/// rather than an eighteen-digit id.
/// </summary>
/// <param name="Id">Meta's form id — what a lead's <see cref="MetaLead.FormId"/> points at.</param>
/// <param name="Name">What the form's author called it, as Meta has it now.</param>
/// <param name="Status">
/// ACTIVE, ARCHIVED or whatever Meta says; null when it says nothing. An archived form is hidden
/// rather than forgotten, because its leads remain.
/// </param>
/// <param name="Questions">
/// The questions as Meta serialises them, raw JSON. Kept for a screen that wants to show the form
/// rather than parsed here, where nothing needs them.
/// </param>
public sealed record MetaLeadFormInfo(string Id, string Name, string? Status, string? Questions);
