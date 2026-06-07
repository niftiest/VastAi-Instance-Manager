# VastSpinup

Rent a vast.ai GPU, get [Ollama](https://ollama.com) running on it, and wire
[OpenCode](https://opencode.ai) up to it. All from one Windows app, in a few clicks,
without the manual SSH and config dance.

![Main window](screenshots/main_screen.jpg)

## Features

**Find a provider fast**
- Live offer search filtered to providers that fit your chosen model (VRAM, disk, reliability)
- Three smart-pick shortcuts at the bottom of the list: **BEST PRICE**, **FASTEST**,
  and **BEST VALUE** (rewards real throughput, not just cheap-but-slow GPUs)
- **Cached** column shows providers you have used before so warm hosts come up first
- Per-provider notes you can attach (double-click any row) and favorites that follow the
  host across rentals

**One-click rent**
- Creates the instance, attaches an ephemeral SSH key, waits for the Ollama daemon,
  pulls your model, and smoke-tests it
- Tracks per-host boot and Ollama-ready times so the second rental on a known good
  host shows a real time estimate
- Live progress bar and status messages through every phase

**Catch broken setups before you waste time**
- If the smoke test fails the app tells you whether the **host** is broken (different
  provider needed), the **model** is incompatible, or only **multimodal** support
  failed and text-only models will still work
- Bad providers get blacklisted for the session; multimodal-incompatible instances
  hide multimodal models from the Change Model picker

**Run more than one instance at a time**
- Keep multiple instances live, switch between them, and explicitly wire your local
  OpenCode at whichever one you want with the **Use** button
- **Change Model** swaps the loaded model on a running instance without rebuilding it
  (unload, delete, pull new, smoke-test, done)

**Add new models without leaving the app**

The Search Hugging Face dialog pre-loads the 100 most-liked GGUF repos so you land
on something useful, then auto-derives the Ollama tag, model size, recommended
sampling params, modality (`text` / `+vis` / `+aud` / `+v/a` / `multi`), and a
tools-capability check from the Ollama library.

![HF model search](screenshots/model_search.jpg)

Pick a row and every field on the model edit dialog pre-populates from what was
found, so you only touch the ones you want to override.

![Edit model](screenshots/model_settings.jpg)

## Get started

Grab the latest release from the [Releases](../../releases) page, unzip, and run
`VastSpinup.Wpf.exe`. First launch will prompt for your vast.ai API key.

**Requires:** Windows 10/11 and the
[.NET 8 Desktop Runtime](https://aka.ms/dotnet/download) (one-time install, ~50 MB,
kept current via Windows Update).

## Build from source

```powershell
dotnet build VastSpinup.sln
dotnet run --project VastSpinup.Wpf
```

## License

[MIT](LICENSE) (c) cpaec
