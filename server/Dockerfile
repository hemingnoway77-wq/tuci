FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY Tuci.Sync.csproj packages.lock.json ./
RUN dotnet restore --locked-mode
COPY *.cs ./
RUN dotnet publish Tuci.Sync.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /data && chown app:app /data && chmod 700 /data
ENV ASPNETCORE_HTTP_PORTS=8080 TUCI_DB=/data/tuci.sqlite3
USER app
ENTRYPOINT ["dotnet","Tuci.Sync.dll"]
