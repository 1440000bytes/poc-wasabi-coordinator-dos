#!/usr/bin/env bash
# Clones the WalletWasabi coordinator source the PoC builds against, pinned to the
# commit these measurements were taken on, and installs the end-to-end test.
set -euo pipefail
# Defaults build against the vulnerable master tip these measurements were taken on. To verify
# the fix, point at the PR head instead (see the README "Verifying the fix" section):
#   REPO=https://github.com/1440000bytes/WalletWasabi REF=fix/unbounded-credential-collection-dos ./setup.sh
REPO="${REPO:-https://github.com/WalletWasabi/WalletWasabi}"
REF="${REF:-13e2a5691be0c34f587f4c73300fa17dbe978999}"   # master tip at time of writing
if [ ! -d WalletWasabi ]; then
  git clone "$REPO" WalletWasabi
fi
( cd WalletWasabi && git remote set-url origin "$REPO" && git fetch -q origin && git checkout -q "$REF" )

# Install the integration tests into the coordinator's test suite:
#   Evidence2KestrelTests.cs   - the real-Kestrel denial-of-service demonstration (Evidence 2)
#   WabiSabiDosPoCTests.cs      - the per-request-cost regression guard
cp tests/Evidence2KestrelTests.cs \
   WalletWasabi/WalletWasabi.Tests/UnitTests/WabiSabi/Integration/Evidence2KestrelTests.cs
cp tests/WabiSabiDosPoCTests.cs \
   WalletWasabi/WalletWasabi.Tests/UnitTests/WabiSabi/Integration/WabiSabiDosPoCTests.cs

# The coordinator's Startup loads a Config.json; create one from the shipped default if absent.
CFG="$HOME/.walletwasabi/coordinator"
mkdir -p "$CFG"
[ -f "$CFG/Config.json" ] || [ ! -f "$CFG/Config.json.default" ] || cp "$CFG/Config.json.default" "$CFG/Config.json"

echo "setup done."
echo "Build:  dotnet build WalletWasabi/WalletWasabi.Tests/WalletWasabi.Tests.csproj -c Release"
echo "Run the Console app (Evidence 1, per-request CPU cost):  dotnet run --project DosPoc -c Release"
echo "Run the integration tests:"
echo "  cd WalletWasabi/WalletWasabi.Tests/bin/Release/net10.0"
echo "  ./WalletWasabi.Tests --filter-display-name '*RealKestrelStarvation*'       # Evidence 2: honest requests denied on real Kestrel"
echo "  ./WalletWasabi.Tests --filter-display-name '*DosPoC_FixNeutralizesFlood*'  # (build against the PR head) the fix neutralizes the flood"
