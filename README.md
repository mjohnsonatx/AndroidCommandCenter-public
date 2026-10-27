# Android Command Center

Android Command Center is a cross-platform Avalonia desktop app for building,
testing, promoting, distributing, and rolling back Android releases. The Fedora
build targets .NET 10 and runs the Android toolchain through the project's
Gradle wrapper.

## Build and run on Fedora

Install the .NET 10 SDK, a JDK, Git, and the Android SDK command-line tools, then:

```bash
dotnet restore
dotnet build -c Release
dotnet run
```

In the GUI, add the Android project directory with the `+` button. Configure the
repository and token on the **Source** tab. Tokens and passwords are stored in
the system keyring when libsecret is available; non-secret settings are stored
under `~/.config/AndroidCommandCenter`.

If GitHub CLI is installed, **Use gh Login** securely imports the active
`gh auth` token, verifies it against the authenticated `/user` API, and stores
it per project in the keyring. **Load Repos** uses the authenticated repository
API so private repositories are included. The `.env` export remains an explicit
plaintext fallback and is not written automatically.

Rider's Flatpak cannot execute host-only credential-helper paths such as
`/usr/bin/gh` or `/usr/bin/ksshaskpass`. GitHub HTTPS fetches and pushes therefore
clear external helpers for that one command and use the verified project token
through a temporary askpass process. The token is never placed in a command
line, remote URL, or application log.

## Promotion CI/CD

Open **CI / CD → Promotion Settings** and set the remote plus the development,
staging, and production branches. A run performs these stages in order:

1. Environment and repository preflight.
2. Fetch and exact staging/main SHA snapshot.
3. Optional version-code, secret, checksum, lint, unit-test, and emulator-only
   instrument-test gates.
4. Build of the configured Android variant and APK SHA-256 recording.
5. A no-fast-forward staging-to-production merge; production, staging, and the
   optional release tag are pushed together atomically.
6. A no-fast-forward back-merge of that exact release into development. Dev-only
   work is never copied to staging.

Use **Validate Promotion (Dry Run)** before enabling a real promotion. Runs use
a per-repository lock and an isolated Git worktree under the application cache,
so the selected developer checkout is never switched, reset, cleaned, or
stashed. Both production and staging are checked again before creating the
release and immediately before pushing. A temporary pre-push guard verifies the
server's advertised heads (and runs any existing pre-push hook); Git checks them
again when updating the refs. The pipeline never force-pushes or falls back to
separate release pushes if atomic updates are unsupported or rejected.

Successful promotion leaves production and staging at the **same release merge
SHA**. The release tree is verified against the exact staging tree that passed
the gates, so synchronizing staging changes history, not files. This prevents
the next run from rejecting the release merge created by the previous run.

### Stale staging and synchronization recovery

Staging must contain all current production history **before** the gates run.
An ordinary merge would retain production commits, but could introduce untested
content into the release. The ancestry error distinguishes history-only gaps
from net production file changes since the common ancestor, lists missing
commits, and recommends updating dev only when dev is also behind.

For an older release that left staging behind, merge the current production
branch (`master` or `main`, as configured) into staging in a clean checkout or
through a pull request. Resolve any conflicts, push normally, and rerun the full
gates. Do not bypass the ancestry gate even for a history-only gap. Do not merge
dev into staging just to repair release history; dev may contain unfinished work.

An atomic rejection (including staging protection or tag collisions) leaves all
release refs unchanged by this push. Fix the reported rejection and validate
again. After a lost connection/cancellation, inspect the remote first: the push
may have completed. The manifest records `ReleasePushStatus`, tested
`StagingSha`, `MainShaBefore`, `MergeSha`, confirmed `MainShaAfter` and
`StagingShaAfter`, `BackMergeSha`, `DevShaAfter`, and any synchronization warning.
An `Unknown` outcome retains local release evidence and must not be blindly retried.

If production and staging succeeded but dev failed, the release is already
published. Merge the manifest's exact `MergeSha` into current dev, resolve any
conflict, and push dev normally (or use a PR). Do not repeat the release or move
staging to dev's back-merge commit. With **Push promoted branches** disabled,
commits/tags are local only and the log explicitly says no remote branches moved.

Run the isolated promotion and failure-diagnostic regression suite (no Android SDK or network needed):

```bash
dotnet run --project tests/PromotionGraphTests/PromotionGraphTests.csproj
```

Every long-running GUI operation exposes an Abort button in both the top status
area and the sidebar. Aborting cancels the operation token and terminates the
entire Gradle/ADB/Git/Firebase child process tree. Single Gradle actions show an
active `1 of 1` status; workflows, dry runs, and promotion runs show the current
phase as `n of y`. Completed test phases also report passed, failed, skipped,
and total test counts. Promotion cleanup has an independent, bounded timeout:
it stops Gradle daemons and removes the isolated worktree even after cancellation,
preserving run records, recorded artifacts, and generated commit references.

Failure details retain Gradle's failed task names, build-file location, `What
went wrong` explanation, causal exceptions, and a bounded tool stack trace in
addition to the surrounding console output. Long asset listings cannot displace
the actual error. The run summary shows the task and reason; full details and the
complete log path remain in the saved manifest after the worktree is removed.
Cleanup warnings do not replace the primary failure in GUI or CLI diagnostics.

### Release tags

An existing tag is not required. In **Promotion Settings**, leave **Create
release tag** enabled, use `v` as the prefix, and enable **Push promoted
branches**. A dry run previews the tag without changing Git. A successful real
promotion creates an annotated tag with this format and pushes it to `origin`:

```text
v<versionName>+<versionCode>
```

For example, `versionName "4.5.0"` and `versionCode 2` produce `v4.5.0+2`.
The version-code gate requires the value on `staging` to be greater than the
one on `main`; bump the staging build before the first promotion. To create an
optional historical baseline tag for the version already on `main`, run:

```bash
git switch main
git pull --ff-only origin main
git tag -a v<current-versionName>+<current-versionCode> -m "Baseline release"
git push origin v<current-versionName>+<current-versionCode>
```

Do not create the tag intended for the next promotion manually—the pipeline
rejects duplicate release tags to prevent overwriting release history.

The optional desktop schedule is evaluated while Android Command Center is
open. Run manifests are stored under
`~/.local/state/AndroidCommandCenter/ci-runs`, with logs in the adjacent
`pipeline-logs` directory. Firestore publication is optional.

## Headless commands

```bash
# Evaluate every enabled gate without merging or pushing
dotnet run -- ci run --project /path/to/android-project --dry-run

# Promote using the saved Promotion Settings
dotnet run -- ci run --project /path/to/android-project

# Show recent run manifests
dotnet run -- ci status --project /path/to/android-project --last 10

# Show the selected Gradle JDK and detected compile toolchains
dotnet run -- ci env --project /path/to/android-project

# Inspect the latest rollback plan without changing anything
dotnet run -- rollback --project /path/to/android-project --plan

# Create and push the rollback after an explicit confirmation bypass
dotnet run -- rollback --project /path/to/android-project --push --yes \
  --reason "release regression"
```

Rollback creates a new `git revert -m 1` commit and an audit tag; it does not
rewrite history. When configured, the rollback is also back-merged into the
development branch. A recorded rollback cannot be applied twice.
