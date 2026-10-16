# SignDesk

A small document-signing backend on ASP.NET Core 10 and PostgreSQL: owners create a document, add signers, send it out; signers sign in a fixed order or decline; an external signing provider can report signatures through a signed webhook; notifications go out through a transactional outbox; unsigned documents expire on their own.

It is a pet project for practising the parts of .NET that matter in a real product backend: the domain model, EF Core against PostgreSQL, background work, idempotency and tests that run against a real database.

## What is inside

| Area | How it is done |
|---|---|
| API | Minimal APIs with route groups, built-in .NET 10 validation (DataAnnotations), ProblemDetails for every error, OpenAPI at `/openapi/v1.json`, health check at `/health` |
| Domain | `Document` aggregate owns its signers and every state transition (Draft, Sent, Completed, Declined, Expired); invalid moves throw `DomainException` with a stable error code, mapped to 409 |
| Persistence | EF Core 10 + Npgsql, snake_case naming, migrations, backing-field navigation, `xmin` as an optimistic concurrency token on documents and signers |
| Audit | Every domain event is written to `audit_entries` in the same transaction as the change |
| Notifications | Transactional outbox: rows are written with the change, `OutboxDispatcher` (`BackgroundService`) claims batches with `FOR UPDATE SKIP LOCKED`, retries with exponential backoff and dead-letters after 5 attempts. A `Channel<T>` wakes the dispatcher right after a commit instead of waiting for the next poll |
| Expiry | `ExpirationSweeper` runs on a `PeriodicTimer`; signing is blocked the moment the window closes, even before the sweeper runs |
| Webhooks | Stripe-style `t=...,v1=...` HMAC-SHA256 signature, constant-time compare, 5 minute replay window, several `v1` values for secret rotation; idempotent by event id (unique index plus a check), so duplicate and parallel deliveries apply once |
| Time | `TimeProvider` everywhere, `FakeTimeProvider` in tests, so expiry and backoff are tested without sleeping |

## Project layout

```
src/
  SignDesk.Domain          entities and rules, no framework dependencies
  SignDesk.Infrastructure  DbContext, migrations, DocumentService, outbox, sweeper, webhook verifier
  SignDesk.Api             endpoints, contracts, error mapping, composition root
tests/
  SignDesk.UnitTests         domain rules, backoff, signature verification (40 tests)
  SignDesk.IntegrationTests  WebApplicationFactory against a real PostgreSQL database (16 tests)
```

## Running locally

Requirements: .NET 10 SDK and PostgreSQL 15+.

```bash
dotnet tool restore
# point ConnectionStrings:SignDesk in src/SignDesk.Api/appsettings.json at your database
dotnet run --project src/SignDesk.Api      # Development applies migrations on startup
```

Without a running PostgreSQL you can start a throwaway local cluster:

```bash
initdb -D .pgdata -U postgres --auth=trust
pg_ctl -D .pgdata -o "-p 54331 -c listen_addresses=localhost" -l .pglog start
```

Docker image: `docker build -t signdesk .`

## Tests

```bash
dotnet test
```

Integration tests create a fresh database per run (`signdesk_test_<guid>`) and drop it afterwards. Connection: `SIGNDESK_TEST_DB`, default `Host=localhost;Port=54331;Username=postgres`. CI runs the same suite against a `postgres:17` service and fails if the model has changes without a migration.

## Example

```bash
curl -s -X POST localhost:5000/documents -H 'content-type: application/json' \
  -d '{"title":"NDA","ownerEmail":"owner@example.com"}'
curl -s -X POST localhost:5000/documents/{id}/signers -H 'content-type: application/json' \
  -d '{"email":"cfo@example.com","name":"CFO"}'
curl -s -X POST localhost:5000/documents/{id}/send -H 'content-type: application/json' -d '{"expiresInDays":7}'
curl -s -X POST localhost:5000/documents/{id}/signers/{signerId}/sign
curl -s localhost:5000/documents/{id}/audit
```

Webhook from the signing provider:

```http
POST /webhooks/signing-provider
SignDesk-Signature: t=1792141200,v1=<hex hmac of "t.body">

{"id":"evt_123","type":"signer.signed","data":{"documentId":"...","signerId":"..."}}
```

## Decisions and trade-offs

- **Outbox instead of sending from the request.** A notification is never lost when the mail provider is down and never sent for a change that rolled back. The cost is eventual delivery, a few seconds at most thanks to the channel signal.
- **Sending inside the claim transaction.** Simple and safe for small batches; with a slow provider the next step would be to mark rows as in-flight and send outside the transaction.
- **Sequential signing only.** Parallel signing is a flag on the document and a different `CurrentSigner` rule; left out to keep the state machine readable.
- **No file storage yet.** The document is metadata; the natural next step is a pre-signed upload URL to S3-compatible storage plus a content hash stored on the document.
- **No authentication yet.** Endpoints trust the caller; the next step is JWT bearer auth with the owner taken from the token instead of the request body.
