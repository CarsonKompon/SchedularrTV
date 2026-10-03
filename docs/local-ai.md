# Local AI writer

The Local AI writer plugin lets a small language model word your promos' voice-overs and on-screen text, instead
of the fill-in-the-blanks wording. Rather than *"Coming up next... SpongeBob. Then SpongeBob at 1:30, and then
SpongeBob at 2!"* it can say *"Coming up next, SpongeBob, followed by even more SpongeBob!"*, and, given the
episode's description from your library, crack a joke about it before it starts.

It's free: the model runs on your own computer (or on an Ollama or LM Studio server you already have). There's no
account, no API key and nothing is sent anywhere.

## Setting it up

1. Install **Local AI writer** from the **Plugins** page (it's in the all-plugins zip).
2. Open its settings (the gear) and, under **Built-in AI**:
   - **Download** the AI engine (llama.cpp built for your computer, a few tens of MB).
   - **Download** a model. **Qwen3.5 2B** (1.3 GB) is the one to start with: a few seconds per promo on an
     ordinary processor. **0.8B** (0.5 GB) is quicker but plainer; **4B** (2.7 GB) is funnier and more reliable
     but about twice as slow and wants 8 GB of memory or more.
   - Downloads carry on if you close the dialog. Choose the model you want with **Use this**, and save.
3. Try it in the **Try it** box at the bottom of the settings.

The model is loaded when it's first needed and let go of after **Free memory after** minutes without use, so it
isn't holding a gigabyte or more all day.

### Using Ollama or LM Studio instead

If you already run [Ollama](https://ollama.com) or [LM Studio](https://lmstudio.ai), set **Server type** and
**Server address** (leave it empty for the usual one), press **Find models on the server** and pick one. It shows
up as a second writer, **Local AI server**, which you can choose per template. Anything with an OpenAI-style chat
API works (llama.cpp's own server, LocalAI, and so on); a paid service would too, with its key, though nothing
needs one.

With Ollama, `ollama pull qwen3.5:2b` gets the same model the built-in writer uses. A server with a graphics card
is much quicker than the built-in writer, which runs on the processor (on Apple silicon it uses the GPU).

### Your own model

Any instruction-tuned GGUF model works: set **Your own model file** and choose it with **Use this**. Keep to small
ones (1 to 4 billion parameters); a promo has to be written in seconds, not minutes.

## Using it

### Voice-overs (Generated promos)

In a promo template's **Voice-over** section, tick **Have AI word it naturally**. Then:

- **Written by**: which writer (the first one that's ready, unless you pick one).
- **How it should sound**: a style, such as *an excitable late-90s cartoon channel announcer*.
- **Anything else it should do**: extra direction, such as *mention it's the Saturday morning marathon*.
- **At most (words)**: worked out from the promo's length when left empty.
- **Written by AI _n_% of the time**: below 100%, the usual wording is used the rest of the time.
- **Tell it what the episode is about**: hands it the episode's description from your library, so it can say
  something about the plot.
- **Try it** writes one with the channel's next shows; each try is different.

The model is given the shows (marking a show that's on again), when they're on, the channel, the description, and
the usual read-out for reference. Your openers and closers still shape that usual wording.

### Captions (Generated promos and Overlays)

A **Text** layer in a promo template, or in an overlay's design, has **Have AI write it**. The text you typed is
the fallback. Say what you want in **What to write**, such as *a five-word tagline teasing the episode*. An overlay
caption is written once per episode and shared by its repeats.

## When it isn't used

Small models make things up now and then, so everything they write is checked first. Writing is set aside, and
the usual wording used instead, when:

- it leaves out one of the shows the promo is for,
- it mentions a number (a time, say) or a day ("tomorrow") it wasn't given,
- it runs well past the word limit, or adds formatting.

It gets a second try before giving up. The usual wording is also used when no writer is set up yet, or the model
or server fails. Nothing ever goes out silent or blank because of it.

## How it fits together

- **Promos** are written when they're rendered, ahead of airing, so the time it takes never shows. What's written
  is kept (in the data folder's `ai-text` folder, for two months after its last use), so a re-render or a repeat
  airing doesn't write it again.
- **Overlay captions** are written in the background for programmes coming up in the next day and a half, sooner
  ones first. Until a caption is written the overlay shows its usual text; once it is, the schedule is published
  again with it.
- Only one piece is written at a time.

## In the schedule file

```yaml
generated-promos:
  templates:
    - name: Up Next
      voice:
        provider: tiktok
        voice: en_us_006
        writer:
          style: An excitable late-90s cartoon channel announcer
          prompt: Mention it's the weekend
          use_descriptions: true
          max_words: 0        # 0 works it out from the promo's length
          chance: 1           # 0 to 1
          generator: local-ai # or local-ai-server; leave out for the first one that's ready
      layers:
        - name: Tagline
          kind: text
          text: "{channel}"   # the fallback
          writer:
            prompt: A five-word tagline teasing the episode
```

Overlay text layers take the same `writer:` section.

## For plugin authors

Writers are a plugin extension point (API 1.5): implement `ITextGenerator` and register it with
`services.AddTextGenerator<T>()`. The scheduler's `AiTextWriter` does the prompting, checking, fallback and
keeping, so a writer only has to turn a `TextGenerationRequest` (instructions, prompt, token limit, temperature and
seed) into text.
