# Run a command (default: make check) inside the dev toolbox container.
# Usage: ./scripts/dev.ps1 make setup check
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
docker build -q -t brightline-dev -f "$root/docker/dev.Dockerfile" "$root/docker" | Out-Null
$cmd = if ($args.Count -gt 0) { $args } else { @('make', 'check') }
docker run --rm `
  -v "${root}:/src" `
  -v brightline-nuget:/root/.nuget/packages `
  -v /var/run/docker.sock:/var/run/docker.sock `
  -e BRIGHTLINE_HOST=host.docker.internal `
  --add-host host.docker.internal:host-gateway `
  -w /src brightline-dev @cmd
exit $LASTEXITCODE
