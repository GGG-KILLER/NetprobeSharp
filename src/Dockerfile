# syntax=docker/dockerfile:1
#
# Multi-arch build. The Alpine base images are published only for these three
# platforms, which also match what .NET 10 supports on musl (x64/arm64/arm32):
#   docker buildx build --platform linux/amd64,linux/arm64,linux/arm/v7 .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /source

# Copy project file and restore as distinct layers
COPY --link *.csproj .
RUN dotnet restore -a $TARGETARCH

# Copy source code and publish app
COPY --link . .
RUN dotnet publish -a $TARGETARCH --no-restore -o /app


# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app

# iputils' ping emits the "rtt min/avg/max/mdev = ..." summary that PingProber
# parses; BusyBox's ping omits mdev and would break RTT/jitter parsing.
RUN apk add --no-cache iputils

COPY --link --from=build /app .
USER $APP_UID

# OCI image metadata. version/revision are build-time inputs so CI can stamp them, e.g.
#   docker buildx build --build-arg VERSION=0.2.0 --build-arg REVISION=$(git rev-parse HEAD) .
# Declared late so changing them doesn't invalidate the layers above.
ARG VERSION=dev
ARG REVISION=
LABEL org.opencontainers.image.title="NetprobeSharp" \
      org.opencontainers.image.description="Internet connection health monitor: pings sites and times DNS resolvers, exposing the results and a health score as Prometheus metrics." \
      org.opencontainers.image.source="https://github.com/GGG-KILLER/NetprobeSharp" \
      org.opencontainers.image.url="https://github.com/GGG-KILLER/NetprobeSharp" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.licenses="MIT"

ENTRYPOINT ["./NetprobeSharp"]
