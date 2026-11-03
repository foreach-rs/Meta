# Changelog

Semantic versioning. While the major is 0, a change to the public surface bumps the minor.

## 0.1.0 — unreleased

First release as a standalone library, behaviour-preserving.

- `MetaResult<T>`, `MetaError` with the failure table (`Configuration`, `Transient`,
  `CallerMistake`, `PolicyRefused`), `MetaChannel`, and the three credentials classes.
- Webhooks: `MetaWebhookSignature` (HMAC and verify token, both constant-time),
  `MetaWebhookEnvelope.Classify` — which tells a lead submission from a message, a distinction
  whose absence silently drops every Instant Form lead.
- Parsers: `MessengerWebhookParser`, `WhatsAppWebhookParser` (messages and delivery receipts),
  `WhatsAppTemplateReader`. All return the library's own models, unclipped — column widths belong
  to whoever owns the table.
- Clients: `MessengerClient` (Messenger and Instagram), `WhatsAppCloudClient` (text, template,
  template list), `LeadAdsClient` (one lead, a form's leads since a moment, the Page's forms, and
  subscribing the app to `leadgen`).
- Lead Ads: `LeadGenWebhookParser` and the `MetaLead` / `MetaLeadFormInfo` models.
