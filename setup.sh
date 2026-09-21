#!/usr/bin/env bash
# Clones the WalletWasabi coordinator source the PoC builds against, pinned to the
# commit these measurements were taken on, and installs the end-to-end test.
set -euo pipefail
PIN=13e2a5691be0c34f587f4c73300fa17dbe978999   # master tip at time of writing
if [ ! -d WalletWasabi ]; then
  git clone https://github.com/WalletWasabi/WalletWasabi WalletWasabi
fi
( cd WalletWasabi && git checkout -q "$PIN" )

# Install the end-to-end test (Level 2) into the coordinator's integration test suite.
cp tests/WabiSabiDosPoCTests.cs \
   WalletWasabi/WalletWasabi.Tests/UnitTests/WabiSabi/Integration/WabiSabiDosPoCTests.cs

# The coordinator's Startup loads a Config.json; create one from the shipped default if absent.
CFG="$HOME/.walletwasabi/coordinator"
mkdir -p "$CFG"
[ -f "$CFG/Config.json" ] || [ ! -f "$CFG/Config.json.default" ] || cp "$CFG/Config.json.default" "$CFG/Config.json"

echo "setup done."
echo "Build:  dotnet build WalletWasabi/WalletWasabi.Tests/WalletWasabi.Tests.csproj -c Release"
echo "Run the Console app (deserialization CPU cost):  dotnet run --project DosPoc -c Release"
echo "Run the Level-2 tests INDIVIDUALLY (a per-process global logger forbids running both in one process):"
echo "  cd WalletWasabi/WalletWasabi.Tests/bin/Release/net10.0"
echo "  ./WalletWasabi.Tests --filter-display-name '*DosPoC_CoordinatorLatency*'   # honest /status latency under flood"
echo "  ./WalletWasabi.Tests --filter-display-name '*DosPoC_CoinJoinRound*'        # a real round fails under flood"
