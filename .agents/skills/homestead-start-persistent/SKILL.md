---
name: homestead-start-persistent
description: Build current Homestead source and start its local persistent Docker instance for manual verification. Use after completing repository work or when asked to refresh the persistent container.
---

# Start the persistent Homestead container

Leave the persistent instance running with the completed checkout changes so the
user can verify them manually. Use PowerShell 7 and Docker Desktop's Linux engine.
Run application checks required by `docs/contributing.md` before this handoff;
container readiness does not replace them.

From the Homestead checkout root, run:

```powershell
pwsh -NoProfile -File .vscode/start-persistent.ps1
```

From another directory, resolve the intended checkout and pass its absolute path
as `-RepoPath` to that checkout's `.vscode/start-persistent.ps1`. Build the checkout
containing the completed changes, including uncommitted edits. Use its existing
state root; pass `-StateRoot` only when the user has selected a different persistent
instance. Omit `-Follow` so the command returns after readiness while containers
continue running.

Replacing an already running app container is authorized: stop/remove and recreate
it through the launcher while retaining persistent volumes, state, and backups.

The launcher selects first-run Start, Upgrade for an existing saved image, or
Recover when that image is missing. Upgrade builds current source, snapshots the
stopped data volume, and pins the previous image before replacement. Recover
archives the stopped data and starts on a restored copy, retaining the original
volume; its archive cannot provide paired rollback without the missing image.
Saved ports and configuration are retained. The default URL is
`https://127.0.0.1:5444`; use the URL actually reported by the launcher.

Complete only after the launcher exits successfully and reports `Ready: True`.
Leave administrator setup and manual verification to the user. Keep existing
content, credentials, keys, volumes, and `.homestead-local` state; do not reset,
delete volumes, or automatically roll back migrated data. Keep setup secrets and
private backup contents out of agent output. Certificate trust changes are a
separate user-requested operation.

If build or startup fails, inspect the local runner's Persistent Status/Logs once,
preserve data and backups, and report the blocker. For these diagnostic commands,
backup/rollback, setup-token retrieval, or certificate trust, read the checkout's
`.agents/skills/homestead-local/SKILL.md`; for startup/configuration failures also
read `docs/deploy.md`. Do not repeatedly upgrade a failing instance.

Report the running URL, readiness result, image ID, backup path when created, and
any recovery limitation. Distinguish successful startup from manual checks still
left to the user. If Docker is unavailable, say that the container refresh was not
completed and provide the command above for the user to run.
