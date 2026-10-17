using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Service.Writing;

namespace SchedularrTV.GeneratedPromos;

/// <summary>A curated promo ready to render: its voice-over, placed in the promo's time, and where its clips go.</summary>
public sealed record CuratedRender(string VoicePath, CuratedTimeline Timeline, string Plan);

/// <summary>
/// How a curated promo's plan came about, for troubleshooting in the editor: the direction it was given, the moments
/// it could pick from, the model's reply as it came, and the plan that was used (or why none was).
/// </summary>
public sealed record CurationReport
{
    /// <summary>Why the promo wasn't curated; null when it was.</summary>
    public string? Problem { get; init; }

    public string? Direction { get; init; }

    public string? Opener { get; init; }

    public string? Closer { get; init; }

    /// <summary>The moments the AI was shown, one per line: number, where in the episode, length, what's said.</summary>
    public string? Moments { get; init; }

    /// <summary>The model's reply as it came (every try, when one was set aside); null when it wasn't asked or it wasn't kept.</summary>
    public string? Raw { get; init; }

    /// <summary>The plan as it was laid out: each line, pause and clip, at its time in the promo.</summary>
    public string? Plan { get; init; }
}

// Layers curated by AI: the episode transcribed, its moments picked and talked about, the lines spoken and placed.
public sealed partial class GeneratedPromoService
{
    /// <summary>
    /// The curated promo's voice-over and clips, or null when it can't be curated (no speech recognition or writer set
    /// up, nothing usable said in the episode, or a plan that didn't pass the checks): it's then made the usual way.
    /// Either way, how it went.
    /// </summary>
    private async Task<(CuratedRender? Render, CurationReport? Report)> CurateAsync(ResolvedGeneratedPromo promo, GeneratedVoiceConfig voice,
        CancellationToken cancellationToken)
    {
        if (promo.Curation is not { } request)
        {
            return (null, null);
        }

        var report = new CurationReport
        {
            Direction = request.Direction,
            Opener = CuratedPromo.Fixed(request.Opener, request),
            Closer = CuratedPromo.Fixed(request.Closer, request)
        };
        if (Writer is not { } writer)
        {
            return (null, report with { Problem = "the AI writer isn't available" });
        }

        Transcript? transcript = await Transcripts.GetAsync(request.EpisodePath, request.Config.Transcriber, cancellationToken).ConfigureAwait(false);
        if (transcript is null)
        {
            string why = Transcripts.LastProblem(request.EpisodePath, request.Config.Transcriber) ?? "it couldn't be made";
            Log.LogInformation("Generated promo '{Template}': no transcript of {File} ({Problem}), so its clips aren't curated.",
                promo.Template.Name, Path.GetFileName(request.EpisodePath), why);
            return (null, report with { Problem = $"no transcript of {Path.GetFileName(request.EpisodePath)}: {why}" });
        }

        IReadOnlyList<CuratedMoment> moments = CuratedPromo.Moments(transcript, request.Config, request.Show.Runtime);
        report = report with { Moments = string.Join('\n', moments.Select(moment => CuratedPromo.DescribeMoment(moment))) };
        if (moments.Count == 0)
        {
            Log.LogInformation("Generated promo '{Template}': nothing usable is said in {File}, so its clips aren't curated.",
                promo.Template.Name, Path.GetFileName(request.EpisodePath));
            return (null, report with { Problem = $"nothing usable is said in {Path.GetFileName(request.EpisodePath)} ({transcript.Segments.Count} lines transcribed; try other clip lengths)" });
        }

        AiTextResult plan = await PlanAsync(writer, request, moments, null, cancellationToken).ConfigureAwait(false);
        report = report with { Raw = plan.Raw };
        if (!plan.Written)
        {
            string why = plan.Problem ?? "the AI's chance setting chose the usual wording this time";
            Log.LogInformation("Generated promo '{Template}': the curated plan wasn't used ({Problem}).", promo.Template.Name, why);
            return (null, report with { Problem = $"the plan wasn't used: {why}" });
        }

        // The template's own opener and closer go around the AI's lines.
        IReadOnlyList<CuratedBeat> beats = CuratedPromo.WithFixedLines(CuratedPromo.Beats(plan.Text), request);
        List<(string Path, TimeSpan Length)> spoken = [];
        foreach (CuratedBeat beat in beats.Where(beat => beat.Voice is not null))
        {
            string line = await VoiceAsync(voice, beat.Voice!, cancellationToken).ConfigureAwait(false);
            spoken.Add((line, await ProbeAsync(line, cancellationToken).ConfigureAwait(false)));
        }

        CuratedTimeline timeline = CuratedPromo.Layout(beats, [.. spoken.Select(line => line.Length)], moments, request);
        report = report with { Plan = CuratedPromo.DescribeTimeline(timeline) };
        if (timeline.Lines.Count == 0)
        {
            return (null, report with { Problem = "nothing was left for the announcer to say once it was fitted to the promo" });
        }

        // Each laid-out line, matched back to its file by its words (lines left out to fit aren't in the timeline).
        List<(string Path, CuratedLine Line)> placed = [];
        int next = 0;
        List<CuratedBeat> said = [.. beats.Where(beat => beat.Voice is not null)];
        foreach (CuratedLine line in timeline.Lines)
        {
            while (next < said.Count && !string.Equals(said[next].Voice, line.Text, StringComparison.Ordinal))
            {
                next++;
            }

            if (next < said.Count)
            {
                placed.Add((spoken[next].Path, line));
                next++;
            }
        }

        string mix = await MixVoiceAsync(placed, promo.Template.Duration, cancellationToken).ConfigureAwait(false);
        return (new CuratedRender(mix, timeline, plan.Text), report);
    }

    /// <summary>
    /// The curated promo with its clips placed as the plan already written says (lines estimated rather than spoken),
    /// for editor stills; otherwise as it was, queueing the transcript and the plan so a later still has them.
    /// </summary>
    public ResolvedGeneratedPromo PeekCuration(ResolvedGeneratedPromo promo)
    {
        if (promo.Curation is not { } request || Writer is not { } writer
            || Transcripts.Peek(request.EpisodePath, request.Config.Transcriber) is not { } transcript)
        {
            return promo;
        }

        IReadOnlyList<CuratedMoment> moments = CuratedPromo.Moments(transcript, request.Config, request.Show.Runtime);
        if (moments.Count == 0 || PeekPlan(writer, request, moments) is not { Written: true } plan)
        {
            return promo;
        }

        IReadOnlyList<CuratedBeat> beats = CuratedPromo.WithFixedLines(CuratedPromo.Beats(plan.Text), request);
        CuratedTimeline timeline = CuratedPromo.Layout(beats, [.. beats.Where(beat => beat.Voice is not null).Select(beat => CuratedPromo.Estimate(beat.Voice!))],
            moments, request);
        return promo with { CuratedClips = timeline.Clips };
    }

    /// <summary>
    /// For the editor's "Try it": a fresh plan for the promo as it would air now, written out with what each clip says.
    /// Transcribes the episode first if it hasn't been (which can take minutes). Says why when it can't be used.
    /// </summary>
    public async Task<AiTextResult> TryCurationAsync(ResolvedGeneratedPromo? promo, string seed, CancellationToken cancellationToken)
    {
        if (promo?.Curation is not { } request)
        {
            return new AiTextResult(promo?.Script ?? string.Empty, false,
                "there's no episode to curate yet (the layer's show isn't on the schedule, or its file can't be read here)");
        }

        if (Writer is not { } writer)
        {
            return new AiTextResult(request.Fallback, false, "the AI writer isn't available here");
        }

        if (Transcripts.Problem(request.Config.Transcriber) is { } setup)
        {
            return new AiTextResult(request.Fallback, false, setup);
        }

        Transcript? transcript = await Transcripts.GetAsync(request.EpisodePath, request.Config.Transcriber, cancellationToken).ConfigureAwait(false);
        if (transcript is null)
        {
            return new AiTextResult(request.Fallback, false,
                $"the episode couldn't be transcribed: {Transcripts.LastProblem(request.EpisodePath, request.Config.Transcriber) ?? "no reason given"}");
        }

        IReadOnlyList<CuratedMoment> moments = CuratedPromo.Moments(transcript, request.Config, request.Show.Runtime);
        if (moments.Count == 0)
        {
            return new AiTextResult(request.Fallback, false, "nothing usable is said in the episode (try longer clips)");
        }

        AiTextResult result = await PlanAsync(writer, request, moments, seed, cancellationToken).ConfigureAwait(false);
        // The AI's replies as they came go underneath, for troubleshooting.
        string raw = result.Raw is { Length: > 0 } reply ? $"\n\n— The AI's reply, as it came —\n{reply}" : string.Empty;
        return result.Written
            ? result with { Text = CuratedPromo.Describe(CuratedPromo.WithFixedLines(CuratedPromo.Beats(result.Text), request), moments) + raw }
            : result with { Text = result.Text + raw };
    }

    /// <summary>
    /// The promo's plan from the AI: with <see cref="GeneratedCuratedClips.PickClipsFirst"/>, in two requests (the moments
    /// picked, then the voice-over written around just those), otherwise in one. When the picking doesn't work, one
    /// request does both. The result's <see cref="AiTextResult.Raw"/> holds every step's replies.
    /// </summary>
    /// <param name="trySeed">For the editor's "Try it": a fresh attempt with this seed, not kept. Null writes (or reuses) what airs.</param>
    private static async Task<AiTextResult> PlanAsync(AiTextWriter writer, CuratedPromoRequest request, IReadOnlyList<CuratedMoment> moments,
        string? trySeed, CancellationToken cancellationToken)
    {
        async Task<AiTextResult> Ask(AiTextJob job) => trySeed is null
            ? await writer.WriteAsync(job, cancellationToken).ConfigureAwait(false)
            : await writer.TryAsync(job with { Seed = $"{job.Seed}|try|{trySeed}" }, cancellationToken).ConfigureAwait(false);

        if (!request.Config.PickClipsFirst)
        {
            return await Ask(CuratedPromo.Job(request, moments)).ConfigureAwait(false);
        }

        AiTextResult pick = await Ask(CuratedPromo.PickJob(request, moments)).ConfigureAwait(false);
        IReadOnlyList<CuratedMoment> picked = pick.Written ? CuratedPromo.Picked(pick.Text, moments) : [];
        if (picked.Count == 0)
        {
            // Not written this time (the chance setting), or no writer: nothing more to ask.
            if (pick.Problem is null || pick.Raw is null)
            {
                return pick;
            }

            AiTextResult whole = await Ask(CuratedPromo.Job(request, moments)).ConfigureAwait(false);
            return whole with { Raw = Steps(("Step 1: picking the moments (didn't work, so one request did both)", pick.Raw), ("Doing both at once", whole.Raw)) };
        }

        AiTextResult plan = await Ask(CuratedPromo.WriteJob(request, picked)).ConfigureAwait(false);
        return plan with { Raw = Steps(("Step 1: picking the moments", pick.Raw), ("Step 2: writing around them", plan.Raw)) };
    }

    /// <summary>For editor stills: the plan if every step of it has been written already (queueing what hasn't); null otherwise.</summary>
    private static AiTextResult? PeekPlan(AiTextWriter writer, CuratedPromoRequest request, IReadOnlyList<CuratedMoment> moments)
    {
        if (!request.Config.PickClipsFirst)
        {
            return writer.Peek(CuratedPromo.Job(request, moments));
        }

        if (writer.Peek(CuratedPromo.PickJob(request, moments)) is not { Written: true } pick)
        {
            return null;
        }

        IReadOnlyList<CuratedMoment> picked = CuratedPromo.Picked(pick.Text, moments);
        return writer.Peek(picked.Count == 0 ? CuratedPromo.Job(request, moments) : CuratedPromo.WriteJob(request, picked));
    }

    private static string? Steps(params (string Title, string? Raw)[] steps)
    {
        List<string> parts = [.. steps.Where(step => step.Raw is { Length: > 0 }).Select(step => $"=== {step.Title} ===\n{step.Raw}")];
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>
    /// The curated lines as one voice-over the length of the promo, each starting where the plan puts it (and read at
    /// its pace), silent between. Kept with the other voice-overs, so the same plan isn't mixed twice.
    /// </summary>
    private async Task<string> MixVoiceAsync(IReadOnlyList<(string Path, CuratedLine Line)> lines, TimeSpan duration, CancellationToken cancellationToken)
    {
        string Seconds(TimeSpan value) => Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        string identity = string.Join('\u001f', lines.Select(line =>
            $"{Path.GetFileName(line.Path)}|{line.Line.At.Ticks}|{line.Line.Tempo.ToString("0.####", CultureInfo.InvariantCulture)}").Prepend($"curated-mix-v1|{duration.Ticks}"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        string path = Path.Combine(VoiceDirectory, $"{hash}.wav");
        if (File.Exists(path))
        {
            Touch(path);
            return path;
        }

        Directory.CreateDirectory(VoiceDirectory);
        string partial = Path.Combine(VoiceDirectory, $"{PartialPrefix}{Guid.NewGuid():N}.wav");
        var info = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-y"];
        foreach ((string file, _) in lines)
        {
            arguments.AddRange(["-i", file]);
        }

        // One after another: each line padded with silence until the next one starts (the first held back to its
        // start), so nothing is mixed on top of anything and levels stay as spoken.
        var filter = new StringBuilder();
        for (int index = 0; index < lines.Count; index++)
        {
            CuratedLine line = lines[index].Line;
            TimeSpan until = index + 1 < lines.Count ? lines[index + 1].Line.At : duration;
            filter.Append(CultureInfo.InvariantCulture, $"[{index}:a]aformat=sample_rates=44100:channel_layouts=mono");
            if (Math.Abs(line.Tempo - 1) > 0.001)
            {
                filter.Append(CultureInfo.InvariantCulture, $",atempo={Math.Clamp(line.Tempo, 0.5, 2).ToString("0.####", CultureInfo.InvariantCulture)}");
            }

            if (index == 0)
            {
                filter.Append(CultureInfo.InvariantCulture, $",adelay={Math.Round(line.At.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}");
                filter.Append(CultureInfo.InvariantCulture, $",apad=whole_dur={Seconds(until)}");
            }
            else
            {
                filter.Append(CultureInfo.InvariantCulture, $",apad=whole_dur={Seconds(until - line.At)}");
            }

            filter.Append(CultureInfo.InvariantCulture, $"[l{index}];");
        }

        for (int index = 0; index < lines.Count; index++)
        {
            filter.Append(CultureInfo.InvariantCulture, $"[l{index}]");
        }

        filter.Append(CultureInfo.InvariantCulture, $"concat=n={lines.Count}:v=0:a=1,atrim=end={Seconds(duration)}[voice]");
        arguments.AddRange(["-filter_complex", filter.ToString(), "-map", "[voice]", "-c:a", "pcm_s16le", "-f", "wav", partial]);
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0 || !File.Exists(partial))
            {
                throw new InvalidOperationException($"ffmpeg couldn't put the curated voice-over together: {(await error.ConfigureAwait(false)).Trim()}");
            }

            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(partial);
        }
    }
}
