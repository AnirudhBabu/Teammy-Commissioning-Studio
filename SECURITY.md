# Security

## Historical credentials

The baseline repository contained embedded MongoDB credentials in `teammy/Models/DataContext.cs` and `teammy/Models/DBConnector.cs`, local plaintext-password comparison in `teammy/ViewModels/LoginVM.cs`, and a checked-in temporary installer signing key. Their values are deliberately omitted here. The new active tree removes those files and does not connect to the old database.

Removing a file does not revoke a credential or remove it from Git history. The credential owner must revoke/rotate the exposed MongoDB credentials, review access logs and network permissions, and revoke any signing material that was ever trusted. This rebuild does not claim those external actions have happened. Coordinate any later history rewrite with the original team and fork owners; ordinary development commits preserve the upstream history.

CI scans the current source tree. A historical scan will still find the inherited exposure until a separately coordinated cleanup occurs. Do not add broad secret-scan exclusions to hide it.

## Current trust boundaries

- Production startup rejects demo authentication and automatic schema initialization.
- Bearer authentication validates a configured HTTPS authority, audience, lifetime and signature. Signed roles must match a registered reference-lab member; unrecognized subjects are denied.
- Actor identity is resolved from authenticated claims and server-side membership mapping. It is not accepted as a command field or client-selected production role.
- Role checks and project-team checks execute in the shared application engine. UI visibility is only an affordance.
- One shared lab workspace is visible to registered members. Tenant isolation is not implemented.
- Commands carry an expected workspace revision. The state update and audit append commit together, or neither commits.
- A release approver must be independent of both the requester and configuration authors. Release evidence is bound to a fingerprint of the current project content.
- PostgreSQL runtime credentials can select state/audit, update state, and insert audit. The migration owner handles schema changes through a separate job. The API has no delete, audit-update, role-management or DDL grants.
- The cloud runtime and migration jobs use separate managed identities and narrowly scoped Key Vault secret access. Terraform state still contains generated secret material: encrypt it, restrict access, enable locking/version history, and never commit it.
- API request bodies are bounded, endpoints rate limited, and errors return structured problem details with correlation information. Tokens and database passwords must not be logged.

## Operational scope

The drive model is a deterministic software simulation. It is not an industrial safety controller, PLC driver, certified commissioning procedure, or substitute for physical commissioning and safety review. There is no command path to real hardware.

Application-level append-only audit protects ordinary API operations. A database owner can still change stored data. Deploy an independent log/archive sink with retention controls when stronger evidentiary guarantees are required.

The desktop receives an externally obtained short-lived token through its process environment. It does not persist that token or implement interactive SSO. Close the process to discard it and use the identity provider to revoke sessions when needed.

## Reporting

Use the repository's private vulnerability-reporting feature when enabled. Otherwise contact the repository owner privately without including usable secrets in a public issue. Include the affected commit, reproduction steps, impact, and suggested mitigation. Rotate exposed credentials before sharing diagnostic material.
