# Trinetra Federation API — container image.
#
# Production also runs on bare metal with systemd (deploy/systemd/, docs/DEPLOYMENT.md); this
# image is an equivalent target, not a replacement.
#
#   docker build -t trinetra-api .
#   cp deploy/docker/api.env.example api.env      # then edit
#   docker run --rm -p 5261:8080 --env-file api.env trinetra-api
#
# CONFIG. Every setting in config/trinetra.settings.json is overridable by an environment
# variable — deploy/docker/api.env.example is the full reference (connection string, JWT/auth,
# retention, CORS, proxy, listen port). The image bakes only the committed non-secret template;
# real secrets come from the environment.
#
# DATABASE. The container never migrates. Point ConnectionStrings__Federation at a database that
# already has the schema (db/full-schema.sql fresh, or db/versions/*.sql in order). On start the
# API only verifies it can reach that schema and inserts the bootstrap admin row if absent.
#
# DOCS. The root path redirects to /scalar (the interactive API reference, served in every
# environment). Every route there is still authenticated.

# ---------------------------------------------------------------------------
# build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from just the project manifests, so this layer is cached until a dependency
# actually changes rather than on every source edit.
COPY Directory.Build.props Directory.Packages.props BannedSymbols.txt ./
COPY src/Trinetra.Federation.Core/*.csproj      src/Trinetra.Federation.Core/
COPY src/Trinetra.Federation.Bus/*.csproj       src/Trinetra.Federation.Bus/
COPY src/Trinetra.Federation.Storage/*.csproj   src/Trinetra.Federation.Storage/
COPY src/Trinetra.Federation.Adapters/*.csproj  src/Trinetra.Federation.Adapters/
COPY src/Trinetra.Federation.Runtime/*.csproj   src/Trinetra.Federation.Runtime/
COPY src/Trinetra.Federation.Api/*.csproj       src/Trinetra.Federation.Api/
RUN dotnet restore src/Trinetra.Federation.Api/Trinetra.Federation.Api.csproj

# The API project links config/trinetra.settings.json as Content, so publish fails without it.
# Bake the committed template; it carries CHANGE_ME for every secret.
COPY config/trinetra.settings.example.json config/trinetra.settings.json

COPY src/ src/
RUN dotnet publish src/Trinetra.Federation.Api/Trinetra.Federation.Api.csproj \
      -c Release -o /app --no-restore /p:UseAppHost=false

# ---------------------------------------------------------------------------
# runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only for the container HEALTHCHECK below. Nothing else is added.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# This process holds the credential encryption key. Run it unprivileged — the aspnet image
# already defines the non-root 'app' user.
USER app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# /health is anonymous. start-period covers the schema check + admin seed on a cold database.
HEALTHCHECK --interval=15s --timeout=3s --start-period=45s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Trinetra.Federation.Api.dll"]
