variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "container_app_environment_id" { type = string }
variable "runtime_identity_id" { type = string }
variable "database_secret_id" { type = string }
variable "registry_login_server" { type = string }
variable "app_name" { type = string }
variable "tags" { type = map(string) }

variable "api_image" {
  description = "Digest of the image whose migration job has completed successfully."
  type        = string
  validation {
    condition     = can(regex("^[a-z0-9.-]+/[a-z0-9/_.-]+@sha256:[a-f0-9]{64}$", var.api_image))
    error_message = "An immutable registry/repository@sha256:<64 hex> image is required."
  }
}

variable "oidc_authority" {
  description = "HTTPS OIDC issuer whose signing keys validate API access tokens."
  type        = string
  validation {
    condition     = can(regex("^https://[^/]+", var.oidc_authority))
    error_message = "Use an HTTPS OIDC authority."
  }
}

variable "oidc_audience" {
  description = "Exact audience of issued API access tokens."
  type        = string
  validation {
    condition     = length(trimspace(var.oidc_audience)) > 0
    error_message = "An explicit API audience is required."
  }
}

variable "member_subjects" {
  description = "Verified issuer subject values for the three reference-lab memberships; these are not passwords."
  type = object({
    engineer = string
    approver = string
    viewer   = string
  })
  sensitive = true
  validation {
    condition = (length(toset(values(var.member_subjects))) == 3 &&
    alltrue([for subject in values(var.member_subjects) : length(trimspace(subject)) > 0]))
    error_message = "Configure three distinct, non-empty OIDC subjects."
  }
}

variable "otel_endpoint" {
  description = "Optional OTLP HTTP collector endpoint reachable from the private network."
  type        = string
  default     = ""
  validation {
    condition     = var.otel_endpoint == "" || can(regex("^https?://", var.otel_endpoint))
    error_message = "OTLP endpoint must be an HTTP(S) URL or empty."
  }
}
