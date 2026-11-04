# Spinarr

Rips music CDs into tagged FLAC and hands the albums to Lidarr. The rip engine is Spinarr's own C#
code driving `cd-paranoia`, `cdrdao` and `flac`, with the logic ported from
[whipper](https://github.com/whipper-team/whipper) (TOC, disc IDs, read offsets, AccurateRip), and a
rip log in EAC's layout. CD images and DVD/Blu-ray audio from an ISO come later. A sister of
[Ripparr](https://github.com/yannb2/ripparr), Salvarr, Specarr and Scratcharr: same look, menus and
Logs page, same way of pinning its tools.

Plan: [Music ripping app: plan](https://claude.ai/artifact/WrjM3mbvKJWQTxDDo46yi4).

## What it does (v0.2)

- **Drives** (`/`): every disc drive the container can see, named from the kernel (vendor, model,
  firmware), whether there is an audio CD in it, Eject, and **Rip this CD**.
  - **Find offset**: reads track 1 once with a margin, tries the known drive offsets (most common
    first) against AccurateRip in memory, then confirms on the other tracks. A few minutes, with a
    well-known CD of at least 3 tracks.
  - **Check cache**: runs `cd-paranoia -A`: can cd-paranoia defeat the drive's audio cache?
  - **Set by hand**: an offset you already know (the
    [AccurateRip list](https://www.accuraterip.com/driveoffsets.htm)).
  - Answers are kept in `/config/drives.json`. An old `/config/whipper/whipper.conf` is taken over
    the first time.
- **Disc page** (`/disc/sr0`): the disc's MusicBrainz, freedb and AccurateRip IDs, the MusicBrainz
  releases with their covers (or paste a release link), the track list with file names, FLAC level
  and tries per track, and **Rip**.
- **The rip**: full TOC with `cdrdao` (pre-gaps, ISRCs, hidden track before track 1), each track
  read twice with `cd-paranoia` (test and copy) and shifted by the drive's offset in Spinarr, the
  two CRCs compared (read again up to the number of tries), AccurateRip v1 and v2 checked, then
  `flac -N --verify` with Picard/Lidarr tags (MusicBrainz IDs, ISRC, label, barcode...). Two
  progress bars (disc and track) and a per-track table while it runs.
- **What lands** in `{RipFolder}/{Artist}/{Album (Year)}/`: `01 - Title.flac`..., a cue sheet
  (EAC's "multiple files with gaps"), the cdrdao `.toc`, and a `.log` in EAC's layout and wording.
  The log's header says Spinarr: it has no EAC checksum, since that would sign it as EAC's.
- **Rips** (`/rips`): the running rips and every rip that ended (`/config/rips.json`), with its log.
- **Logs** (`/logs`), **Tools** (`/tools`), **Settings** (`/settings`: the ripping settings and a
  status tab), same as the sister apps.
- `GET /health`, and `GET /api/stats` for a Homepage widget: `drives`, `discs` (audio CDs in them),
  `driveTasks`, `rips` (running), `toolUpdates`.

## Run it

See [compose.example.yaml](compose.example.yaml). Port 8187, config in `/config/spinarr` on the VM
(owned by uid 1000), rips in `/downloads/rips/music` (Lidarr must see it at the same path). The
drive goes in with `devices:` and its group with `group_add:`.

## Settings

Environment variables, all optional:

| Variable | Default | What it does |
| --- | --- | --- |
| `Spinarr__RipFolder` | `/downloads/rips/music` | Where albums go; the rip in progress is in `.work` under it |
| `Spinarr__FlacLevel` | `8` | FLAC level the disc page starts with (0 to 8) |
| `Spinarr__Retries` | `5` | Tries per track when the two reads differ (1 to 20) |
| `Spinarr__ConfigFolder` | `/config` | drives.json, rips.json, logs, keys |
| `Spinarr__CacheFolder` | `/config/cache` | MusicBrainz answers (kept 7 days) |
| `Spinarr__Devices__0`, `__1`... | every `/dev/sr*` | Only these drives |
| `Spinarr__Updates__Enabled` | `true` | Look for newer tool versions once a day |
| `Logs__KeepLines` | `2000` | Log lines kept in `/config/logs` across restarts |

## Tools in the image

Pinned with `ARG` lines at the top of the [Dockerfile](Dockerfile); a weekly workflow opens a pull
request when there is something newer (needs Settings > Actions > General > "Allow GitHub Actions to
create and approve pull requests").

| Tool | Pin | Where from |
| --- | --- | --- |
| libcdio | `LIBCDIO_VERSION` | GNU tarball, built in the image |
| cd-paranoia | `LIBCDIO_PARANOIA_VERSION` | GNU tarball, built in the image |
| cdrdao, FLAC | none | Ubuntu packages of the base image |

No whipper and no Python in the image.

## Build and test

```bash
git submodule update --init      # arrkit, the code shared with the sister apps
dotnet test                      # C# tests (whipper's own test discs and AccurateRip files included)
python3 -I -m unittest discover -s .github/scripts -p 'test_*.py'
docker build -t spinarr .
```

Licence: GPLv3, like whipper, Ripparr and Muxarr. The test fixtures under
`tests/Spinarr.Tests/Fixtures/whipper` come from whipper (GPLv3).
