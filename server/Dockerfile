FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY Directory.Build.props ./
COPY server/Tuci.Sync.csproj server/packages.lock.json ./server/
COPY src/VocabCollector.Core/VocabCollector.Core.csproj src/VocabCollector.Core/packages.lock.json ./src/VocabCollector.Core/
RUN dotnet restore server/Tuci.Sync.csproj --locked-mode
COPY server/ ./server/
COPY src/VocabCollector.Core/ ./src/VocabCollector.Core/
RUN dotnet publish server/Tuci.Sync.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /data && chown app:app /data && chmod 700 /data
ENV ASPNETCORE_HTTP_PORTS=8080 TUCI_DB=/data/tuci.sqlite3
USER app
ENTRYPOINT ["dotnet","Tuci.Sync.dll"]
