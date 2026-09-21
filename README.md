# poc-wasabi-coordinator-dos

Unauthenticated CPU-exhaustion PoC against the WalletWasabi coordinator's request
**deserialization** layer. This is independent of the KVAC issuer; it is a missing
bound in the coordinator's custom JSON decoders and almost certainly predates the
native-issuer migration.

## The bug

The coordinator decodes request bodies with a custom JSON layer
(`WalletWasabi/Serialization`). Two problems combine:

1. **Uncapped collections.** The generic array decoder `Array<T>` (`Primitives.cs:154`) and
   `GroupElementVector` (`WabiSabi.cs:77`) read `EnumerateArray()` into a list with **no
   length limit**. `Presented` / `Requested` / `Proofs` and their nested `PublicNonces` /
   `BitCommitments` arrays are all unbounded.
2. **Eager per-element EC work.** Every group element is decoded via
   `GroupElement.FromBytes`, which performs an **on-curve check (a field square root)** for
   each element — measured at ~9 µs/element.

The decode runs in the `WasabiJsonInputFormatter` during model binding, i.e. **before** the
controller action and before any `AliceId` / round / ownership / UTXO validation in the
`Arena`. There is no `[Authorize]`, no rate-limit middleware, and no in-app body-size cap;
the effective limit is Kestrel's default (~30 MB).

## Result (measured end-to-end against the real decoder)

`DosPoc` drives the exact method the formatter calls,
`Decode.CoordinatorMessageFromStreamAsync`, with a crafted `ConnectionConfirmationRequest`
whose `RealAmountCredentialRequests.Proofs[0].PublicNonces` is oversized:

```
  PublicNonces=   10000  body= 0.7 MB  decode=0.12 s  materialized=True
  PublicNonces=  100000  body= 6.6 MB  decode=1.06 s  materialized=True
  PublicNonces=  300000  body=19.7 MB  decode=2.87 s  materialized=True
  PublicNonces=  449000  body=29.5 MB  decode=4.24 s  materialized=True
```

`materialized=True` means the full request object is built and handed toward the
(pre-auth) controller. **~4 s of single-core CPU per ~30 MB request, unauthenticated.**
The attacker needs no valid credentials or UTXO — invalid points cost the same on-curve
attempt, so garbage payloads work equally well.

## Impact / honest scope

- Sustained concurrent requests saturate coordinator cores → clients cannot register /
  confirm → rounds stall. A resource-exhaustion DoS.
- **Amplification is roughly linear** (~30 MB in ≈ 4 CPU-s), bounded by the 30 MB body cap,
  so it needs real bandwidth (e.g. a botnet) to sustain — it is not a small-packet amplifier.
- A production coordinator behind a reverse proxy with body-size / rate limits, or with a
  tight request timeout, may blunt this. In the application code as written there is no such
  cap. This PoC exercises the decoder directly, not a live HTTP server.

## Run

```
./setup.sh
dotnet run --project DosPoc -c Release
```
Requires the .NET 10 SDK.

## Fix

Cap collection lengths in the decoders to protocol maxima (presentations / requested =
`ProtocolConstants.CredentialNumber`, bit commitments ≤ range-proof width, proofs ≤ the
statement count, public nonces ≤ equations), and/or set a small `MaxRequestBodySize` on the
WabiSabi endpoints. Reject before materializing the whole array.
