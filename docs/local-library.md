# Local TV and movies

Play shows, movies and music straight from folders on disk, without Jellyfin or alongside it. Folders laid out for Sonarr, Radarr, Kodi, Jellyfin, Plex or ErsatzTV work as they are.

## Adding folders

1. Open **App settings → Local TV and movies** (or the setup guide).
2. Choose **Add a TV folder** or **Add a movies folder**.
3. Choose the folder.

The shows and movies then appear on the **TV Shows** and **Movies** pages, in the pickers and in queries, like anything from Jellyfin.

The folders are read the way Sonarr, Radarr, Kodi, Jellyfin, Plex and ErsatzTV lay them out, so a library any of those already manages works as it is.

## TV folders

```text
TV/
  Clone High (2002) [tvdbid-71035]/
    tvshow.nfo            show details (optional)
    poster.jpg            the show's poster (or folder.jpg, cover.jpg)
    fanart.jpg            its background (or backdrop.jpg, background.jpg)
    landscape.jpg         a wide picture (or thumb.jpg)
    Season 01/
      Clone High - S01E01 - Escape to Beer Mountain.mkv
      Clone High - S01E01 - Escape to Beer Mountain.nfo        episode details (optional)
      Clone High - S01E01 - Escape to Beer Mountain-thumb.jpg  its still (optional)
      Clone High - S01E02E03 - Two Parter.mkv                  two episodes in one file
    Specials/                                                  season 0
  Cartoons/
    Daria/Season 1/Daria - 1x01 - Esteemsters.mkv              "Cartoons" becomes a tag
```

### Shows

A show is a folder with a `tvshow.nfo`, season folders, or episodes in it.

Its name is the folder's name, with a year in brackets and ids like `[tvdbid-…]` or `{tmdb-…}` taken off, unless `tvshow.nfo` says otherwise.

### Seasons

Season folders can be named `Season 1`, `Season 01`, `S01` or `Series 1`. `Specials` is season 0.

Inside a show, folders are also read as ErsatzTV reads them:

- a bare number (`01`)
- an S or Season number anywhere (`Season 2 (2005)`)
- a number at the end (`Staffel 3`, `Disc 1`)
- a name ending in "specials"

**The season folder decides the season**, as in ErsatzTV. A file named `S03E09` filed under `Season 2` is in season 2. Folders below a season folder (`Season 1/Disc 2`) stay in it.

Episodes can also sit directly in the show's folder. They then take their season from the .nfo or their name.

### Episodes

Episode file names can use:

- `S01E02` or `s01e02`
- `S01E02E03`, `S01E02-E03` or `S01E02-03` (one file, several episodes)
- `1x02`, `1.02` or `1_02`
- a date, for daily shows (`2024-03-15`)
- inside a season folder, a leading number (`02 - Title`) or `Episode 2`

The title is what follows (`- Title`), without quality tags (`WEBDL-1080p`, `[x265]`…).

### Grouping folders

Grouping folders between the root and a show (`TV/Cartoons/Daria`) become tags on its episodes, as commercials' folders do. Switch that off with **Tag from grouping folders**.

## Movie folders

```text
Movies/
  The Matrix (1999)/
    The Matrix (1999) - 1080p.mkv     two versions of one movie
    The Matrix (1999) - 4K.mkv
    movie.nfo                          or The Matrix (1999) - 1080p.nfo
    poster.jpg   fanart.jpg   landscape.jpg
    The Matrix (1999)-trailer.mkv      left out
  Kill Bill (2003)/Kill Bill (2003) - cd1.mkv, … - cd2.mkv     parts
  Loose.Movie.2010.720p.BluRay.mkv    a movie on its own is fine too
```

- A movie's folder is named `Title (Year)`, as Radarr names it. Its pictures and `movie.nfo` are the movie's.
- Several videos in one movie folder are **versions** of one movie (only one airs).
- The exception is **parts** (`cd1`, `part2`, `disc1`), which are separate items titled "(Part 2)".
- Videos loose in a folder of their own are each a movie, named from the file (`Title (2010)`, `Title.2010.1080p…`). Only pictures named for the file (`Title (2010)-poster.jpg`, `-fanart.jpg`) are theirs.
- Grouping folders between the root and the movie become tags, as for TV.

## Music folders

Add a music folder with **Add a music folder** under **App settings → Local TV and movies**. It becomes the **Local music folders** music library.

Radio channels can play from it without Jellyfin (*Music from* on the Radio shows page), and so can everything else with [background music](background-music.md): web page channels, the TV guide, news shows, streamer channels and generated promos.

```text
Music/
  Daft Punk/
    Discovery/
      cover.jpg                    the album's cover (or folder.jpg, front.jpg, album.jpg)
      01 - One More Time.flac
      01 - One More Time.lrc       timed lyrics (or a .txt for plain ones)
  Road Trip.m3u                    a playlist (paths relative to it, or full paths)
```

- **Tags:** titles, artists, album artists, albums, genres, years and track numbers come from the files' tags. They're read once with ffprobe and kept in `state/music-cache.json`. Without tags, `Artist/Album/01 - Title` (or `Artist - Title`) is used.
- **Lyrics** come from an `.lrc` file beside the song (timed), a `.txt` file, or the song's lyrics tag, else LRCLIB online.
- **Playlists:** `.m3u` and `.m3u8` files are playlists. Genres and artists come from the tags, for stations that play some of them.
- Music folders aren't scheduled as shows; they're for music. Like videos, songs ffprobe can't read are left out.

## What is left out

These are never added to the library:

- Extras folders (`Extras`, `Featurettes`, `Behind The Scenes`, `Deleted Scenes`, `Interviews`, `Scenes`, `Shorts`, `Trailers`, `Other`, `Samples`…)
- Files ending `-trailer`, `-sample`, `-featurette` and the like
- macOS `._` files
- Hidden folders
- Any folder with an `.etvignore` (as ErsatzTV uses) or `.ignore` (as Jellyfin uses) file in it

Hidden and ignored folders and `._` files are left out of commercials folders too.

## Intros and outros

A local show's extras are offered when choosing its intros and outros (**Intros/outros…** on the TV Shows page or in a collection), as Jellyfin's are for its shows. These are:

- the videos in the show's (or a season's) `Extras`, `Trailers`, `Featurettes`… folders
- videos named as extras (`Daria-theme.mkv`, `Daria - S01E01-featurette.mkv`)

These are never scheduled as episodes. Any other file can still be added with **Browse files on disk**. Jellyfin doesn't need to be set up for this.

## .nfo files

Kodi-style .nfo files (as written by Kodi, Jellyfin, Emby, Sonarr, Radarr, tinyMediaManager…) are read where they exist, and win over what names say.

| From | Used for |
| --- | --- |
| `title`, `plot` (or `outline`) | Title and description |
| `season`, `episode` | Where an episode sits |
| `aired`, `premiered`, `releasedate`, `year` | Premiere date (and `year:` in queries) |
| `mpaa` (`Rated PG-13`, `US:TV-14`) | Rating; an episode without one takes its show's |
| `genre` | Genres (`genre:Comedy`); an episode without any takes its show's |
| `studio` | Networks (`network:MTV`) |
| `tag` | Tags |
| `set` | Collections (movies) |
| `uniqueid`, `imdbid`, `tmdbid`, `tvdbid` | Attributes such as `tvdb_id` (a show's as `series_tvdb_id`) |
| `durationinseconds`, `runtime` | The length, when ffprobe can't read the file |

- A file with several `<episodedetails>` (one video, several episodes) is read as one item named after all of them.
- A link left after the XML (as some scrapers write) is ignored.

## Lengths, chapters and pictures

- **Lengths and chapters:** every video is measured with **ffprobe** once, along with its **chapters**. Mid-roll breaks can use the chapters, and a chapter named "Credits" marks the end credits for Credit squeeze. Results are kept in `state/probe-cache.json` until the file changes.
- **Pictures** are served by the scheduler: posters on the TV Shows and Movies pages, the next show's picture in Credit squeeze, and the guide's icons. Big pictures are scaled down once with ffmpeg and kept in `state/artwork`.
- **The guide** points at this scheduler for local pictures: `http://localhost:5252` unless `ErsatzTv:LocalArtworkBaseUrl` says otherwise. If other devices' guides should show the pictures, set it to the address those devices reach the scheduler at.

## Playback and rescanning

ErsatzTV plays the files from their paths, so it needs to see the same paths as the scheduler. When it runs elsewhere (another computer, a container), set its path mappings (`ErsatzTv:PathMappings`, see [ErsatzTV Next](ersatztv.md#path-mappings-and-guide-options)), as for Jellyfin.

Scans are kept for 30 minutes (2 hours on a Raspberry Pi). To read new episodes and movies straight away, use **Rescan** on the TV Shows page, or **Rescan and rebuild** under Settings.

## Coming from ErsatzTV

A local library set up for ErsatzTV (legacy) reads the same here:

- the same video types (`.mkv`, `.mp4`, `.avi`, `.ts`, `.m2ts`, `.wmv`, `.ogv`…)
- show folders with season folders named any way ErsatzTV accepts
- `tvshow.nfo`, `movie.nfo` and `name.nfo` files when they're there, and names when they're not
- `poster`/`folder`/`fanart`/`thumb` pictures (`.jpg`, `.png`, `.gif`, `.webp`, `.tbn`)
- `.etvignore`

An ErsatzTV "Other Videos" library works as a commercials folder. Every folder becomes a tag, including the library folder's own name when no tags are set for it.

### Differences from ErsatzTV

Where this reads more than ErsatzTV does, it's only extra:

- grouping folders between the root and a show
- episodes loose in a show folder
- videos loose in the movies root
- versions and parts of one movie

Two things it deliberately leaves out that ErsatzTV would pick up:

- Videos in a season's `Extras`/`Trailers` folders. ErsatzTV adds them as episodes.
- Movies whose file name merely ends in a word like "short". `The Big Short.mkv` is a movie here; extras need a dash (`Movie-short.mkv`).

Music videos, songs and image libraries aren't supported.
