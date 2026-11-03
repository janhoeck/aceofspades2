# Docker-Image des Backends. Bauen (im Repo-Hauptordner):
#   docker build -f deploy/backend.Dockerfile -t aos2-backend .
# Die CI baut es bei jedem Release und legt es unter ghcr.io/<owner>/aos2-backend ab (siehe docs/backend.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Erst nur die Projektdateien: Solange sie sich nicht ändern, nimmt Docker die Pakete aus dem Cache.
COPY global.json Directory.Build.props ./
COPY libs/AoS2.Contracts/AoS2.Contracts.csproj libs/AoS2.Contracts/
COPY libs/AoS2.JoinTickets/AoS2.JoinTickets.csproj libs/AoS2.JoinTickets/
COPY libs/AoS2.Shared/AoS2.Shared.csproj libs/AoS2.Shared/
COPY backend/AoS2.Backend/AoS2.Backend.csproj backend/AoS2.Backend/
RUN dotnet restore backend/AoS2.Backend/AoS2.Backend.csproj

COPY libs/AoS2.Contracts/ libs/AoS2.Contracts/
COPY libs/AoS2.JoinTickets/ libs/AoS2.JoinTickets/
# Für den Server-Browser: Format der UDP-Abfrage (Erreichbarkeitsprüfung der Spielserver).
COPY libs/AoS2.Shared/ libs/AoS2.Shared/
COPY backend/AoS2.Backend/ backend/AoS2.Backend/
ARG VERSION=0.0.0
RUN dotnet publish backend/AoS2.Backend/AoS2.Backend.csproj --configuration Release --no-restore \
    --output /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Ohne Root-Rechte laufen. Der Benutzer "app" ist im Microsoft-Image schon angelegt.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Im Image gibt es weder curl noch wget; das Backend prüft sich selbst.
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
    CMD ["dotnet", "AoS2.Backend.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "AoS2.Backend.dll"]
