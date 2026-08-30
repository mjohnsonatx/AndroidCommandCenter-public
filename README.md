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

# Inspect the latest rollback plan without changing anything
dotnet run -- rollback --project /path/to/android-project --plan

# Create and push the rollback after an explicit confirmation bypass
dotnet run -- rollback --project /path/to/android-project --push --yes \
  --reason "release regression"
```

Rollback creates a new `git revert -m 1` commit and an audit tag; it does not
rewrite history. When configured, the rollback is also back-merged into the
development branch. A recorded rollback cannot be applied twice.
