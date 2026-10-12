# Background music

Music pickers share folder and library sources across **Web shows**, **Guide shows**, **News shows**, **Streamer channels**, and **Generated promos**. **Radio shows** uses the same libraries for station programming.

## Choose a source

1. Open the feature's **Music**, **Sound**, or **Background music** section.
2. Choose a folder or an enabled music library.
3. Select all songs, a playlist, genres, or artists where available.
4. Set the volume and save the feature's settings.

| Source | Behavior |
| --- | --- |
| **No music** | Silence |
| **Folder** | Audio files in the folder and subfolders, shuffled and looped |
| **Jellyfin** | Songs streamed through the configured library connection |
| **Local music folders** | Tagged files added through **App settings → Local TV and movies** |
| **Plugin music library** | Songs from an enabled `IMusicLibrary` provider |

A disabled or missing library produces silence rather than selecting another library.

## Live channels and renders

Live features play songs sequentially, with a brief gap while the next file opens. Generated promos choose one stable song per airing; remotely served songs are cached as local files for rendering.

News can duck music under speech. Generated promos expose additional fades, song start points, and ducking controls.

## Fix missing music

- Verify the folder exists on the **scheduler's computer**
- Confirm the selected **library** is enabled and configured
- Check that the selected playlist, genres, or artists contain songs
- Inspect the feature's **preview error** or **server log**

A promo can render without music when its source is unavailable.
