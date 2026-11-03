namespace ForEach.Infrastructure.External.Meta.Ads;

/// <summary>
/// One day of one ad campaign, as Meta reports it.
///
/// A day at a time rather than a total, because a total cannot be re-read: a host that stored
/// "spend so far" would have no way to correct yesterday when Meta revises it, and Meta does
/// revise — attribution windows close for up to 28 days after the click.
/// </summary>
/// <param name="CampaignId">Meta's campaign id. With the date, what a stored row is keyed on.</param>
/// <param name="CampaignName">
/// The name as it stood when fetched, or the id when Meta omits it. Campaigns get renamed; the id
/// is what joins two days of the same one.
/// </param>
/// <param name="Date">The day the figures are for — Meta's `date_start`, one row per campaign per day.</param>
/// <param name="Spend">
/// What that day cost, in the ad account's currency. The currency is not on the row; see
/// <see cref="AdsInsightsClient.GetCurrencyAsync"/>.
/// </param>
/// <param name="Impressions">How many times the campaign's ads were shown.</param>
/// <param name="Clicks">Clicks on them, as Meta counts them — every click, not only link clicks.</param>
/// <param name="Results">
/// What Meta counts as the objective's result — leads, for a lead campaign. Read out of the actions
/// array rather than a field of its own, because Meta reports every action type in one list and the
/// one that matters depends on what the campaign was optimising for.
/// </param>
public sealed record MetaAdInsight(
    string CampaignId,
    string CampaignName,
    DateOnly Date,
    decimal Spend,
    long Impressions,
    long Clicks,
    long Results);
