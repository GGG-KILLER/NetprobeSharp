# syntax=docker/dockerfile:1
#
# Multi-arch build. The Alpine base images are published only for these three
# platforms, which also match what .NET 10 supports on musl (x64/arm64/arm32):
#   docker buildx build --platform linux/amd64,linux/arm64,linux/arm/v7 .

# Sysroot stages: pulled from the registry as foreign-arch images by buildx —
# no QEMU execution, just layer fetching. gcc + musl-dev supply the CRT files
# (crtbeginS.o, crtendS.o) and libgcc that NativeAOT's linker needs for the
# target arch. amd64 uses the host system directly and needs no sysroot.
FROM --platform=linux/arm64  alpine:3 AS sysroot-arm64
RUN apk add --no-cache musl-dev gcc

FROM --platform=linux/arm/v7 alpine:3 AS sysroot-arm
RUN apk add --no-cache musl-dev gcc

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine-aot AS build
ARG TARGETARCH
WORKDIR /source

# lld is a multi-arch linker; ld.bfd on Alpine only knows x86 emulations and
# fails with "unrecognised emulation mode: aarch64linux/armelf_linux_eabi" during
# cross-compilation.
RUN apk add --no-cache clang lld llvm

COPY --from=sysroot-arm64 / /sysroots/linux-arm64/
COPY --from=sysroot-arm   / /sysroots/linux-arm/

# Copy project file and restore as distinct layers
COPY --link *.csproj .
RUN dotnet restore -a $TARGETARCH

# Copy source code and publish app
COPY --link . .
RUN if [ "$TARGETARCH" != "amd64" ]; then \
        dotnet publish -a $TARGETARCH --no-restore -p:CppCompilerAndLinker=clang -p:LinkerFlavor=lld -p:ObjCopyName=llvm-objcopy -p:SysRoot=/sysroots/linux-$TARGETARCH -o /app; \
    else \
        dotnet publish -a $TARGETARCH --no-restore -p:CppCompilerAndLinker=clang -p:LinkerFlavor=lld -p:ObjCopyName=llvm-objcopy -o /app; \
    fi


# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine
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