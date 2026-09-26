# SpMS API image. Build from the repository root (the host embeds the schema,
# the dev seed and the OpenFGA model from database/ and authorization/):
#
#   docker build -f backend/Dockerfile -t spms-api:<tag> .
#
# The same image runs the API, `--migrate` (the pipeline's schema step) and
# `--maintain` (the scheduled partition/outbox housekeeping).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/NuGet.config backend/Directory.Build.props backend/Spms.sln backend/dotnet-tools.json backend/
COPY backend/src backend/src
COPY database database
COPY authorization authorization
RUN dotnet restore backend/src/Spms.Host/Spms.Host.csproj --configfile backend/NuGet.config
RUN dotnet publish backend/src/Spms.Host/Spms.Host.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Spms.Host.dll"]
