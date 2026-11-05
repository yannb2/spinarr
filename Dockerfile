# Spinarr: the app plus every tool it drives, each at a pinned version.
# The weekly "Tool updates" workflow bumps these ARG lines in a pull request; nothing updates on its own.
ARG LIBCDIO_VERSION=2.1.0
ARG LIBCDIO_PARANOIA_VERSION=10.2+2.0.2

# libcdio and cd-paranoia from GNU's tarballs, so the pinned versions are the ones that run (Ubuntu's
# packages lag behind). Built on the same Ubuntu as the final image, into /usr.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute AS cdio
ARG LIBCDIO_VERSION
ARG LIBCDIO_PARANOIA_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential autoconf automake libtool pkgconf curl ca-certificates bzip2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /build
RUN set -eux; \
    curl -fsSL "https://ftp.gnu.org/gnu/libcdio/libcdio-${LIBCDIO_VERSION}.tar.bz2" | tar -xj; \
    cd "libcdio-${LIBCDIO_VERSION}"; \
    autoreconf -fi; \
    ./configure --prefix=/usr --disable-dependency-tracking --disable-cxx --disable-example-progs --disable-static; \
    make -j"$(nproc)"; \
    make install; \
    make install DESTDIR=/out
RUN set -eux; \
    curl -fsSL "https://ftp.gnu.org/gnu/libcdio/libcdio-paranoia-${LIBCDIO_PARANOIA_VERSION}.tar.bz2" | tar -xj; \
    cd "libcdio-paranoia-${LIBCDIO_PARANOIA_VERSION}"; \
    autoreconf -fi; \
    ./configure --prefix=/usr --disable-dependency-tracking --disable-example-progs --disable-static; \
    make -j"$(nproc)"; \
    make install DESTDIR=/out; \
    ls -l /out/usr/bin

FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute AS build
WORKDIR /src
COPY global.json ./
COPY src/Spinarr/Spinarr.csproj src/Spinarr/
# arrkit is a git submodule: CI checks it out (submodules: true); locally run
# "git submodule update --init" before building.
COPY arrkit/src/Arrkit/Arrkit.csproj arrkit/src/Arrkit/
RUN dotnet restore src/Spinarr/Spinarr.csproj
COPY src/ src/
COPY arrkit/src/ arrkit/src/
RUN dotnet publish src/Spinarr/Spinarr.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute
ARG LIBCDIO_VERSION
ARG LIBCDIO_PARANOIA_VERSION

COPY --from=cdio /out/usr/ /usr/
RUN ldconfig

# What Spinarr runs besides cd-paranoia: cdrdao (the full table of contents: pre-gaps, ISRCs,
# hidden track) and flac (encode and verify). Everything else (disc IDs, AccurateRip, MusicBrainz,
# offsets, the log) is Spinarr's own C# code.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends curl ca-certificates tzdata cdrdao flac; \
    rm -rf /var/lib/apt/lists/*; \
    cd-paranoia --version 2>&1 | head -n 2; \
    cdrdao 2>&1 | head -n 1 || true; \
    flac --version

WORKDIR /app
COPY --from=build /app ./

# /config holds drives.json (each drive's read offset and cache answer), rips.json (the rips),
# the logs, the ASP.NET keys and the MusicBrainz cache.
RUN mkdir -p /config && chown $APP_UID /config
VOLUME /config

# The pins show on the Tools page next to what each tool reports and the newest upstream version.
ENV ASPNETCORE_HTTP_PORTS=8187 \
    TZ=Etc/UTC \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    SPINARR_PIN_LIBCDIO=${LIBCDIO_VERSION} \
    SPINARR_PIN_CD_PARANOIA=${LIBCDIO_PARANOIA_VERSION}
EXPOSE 8187

# Compose picks the user (user: 1000:1000) and adds the cdrom group for the disc drive.
USER $APP_UID
ENTRYPOINT ["dotnet", "Spinarr.dll"]
