variable "prefix" {
  description = "Lowercase resource name prefix, unique suffix added automatically."
  type        = string
  default     = "teammy"
  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{2,11}$", var.prefix))
    error_message = "Use 3-12 lowercase letters, digits, or hyphens, starting with a letter."
  }
}

variable "location" {
  type    = string
  default = "westeurope"
}

variable "api_image" {
  description = "Already built API image in this registry, pinned by sha256 digest. Used for the migration job's first deployment."
  type        = string
  default     = null
  validation {
    condition     = var.api_image == null || can(regex("^[a-z0-9.-]+/[a-z0-9/_.-]+@sha256:[a-f0-9]{64}$", var.api_image))
    error_message = "Provide an immutable registry/repository@sha256:<64 hex> image."
  }
  validation {
    condition     = !var.create_migration_job || var.api_image != null
    error_message = "An api_image is required when create_migration_job is true."
  }
}

variable "create_migration_job" {
  description = "Use false to provision registry/network first; enable after pushing the first API image."
  type        = bool
  default     = false
}

variable "database_sku" {
  description = "Cost-conscious showcase default. Choose a General Purpose SKU and HA for an availability commitment."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "database_high_availability" {
  description = "Zone redundant PostgreSQL; requires a compatible General Purpose SKU and region."
  type        = bool
  default     = false
}

variable "tags" {
  type = map(string)
  default = {
    application = "teammy"
    environment = "showcase"
    managed-by  = "terraform"
  }
}
