#!/usr/bin/env bash
# Clones the WalletWasabi coordinator source the PoC builds against, pinned to the
# commit these measurements were taken on.
set -euo pipefail
PIN=13e2a5691be0c34f587f4c73300fa17dbe978999   # master tip at time of writing
if [ ! -d WalletWasabi ]; then
  git clone https://github.com/WalletWasabi/WalletWasabi WalletWasabi
fi
( cd WalletWasabi && git checkout -q "$PIN" )
echo "setup done. Now: dotnet run --project DosPoc -c Release"
