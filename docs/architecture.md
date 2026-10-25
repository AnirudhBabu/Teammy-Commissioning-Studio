# Architecture and decisions

## Product boundary

The original application organized teams, projects, and task cards. The rebuild keeps that useful coordination idea and gives it a concrete engineering workflow: a team owns commissioning work and a set of drive configurations, gathers simulation evidence, then requests an independently reviewed release.

The application represents one shared reference lab. Both seeded projects and all seeded equipment are synthetic. Registered users can inspect the whole workspace. Membership constrains writes, but there is no tenant boundary or physical device transport.

## 1. A modular monolith with a native desktop

The domain is small and its release decisions need a consistent view of work, settings, and evidence. A single application engine makes that consistency explicit. Splitting the product into independent task, simulation, audit, and release services would introduce distributed transactions without a demonstrated scaling need.

`Teammy.Domain` contains immutable records and enums. `Teammy.Application` accepts a snapshot, a typed command, an authenticated actor, and a supplied timestamp; it produces a new snapshot plus one audit entry, or a typed error. It does not know about EF Core, WPF, ASP.NET, or a live clock.

The WPF client uses `Teammy.Presentation`, whose observable state and async commands are independently testable. An in-memory client invokes the same engine for a clearly labeled offline demo. A second client uses HTTP. The desktop application is the composition root and owns its UI lifetime.

## 2. Explicit identity and authorization

In hosted mode, the API authenticates a signed JWT against its configured issuer and audience. A subject-to-member map connects external identities with the three reference-lab members. A signed `roles` claim must agree with the member's role. The `/api/me` response tells the desktop which actor is actually authenticated.

Viewer, engineer, and approver are distinct capabilities. The command engine checks them again alongside project-team membership. The demo-role header is only enabled explicitly in `Development` or `Testing`; production startup rejects that option.

The small, fixed reference membership is deliberate. An organization directory, invitation flow, and multi-tenant membership administration are future product work, not hidden capabilities.

## 3. Atomic snapshot and audit

The workspace is a bounded aggregate persisted as a JSON snapshot in one relational row. Its revision is the optimistic-concurrency token. Every write also inserts an audit row in the same database transaction.

```mermaid
sequenceDiagram
    participant Client
    participant API
    participant Engine
    participant Database
    Client->>API: Command and expected revision
    API->>Database: Read current snapshot
    API->>Engine: Snapshot, command, verified actor
    Engine-->>API: New state and audit entry
    API->>Database: Update where revision matches; insert audit
    alt Both changes commit
        Database-->>API: Success
        API-->>Client: Updated snapshot and ETag
    else Another writer won
        Database-->>API: Transaction rolled back
        API-->>Client: 409 conflict; refresh required
    end
```

The database compare-and-swap is essential: an application-only revision check would still lose updates when two API instances read the same revision. SQLite and PostgreSQL integration tests cover the race and audit rollback.

This approach favors clear invariants and a bounded engineering lab over high-volume querying. All writers contend on one revision, reads load the snapshot, and evidence increases its size. For a larger product, separate aggregates by project, retain immutable release snapshots, move telemetry to an append-oriented time-series store, and introduce a deliberate archival policy. Do not simply raise replica counts and assume that removes the aggregate bottleneck.

## 4. Evidence belongs to a configuration revision

Drive profiles define speed, rated current, ramp-rate and temperature limits. The deterministic simulator derives a repeatable telemetry series and checks operating assumptions against these limits. There is no network delay, random pass/fail behavior, vendor protocol, or real-time-control claim.

Simulation runs record their configuration revision. Editing settings requires fresh evidence. Release readiness also checks unfinished high/critical work. A requested candidate captures a project-content fingerprint; subsequent changes prevent approval of stale evidence. Approval cannot be performed by the requester or a configuration author.

Because the same engine enforces these rules in API, demo and tests, desktop affordances cannot bypass them.

## 5. Separate schema and runtime privileges

SQLite is a convenient persisted developer host. PostgreSQL is the deployment provider. Each has its own explicit EF Core migrations.

`--migrate` runs under an owner connection, applies migrations and initializes the synthetic reference lab once. With `Database__RuntimePassword`, the PostgreSQL migration also provisions a fixed runtime role with only the required state/audit permissions. The normal API uses that role and cannot run schema changes. Production startup rejects `Database__Initialize=true`.

Azure Terraform gives migration and API processes different managed identities and Key Vault scopes. Database passwords still enter protected Terraform state, so a secure remote backend is part of the deployment contract.

## 6. Deployment ownership

The platform Terraform state owns networking, PostgreSQL, Key Vault, identities, ACR, logs, the Container Apps environment, and the migration-job definition. The service state owns API settings, probes, scaling, and ingress configuration. This separation allows the first migration to finish before the first API revision is created.

After bootstrap, the protected deployment workflow owns image digests, revision suffixes and traffic weights. Terraform explicitly ignores those fields; all other service configuration remains under infrastructure review. Releases pin traffic to the current healthy revision, migrate, start a candidate, check it, switch traffic, and verify the public endpoint. A failed rollout restores traffic. The previous revision remains available for recovery.

This is a health-gated blue/green rollout. It does not claim traffic-based p99 or statistical canary analysis. Longer production observation, authenticated synthetic journeys, and service-specific SLO gates can be added when traffic and operational ownership exist.

## Extension points

| Need | Intended seam |
| --- | --- |
| More equipment profiles | Add validated profile records and simulator tests |
| Vendor-specific offline models | Implement the simulator interface behind the application boundary |
| Organization identity | Extend verified subject/member management without trusting client role selection |
| Large fleets and telemetry retention | Split project aggregates and stream evidence with an explicit archival model |
| Stronger audit retention | Export events to an independent immutable archive with identity and retention controls |
| Desktop sign-in and distribution | Add an identity-provider PKCE flow and organization-controlled code signing/update channel |

## Primary references

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [EF Core optimistic concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)
- [Azure Container Apps revision management](https://learn.microsoft.com/en-us/azure/container-apps/revisions)
- [Azure Container Apps blue/green deployment](https://learn.microsoft.com/en-us/azure/container-apps/blue-green-deployment?pivots=azure-cli)
