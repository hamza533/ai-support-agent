# Dev toolbox: the official .NET SDK image plus make and the docker CLI (for Testcontainers).
# Lets any machine with Docker run the same `make` targets as CI.
FROM docker:29-cli AS dockercli

FROM mcr.microsoft.com/dotnet/sdk:11.0.100
RUN apt-get update \
 && apt-get install -y --no-install-recommends make \
 && rm -rf /var/lib/apt/lists/*
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal
WORKDIR /src
