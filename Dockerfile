# syntax=docker/dockerfile:1
ARG DOTNET_SDK_VERSION=8.0.425-bookworm-slim
ARG DOTNET_RUNTIME_VERSION=8.0.31-bookworm-slim

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /src
COPY MasterServer.csproj nuget.config ./
RUN dotnet restore MasterServer.csproj
COPY . .
RUN dotnet publish MasterServer.csproj -c Release --no-restore -o /out/publish \
    && rm -f /out/publish/appsettings*.json
RUN dotnet tool install --tool-path /tools dotnet-ef --version 8.0.0 \
    && ConnectionStrings__DefaultConnection='Host=127.0.0.1;Database=build_only;Username=build_only;Password=build_only' \
       /tools/dotnet-ef migrations bundle --project MasterServer.csproj \
       --configuration Release --self-contained --runtime linux-x64 \
       --output /out/efbundle --force

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION} AS master
ARG DOTNET_RUNTIME_VERSION
ARG SOURCE_REVISION=unknown
ARG RELEASE_ID=unknown
LABEL org.opencontainers.image.revision=${SOURCE_REVISION} \
      org.opencontainers.image.version=${RELEASE_ID} \
      org.opencontainers.image.base.name=mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION}
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /out/publish/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "MasterServer.dll"]

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_RUNTIME_VERSION} AS migrations
ARG DOTNET_RUNTIME_VERSION
ARG SOURCE_REVISION=unknown
ARG RELEASE_ID=unknown
LABEL org.opencontainers.image.revision=${SOURCE_REVISION} \
      org.opencontainers.image.version=${RELEASE_ID} \
      org.opencontainers.image.base.name=mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_RUNTIME_VERSION}
WORKDIR /app
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/bundle
COPY --from=build /out/efbundle ./efbundle
USER 1654:1654
ENTRYPOINT ["/app/efbundle"]
