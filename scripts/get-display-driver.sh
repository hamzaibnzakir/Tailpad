#!/usr/bin/env bash
# Downloads the Virtual Display Driver (MIT, signed via SignPath) that gives TailPad its hidden screen.
set -euo pipefail
cd "$(dirname "$0")/../pc/TailPad.Host"
mkdir -p Driver
curl -fsSL -o Driver/VirtualDisplayDriver.zip \
  https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip
echo "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a  Driver/VirtualDisplayDriver.zip" | sha256sum -c -
