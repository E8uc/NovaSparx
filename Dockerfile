FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY NovaSparx.Backend.csproj ./
RUN dotnet restore ./NovaSparx.Backend.csproj

COPY . .

RUN dotnet publish ./NovaSparx.Backend.csproj \
    -c Release \
    -o /out \
    --no-restore \
    -p:UseAppHost=true


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

COPY --from=build /out/ /app/
COPY start.sh /app/start.sh

RUN sed -i 's/\r$//' /app/start.sh \
    && chmod 755 /app/start.sh \
    && mkdir -p /tmp/novasparx-cache

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_GCServer=0
ENV DOTNET_EnableDiagnostics=0

ENV NOVASPARX_CACHE_DIR=/tmp/novasparx-cache
ENV NOVASPARX_MAX_VERTICES=160000
ENV NOVASPARX_MAX_INDICES=480000
ENV NOVASPARX_PARSE_CONCURRENCY=1
ENV NOVASPARX_TEXTURE_CONCURRENCY=1
ENV NOVASPARX_TEXTURE_MAX_SIZE=2048
ENV NOVASPARX_TEXTURE_MAX_BYTES=12582912
ENV NOVASPARX_ARCHIVE_REGISTER_CONCURRENCY=3
ENV NOVASPARX_ONDEMAND_TIMEOUT_SECONDS=80
ENV NOVASPARX_LOW_MEMORY_MODE=true
ENV NOVASPARX_ENABLE_STUDIO_MANIFEST=false
ENV NOVASPARX_ENABLE_TEXTURE_STREAMING_TOC=false
ENV NOVASPARX_READ_NANITE_DATA=false
ENV NOVASPARX_SKIP_REFERENCED_TEXTURES=true
ENV NOVASPARX_CLIENT_PACKAGE_MAX_BYTES=25165824
ENV NOVASPARX_MESH_CACHE_MINUTES=3
ENV NOVASPARX_PREVIEW_CACHE_MINUTES=3
ENV NOVASPARX_LINK_URL=wss://fortnite-ai-agent-autolink.a39328122.workers.dev/connect

EXPOSE 10000

ENTRYPOINT ["/app/start.sh"]
