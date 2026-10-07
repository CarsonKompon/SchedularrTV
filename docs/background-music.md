# Background music

Everything that plays music under something takes it from the same place, and offers the same choices:

- **Web page channels**: music under the page.
- **TV guide channels**: the guide's music.
- **News shows**: the bed under the news (it dips under the voice).
- **Streamer channels**: music while nobody is live.
- **Generated promos**: one song per promo (*Songs from* in the template's Background music).
- **Radio shows**: the station itself (see [Radio shows](radio.md)).

## The choices

| Music from | What plays |
| --- | --- |
| No music | Silence. |
| A folder on disk | Every audio file in it and its subfolders (MP3, M4A, AAC, OGG, Opus, FLAC, WAV…), shuffled, starting again when they run out. |
| A music library | **Jellyfin**'s music, your **Local music folders** (see [Local TV and movies](local-library.md#music-folders)), or any music library plugin's: every song, a playlist, some genres or some artists. |

A library that isn't set up yet (App settings) is listed as *not set up*; music from it plays once it is. A library
whose plugin is switched off or removed plays nothing, rather than another library's songs.

## How library songs are played

- **Live channels** (web pages, the guide, news, streamers) play the songs one after another, each read the way the
  library serves it: a music folder's files directly, Jellyfin's over its API with the key kept out of the address.
  Between songs there's a moment of silence while the next one opens.
- **Generated promos** need a file to render: each promo gets one song, the same one whenever that promo is
  rendered again. Songs a library keeps as files are used where they are; Jellyfin's are copied once into the promos
  folder's `music` folder, and cleared out with old renders when they haven't been used for a while.

## Troubleshooting

- **Silence.** The folder doesn't exist on the scheduler's computer or has no audio files; the library isn't set up;
  or the playlist, genres or artists chosen have no songs. Live channels show the reason in their preview.
- **A promo has no music.** The library couldn't be reached when it was rendered (the scheduler's log says why); it
  renders without music rather than not at all.
