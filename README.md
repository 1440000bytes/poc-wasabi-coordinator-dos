# poc-wasabi-coordinator-dos

Unauthenticated CPU-exhaustion in the WalletWasabi coordinator, caused by **unbounded
pre-authentication collection processing** in its request deserialization.

## The bug

The coordinator decodes request bodies with a custom JSON layer (`WalletWasabi/Serialization`).
Two properties combine:

1. **Uncapped collections.** The generic array decoder `Array<T>` (`Primitives.cs:154`) and
   `GroupElementVector` (`WabiSabi.cs:77`) read `EnumerateArray()` into a list with **no length
   limit**. `Presented` / `Requested` / `Proofs` and their nested `PublicNonces` /
   `BitCommitments` arrays are all unbounded.
2. **Eager per-element EC work.** Each group element is decoded with `GroupElement.FromBytes`,
   which performs an **on-curve check (a field square root)** — measured ~9 µs/element.

Decoding runs in `WasabiJsonInputFormatter` during model binding, i.e. **before** the
controller action and before any `AliceId` / round / ownership / UTXO validation in `Arena`
(e.g. `ConfirmConnectionAsync`, `RegisterInputAsync`).

## Measured (against the coordinator's real decoder)

`DosPoc` drives the exact method the formatter calls, `Decode.CoordinatorMessageFromStreamAsync`,
with a crafted `ConnectionConfirmationRequest` whose `RealAmountCredentialRequests.Proofs[0].PublicNonces`
is oversized:

```
  PublicNonces=  10000  body= 0.7 MB  decode=0.12 s  materialized=True
  PublicNonces= 100000  body= 6.6 MB  decode=1.06 s  materialized=True
  PublicNonces= 300000  body=19.7 MB  decode=2.87 s  materialized=True
  PublicNonces= 449000  body=29.5 MB  decode=4.24 s  materialized=True   (~30 MB Kestrel default cap)
```

`materialized=True` — the full request object is built and handed toward the (pre-auth)
controller. **~4 s of single-core CPU per ~30 MB request, unauthenticated.** CPU scales
linearly with the attacker-controlled collection size. Invalid points cost the same on-curve
attempt, so no valid credentials/UTXO are needed.

## Severity assessment

| Question | Answer (evidence) |
|---|---|
| Remotely reachable without auth? | **Yes.** No `[Authorize]`, no `UseAuthentication/Authorization` on the coordinator; decode happens in the input formatter before the action. |
| Rate limiting? | **None** found in the coordinator (`Startup.cs`) or the app. |
| Concurrent requests? | **Yes** — ASP.NET Core serves requests in parallel; the PoC's concurrency demo shows simultaneous decodes running on separate cores. |
| CPU linear in collection size? | **Yes** — measured 0.7→29.5 MB scales 0.12→4.24 s. |
| Max HTTP body? | Kestrel **default ~30 MB** (no `MaxRequestBodySize` override), so ~4 s CPU is the per-request ceiling. |
| One attacker exhaust all cores? | **Yes, given bandwidth.** Each request pins a thread for ~4 s of CPU; N concurrent connections pin N cores. |
| Prevents rounds progressing / serving participants? | **Plausible, reasoned not proven.** Saturating the coordinator's cores delays or drops legitimate registration/confirmation requests; WabiSabi phases have timeouts, so sustained saturation stalls or fails rounds. Not demonstrated end-to-end here. |
| Sustainable cheaply over the network? | **Partly.** Cost is ~linear (~30 MB in ≈ 4 CPU-s ≈ ~60 Mbps to keep one core busy). Not a small-packet amplifier — sustaining broad saturation needs real bandwidth (e.g. a botnet). |

**Existing mitigations don't help:** there is a 5 s `RequestTimeout` policy, but a 30 MB body
decodes in ~4.2 s (< 5 s, never triggered), and the synchronous decode loop observes no
cancellation token, so the CPU is spent regardless.

**Severity: High is defensible** — a publicly reachable, unauthenticated client can repeatedly
consume multiple CPU-seconds per request with no rate limit, degrading coordinator availability
and round progression. **Not Critical:** the impact is availability only — no funds compromise,
no authentication bypass, no other security-boundary crossing. A production coordinator behind
a reverse proxy with body-size / rate limits would blunt it; the application code as written has
no such cap.

## Run

```
./setup.sh                          # clones WalletWasabi @ the pinned commit as a sibling
dotnet run --project DosPoc -c Release
```
Requires the .NET 10 SDK.

## Fix

Cap collection lengths in the decoders to protocol maxima (presentations / requested =
`ProtocolConstants.CredentialNumber`, bit commitments ≤ range-proof width, proofs ≤ statement
count, public nonces ≤ equations) and reject before materializing the array; and/or set a small
`MaxRequestBodySize` on the WabiSabi endpoints.

## Scope

This is a coordinator JSON-deserialization issue, independent of the KVAC issuer and almost
certainly predating the native-issuer migration. The PoC exercises the decoder directly (the
pre-auth code path and its CPU cost), not a live HTTP server; a live deployment's reverse-proxy /
rate-limit configuration determines real-world reachability.
