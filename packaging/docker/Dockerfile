# The Kzg bot as a container, for a Linux host whose glibc is older than 2.38 (Debian 12, Ubuntu 22.04):
# libdave needs glibc 2.38 or newer, and Ubuntu 24.04 ("noble") has 2.39. Both stages are noble.
#
# Build from the repository root:
#   docker build -f packaging/docker/Dockerfile -t kzg-bot .
# Run it (docs/guide/hosting.md has the full command and a compose file). --memory is the safety net: the bot
# holds about 150 MB in a full war, and a container that passes 512 MB is killed and, by --restart, started again.
#   docker run -d --name kzg-bot --restart unless-stopped --memory 512m --memory-swap 512m \
#       --log-opt max-size=10m --log-opt max-file=3 -p 127.0.0.1:7878:7878 -v kzg-data:/data kzg-bot
#
# Builds linux-x64 on an x64 builder and linux-arm64 on an ARM one; --platform picks.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /repo
COPY global.json Directory.Build.props ./
COPY src ./src
COPY packaging ./packaging
# The same script that builds the release archive: one place knows which files the bot needs.
RUN case "$TARGETARCH" in \
        amd64) rid=linux-x64 ;; \
        arm64) rid=linux-arm64 ;; \
        *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && packaging/publish-bot.sh --rid "$rid" --out /out --no-archive \
    && mv /out/kzg-bot-* /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
COPY --from=build /app /app
# The data folder holds config.json, secrets.json and devices.json; mount a volume here.
RUN mkdir /data && chown app:app /data
VOLUME /data
USER app
WORKDIR /app
# The base image sets a default port; the bot listens on the one it is told.
ENV ASPNETCORE_HTTP_PORTS=
EXPOSE 7878
# The container's own address, not 127.0.0.1: publish the port to the host's loopback, not to the world.
ENTRYPOINT ["/app/Kzg.Bot"]
CMD ["--data-dir", "/data", "--listen", "0.0.0.0:7878"]
# /healthz answers 200 when the bot is up. runtime-deps has no curl, so bash opens the socket itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/7878 && printf 'GET /healthz HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && grep -q ' 200 ' <&3"]
