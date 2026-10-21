# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src

COPY . .
RUN test "${TARGETARCH}" = "amd64" \
    && dotnet restore src/RfidMiddleware.Host/RfidMiddleware.Host.csproj --runtime linux-x64 \
    && dotnet restore tools/RfidMiddleware.MockServer/RfidMiddleware.MockServer.csproj --runtime linux-x64 \
    && dotnet publish src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /out/middleware \
    && dotnet publish tools/RfidMiddleware.MockServer/RfidMiddleware.MockServer.csproj \
        --configuration Release --runtime linux-x64 --self-contained false --no-restore \
        --output /out/mock-backend

RUN apt-get update \
    && apt-get install --yes --no-install-recommends patchelf \
    && patchelf --set-rpath '$ORIGIN' /out/middleware/native/librfid.so \
    && LD_LIBRARY_PATH=/out/middleware/native ldd /out/middleware/native/librfid.so \
    && ! LD_LIBRARY_PATH=/out/middleware/native ldd /out/middleware/native/librfid.so | grep 'not found' \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS middleware
ARG APP_UID=10001
ARG APP_GID=10001
RUN groupadd --gid "${APP_GID}" rfid-middleware \
    && useradd --uid "${APP_UID}" --gid "${APP_GID}" --no-create-home --shell /usr/sbin/nologin rfid-middleware \
    && install -d -o rfid-middleware -g rfid-middleware -m 0750 /app /run/rfid-middleware /var/lib/rfid-middleware
WORKDIR /app
COPY --from=build --chown=rfid-middleware:rfid-middleware /out/middleware/ ./
COPY --chown=rfid-middleware:rfid-middleware packaging/appsettings.production.json ./appsettings.json
ENV RFID_NATIVE_DIR=/app/native \
    LD_LIBRARY_PATH=/app/native \
    DOTNET_EnableDiagnostics=0
USER rfid-middleware:rfid-middleware
EXPOSE 1969
ENTRYPOINT ["/app/rfid-middleware"]
CMD ["run", "--config", "/app/appsettings.json"]
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=3 \
  CMD ["/app/rfid-middleware", "healthcheck", "--config", "/app/appsettings.json"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS mock-backend
ARG APP_UID=10001
ARG APP_GID=10001
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid "${APP_GID}" rfid-mock \
    && useradd --uid "${APP_UID}" --gid "${APP_GID}" --no-create-home --shell /usr/sbin/nologin rfid-mock \
    && install -d -o rfid-mock -g rfid-mock -m 0750 /app
WORKDIR /app
COPY --from=build --chown=rfid-mock:rfid-mock /out/mock-backend/ ./
ENV URLS=http://0.0.0.0:5080 \
    DOTNET_EnableDiagnostics=0
USER rfid-mock:rfid-mock
EXPOSE 5080
ENTRYPOINT ["/app/RfidMiddleware.MockServer"]
