# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/Brightline.Web/Brightline.Web.csproj
RUN dotnet publish src/Brightline.Web/Brightline.Web.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=build /app ./
COPY sample_data/ ./sample_data/
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Knowledge__SampleDataPath=/app/sample_data/help-center
EXPOSE 8080
# Non-root user provided by the official image (UID 1654)
USER $APP_UID
ENTRYPOINT ["dotnet", "Brightline.Web.dll"]
