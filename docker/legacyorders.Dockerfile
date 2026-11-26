# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/Brightline.LegacyOrders/Brightline.LegacyOrders.csproj
RUN dotnet publish src/Brightline.LegacyOrders/Brightline.LegacyOrders.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
# Non-root user provided by the official image (UID 1654)
USER $APP_UID
ENTRYPOINT ["dotnet", "Brightline.LegacyOrders.dll"]
