# FlowDeck API.
#
# Built from the **repository root**, not from src/backend:
#
#     docker build -f src/backend/Dockerfile .
#
# global.json pins the SDK and Directory.Build.props carries
# TreatWarningsAsErrors and the SonarAnalyzer reference. Both live at the root,
# so a build scoped to src/backend would quietly compile with a different SDK
# and none of the analysis this repository relies on - and would still succeed.

# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Restore before the sources are copied, so editing a .cs file does not
# invalidate the layer that downloads every package.
COPY global.json Directory.Build.props ./
COPY src/backend/FlowDeck.Api/FlowDeck.Api.csproj src/backend/FlowDeck.Api/
COPY src/backend/FlowDeck.Core/FlowDeck.Core.csproj src/backend/FlowDeck.Core/
COPY src/backend/FlowDeck.Persistence.EntityFrameworkCore/FlowDeck.Persistence.EntityFrameworkCore.csproj \
     src/backend/FlowDeck.Persistence.EntityFrameworkCore/

RUN dotnet restore src/backend/FlowDeck.Api/FlowDeck.Api.csproj

COPY src/backend/ src/backend/

# No --no-restore: the sources arriving may reference a package the csproj-only
# restore above did not see. Restore is incremental and nearly free when the
# layer above is warm.
RUN dotnet publish src/backend/FlowDeck.Api/FlowDeck.Api.csproj \
        --configuration Release \
        --output /app \
        /p:UseAppHost=false

# -------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# curl, solely so the container can report its own health.
#
# This was left out at first, on the reasoning that shipping a package for the
# orchestrator's benefit was not worth it, and the health check used a bash
# /dev/tcp redirection instead. The image's /bin/sh is dash, which has no
# /dev/tcp - so the check failed every time and the container reported
# unhealthy while serving traffic perfectly well.
#
# A health check that cannot run is worse than the ~1 MB it saves.
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Runs as the image's non-root user. A workflow engine executes author-written
# step code, so the blast radius of a bug in that code is worth keeping small.
USER $APP_UID

# 8080 rather than 80, which is what the .NET base images default to for a
# non-root user - binding 80 needs a capability this user does not have.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app .

ENTRYPOINT ["dotnet", "FlowDeck.Api.dll"]
