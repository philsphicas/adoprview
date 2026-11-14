# Development

## Build

```powershell
cd src\AdoPrView
dotnet build                     # dev
dotnet publish -c Release -r win-x64 --self-contained false -o <publish-folder>
```

`.github/scripts/New-ReleasePackage.ps1` builds the self-contained release ZIP,
installer (needs Inno Setup 6) and checksums. CI runs the native, frontend and
install-guide tests on every push and pull request; pushing a `v<version>` tag
that matches the project version and is on `main` publishes a release.

Dev flags: `--mock` (fixture data), `--screenshot out.png [--size WxH] [--eval js] [--eval-wait ms]`.

The pinned `GitHub.Copilot.SDK` package downloads and verifies its matching
Windows Copilot runtime at build time. Explain uses this native runtime;
the existing Fix runner still uses WSL.

## Offline checks

No live approval, PR or auth requests are made:

```powershell
dotnet run --project tests\AdoPrView.Tests
node --test tests\approval-ui.test.cjs
node --test tests\row-actions.browser.test.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File tests\install-guide.tests.ps1
```

The browser regression uses Node 22+ and the installed Microsoft Edge (or set
`ROW_TEST_BROWSER` to another Chromium executable). Real mouse/keyboard input
is used, with all URL launches and PR actions intercepted; no live PR is changed.
Native fake-HTTP/fake-clock checks cover periodic/forced restarts, target-change
sweeps, retries, in-flight deduplication and fresh result handling. The install
guide check parses every PowerShell block in INSTALL.md and runs its settings
merge against a disposable folder.

The opt-in WSL preflight regression uses a disposable `/tmp` checkout and fake
CLI executables. It checks non-login discovery, quoted paths, inherited child
PATH and dependency diagnostics without starting a Fix or contacting ADO:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'WSL preflight'
$env:ADOPRVIEW_TEST_WSL_PREFLIGHT = '1'
dotnet run --project tests\AdoPrView.Tests
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_WSL_PREFLIGHT
```

`src\AdoPrView\wwwroot\index.html?mock=1` runs the UI in a browser against
synthetic fixtures. The screenshots in [images](images) were captured from that
mock UI with synthetic data.

The opt-in native PoP check intercepts all website requests with synthetic pages
and uses a disposable browser profile. It exercises sign-in popup/page
`window.close()` in normal and maximized windows, captured-policy rechecks,
host disposal and ordinary deliberate window close, without real sign-in or PR writes:

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'Native PoP WebView2'
$env:ADOPRVIEW_TEST_POP_WEBVIEW = '1'
dotnet run --project tests\AdoPrView.Tests
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_POP_WEBVIEW
```

## Isolated real-model Explain demonstration

This opt-in check opens a native WebView2 window with the production frontend,
bridge and Explain service. It uses real Copilot inference with synthetic ADO
GET responses, a disposable browser profile and a separate runtime directory.
It does not launch `MainWindow`, read/save production app settings or change
any PR. Native Copilot sign-in/access is required, and inference consumes usage.
The ordinary `--mock` preview only simulates Explain; it is not model integration.

```powershell
$env:ADOPRVIEW_TEST_FILTER = 'Explain live native'
$env:ADOPRVIEW_TEST_EXPLAIN_LIVE = '1'
$env:ADOPRVIEW_EXPLAIN_SCREENSHOT = Join-Path $env:TEMP 'adoprview-explain-demo.png'
dotnet run --project tests\AdoPrView.Tests
Remove-Item Env:\ADOPRVIEW_TEST_FILTER, Env:\ADOPRVIEW_TEST_EXPLAIN_LIVE, Env:\ADOPRVIEW_EXPLAIN_SCREENSHOT
```

The check writes the requested screenshot, `.narrow.png` and `.evidence.png` captures and a `.json`
result, and removes its temporary browser/runtime directory. It verifies
streaming through the native bridge, a 1-3 sentence/80-word summary, collapsed
evidence, and the repository role of a developer/test utility whose nested
`go.mod` has a one-line dependency bump. The real model must inspect README
context, not just repeat version numbers. This does **not** establish live ADO endpoint compatibility.
Offline Explain tests run with the normal native/frontend suites.

See [DESIGN.md](DESIGN.md) for the architecture, approval provider contract,
ADO calls, classification and merge preview scheduling.
