# Verification record

This record separates checks executed during implementation from additional environment-specific gates. A workflow definition is not evidence that the workflow has already run in GitHub or Azure.

**Final integrated verification: 25 October 2026.** `scripts/verify.sh` completed successfully with a live PostgreSQL 17.11 test server and runtime-role checks enabled: **212 passed, zero failed, zero skipped**. Locked restore, formatting, the Release build and the direct/transitive NuGet audit all passed. The build reported **zero warnings and zero errors**. Shared-rule line coverage was **99.72% (351/352 lines)** against an 80% gate.

## Executed during the rebuild

| Check | Evidence |
| --- | --- |
| Domain/application tests | 112 tests passed; shared-rule line coverage measured at 351/352 lines (99.72%). |
| Infrastructure syntax and provider schema | Both platform and service stacks passed `terraform validate` with Terraform 1.17.0, AzureRM 5.11.0 and Random 3.9.1. Provider packages were signature/checksum verified and lockfiles generated. |
| Presentation tests | 45 tests passed, exercising viewmodels, async commands, HTTP clients and launch modes. |
| Database-provider tests | 23 infrastructure tests passed against SQLite and an actual PostgreSQL 17.11 process, including concurrent writes, transaction rollback, and runtime-role permission regressions. |
| Official action/container pins | GitHub action commit targets checked against upstream repositories; .NET SDK/runtime and PostgreSQL image digests retrieved from their official registries. |
| API tests | 32 tests passed, including signed-token validation and the HTTP authorization/concurrency boundary. |
| Live HTTP host | The real local HTTP smoke script passed readiness, unauthorized/forbidden requests, successful mutation, stale-write rejection and audit persistence. |
| Delivery configuration | GitHub workflows passed actionlint 1.7.12; eight YAML files parsed; all Bash and Python scripts passed syntax checks; Terraform formatting passed. |
| WPF build and package | Native XAML cross-compilation and self-contained Windows x64 publication succeeded with zero warnings/errors. The executable is a Windows PE32+ GUI binary; Windows execution remains a separate gate. |
| Reproducible Windows release | Normal full-solution locked restore followed by the exact CI locked single-file publish both passed. Separate `packages.publish-win-x64.lock.json` files preserve the runtime/tool dependency graph without changing ordinary build/test locks. |
| Source secret scan | Checksum-verified Trivy 0.76.1 scanned a clean archive of the current source: exit code 0 and zero findings. Inherited Git history was deliberately outside this current-source scan. |
| Dependency audit | No known vulnerable direct or transitive NuGet packages were reported by the final integrated audit on this date. |

## Platform-specific gates

| Gate | How it is run | What it proves |
| --- | --- | --- |
| Linux shared/server suites | `bash scripts/verify.sh` | Rules, presentation behavior, API behavior, formatting, analyzers, lockfile consistency and dependency audit |
| PostgreSQL integration | `TEAMMY_TEST_POSTGRES` and `TEAMMY_TEST_PROVISION_ROLE=1` on a disposable database | Real provider migrations, atomic concurrency/audit and runtime permissions |
| Live HTTP host | `python3 scripts/smoke-api.py --demo` against the local host | Ready/live checks, 401/403, successful mutation, stale-write 409 and audit persistence |
| Windows native UI | `Teammy.Desktop.exe --smoke-test artifacts/desktop-smoke.png` | The actual WPF window loads, a simulation-to-independent-approval journey succeeds, five surfaces render to PNG, and binding errors fail the run |
| Container scan/SBOM | The `security` CI job | Current-tree scan, runtime image scan, CycloneDX inventory |
| Azure release | Manual protected `Deploy to Azure` workflow | Cloud identity, image push, job execution, isolated candidate health, traffic movement and rollback paths in that configured subscription |

The Linux development environment cannot execute a WPF window. Cross-compilation is useful build evidence, but is not equivalent to the Windows smoke test. Azure resource provisioning, identity-provider configuration, Windows code signing and real hardware integration are not implied by Terraform validation or unit tests.

The optional local collector/Prometheus stack is supplied as configuration; its full container startup and alert delivery have not been exercised in this environment.

## Evidence artifacts

CI uploads `server-verification`, `windows-desktop`, and `software-bill-of-materials`. A successful cloud release also uploads `deployment-evidence`, containing the immutable image, previous/current revision, URL and timestamp. GitHub retention settings in the workflows bound artifact lifetime; archive release evidence separately when required.

Dependency and container vulnerability results are time-dependent. A later security database update may correctly fail the same source commit. Investigate and update the affected dependency or base image rather than treating a previous green run as an exemption.
