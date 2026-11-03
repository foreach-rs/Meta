# ForEach.Infrastructure.External.Meta

Everything that talks to Meta — Messenger, Instagram, the WhatsApp Cloud API, Lead Ads and the
webhooks all of them arrive on — in one library with no dependencies, so more than one product can
use it.

## What it does not do

Three things, deliberately, because each of them is the host's:

- **It persists nothing.** No conversation, no message, no inbound event. The library parses and
  calls; where any of it is stored is a decision about somebody's database.
- **It retries nothing.** A failure comes back with `IsWorthRetrying` and the host's job scheduler
  decides. A library that retried on its own would do it inside a request somebody is waiting on.
- **It decides no policy.** Consent, opt-out words, who may reply to whom, which template belongs
  to which feature — all of that is a business rule, and business rules differ per product.

## Credentials

Three classes, because they have three different owners and lifetimes:

| Class | Holds | Whose it is |
|---|---|---|
| `MetaAppCredentials` | app id, app secret, webhook verify token, Graph version | ours |
| `MetaPageCredentials` | page id, page token, Instagram account id | the customer's, revocable by them |
| `WhatsAppCredentials` | phone number id, WABA id, token | the customer's |

Each has `IsConfigured` — whether a call is worth making at all — so a host can run dark until
somebody connects an account rather than failing a job every day.

## Results and errors

```csharp
var result = await client.SendTextAsync(recipientId, "Dobar dan");

if (result.IsSuccess) return result.Value;          // the message id
if (result.IsWorthRetrying) return Retry(result);   // transport, or Meta-side
Notify(result.FailureMessage);                       // Meta's own words, for a person to read
```

`MetaResult<T>` keeps two kinds of failure apart: a `MetaError` Meta itself reported, and a
transport problem that never reached it. A caller deciding whether to retry needs to know that a
timeout is not an expired token.

`MetaError.Kind` sorts Meta's codes into the four responses a host actually has:

| Kind | Examples | What to do |
|---|---|---|
| `Configuration` | 190, 102, 104 | token invalid or expired — tell somebody, stop retrying |
| `PolicyRefused` | 10/2018278, 551, 131047 | outside the 24-hour window, recipient unreachable — show it, never retry |
| `CallerMistake` | 132000–132016, 100 | wrong parameters or missing permission — fix the call |
| `Transient` | 4, 17, 32, 613, 130429, and anything unknown | retry later |

Unknown codes are transient on purpose: a wasted retry costs one scheduled run, and giving up on
something that would have worked costs the message until somebody notices.

## Testing

`ForEach.Infrastructure.External.Meta.UnitTests` covers the parsers and the error table against
recorded payloads; the clients are exercised through a recording `HttpMessageHandler`.
`…IntegrationTests` makes live calls and skips itself unless the `META_*` and `WHATSAPP_*`
environment variables are set, so it never runs in CI by accident.
