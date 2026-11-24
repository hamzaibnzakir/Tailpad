#!/usr/bin/env bash
# Downloads the ViGEmBus controller-driver installer that gets bundled inside TailPad.exe.
set -euo pipefail
cd "$(dirname "$0")/../pc/TailPad.Host"
mkdir -p Driver
curl -fsSL -o Driver/ViGEmBus_Setup.exe \
  https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe
echo "89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a  Driver/ViGEmBus_Setup.exe" | sha256sum -c -
