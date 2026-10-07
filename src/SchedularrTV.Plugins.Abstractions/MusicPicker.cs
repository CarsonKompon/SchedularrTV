using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Plugins;

/// <summary>
/// Chooses background music (<see cref="BackgroundMusic"/>): none, a folder on disk, or any installed music library
/// (Jellyfin's, local music folders, a plugin's) with every song, a playlist, some genres or some artists. The same
/// picker everywhere music can be set; use it as <c>&lt;MusicPicker Value="@x.Music" ValueChanged="v =&gt; x.Music = v" /&gt;</c>.
/// </summary>
public sealed class MusicPicker : ComponentBase
{
    [Inject] private IEnumerable<IMusicLibrary> InjectedLibraries { get; set; } = [];

    [Parameter] public BackgroundMusic? Value { get; set; }

    /// <summary>Raised with a fresh copy on every change; null for no music.</summary>
    [Parameter] public EventCallback<BackgroundMusic?> ValueChanged { get; set; }

    [Parameter] public string Label { get; set; } = "Music";

    [Parameter] public string NoneLabel { get; set; } = "No music";

    /// <summary>Offers "no music" (on by default).</summary>
    [Parameter] public bool AllowNone { get; set; } = true;

    /// <summary>Offers a folder on disk (on by default).</summary>
    [Parameter] public bool AllowFolder { get; set; } = true;

    [Parameter] public string FolderPlaceholder { get; set; } = @"D:\Music";

    /// <summary>Said under a folder that doesn't exist.</summary>
    [Parameter] public string MissingFolderText { get; set; } = "That folder doesn't exist on this computer; there'll be no music.";

    private IMusicLibrary[] Libraries => [.. InjectedLibraries];

    private IReadOnlyList<MusicChoice> _choices = [];
    private string? _choicesFor;
    private string? _problem;
    private bool _loading;
    private string _filter = string.Empty;

    protected override Task OnParametersSetAsync() => LoadChoicesAsync();

    private async Task LoadChoicesAsync()
    {
        BackgroundMusic? music = Value;
        if (music is not { FromLibrary: true } || music.Pick == BackgroundMusicPick.All)
        {
            return;
        }

        string key = $"{music.LibraryId}|{music.Pick}";
        if (key == _choicesFor)
        {
            return;
        }

        _choicesFor = key;
        _choices = [];
        _problem = null;
        IMusicLibrary? library = Libraries.FirstOrDefault(candidate => string.Equals(candidate.Id, music.LibraryId, StringComparison.OrdinalIgnoreCase));
        if (library is not { IsConfigured: true })
        {
            return;
        }

        _loading = true;
        StateHasChanged();
        try
        {
            _choices = music.Pick switch
            {
                BackgroundMusicPick.Playlist => await library.GetPlaylistsAsync(CancellationToken.None),
                BackgroundMusicPick.Genres => await library.GetGenresAsync(CancellationToken.None),
                _ => await library.GetArtistsAsync(CancellationToken.None)
            };
            if (_choices.Count == 0)
            {
                _problem = music.Pick == BackgroundMusicPick.Playlist ? $"{library.Name} has no music playlists." : $"{library.Name} has none.";
            }
        }
        catch (Exception ex)
        {
            _problem = $"Couldn't ask {library.Name}: {ex.Message}";
            _choicesFor = null;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string SourceValue(BackgroundMusic? music) =>
        music is null ? "" : music.FromLibrary ? "library:" + music.LibraryId : "folder";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        BackgroundMusic? music = Value;
        IMusicLibrary[] libraries = Libraries;
        string source = SourceValue(music);

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "music-picker");

        builder.OpenElement(2, "label");
        builder.AddAttribute(3, "class", "field");
        builder.OpenElement(4, "span");
        builder.AddContent(5, Label);
        builder.CloseElement();
        builder.OpenElement(6, "select");
        builder.AddAttribute(7, "value", source);
        builder.AddAttribute(8, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => ChooseSourceAsync(e.Value?.ToString() ?? "")));
        if (AllowNone || music is null)
        {
            Option(builder, 9, "", NoneLabel);
        }

        if (AllowFolder || music is { FromLibrary: false })
        {
            Option(builder, 10, "folder", "A folder on disk");
        }

        foreach (IMusicLibrary library in libraries)
        {
            builder.OpenElement(11, "option");
            builder.SetKey(library.Id);
            builder.AddAttribute(12, "value", "library:" + library.Id);
            builder.AddContent(13, library.IsConfigured ? $"{library.Name} (music library)" : $"{library.Name} (music library, not set up)");
            builder.CloseElement();
        }

        if (music is { FromLibrary: true } && !libraries.Any(library => string.Equals(library.Id, music.LibraryId, StringComparison.OrdinalIgnoreCase)))
        {
            Option(builder, 14, source, $"{music.LibraryId} (not installed)");
        }

        builder.CloseElement();
        builder.CloseElement();

        if (music is { FromLibrary: false })
        {
            BuildFolder(builder, music);
        }
        else if (music is { FromLibrary: true })
        {
            BuildLibrary(builder, music, libraries);
        }

        builder.CloseElement();
    }

    private static void Option(RenderTreeBuilder builder, int sequence, string value, string text)
    {
        builder.OpenElement(sequence, "option");
        builder.AddAttribute(sequence + 100, "value", value);
        builder.AddContent(sequence + 200, text);
        builder.CloseElement();
    }

    private void BuildFolder(RenderTreeBuilder builder, BackgroundMusic music)
    {
        builder.OpenElement(20, "label");
        builder.AddAttribute(21, "class", "field");
        builder.OpenElement(22, "span");
        builder.AddContent(23, "Folder ");
        builder.OpenElement(24, "span");
        builder.AddAttribute(25, "class", "muted");
        builder.AddContent(26, "(its songs, shuffled)");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(27, "input");
        builder.AddAttribute(28, "type", "text");
        builder.AddAttribute(29, "value", music.Folder);
        builder.AddAttribute(30, "placeholder", FolderPlaceholder);
        builder.AddAttribute(31, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => ChangeAsync(copy => copy.Folder = e.Value?.ToString())));
        builder.CloseElement();
        builder.CloseElement();

        if (!string.IsNullOrWhiteSpace(music.Folder) && !Directory.Exists(music.Folder.Trim().Trim('"')))
        {
            Hint(builder, 32, MissingFolderText, warn: true);
        }
    }

    private void BuildLibrary(RenderTreeBuilder builder, BackgroundMusic music, IMusicLibrary[] libraries)
    {
        IMusicLibrary? library = libraries.FirstOrDefault(candidate => string.Equals(candidate.Id, music.LibraryId, StringComparison.OrdinalIgnoreCase));
        if (library is not { IsConfigured: true })
        {
            Hint(builder, 40, library is null
                ? "That music library's plugin isn't installed or is switched off; there'll be no music."
                : $"{library.Name} isn't set up yet (App settings); there'll be no music until it is.", warn: true);
            return;
        }

        builder.OpenElement(41, "label");
        builder.AddAttribute(42, "class", "field");
        builder.OpenElement(43, "span");
        builder.AddContent(44, "Songs");
        builder.CloseElement();
        builder.OpenElement(45, "select");
        builder.AddAttribute(46, "value", music.Pick.ToString());
        builder.AddAttribute(47, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this,
            e => ChangeAsync(copy => copy.Pick = Enum.TryParse(e.Value?.ToString(), out BackgroundMusicPick pick) ? pick : BackgroundMusicPick.All)));
        Option(builder, 48, nameof(BackgroundMusicPick.All), "Every song");
        Option(builder, 49, nameof(BackgroundMusicPick.Playlist), "A playlist");
        Option(builder, 50, nameof(BackgroundMusicPick.Genres), "Some genres");
        Option(builder, 51, nameof(BackgroundMusicPick.Artists), "Some artists");
        builder.CloseElement();
        builder.CloseElement();

        if (music.Pick == BackgroundMusicPick.All)
        {
            return;
        }

        if (_loading)
        {
            Hint(builder, 52, $"Asking {library.Name}…", warn: false);
            return;
        }

        if (_problem is { } problem)
        {
            Hint(builder, 53, problem, warn: true);
            return;
        }

        if (music.Pick == BackgroundMusicPick.Playlist)
        {
            builder.OpenElement(60, "label");
            builder.AddAttribute(61, "class", "field");
            builder.OpenElement(62, "span");
            builder.AddContent(63, "Playlist");
            builder.CloseElement();
            builder.OpenElement(64, "select");
            builder.AddAttribute(65, "value", music.PlaylistId ?? "");
            builder.AddAttribute(66, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => ChangeAsync(copy =>
            {
                string id = e.Value?.ToString() ?? "";
                copy.PlaylistId = id.Length == 0 ? null : id;
                copy.PlaylistName = _choices.FirstOrDefault(choice => choice.Id == id)?.Name;
            })));
            Option(builder, 67, "", "Choose…");
            foreach (MusicChoice playlist in _choices)
            {
                builder.OpenElement(68, "option");
                builder.SetKey(playlist.Id);
                builder.AddAttribute(69, "value", playlist.Id);
                builder.AddContent(70, playlist.Count is { } count ? $"{playlist.Name} ({count})" : playlist.Name);
                builder.CloseElement();
            }

            builder.CloseElement();
            builder.CloseElement();
            if (string.IsNullOrEmpty(music.PlaylistId))
            {
                Hint(builder, 71, "Choose a playlist, or there'll be no music.", warn: true);
            }

            return;
        }

        bool genres = music.Pick == BackgroundMusicPick.Genres;
        List<string> chosen = genres ? music.Genres : music.Artists;
        builder.OpenElement(80, "div");
        builder.AddAttribute(81, "class", "field");
        builder.OpenElement(82, "span");
        builder.AddContent(83, $"{(genres ? "Genres" : "Artists")} ({chosen.Count} chosen)");
        builder.CloseElement();
        builder.OpenElement(84, "input");
        builder.AddAttribute(85, "type", "search");
        builder.AddAttribute(86, "placeholder", "Filter");
        builder.AddAttribute(87, "value", _filter);
        builder.AddAttribute(88, "style", "margin-bottom:6px;");
        builder.AddAttribute(89, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => _filter = e.Value?.ToString() ?? ""));
        builder.CloseElement();
        builder.OpenElement(90, "div");
        builder.AddAttribute(91, "style", "max-height:220px;overflow:auto;display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:2px 12px;");
        foreach (MusicChoice choice in _choices.Where(choice => _filter.Length == 0 || choice.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)).Take(300))
        {
            bool on = chosen.Contains(choice.Name, StringComparer.OrdinalIgnoreCase);
            builder.OpenElement(92, "label");
            builder.SetKey(choice.Id);
            builder.AddAttribute(93, "class", "check");
            builder.AddAttribute(94, "style", "margin:0;");
            builder.OpenElement(95, "input");
            builder.AddAttribute(96, "type", "checkbox");
            builder.AddAttribute(97, "checked", on);
            builder.AddAttribute(98, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => ChangeAsync(copy =>
            {
                List<string> list = genres ? copy.Genres : copy.Artists;
                list.RemoveAll(name => string.Equals(name, choice.Name, StringComparison.OrdinalIgnoreCase));
                if (e.Value is true)
                {
                    list.Add(choice.Name);
                }
            })));
            builder.CloseElement();
            builder.OpenElement(99, "span");
            builder.AddContent(100, choice.Name);
            builder.CloseElement();
            builder.CloseElement();
        }

        builder.CloseElement();
        builder.CloseElement();
        if (chosen.Count == 0)
        {
            Hint(builder, 101, $"Choose at least one {(genres ? "genre" : "artist")}, or there'll be no music.", warn: true);
        }
    }

    private static void Hint(RenderTreeBuilder builder, int sequence, string text, bool warn)
    {
        builder.OpenElement(sequence, "p");
        builder.AddAttribute(sequence + 1000, "class", warn ? "hint warn" : "hint");
        builder.AddContent(sequence + 2000, text);
        builder.CloseElement();
    }

    private async Task ChooseSourceAsync(string value)
    {
        BackgroundMusic? next = value switch
        {
            "" => null,
            "folder" => new BackgroundMusic { Folder = Value is { FromLibrary: false } current ? current.Folder : null },
            _ when value.StartsWith("library:", StringComparison.Ordinal) => Value is { FromLibrary: true } same && same.LibraryId == value["library:".Length..]
                ? same.Copy()
                : new BackgroundMusic { LibraryId = value["library:".Length..] },
            _ => Value?.Copy()
        };
        _filter = string.Empty;
        await ValueChanged.InvokeAsync(next);
    }

    private async Task ChangeAsync(Action<BackgroundMusic> change)
    {
        BackgroundMusic copy = Value?.Copy() ?? new BackgroundMusic();
        change(copy);
        await ValueChanged.InvokeAsync(copy);
    }
}
