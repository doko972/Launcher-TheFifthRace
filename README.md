# The Fifth Race — Launcher

The updater for [The Fifth Race](https://thefifthrace.online), a fan-made
private server for the 2008 MMO *Stargate Worlds*. It downloads server patches,
verifies them, and starts the game.

**This repository exists so you can read the code before you run it.** Some
antivirus engines flag the released binary. That is explained in full below,
with the evidence — and with what you can do instead of taking our word for it.

---

## Why your antivirus may flag this

Windows Defender reports `Trojan:Win32/Wacatac.B!ml` on some machines.

The important part is the **`!ml` suffix**: it means the verdict came from a
machine-learning model, not from a signature matching known malware. Defender is
saying *"this looks unusual"*, not *"I recognise this threat"*.

VirusTotal told the same story for release 2.0.0 — **7 engines out of 71**, and
every single one of them heuristic:

| Engine | Verdict |
|---|---|
| Malwarebytes | `MachineLearning/Anomalous.95%` |
| SentinelOne | `Static AI - Suspicious PE` |
| McAfee | `Real Protect-LS!…` (behavioural) |
| Elastic | `Malicious (moderate Confidence)` |
| Arctic Wolf | `Unsafe` |
| MaxSecure, SecureAge | generic |

VirusTotal's own summary label was **`trojan.anomalous/machinelearning`**.

Meanwhile every signature-based engine — Kaspersky, BitDefender, ESET, Avast,
AVG, Avira and the rest of the remaining 64 — returned **Undetected**. Real
malware is normally the other way round: signature engines hit it, because
someone has already analysed and catalogued it.

### What actually triggers the models

Nothing here is hidden, and every item is a legitimate part of being an updater.
Taken together, though, they look like a downloader:

- it **downloads executables** and writes them to disk (game patches, and
  Microsoft's WebView2 installer if that component is missing);
- it **replaces its own binary** when a launcher update is published;
- it **starts another executable** (the game);
- it **reads the registry** to find where you installed the original game;
- and, until version 2.1.0, it was **unsigned with no version metadata at all** —
  no publisher, no product name, version `0.0.0.0`.

That last point matters more than people expect. Almost all legitimate software
fills those fields in, so an empty version resource is itself a signal.

### What changed in 2.1.0

- Real version metadata: publisher, product, description, version.
- The self-update helper script no longer lives in `%TEMP%`, no longer runs
  hidden, and no longer deletes itself — that combination is a dropper idiom.
- Embedded WebView2 assemblies are extracted to disk and loaded by path instead
  of `Assembly.Load(byte[])`, which is how packers unpack a payload.
- The WebView2 installer is downloaded into the launcher's own folder rather
  than the temp directory.

**We are not promising this makes every engine happy.** Without a code signing
certificate, an unsigned binary with no download history stays suspect to
heuristic models. A certificate is the only real fix and it is on the list.

---

## Don't trust us — build it yourself

The launcher is two C# files. You can read them in an afternoon:

| File | What it does |
|---|---|
| [`SGWLauncher.cs`](SGWLauncher.cs) | Window, WebView2 host, native dialogs |
| [`UpdateEngine.cs`](UpdateEngine.cs) | Manifest, hashing, downloads, self-update, launching the game |

### Building

You need nothing but Windows. The compiler ships with the .NET Framework, which
is already on your machine:

```powershell
git clone https://github.com/doko972/Launcher-TheFifthRace.git
cd Launcher-TheFifthRace
.\build.ps1
```

The result lands in `build\SGWLauncher.exe`, and the script prints its SHA-256.

There is no package restore, no SDK to install, no network access required —
the WebView2 assemblies are vendored in [`lib/`](lib/), with their origin and
hashes documented in [`lib/README.txt`](lib/README.txt) so you can check them
against the official NuGet package.

### A caveat about reproducibility

**Your build will not have the same SHA-256 as ours, and that is expected.**

The `csc.exe` bundled with .NET Framework 4.8 is the legacy compiler
(version 4.8.9221.0). It does not support `/deterministic`, and it stamps a
fresh MVID and timestamp into every compilation. Two builds of *identical*
source on the *same* machine already produce different hashes — we tested it.

So comparing hashes proves nothing here. What you can do instead:

1. **Read the source** and decide whether you trust it.
2. **Run your own build** rather than our download. It is the same program.
3. **Submit our binary to VirusTotal yourself** instead of trusting a table in
   a README written by the people who made it.

If byte-for-byte reproducibility matters to you, it would require moving to the
Roslyn compiler with `<Deterministic>true</Deterministic>`, at the cost of the
"no tools to install" property this build deliberately keeps.

---

## What the launcher does at runtime

| Path | Purpose |
|---|---|
| `%LOCALAPPDATA%\SGWLauncher\SGWLauncher.ini` | Settings: game path, shard, update URL |
| `%LOCALAPPDATA%\SGWLauncher\runtime\` | WebView2 assemblies extracted at first run |
| `%LOCALAPPDATA%\SGWLauncher\webview\` | WebView2 profile data |
| `%LOCALAPPDATA%\SGWLauncher\maj-launcher.cmd` | Self-update helper, deleted on next start |

It talks to exactly two hosts: `thefifthrace.online` for the manifest and
patches, and `go.microsoft.com` for the WebView2 installer — and only after you
agree to a prompt.

Setting `SelfUpdate=0` in the `.ini` disables the self-update entirely, if you
would rather decide for yourself when to upgrade.

---

## Third-party components

| Component | Licence |
|---|---|
| [`lib/*.dll`](lib/) — Microsoft.Web.WebView2 SDK 1.0.4129.50 | Microsoft's, redistributed under the terms shipping with the SDK |
| [`ui/fonts/`](ui/fonts/) — Orbitron, Rajdhani | SIL Open Font License 1.1, see [`ui/fonts/OFL.txt`](ui/fonts/OFL.txt) |

Artwork under `ui/` depicts material from the Stargate franchise and is used
here as part of a non-commercial fan project.

*The Fifth Race is a fan-made project, not affiliated with or endorsed by MGM or
Amazon Studios. Stargate and Stargate Worlds are the property of their
respective owners.*
