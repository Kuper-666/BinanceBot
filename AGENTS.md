# AGENTS.md — BinanceBotWpf

## What this is

C# .NET 8.0 WPF desktop app — a Binance futures trading bot with ML signals, Telegram notifications, and live charts. Windows-only (DPAPI encryption, WPF).

## Solution structure

| Project | Target | Purpose |
|---|---|---|
| `BinanceBotWpf/` | net8.0-windows, WinExe | Main WPF app (MVVM) |
| `BinanceBotWpf.Tests/` | net8.0-windows, xUnit | Unit tests (require Windows for DPAPI) |
| `DataDownloader/` | net8.0, console exe | Standalone CLI for historical data download |

Entry point: `BinanceBotWpf/App.xaml.cs` → `OnStartup` → creates `TradingService` → `MainWindowViewModel` → `MainWindow.xaml`.

## Build & test commands

```bash
# Build entire solution
dotnet build BinanceBotWpf.sln

# Run all tests
dotnet test BinanceBotWpf.Tests/BinanceBotWpf.Tests.csproj

# Publish self-contained single-file exe (CI command)
dotnet publish BinanceBotWpf/BinanceBotWpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Tests **must** run on Windows — `SecureStringHelper` uses `System.Security.Cryptography.ProtectedData` (DPAPI), which is user-bound and platform-specific.

## Code style

`.editorconfig` enforces:
- **No `var`** — always explicit types (`csharp_style_var_*` = false)
- Block-scoped namespaces (`csharp_style_namespace_declarations = block_scoped`)
- Braces required (`csharp_prefer_braces = true`)
- No `this.` qualification
- PascalCase for types and non-field members
- Interfaces prefixed with `I`
- Space after keywords in control flow (`if (`, `for (`, etc.)
- Spaces inside parentheses for expressions only

## Configuration

- Runtime config: `config.json` (encrypted via DPAPI, user-bound to Windows account)
- Sample: `config.sample.json` / `config.sample.txt`
- Legacy migration: `config.txt` → `config.json` happens automatically on first run after update
- **Never commit real API keys or secrets** — the `.gitignore` excludes `config.json`

## Key architectural notes

- **MVVM pattern**: `Views/` → `ViewModels/` → `Services/` + `Models/`
- **Single-instance enforcement**: `App.xaml.cs` uses a global Mutex — second launch shows warning and exits
- **Encryption**: `BotConfig` properties (`ApiKey`, `ApiSecret`, etc.) auto-encrypt on set / decrypt on get via `SecureStringHelper` (DPAPI `ENC:` prefix)
- **Services layer** (`Services/`): 30 service classes handling exchange, ML, Telegram, risk, backtesting, etc.
- **LiveCharts + OxyPlot** for charting; both WPF charting libs are present

## CI / Release

`.github/workflows/release.yml`:
- Triggers on push to `master` (skips `.csproj` version-bump commits)
- Auto-increments `VersionPrefix` in `.csproj` patch number
- Runs: restore → test → publish → zip → GitHub Release

## Versioning

- `VersionPrefix` in `BinanceBotWpf.csproj` is the source of truth (currently `1.0.182`)
- `AppConstants.AppVersion` in `AppConstants.cs` is a separate display string — update both when releasing
- CI bumps only the `.csproj` version; `AppConstants.AppVersion` is updated manually in commits

## Common pitfalls

- Tests reference `BotConfig.MigrateFromLegacyTxt` via reflection (private method) — renaming it breaks tests without compile errors
- `DataDownloader` uses different NuGet versions than the main app (e.g., `Binance.Net` 12.14.0 vs 12.13.0) — don't assume version parity
- WPF project targets `net8.0-windows`; cross-platform code or `dotnet test` on non-Windows will fail
- `config.json` is gitignored — `config.sample.json` must be kept in sync with `BotConfig` class properties
