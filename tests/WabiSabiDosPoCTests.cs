using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.TransactionOutputs;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Integration;

// Regression guard for WalletWasabi PR #15066, using the in-process TestServer.
//
// The end-to-end denial-of-service proof lives in Evidence2KestrelTests, which needs a real
// Kestrel server; the in-process TestServer used here does not reproduce the honest-request
// starvation. What it does reproduce deterministically is the per-request cost of an oversized
// body, which is the strict discriminator between the vulnerable and fixed coordinators.
//
// Drop this file into WalletWasabi.Tests/UnitTests/WabiSabi/Integration/ and run:
//   ./WalletWasabi.Tests --filter-display-name "*DosPoC*"
[Collection("Serial unit tests collection")]
public class WabiSabiDosPoCTests : IClassFixture<WabiSabiApiApplicationFactory<WalletWasabi.Coordinator.Startup>>
{
	private readonly WabiSabiApiApplicationFactory<WalletWasabi.Coordinator.Startup> _factory;

	public WabiSabiDosPoCTests(WabiSabiApiApplicationFactory<WalletWasabi.Coordinator.Startup> factory)
	{
		_factory = factory;
	}

	// Built against the FIXED coordinator (see the README "Verifying the fix" section) the
	// oversized connection-confirmation is rejected before the per-element curve work. The strict
	// discriminator is the per-request cost: ~60 ms on the fixed build versus ~3 s on the
	// vulnerable one, so the assertion below FAILS on the pinned vulnerable commit, which is the
	// point of a regression guard.
	[Fact]
	public async Task DosPoC_FixNeutralizesFloodAsync()
	{
		var httpClient = _factory.WithWebHostBuilder(b => b.AddMockRpcClient(Array.Empty<SmartCoin>(), _ => { })).CreateClient();

		// (a) The oversized body the flood uses is rejected cheaply. On the fixed coordinator the
		// collection and body caps reject it before the ~3 s of on-curve checks; on the vulnerable
		// one the same request materializes and takes seconds, so latency separates them.
		string oversized = OversizedConnectionConfirmation(150_000); // ~10 MB
		var rejectMs = new List<double>();
		int lastStatus = 0;
		for (int i = 0; i < 5; i++)
		{
			using var c = new StringContent(oversized, Encoding.UTF8, "application/json");
			var sw = System.Diagnostics.Stopwatch.StartNew();
			using var r = await httpClient.PostAsync("WabiSabi/connection-confirmation", c);
			sw.Stop();
			rejectMs.Add(sw.Elapsed.TotalMilliseconds);
			lastStatus = (int)r.StatusCode;
			Assert.False(r.IsSuccessStatusCode); // never accepted
		}
		rejectMs.Sort();
		double rejectMed = rejectMs[rejectMs.Count / 2];
		Console.WriteLine($"[DosPoC-fix] oversized connection-confirmation: status {lastStatus}, median {rejectMed:F0} ms");
		Assert.True(rejectMed < 750); // rejected before the per-element curve work

		// (b) Honest /status stays responsive under the same flood. This is a loose in-process
		// sanity check; the real starvation demonstration is Evidence2KestrelTests.
		async Task<double> StatusMsAsync()
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			using var c = new StringContent("{\"RoundCheckpoints\":[]}", Encoding.UTF8, "application/json");
			using var r = await httpClient.PostAsync("WabiSabi/status", c);
			sw.Stop();
			return sw.Elapsed.TotalMilliseconds;
		}

		using var floodCts = new CancellationTokenSource();
		var flood = FloodAsync(httpClient, floodCts.Token);
		await Task.Delay(TimeSpan.FromSeconds(4));
		var atkS = new List<double>();
		for (int i = 0; i < 8; i++) atkS.Add(await StatusMsAsync());
		floodCts.Cancel();
		try { await flood; } catch { }
		atkS.Sort();
		double atkMed = atkS[atkS.Count / 2];
		Console.WriteLine($"[DosPoC-fix] honest /status under flood: median {atkMed:F0} ms, max {atkS[^1]:F0} ms");
		Assert.True(atkMed < 1000);
	}

	private static async Task FloodAsync(HttpClient client, CancellationToken ct)
	{
		string body = OversizedConnectionConfirmation(150_000);        // ~10 MB, ~3 s CPU/decode
		int workers = Environment.ProcessorCount * 12;
		await Task.WhenAll(Enumerable.Range(0, workers).Select(async _ =>
		{
			while (!ct.IsCancellationRequested)
			{
				try { using var c = new StringContent(body, Encoding.UTF8, "application/json"); using var r = await client.PostAsync("WabiSabi/connection-confirmation", c, ct); }
				catch { }
			}
		}));
	}

	private static string OversizedConnectionConfirmation(int nNonces)
	{
		const string pt = "0279BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798";
		var sb = new StringBuilder(nNonces * 70 + 1024);
		const string emptyZero = "{\"Requested\":[],\"Proofs\":[]}";
		sb.Append("{\"RoundId\":\"0000000000000000000000000000000000000000000000000000000000000000\",");
		sb.Append("\"AliceId\":\"00000000-0000-0000-0000-000000000000\",");
		sb.Append("\"ZeroAmountCredentialRequests\":").Append(emptyZero).Append(',');
		sb.Append("\"RealAmountCredentialRequests\":{\"Delta\":0,\"Presented\":[],\"Requested\":[],\"Proofs\":[{\"PublicNonces\":[");
		for (int i = 0; i < nNonces; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(pt).Append('"'); }
		sb.Append("],\"Responses\":[\"0000000000000000000000000000000000000000000000000000000000000001\"]}]},");
		sb.Append("\"ZeroVsizeCredentialRequests\":").Append(emptyZero).Append(',');
		sb.Append("\"RealVsizeCredentialRequests\":{\"Delta\":0,\"Presented\":[],\"Requested\":[],\"Proofs\":[]}}");
		return sb.ToString();
	}
}
