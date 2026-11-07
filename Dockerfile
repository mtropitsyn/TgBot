FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/TgBroadcast/TgBroadcast.csproj src/TgBroadcast/
RUN dotnet restore src/TgBroadcast/TgBroadcast.csproj
COPY src/TgBroadcast/ src/TgBroadcast/
RUN dotnet publish src/TgBroadcast/TgBroadcast.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "TgBroadcast.dll"]
