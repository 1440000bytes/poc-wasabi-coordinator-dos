# poc-wasabi-coordinator-dos

Unauthenticated CPU-exhaustion in the WalletWasabi coordinator, caused by **unbounded
pre-authentication collection processing** in its request deserialization. Demonstrated two
ways: a direct measurement of the deserialization cost, and an end-to-end test where a real
coinjoin round **fails** under the attack.

## The bug

The coordinator decodes request bodies with a custom JSON layer (`WalletWasabi/Serialization`):

1. **Uncapped collections.** `Array<T>` (`Primitives.cs:154`) and `GroupElementVector`
   (`WabiSabi.cs:77`) read `EnumerateArray()` into a list with **no length limit**.
   `Presented` / `Requested` / `Proofs` and their nested `PublicNonces` / `BitCommitments`
   arrays are all unbounded.
2. **Eager per-element EC work.** Each group element is decoded via `GroupElement.FromBytes`,
   an **on-curve check (field square root)** — ~9 µs/element.

Decoding runs in `WasabiJsonInputFormatter` during model binding, **before** the controller
action and before any `AliceId` / round / ownership / UTXO validation (`RegisterInputAsync`,
`ConfirmConnectionAsync`, …). No `[Authorize]`, no rate limiting, no in-app body cap (Kestrel
default ~30 MB). A 5 s `RequestTimeout` policy does not help: a 30 MB body decodes in ~4.2 s
(< 5 s, never triggered) and the synchronous decode loop observes no cancellation token.

## Evidence 1 — deserialization CPU cost (`DosPoc` console app)

Drives the exact method the formatter calls, `Decode.CoordinatorMessageFromStreamAsync`, on a
crafted `ConnectionConfirmationRequest` with an oversized `PublicNonces`:

```
  PublicNonces=  10000  body= 0.7 MB  decode=0.12 s
  PublicNonces= 100000  body= 6.6 MB  decode=1.06 s
  PublicNonces= 300000  body=19.7 MB  decode=2.87 s
  PublicNonces= 449000  body=29.5 MB  decode=4.24 s   (~30 MB Kestrel default cap)
```

~4 s single-core CPU per ~30 MB request, unauthenticated, linear in the attacker-controlled
collection size. Invalid points cost the same on-curve attempt, so no valid credentials/UTXO
are needed.

## Evidence 2 — end-to-end: a real coinjoin round fails under attack (`tests/`)

`WabiSabiDosPoCTests` runs against the coordinator's real HTTP pipeline (in-process TestServer
+ mock RPC, no bitcoind). Measured on a 4-core box, each test in its own process:

- **`DosPoC_CoordinatorLatencyAsync`** (unconfounded — no coinjoin client, isolates the
  coordinator): honest `/status` latency **baseline median 0 ms → under flood median 541 ms,
  max 6485 ms** (≈541× slowdown). The coordinator can no longer serve honest requests promptly.

- **`DosPoC_CoinJoinRoundAsync`**: a real coinjoin round **succeeds without the attacker
  (`SuccessfulCoinJoinResult`, ~19 s) and FAILS under the flood (`FailedCoinJoinResult`, ~90 s)**.

The flood is 48 concurrent workers POSTing ~10 MB oversized `connection-confirmation` bodies.

## Severity assessment

| Question | Answer (evidence) |
|---|---|
| Remotely reachable without auth? | **Yes** — no `[Authorize]`/auth middleware; decode is in the input formatter before the action. |
| Rate limiting? | **None** found. |
| Concurrent? | **Yes** — parallel on the thread pool. |
| CPU linear in collection size? | **Yes** — 0.7→29.5 MB ⇒ 0.12→4.24 s. |
| Max HTTP body? | Kestrel **default ~30 MB** (no override) ⇒ ~4 s CPU/request ceiling. |
| Exhaust all cores? | **Yes given bandwidth** — each request pins a thread ~4 s; honest `/status` latency measured at up to 6.5 s under flood. |
| Prevents rounds / serving participants? | **Demonstrated** — a real round goes `Successful`→`Failed` under the flood, and honest requests are delayed to seconds. |
| Sustainable cheaply? | **Partly** — ~1:1 (~30 MB ≈ 4 CPU-s ≈ ~60 Mbps to keep one core busy). Not a small-packet amplifier; broad saturation needs real bandwidth (botnet). |

**Severity: High.** A publicly reachable, unauthenticated client with no rate limit can
repeatedly consume multiple CPU-seconds per request, drive honest-request latency to seconds,
and prevent coinjoin rounds from completing. **Not Critical** — impact is availability only; no
funds compromise, no auth bypass, no other security-boundary crossing.

## Honest caveats

- Measured on a **4-core box**; the flood ran **in-process** for the round test. The
  `CoordinatorLatency` test is the clean, unconfounded signal (no coinjoin client competing);
  the round-failure corroborates it. A remote attacker against a larger coordinator needs
  proportional bandwidth (~60 Mbps/core) — it scales linearly, so a botnet reproduces it.
- The tests use the in-process `TestServer`, not a live network socket; a production deployment
  behind a reverse proxy with body-size / rate limits would blunt it. The application code as
  written has no such cap.
- The two Level-2 tests must be run **in separate processes** (the coordinator's global logger
  may be configured only once per process).

## Run

```
./setup.sh
dotnet build WalletWasabi/WalletWasabi.Tests/WalletWasabi.Tests.csproj -c Release
dotnet run --project DosPoc -c Release                                   # Evidence 1
cd WalletWasabi/WalletWasabi.Tests/bin/Release/net10.0
./WalletWasabi.Tests --filter-display-name '*DosPoC_CoordinatorLatency*' # Evidence 2a
./WalletWasabi.Tests --filter-display-name '*DosPoC_CoinJoinRound*'       # Evidence 2b
```
Requires the .NET 10 SDK.

## Fix

Cap collection lengths in the decoders to protocol maxima (presentations / requested =
`ProtocolConstants.CredentialNumber`, bit commitments ≤ range-proof width, proofs ≤ statement
count, public nonces ≤ equations) and reject before materializing the array; and/or set a small
`MaxRequestBodySize` on the WabiSabi endpoints.

This is implemented in WalletWasabi PR #15066
(https://github.com/WalletWasabi/WalletWasabi/pull/15066).

## Related lower-severity vector

The same unbounded-collection pattern reaches the unauthenticated `status` endpoint, whose
`RoundStateRequest.RoundCheckpoints` array had no length limit. Each element is only a hash and
an integer with no elliptic-curve work, so the per-element cost is far below the credential path
above, but it is the same class of issue. PR #15066 caps it too.

## Verifying the fix

To confirm the attack is neutralized, build against the PR head instead of the pinned vulnerable
commit. `setup.sh` takes `REPO` and `REF` overrides:

```
REPO=https://github.com/1440000bytes/WalletWasabi \
REF=fix/unbounded-credential-collection-dos \
./setup.sh
dotnet build WalletWasabi/WalletWasabi.Tests/WalletWasabi.Tests.csproj -c Release
dotnet run --project DosPoc -c Release                                     # prints REJECTED, decode collapses
cd WalletWasabi/WalletWasabi.Tests/bin/Release/net10.0
./WalletWasabi.Tests --filter-display-name '*DosPoC_FixNeutralizesFlood*'  # regression guard
```

On the fixed build the console app reports `REJECTED (fix present)` for every oversized body and
the decode time drops to a plain DOM-parse cost with no per-element curve work. The regression
test `DosPoC_FixNeutralizesFloodAsync` passes: the oversized `connection-confirmation` is rejected
before the curve work and honest `/status` stays responsive under the flood. Both assertions fail
on the pinned vulnerable commit, which is the point.

## Scope

A coordinator JSON-deserialization issue, independent of the KVAC issuer and almost certainly
predating the native-issuer migration.
