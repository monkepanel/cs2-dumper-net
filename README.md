# Used in Production by [Monke Panel](https://www.monkepanel.com)

# cs2-dumper (.NET AOT port)

An external offset / interface / schema dumper for **Counter-Strike 2**, rewritten in
**NativeAOT-compatible .NET 8 (C#)**. It is a faithful port of the Rust tool
[a2x/cs2-dumper](https://github.com/a2x/cs2-dumper): it reads the live game process, scans for
offsets and the Source 2 schema system, and emits the results as `cs`, `hpp`, `json`, `rs` and
`zig` files that are byte-for-byte compatible with the original.

The whole thing compiles to a **single self-contained native `.exe`** (~1–2 MB) with no .NET
runtime dependency, no GC-visible reflection, and no JIT.

## What it dumps

For every run it produces, in the output directory:

- `buttons.*` — client.dll key-button state RVAs
- `interfaces.*` — `CreateInterface` registrations per module
- `offsets.*` — signature-scanned globals (`dwEntityList`, `dwLocalPlayerPawn`, …)
- `<module>_dll.*` — the full Source 2 schema (classes, fields, enums, metadata) per module
- `info.json` — build number + timestamp

…in each requested file type (`cs`, `hpp`, `json`, `rs`, `zig` by default).

## Requirements

- **To run:** 64-bit Windows, CS2 running (the main menu is enough), and permission to read the
  game process (run as administrator if a read is denied). The published `.exe` needs nothing else.
- **To build:** the .NET SDK (8 or newer — this repo was built with SDK 10 targeting `net8.0`).
  For `PublishAot`, the Visual C++ build tools (the MSVC linker + Windows SDK) must be installed.

## Build & run

The solution and both projects live under `src/`. Build everything at once with
`dotnet build src/cs2-dumper.slnx`, or work with the dumper project directly.

Fast iteration (framework-dependent JIT build):

```pwsh
dotnet run --project src/cs2-dumper -c Release -- -vv
```

Produce the optimized native AOT executable:

```pwsh
dotnet publish src/cs2-dumper -c Release
# -> src/cs2-dumper/bin/Release/net8.0/win-x64/publish/cs2-dumper.exe
```

Then, with CS2 running:

```pwsh
.\cs2-dumper.exe -vv
```

## Usage

```
Usage: cs2-dumper [OPTIONS]

  -f, --file-types <list>      File types to generate [default: cs,hpp,json,rs,zig]
  -i, --indent-size <n>        Spaces per indentation level [default: 4]
  -o, --output <dir>           Output directory [default: output]
  -p, --process-name <name>    Game process name [default: cs2.exe]
  -v...                        Increase logging verbosity (repeatable: -v, -vv, -vvv)
  -n, --no-log-file            Do not create cs2-dumper.log
  -c, --connector <name>       Accepted for CLI compatibility, ignored (see below)
  -a, --connector-args <args>  Accepted for CLI compatibility, ignored
  -h, --help                   Print help
  -V, --version                Print version
```

## Differences from the Rust original

- **Memory backend.** The original is powered by [memflow](https://github.com/memflow/memflow)
  and can use external connectors (pcileech, kvm, …). This port reads the target directly through
  the Windows API (`OpenProcess` + `ReadProcessMemory`, modules via Toolhelp32) — the equivalent of
  the original's default `memflow-native` path. The `--connector` / `--connector-args` flags are
  still parsed for CLI compatibility but ignored.
- **Windows only.** The native reader targets Win32; the Linux branch is not ported.
- Everything else — the pattern-scanning engine, the Source 2 struct layouts, the schema-system
  traversal, and all five output formatters — is a direct, behavior-preserving port.

## Architecture

The layout mirrors the Rust crate so the two can be diffed side by side:

| C# (`src/cs2-dumper/`)          | Rust (`src/`)                  | Responsibility |
|---------------------------------|--------------------------------|----------------|
| `Memory/Win32`, `GameProcess`   | `memflow` + `memflow-native`   | open process, enumerate modules, read memory |
| `Memory/Address`                | `memory/address.rs`            | RIP-relative resolution |
| `Pe/PeImage`                    | `pelite` (PE parsing, exports) | PE64 headers, code range, export lookup |
| `Pe/Pattern`                    | `pelite` (`pattern`, `scanner`)| signature parser + interpreter + scanner |
| `Source2/Layout`                | `source2/**`                   | struct field offsets |
| `Analysis/*`                    | `analysis/*`                   | buttons / interfaces / offsets / schemas |
| `Output/*`                      | `output/*`                     | Formatter, heck casing, JSON, 5 writers |
| `Cli/Args`, `Logging/Log`       | `clap`, `simplelog`            | argument parsing, logging |

The pattern engine reproduces pelite's atoms, parser and interpreter exactly; the JSON writer
reproduces `serde_json::to_string_pretty` exactly; the indentation `Formatter` and `heck` casing
match the originals.

## Verification

`src/verify/` is an offline test harness that builds the dumper's own source into a test assembly
and checks the deterministic half of the pipeline against ground truth:

- the pattern parser and interpreter against **pelite's own test vectors**;
- `buttons` / `interfaces` / `offsets`, reconstructed from the reference repo's committed
  `output/*.json`, regenerated in all five formats and compared **byte-for-byte**;
- every committed `*.json` round-tripped through the JSON writer **byte-for-byte**
  (serde_json compatibility);
- the schema writers' enum/metadata/field formatting.

Run it (the reference-file checks need a checkout of the upstream repo's `output/` directory; the
rest run standalone):

```pwsh
dotnet run --project src/verify -- path\to\a2x-cs2-dumper\output
```

## License

The original cs2-dumper is MIT-licensed by a2x. This port is provided under the same terms.
Intended for offline analysis, modding and educational use.
