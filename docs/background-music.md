# Background music

Everything in SchedularrTV that plays music under something takes it from the same place and offers the same choices. This page explains those choices and how library songs are played.

## Where it's used

- **Web page channels**: music under the page.
- **TV guide channels**: the guide's music.
- **News shows**: the bed under the news (it dips under the voice).
- **Streamer channels**: music while nobody is live.
- **Generated promos**: one song per promo (**Songs from** in the template's **Background music**).
- **Radio shows**: the station itself (see [Radio shows](radio.md)).

## The choices

### No music

Silence.

### A folder on disk

Every audio file in the folder and its subfolders (MP3, M4A, AAC, OGG, Opus, FLAC, WAV…), shuffled, starting again when they run out.

### A music library

Any of these:

- **Jellyfin**'s music.
- Your **Local music folders** (see [Music folders](local-library.md#music-folders)).
- Any music library plugin's.

From a library you can play every song, a playlist, some genres or some artists.

A library that isn't set up yet (in **App settings**) is listed as *not set up*; music from it plays once it is. A library whose plugin is switched off or removed plays nothing, rather than another library's songs.

## How library songs are played

### Live channels

Web pages, the guide, news and streamers play the songs one after another. Each is read the way the library serves it: a music folder's files directly, Jellyfin's over its API with the key kept out of the address.

Between songs there's a moment of silence while the next one opens.

### Generated promos

Generated promos need a file to render. Each promo gets one song, the same one whenever that promo is rendered again.

- Songs a library keeps as files are used where they are.
- Jellyfin's songs are copied once into the promos folder's `music` folder, and cleared out with old renders when they haven't been used for a while.

## Troubleshooting

- **Silence.** The folder doesn't exist on the scheduler's computer or has no audio files; the library isn't set up; or the playlist, genres or artists chosen have no songs. Live channels show the reason in their preview.
- **A promo has no music.** The library couldn't be reached when it was rendered (the scheduler's log says why). The promo renders without music rather than not at all.
