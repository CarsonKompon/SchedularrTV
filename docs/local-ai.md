# Local AI writer

**Local AI writer** generates promo scripts and captions through a local model or a configured AI server. Standard template text remains the fallback when generation fails or validation rejects the result.

## Set up the built-in writer

1. Install **Local AI writer** and restart.
2. Open **Plugins → Local AI writer → Settings**.
3. Download the engine under **Built-in AI**.
4. Download a model and select **Use this**.
5. Save and test with **Try it**.

| Model | Approximate download | Use |
| --- | --- | --- |
| **Qwen3.5 0.8B** | 0.5 GB | Lower-memory systems |
| **Qwen3.5 2B** | 1.3 GB | General-purpose starting point |
| **Qwen3.5 4B** | 2.7 GB | Higher quality with more memory and compute |

The model loads on demand and unloads after **Free memory after** minutes. **Your own model file** accepts instruction-tuned GGUF models.

## Use Ollama or LM Studio

1. Set **Server type** and **Server address** in plugin settings.
2. Select **Find models on the server**.
3. Choose a model and save.
4. Select **Local AI server** in the template's writer options.

Other OpenAI-compatible chat servers can use the same integration. Prompts go to the configured server; built-in inference stays on the scheduler computer.

For Ollama, download the model with **`ollama pull`**:

```sh
ollama pull qwen3.5:2b
```

## Generate voice-over scripts

1. Open a generated promo template's **Voice-over** section.
2. Enable **Have AI word it naturally**.
3. Choose **Written by** and describe **How it should sound**.
4. Set optional directions, word limit, generation percentage, and episode-description access.
5. Select **Try it**, then save the schedule.

The writer produces text; a separate [voice plugin](generated-promos.md#voices) produces speech.

## Generate captions

1. Select a **Text** layer in a promo or overlay.
2. Enter fallback text.
3. Enable the AI-writing option and describe **What to write**.
4. Save the schedule.

Overlay captions are prepared ahead of airing and shared by repeat episodes. Promos are written during rendering; successful text is cached under **`ai-text/`**.

## In the schedule file

Add **`writer`** to a generated promo's voice or a supported text layer:

```yaml
writer:
  generator: local-ai
  style: An energetic cartoon channel announcer
  prompt: Mention the weekend marathon
  use_descriptions: true
  max_words: 0
  chance: 1
```

**`max_words: 0`** derives the limit from promo duration; **`chance`** ranges from `0` to `1`. Use **`generator: local-ai-server`** for the configured server.

## Rejected or missing output

Generated text is checked for missing shows, unsupported times or days, excessive length, and formatting. A failed retry uses standard wording; unavailable writers also fall back.

Plugin authors can implement **`ITextGenerator`**; prompting, checks, fallback, and caching belong to the host. See [Plugin development](plugin-development.md).
