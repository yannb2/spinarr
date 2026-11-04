# Spinarr: the app plus every tool it drives, each at a pinned version.
# The weekly "Tool updates" workflow bumps these ARG lines in a pull request; nothing updates on its own.
# whipper is the homelab fork (flac_level option), pinned to a commit on its develop branch.
ARG WHIPPER_COMMIT=fdb2f1ef5a6df0f812ab1b94beb66d33aaf4987c
ARG LIBCDIO_VERSION=2.1.0
ARG LIBCDIO_PARANOIA_VERSION=10.2+2.0.1

# libcdio and cd-paranoia from GNU's tarballs, as whipper's own Dockerfile does: the distribution
# packages split them in ways whipper can't use. Built on the same Ubuntu as the final image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS cdio
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
    ./configure --prefix=/usr/local --disable-dependency-tracking --disable-cxx --disable-example-progs --disable-static; \
    make -j"$(nproc)"; \
    make install; \
    make install DESTDIR=/out
RUN set -eux; \
    curl -fsSL "https://ftp.gnu.org/gnu/libcdio/libcdio-paranoia-${LIBCDIO_PARANOIA_VERSION}.tar.bz2" | tar -xj; \
    cd "libcdio-paranoia-${LIBCDIO_PARANOIA_VERSION}"; \
    autoreconf -fi; \
    ./configure --prefix=/usr/local --disable-dependency-tracking --disable-example-progs --disable-static; \
    make -j"$(nproc)"; \
    make install DESTDIR=/out; \
    ls -l /out/usr/local/bin

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG WHIPPER_COMMIT
ARG LIBCDIO_VERSION
ARG LIBCDIO_PARANOIA_VERSION

COPY --from=cdio /out/usr/local/ /usr/local/
RUN ldconfig

# whipper and what it runs: cdrdao (table of contents), flac, sox (peak level), libdiscid
# (MusicBrainz disc id), eject. Its Python packages come from Ubuntu where Ubuntu has them;
# pycdio and discid from PyPI, built against the libcdio above. Build tools are removed after.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
        curl ca-certificates tzdata eject cdrdao flac sox libsndfile1 libdiscid0 \
        python3 python3-musicbrainzngs python3-mutagen python3-pil python3-ruamel.yaml \
        python3-packaging python3-gi python3-setuptools python3-setuptools-scm python3-pip \
        build-essential python3-dev swig pkgconf libsndfile1-dev; \
    pip3 install --no-cache-dir --break-system-packages pycdio==2.1.0 discid; \
    mkdir /tmp/whipper; \
    curl -fsSL "https://github.com/yannb2/whipper/archive/${WHIPPER_COMMIT}.tar.gz" | tar -xz -C /tmp/whipper --strip-components=1; \
    # No git history in a tarball: tell setuptools-scm the version (after upstream's 0.10.0, plus the fork commit).
    SETUPTOOLS_SCM_PRETEND_VERSION="0.10.1.dev0+yannb2.g$(echo "${WHIPPER_COMMIT}" | cut -c1-7)" \
        pip3 install --no-cache-dir --break-system-packages --no-build-isolation /tmp/whipper; \
    rm -rf /tmp/whipper; \
    apt-get purge -y build-essential python3-dev swig pkgconf libsndfile1-dev; \
    apt-get autoremove -y; \
    rm -rf /var/lib/apt/lists/*; \
    whipper -v; \
    cd-paranoia --version 2>&1 | head -n 2; \
    python3 -c "import cdio, discid, accuraterip; print('python modules ok')"

WORKDIR /app
COPY --from=build /app ./

# /config holds whipper.conf (drive offsets and cache answers) and whipper's caches.
RUN mkdir -p /config && chown $APP_UID /config
VOLUME /config

# The pins show on the Tools page next to what each tool reports and the newest upstream version.
# XDG_* here too, so "docker exec spinarr whipper ..." uses the same whipper.conf as the app.
ENV ASPNETCORE_HTTP_PORTS=8187 \
    TZ=Etc/UTC \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    PYTHONIOENCODING=utf-8 \
    XDG_CONFIG_HOME=/config \
    XDG_CACHE_HOME=/config/cache \
    SPINARR_PIN_WHIPPER=${WHIPPER_COMMIT} \
    SPINARR_PIN_LIBCDIO=${LIBCDIO_VERSION} \
    SPINARR_PIN_CD_PARANOIA=${LIBCDIO_PARANOIA_VERSION}
EXPOSE 8187

# Compose picks the user (user: 1000:1000) and adds the cdrom group for the disc drive.
USER $APP_UID
ENTRYPOINT ["dotnet", "Spinarr.dll"]
