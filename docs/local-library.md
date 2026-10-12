# Local media

Local folders supply TV, movies, and music without Jellyfin. The scanner supports common Sonarr, Radarr, Kodi, Jellyfin, Plex, and ErsatzTV layouts.

## Add folders

1. Open **App settings → Local TV and movies**.
2. Select **Add a TV folder**, **Add a movies folder**, or **Add a music folder**.
3. Choose a path readable by the scheduler.
4. Select **Save**.
5. Check **TV Shows** or **Movies** for video content.

Music appears as the **Local music folders** library in music pickers. ErsatzTV must also be able to read video paths; use [path mappings](ersatztv.md#path-mappings-and-guide-options) when it runs elsewhere.

## TV folders

```text
TV/
  Clone High (2002)/
    tvshow.nfo
    poster.jpg
    fanart.jpg
    Season 01/
      Clone High - S01E01 - Escape to Beer Mountain.mkv
      Clone High - S01E01 - Escape to Beer Mountain.nfo
      Clone High - S01E01 - Escape to Beer Mountain-thumb.jpg
    Specials/
      Clone High - S00E01 - Special.mkv
```

Metadata and artwork files are optional. A show folder is recognized by **`tvshow.nfo`**, season folders, or episode files.

| Naming | Interpretation |
| --- | --- |
| `Season 1`, `Season 01`, `S01`, `Series 1` | Season folder |
| `01`, `Season 2 (2005)`, `Staffel 3`, `Disc 1` | Additional supported season names |
| `Specials` | Season `0` |
| `S01E02`, `1x02`, `1.02`, `1_02` | Episode numbering |
| `S01E02E03`, `S01E02-E03`, `S01E02-03` | Multiple episodes in one file |
| `2024-03-15` | Date-based episode |
| `02 - Title`, `Episode 2` | Episode inside a season folder |

**Season folders determine the season**, even when the filename differs. Episodes directly inside a show folder use their metadata or filename.

Grouping folders such as **`TV/Cartoons/Daria`** add tags when **Tag from grouping folders** is enabled.

## Movie folders

```text
Movies/
  The Matrix (1999)/
    The Matrix (1999) - 1080p.mkv
    The Matrix (1999) - 4K.mkv
    movie.nfo
    poster.jpg
    fanart.jpg
  Loose.Movie.2010.1080p.mkv
```

Multiple videos in one movie folder are alternate **versions**; only one version airs. Files marked **`cd1`**, **`part2`**, or **`disc1`** are separate parts.

Loose movies use filename-based metadata and artwork named for the file, such as **`Loose.Movie.2010.1080p-poster.jpg`**. Grouping folders also become tags.

## Music folders

```text
Music/
  Daft Punk/
    Discovery/
      cover.jpg
      01 - One More Time.flac
      01 - One More Time.lrc
  Road Trip.m3u
```

**ffprobe** reads song tags; folder and filename conventions supply missing metadata. **`.m3u`** and **`.m3u8`** files become playlists, using relative or absolute paths.

Lyrics can come from matching **`.lrc`** files, **`.txt`** files, embedded tags, or online lookup. Music feeds [Radio shows](radio.md) and [Background music](background-music.md), rather than video programme rotation.

## Metadata and artwork

Kodi-style **`.nfo`** metadata generally takes precedence over filename-derived values; season-folder placement still controls the season.

| Metadata | Use |
| --- | --- |
| `title`, `plot`, `outline` | Display text and search |
| `season`, `episode` | Episode ordering |
| `aired`, `premiered`, `releasedate`, `year` | Dates and year queries |
| `mpaa`, `genre`, `studio`, `tag`, `set` | Ratings, genres, networks, tags, movie collections |
| `uniqueid`, `imdbid`, `tmdbid`, `tvdbid` | Provider attributes |
| `durationinseconds`, `runtime` | Runtime fallback |

Artwork includes **`poster`**, **`folder`**, **`cover`**, **`fanart`**, **`backdrop`**, **`landscape`**, and episode thumbnail conventions. Guide artwork for remote players needs a reachable **`ErsatzTv:LocalArtworkBaseUrl`**.

## Intros, outros, and excluded files

Open **Intros/outros** on **TV Shows** or a collection to select show extras. **Browse files on disk** accepts other clips.

| Content | Scanner behavior |
| --- | --- |
| Extras, trailers, featurettes, samples | Excluded from normal episodes and movies; eligible extras appear in bumper pickers |
| Hidden folders and macOS `._` files | Ignored |
| Folders containing `.etvignore` or `.ignore` | Ignored |
| Multi-episode NFO in one video | One media item with combined episode naming |

## Rescan

Use **TV Shows → Rescan** for new media, or **Channel settings → Rescan and rebuild** to refresh local folders and replan.

Scans cache for **30 minutes**, or **2 hours** with low-power defaults. Video runtimes and chapters cache in **`state/probe-cache.json`** until file size or modification time changes; music metadata caches in **`state/music-cache.json`**.
