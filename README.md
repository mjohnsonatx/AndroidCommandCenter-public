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
5. A no-fast-forward staging-to-production merge, optional release tag, and push.
6. A no-fast-forward production-to-development back-merge.

Use **Validate Promotion (Dry Run)** before enabling a real promotion. Runs use
a per-repository lock and an isolated Git worktree under the application cache,
so the selected developer checkout is never switched, reset, cleaned, or
stashed. Main-branch movement is checked again immediately before promotion;
concurrent remote changes cause a safe failure instead of an overwrite. The
pipeline never force-pushes.

Every long-running GUI operation exposes an Abort button in both the top status
area and the sidebar. Aborting cancels the operation token and terminates the
entire Gradle/ADB/Git/Firebase child process tree. Single Gradle actions show an
active `1 of 1` status; workflows, dry runs, and promotion runs show the current
phase as `n of y`. Completed test phases also report passed, failed, skipped,
and total test counts. An aborted run does not start a new Gradle daemon-cleanup
command afterward.

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
