# Spinarr

Rips music discs into tagged FLAC and hands the albums to Lidarr. CDs first, with
[Yann's whipper fork](https://github.com/yannb2/whipper) doing the reading (secure rip, AccurateRip,
FLAC level); CD images and DVD/Blu-ray audio from an ISO come later. A sister of
[Ripparr](https://github.com/yannb2/ripparr): same look, same way of pinning its tools.

Plan: [Music ripping app: plan](https://claude.ai/artifact/WrjM3mbvKJWQTxDDo46yi4).

## What v0.1 does

- **Drives page** (`/`): every disc drive the container can see, named from the kernel (vendor, model,
  firmware), whether there is an audio CD in it, and an Eject button.
  - **Find offset** runs `whipper offset find`: it rips track 1 at each likely read offset until
    AccurateRip agrees, then saves the offset. Needs a well-known CD with at least 3 tracks.
    It can take from a few minutes to an hour.
  - **Check cache** runs `whipper drive analyze`: can cd-paranoia defeat the drive's audio cache?
  - **Set by hand**: type an offset you already know (from an old `whipper.conf` or the
    [AccurateRip list](https://www.accuraterip.com/driveoffsets.htm)).
  - Every answer goes into `/config/whipper/whipper.conf`, the same file whipper reads when it rips,
    so `docker exec spinarr whipper ...` and the app always agree.
- **Tools page** (`/tools`): each tool's version in the image, its pin, and the newest upstream.
- `GET /health`, and `GET /api/stats` for a Homepage widget: `drives`, `discs` (audio CDs in them),
  `driveTasks` (one running), `toolUpdates`.

## Run it

See [compose.example.yaml](compose.example.yaml). Port 8187, config in `/config/spinarr` on the VM
(owned by uid 1000). The drive goes in with `devices:` and its group with `group_add:`.

Already have a `whipper.conf` from the `arm` VM? Copy it to `/config/spinarr/whipper/whipper.conf`
and the Drives page shows its offsets straight away.

## Settings

Environment variables, all optional:

| Variable | Default | What it does |
| --- | --- | --- |
| `Spinarr__WhipperHome` | `/config` | whipper.conf goes in `<this>/whipper/` (`XDG_CONFIG_HOME`) |
| `Spinarr__CacheFolder` | `/config/cache` | whipper's caches (`XDG_CACHE_HOME`) |
| `Spinarr__Devices__0`, `__1`... | every `/dev/sr*` | Only these drives |
| `Spinarr__Updates__Enabled` | `true` | Look for newer tool versions once a day |

## Tools in the image

Pinned with `ARG` lines at the top of the [Dockerfile](Dockerfile); a weekly workflow opens a pull
request when there is something newer (needs Settings > Actions > General > "Allow GitHub Actions to
create and approve pull requests").

| Tool | Pin | Where from |
| --- | --- | --- |
| whipper (fork) | `WHIPPER_COMMIT` | `github.com/yannb2/whipper`, a commit on `develop` |
| libcdio | `LIBCDIO_VERSION` | GNU tarball, built in the image |
| cd-paranoia | `LIBCDIO_PARANOIA_VERSION` | GNU tarball, built in the image |
| cdrdao, FLAC, SoX | none | Ubuntu packages of the base image |

## Build and test

```bash
git submodule update --init      # arrkit, the code shared with Ripparr and Muxarr
dotnet test                      # C# tests
python3 -I -m unittest discover -s .github/scripts -p 'test_*.py'
docker build -t spinarr .
```

Licence: GPLv3, like whipper, Ripparr and Muxarr.
