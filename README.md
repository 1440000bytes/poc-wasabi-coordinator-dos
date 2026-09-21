# poc-wasabi-coordinator-dos

Unauthenticated CPU-exhaustion in the WalletWasabi coordinator, caused by **unbounded
pre-authentication collection processing** in its request deserialization. Shown two ways: a
direct measurement of the per-request deserialization cost, and an end-to-end test where honest
requests to a real coordinator are **starved to seconds** under an unauthenticated flood.

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

## Evidence 2 — end-to-end denial of service on a real coordinator (`tests/`)

`Evidence2KestrelTests.RealKestrelStarvationAsync` runs the coordinator on a **real Kestrel
server** bound to a loopback port (not the in-process `TestServer`) with a mock RPC. It floods
the server over real HTTP connections with unauthenticated oversized `connection-confirmation`
bodies, then times an honest `/status` request while the flood runs. Workers scale with core
count so the oversized decodes saturate every core.

Measured on a 4-core box against the vulnerable coordinator:

```
  honest /status: baseline median 2 ms; under flood median 1031 ms, max 12810 ms (workers=32)
```

Honest requests go from about 2 ms to a **median near one second, with a worst case close to 13
seconds**. That is honest users denied timely service, which is what makes this a DoS rather than
just an expensive request.

Why a real server is required: the in-process `TestServer` does not schedule the synchronous
decode work the way Kestrel does, so an in-process flood leaves honest `/status` near 1 ms and
hides the impact. The starvation only appears on real Kestrel.

## Severity assessment

| Question | Answer (evidence) |
|---|---|
| Remotely reachable without auth? | **Yes** — no `[Authorize]`/auth middleware; decode is in the input formatter before the action. |
| Rate limiting? | **None** found. |
| Concurrent? | **Yes** — parallel on the thread pool. |
| CPU linear in collection size? | **Yes** — 0.7→29.5 MB ⇒ 0.12→4.24 s. |
| Max HTTP body? | Kestrel **default ~30 MB** (no override) ⇒ ~4 s CPU/request ceiling. |
| Exhaust all cores? | **Yes given bandwidth** — each request pins a thread ~3 s; workers scale with cores. |
| Prevents serving participants? | **Demonstrated** — on real Kestrel honest `/status` goes from ~2 ms to a ~1 s median and ~13 s worst case under the flood. |
| Sustainable cheaply? | **Partly** — ~1:1 (~30 MB ≈ 4 CPU-s ≈ ~60 Mbps to keep one core busy). Not a small-packet amplifier; broad saturation needs real bandwidth (botnet). |

**Severity: High.** A publicly reachable, unauthenticated client with no rate limit can
repeatedly consume multiple CPU-seconds per request, drive honest-request latency to seconds,
and prevent coinjoin rounds from completing. **Not Critical** — impact is availability only; no
funds compromise, no auth bypass, no other security-boundary crossing.

## Honest caveats

- Measured on a **4-core box**. The flood client runs in the same process as the coordinator, so
  it competes for the same CPU. A remote attacker needs proportional bandwidth (about 60 Mbps per
  core); the cost is linear, so a botnet reproduces it.
- The starvation only appears against a **real Kestrel** server. The in-process `TestServer` does
  not reproduce it (honest `/status` stays near 1 ms), which is why Evidence 2 stands up a real
  server. `WabiSabiDosPoCTests` uses the in-process harness only for the deterministic per-request
  cost, which is the regression guard, not for the starvation.
- A production deployment behind a reverse proxy with body-size or rate limits would blunt it. The
  application code as written has no such cap.

## Run

```
./setup.sh
dotnet build WalletWasabi/WalletWasabi.Tests/WalletWasabi.Tests.csproj -c Release
dotnet run --project DosPoc -c Release                                # Evidence 1: per-request CPU cost
cd WalletWasabi/WalletWasabi.Tests/bin/Release/net10.0
./WalletWasabi.Tests --filter-display-name '*RealKestrelStarvation*'  # Evidence 2: honest requests denied
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
./WalletWasabi.Tests --filter-display-name '*DosPoC_FixNeutralizesFlood*'  # regression guard: passes on fixed
./WalletWasabi.Tests --filter-display-name '*RealKestrelStarvation*'       # Evidence 2: now fails (no starvation)
```

On the fixed build the console app reports `REJECTED (fix present)` for every oversized body and
the decode time drops to a plain DOM-parse cost with no per-element curve work.
`DosPoC_FixNeutralizesFloodAsync` passes: the oversized `connection-confirmation` is rejected
before the curve work. And `RealKestrelStarvationAsync` now FAILS, because the same flood no
longer starves honest requests: honest `/status` stays bounded (median about 120 ms, worst case
about 135 ms) instead of reaching seconds. That failure is the proof the fix works.

## Scope

A coordinator JSON-deserialization issue, independent of the KVAC issuer and almost certainly
predating the native-issuer migration.
