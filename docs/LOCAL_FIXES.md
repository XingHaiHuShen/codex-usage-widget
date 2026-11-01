# Reliability fixes in the personal branch

This branch extends upstream 1.7.1. It keeps authentication owned by the local
Codex CLI and does not change account credentials, redeem credits automatically,
or send lifecycle data to a remote service.

## Usage refresh

- Required quota reads no longer await optional token statistics. Token statistics
  refresh separately with their own connection, timeout and five-minute cache.
- Refreshes retry a transient transport failure once. Cancelled or unresponsive
  app-server connections are discarded, and closed output streams fail pending
  requests instead of leaving them waiting forever.
- Windows system proxy settings are applied to child CLI processes when there is
  no explicit proxy environment variable. System proxy configuration is unchanged.
- Overrides pointing inside the managed Codex installation follow the newest
  installed executable, so a desktop update does not leave the widget on an old CLI.
- Failed refreshes retain the last successful percentages and timestamp with a
  visible stale-data warning. Earned-reset controls are withheld until a successful
  refresh. Changing display preferences does not turn stale readings into live data.

## Lifecycle delivery

The updated bridge handles `UserPromptSubmit`, `Stop`, `Interrupt` and `SessionEnd`.
It retries short local pipe connections and saves only the latest minimal state
per session under `%LOCALAPPDATA%\CodexUsageWidget\activity-state`:
event kind, session id, turn id and timestamp. Prompts, answers and transcript
contents are excluded. Files are overwritten with current state, not appended as
conversation history. The widget checks for changed state files every 500 ms,
deduplicates live/recovered signals and can recover delivery after a brief outage
or a widget restart. A late stop cannot overwrite the state of a newer turn.

Completion checks use a separate app-server connection every three seconds.
Failures log the actual reason. If all tracked activity is unconfirmed, the UI
shows a dim idle dot with an uncertainty tooltip instead of an endless running
animation. A confirmed running chat remains visible even if another chat's state
is uncertain. Recovered starts from before the widget launched are unconfirmed
until a new live event or explicit completion arrives; they are never assumed
to be active solely because a file exists.

Installing the updated definitions changes hook commands. Codex must trust them
through `/hooks`; the widget does not edit trust hashes. Hook setup preserves
unrelated definitions and removes exact recognized older widget handlers.

## Validation and maintenance

Run the repository restore/build/test checks and `scripts/publish.ps1` for a
portable build. `scripts/diagnose-local.ps1 -CodexPath <path>` performs read-only
checks of quotas, hook trust and recent turn metadata without starting a model
turn or printing credentials or conversation text.

Install a built portable folder with `scripts/install-local.ps1 -PublishDirectory
<publish-folder> -InstallDirectory <existing-install-folder> -UpdateHooks`.
The script backs up the old executable and hook file, stops only a widget running
from that exact installation path, installs the replacement and restarts it.
Approving changed hook definitions through `/hooks` remains a separate Codex action.

Keep the fork as `origin` and the original repository as `upstream`. To bring
upstream changes into the personal branch:

```powershell
git fetch upstream
git switch fix/reliable-usage-and-activity
git merge upstream/master
dotnet restore .\CodexUsageWidget.slnx
dotnet build .\CodexUsageWidget.slnx -c Release --no-restore
dotnet test .\CodexUsageWidget.slnx -c Release --no-build
```

Commit and push only after resolving any conflicts and verifying the merged build.
