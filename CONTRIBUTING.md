# Contributing

## Local workflow

Use the SDK pinned by `global.json`. Restore with `--locked-mode` for ordinary work. Dependency changes must update `Directory.Packages.props` and the affected `packages.lock.json` files together. Windows is required to run the WPF shell; the domain, presentation, API, and infrastructure tests run on Linux.

Run `bash scripts/verify.sh` or `./scripts/verify.ps1 -Desktop` before proposing a change. For persistence changes also run against a disposable PostgreSQL 17 database, including the optional runtime-role tests. Never point that test connection at a database with valuable data: the integration tests exercise schema and role changes.

## Design boundaries

- Keep business decisions in `Teammy.Application`; keep immutable data contracts in `Teammy.Domain`.
- Views bind to presentation state and commands. Do not introduce database access, security decisions, blocking network calls, or global service locators into code-behind.
- HTTP clients pass cancellation, dispose responses, and show recoverable errors. Do not silently retry state-changing commands after a conflict or ambiguous network failure.
- Add boundary and regression tests for a changed rule. Cover rejected actions and concurrency, not only the successful UI path.
- Do not add real device transport to the simulated adapter without a separate hazard analysis and explicit product requirements.

## Database and release changes

Migrations run under the schema-owner job. Add a migration for **both** supported providers. The live API must remain compatible with the previous revision during a rollout; use additive changes first and retire old columns only in a subsequent release. A traffic rollback cannot reverse data loss.

Keep package/action/image/provider pins explicit. Update lockfiles through the upstream tools and review the resulting hashes. Do not disable a security check to make a release green; record a narrowly scoped, expiring exception only after a concrete finding is assessed by the owner.

## Review evidence

Explain the user or operational problem, resulting behavior, important tradeoffs, and checks actually run. Attach a WPF screenshot for layout changes. Distinguish implemented workflows from checks executed in your environment. Do not add a passing badge, a deployment URL, a signing claim, or a performance claim without corresponding evidence.

Keep personal contact details, identity tokens, cloud secrets, database files, and signing material out of source control. The project history contains an inherited credential incident; follow `SECURITY.md` rather than copying old configuration.
# Windows publication dependency locks

Ordinary builds and tests use `packages.lock.json`. Self-contained single-file publication adds a runtime identifier and SDK tooling, so it uses the separate `packages.publish-win-x64.lock.json` graph in the desktop and its referenced projects. Both graphs are committed and CI restores them in locked mode.

When deliberately updating the SDK or release dependencies, regenerate the publication locks with the same properties as the release command:

```bash
dotnet restore src/Teammy.Desktop/Teammy.Desktop.csproj \
  -p:RuntimeIdentifier=win-x64 -p:SelfContained=true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true --force-evaluate
```

Use the singular `RuntimeIdentifier` property here; `dotnet restore -r` supplies the plural property and does not select the publication lock. Review every lockfile diff, run ordinary verification, then execute the locked publication command from `.github/workflows/ci.yml`. Do not remove locked-mode enforcement to make a release pass.

