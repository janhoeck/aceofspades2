# Docker-Image des Spielservers (für Self-Hoster und die offiziellen Server auf Coolify). Bauen (im Repo-Hauptordner,
# mit Git LFS ausgecheckt, sonst fehlt die Map):
#   docker build -f deploy/server.Dockerfile -t aos2-server .
# Starten (Config aus ./config/server.json, Vorlage: server/AoS2.Server/server.example.json):
#   docker run -d --name aos2-server -p 32887:32887/udp -v ./config:/config ghcr.io/janhoeck/aos2-server
# Die CI legt es bei jedem Release für amd64 und arm64 unter ghcr.io/<owner>/aos2-server ab (siehe docs/self-hosting.md).

# Gebaut wird immer auf der Plattform des Build-Rechners; dotnet publish übersetzt für die Zielplattform (-r linux-arm64 …).
# So braucht der arm64-Build keine Emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Erst nur die Projektdateien: Solange sie sich nicht ändern, nimmt Docker die Pakete aus dem Cache.
COPY global.json Directory.Build.props ./
COPY libs/AoS2.Shared/AoS2.Shared.csproj libs/AoS2.Shared/
COPY libs/AoS2.Contracts/AoS2.Contracts.csproj libs/AoS2.Contracts/
COPY libs/AoS2.JoinTickets/AoS2.JoinTickets.csproj libs/AoS2.JoinTickets/
COPY server/AoS2.Server/AoS2.Server.csproj server/AoS2.Server/
# Docker-Architektur → .NET-Runtime-ID (amd64 → linux-x64, arm64 → linux-arm64).
RUN case "${TARGETARCH:-amd64}" in amd64) echo linux-x64 ;; *) echo "linux-${TARGETARCH}" ;; esac > /rid \
    && dotnet restore server/AoS2.Server/AoS2.Server.csproj -r "$(cat /rid)"

COPY libs/AoS2.Shared/ libs/AoS2.Shared/
COPY libs/AoS2.Contracts/ libs/AoS2.Contracts/
COPY libs/AoS2.JoinTickets/ libs/AoS2.JoinTickets/
COPY server/AoS2.Server/ server/AoS2.Server/
COPY data/ data/
COPY maps/*.aos2map maps/
# Maps liegen in Git LFS. Ohne LFS stehen hier nur kleine Verweis-Dateien, und der Server startet nicht.
RUN for f in maps/*.aos2map; do head -c 7 "$f" | grep -q AOS2MAP \
    || { echo "$f ist nur ein Git-LFS-Verweis: vorher 'git lfs pull' (bzw. in der CI checkout mit lfs: true)."; exit 1; }; done
ARG VERSION=0.0.0
# Self-contained: Das Image braucht keine .NET-Laufzeit, nur die Grundbibliotheken (runtime-deps).
RUN dotnet publish server/AoS2.Server/AoS2.Server.csproj --configuration Release --no-restore \
    -r "$(cat /rid)" --self-contained --output /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /app .

# Hierhin kommt die eigene server.json (docker run -v ./config:/config …).
VOLUME /config

# Ohne Root-Rechte laufen. Der Benutzer "app" ist im Microsoft-Image schon angelegt.
USER $APP_UID
EXPOSE 32887/udp

ENTRYPOINT ["/app/AoS2.Server"]
CMD ["--config", "/config/server.json"]
