data "azurerm_client_config" "current" {}

resource "random_id" "suffix" {
  byte_length = 3
}

locals {
  name = "${var.prefix}-${random_id.suffix.hex}"
  administrator_connection = join(";", [
    "Host=${azurerm_postgresql_flexible_server.database.fqdn}",
    "Database=teammy", "Username=teammy_owner", "Password=${random_password.owner.result}",
    "SSL Mode=VerifyFull", "Timeout=10", "Command Timeout=30"
  ])
  runtime_connection = join(";", [
    "Host=${azurerm_postgresql_flexible_server.database.fqdn}",
    "Database=teammy", "Username=teammy_runtime", "Password=${random_password.runtime.result}",
    "SSL Mode=VerifyFull", "Timeout=5", "Command Timeout=10"
  ])
}

resource "azurerm_resource_group" "platform" {
  name     = "rg-${local.name}"
  location = var.location
  tags     = var.tags
}

resource "azurerm_virtual_network" "platform" {
  name                = "vnet-${local.name}"
  resource_group_name = azurerm_resource_group.platform.name
  location            = var.location
  address_space       = ["10.64.0.0/16"]
  tags                = var.tags
}

resource "azurerm_subnet" "apps" {
  name                 = "container-apps"
  resource_group_name  = azurerm_resource_group.platform.name
  virtual_network_name = azurerm_virtual_network.platform.name
  address_prefixes     = ["10.64.0.0/23"]
  delegation {
    name = "container-apps"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_subnet" "database" {
  name                 = "postgresql"
  resource_group_name  = azurerm_resource_group.platform.name
  virtual_network_name = azurerm_virtual_network.platform.name
  address_prefixes     = ["10.64.2.0/24"]
  delegation {
    name = "postgresql"
    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_private_dns_zone" "database" {
  name                = "${local.name}.postgres.database.azure.com"
  resource_group_name = azurerm_resource_group.platform.name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "database" {
  name                = "postgresql"
  private_dns_zone_id = azurerm_private_dns_zone.database.id
  virtual_network_id  = azurerm_virtual_network.platform.id
  tags                = var.tags
}

resource "random_password" "owner" {
  length      = 40
  special     = false
  min_lower   = 4
  min_upper   = 4
  min_numeric = 4
}

resource "random_password" "runtime" {
  length      = 40
  special     = false
  min_lower   = 4
  min_upper   = 4
  min_numeric = 4
}

resource "azurerm_postgresql_flexible_server" "database" {
  name                          = "pg-${local.name}"
  resource_group_name           = azurerm_resource_group.platform.name
  location                      = var.location
  version                       = "17"
  administrator_login           = "teammy_owner"
  administrator_password        = random_password.owner.result
  delegated_subnet_id           = azurerm_subnet.database.id
  private_dns_zone_id           = azurerm_private_dns_zone.database.id
  public_network_access_enabled = false
  sku_name                      = var.database_sku
  storage_mb                    = 32768
  backup_retention_days         = 14
  geo_redundant_backup_enabled  = false
  tags                          = var.tags

  dynamic "high_availability" {
    for_each = var.database_high_availability ? [true] : []
    content {
      mode = "ZoneRedundant"
    }
  }

  lifecycle {
    prevent_destroy = true
  }
  depends_on = [azurerm_private_dns_zone_virtual_network_link.database]
}

resource "azurerm_postgresql_flexible_server_database" "workspace" {
  name      = "teammy"
  server_id = azurerm_postgresql_flexible_server.database.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

resource "azurerm_postgresql_flexible_server_configuration" "tls" {
  name      = "require_secure_transport"
  server_id = azurerm_postgresql_flexible_server.database.id
  value     = "on"
}

resource "azurerm_log_analytics_workspace" "logs" {
  name                = "log-${local.name}"
  resource_group_name = azurerm_resource_group.platform.name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = var.tags
}

resource "azurerm_container_app_environment" "platform" {
  name                       = "cae-${local.name}"
  resource_group_name        = azurerm_resource_group.platform.name
  location                   = var.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.logs.id
  infrastructure_subnet_id   = azurerm_subnet.apps.id
  zone_redundancy_enabled    = false
  tags                       = var.tags
  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
}

resource "azurerm_container_registry" "images" {
  name                = "acr${replace(local.name, "-", "")}"
  resource_group_name = azurerm_resource_group.platform.name
  location            = var.location
  sku                 = "Basic"
  admin_enabled       = false
  tags                = var.tags
}

resource "azurerm_user_assigned_identity" "runtime" {
  name                = "id-${local.name}-runtime"
  resource_group_name = azurerm_resource_group.platform.name
  location            = var.location
  tags                = var.tags
}

resource "azurerm_user_assigned_identity" "migration" {
  name                = "id-${local.name}-migration"
  resource_group_name = azurerm_resource_group.platform.name
  location            = var.location
  tags                = var.tags
}

resource "azurerm_role_assignment" "runtime_pull" {
  scope                = azurerm_container_registry.images.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.runtime.principal_id
}

resource "azurerm_role_assignment" "migration_pull" {
  scope                = azurerm_container_registry.images.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.migration.principal_id
}

resource "azurerm_key_vault" "secrets" {
  name                       = "kv-${local.name}"
  resource_group_name        = azurerm_resource_group.platform.name
  location                   = var.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90
  tags                       = var.tags
}

resource "azurerm_role_assignment" "provision_secrets" {
  scope                = azurerm_key_vault.secrets.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_key_vault_secret" "migration" {
  name         = "migration-connection"
  value        = local.administrator_connection
  key_vault_id = azurerm_key_vault.secrets.id
  depends_on   = [azurerm_role_assignment.provision_secrets]
}

resource "azurerm_key_vault_secret" "runtime" {
  name         = "runtime-connection"
  value        = local.runtime_connection
  key_vault_id = azurerm_key_vault.secrets.id
  depends_on   = [azurerm_role_assignment.provision_secrets]
}

resource "azurerm_key_vault_secret" "runtime_password" {
  name         = "runtime-password"
  value        = random_password.runtime.result
  key_vault_id = azurerm_key_vault.secrets.id
  depends_on   = [azurerm_role_assignment.provision_secrets]
}

resource "azurerm_role_assignment" "runtime_secret" {
  scope                = "${azurerm_key_vault.secrets.id}/secrets/${azurerm_key_vault_secret.runtime.name}"
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.runtime.principal_id
}

resource "azurerm_role_assignment" "migration_secrets" {
  for_each             = toset([azurerm_key_vault_secret.migration.name, azurerm_key_vault_secret.runtime_password.name])
  scope                = "${azurerm_key_vault.secrets.id}/secrets/${each.value}"
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.migration.principal_id
}

resource "azurerm_container_app_job" "migrate" {
  count                        = var.create_migration_job ? 1 : 0
  name                         = "job-${local.name}-migrate"
  resource_group_name          = azurerm_resource_group.platform.name
  location                     = var.location
  container_app_environment_id = azurerm_container_app_environment.platform.id
  replica_timeout_in_seconds   = 300
  replica_retry_limit          = 0
  tags                         = var.tags
  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }
  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.migration.id]
  }
  registry {
    server   = azurerm_container_registry.images.login_server
    identity = azurerm_user_assigned_identity.migration.id
  }
  secret {
    name                = "database"
    identity            = azurerm_user_assigned_identity.migration.id
    key_vault_secret_id = azurerm_key_vault_secret.migration.versionless_id
  }
  secret {
    name                = "runtime-password"
    identity            = azurerm_user_assigned_identity.migration.id
    key_vault_secret_id = azurerm_key_vault_secret.runtime_password.versionless_id
  }
  template {
    container {
      name   = "migrate"
      image  = var.api_image
      cpu    = 0.5
      memory = "1Gi"
      args   = ["--migrate"]
      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "Database__Provider"
        value = "PostgreSql"
      }
      env {
        name        = "ConnectionStrings__Workspace"
        secret_name = "database"
      }
      env {
        name        = "Database__RuntimePassword"
        secret_name = "runtime-password"
      }
    }
  }
  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }
  depends_on = [
    azurerm_role_assignment.migration_pull,
    azurerm_role_assignment.migration_secrets,
    azurerm_postgresql_flexible_server_database.workspace
  ]
}
