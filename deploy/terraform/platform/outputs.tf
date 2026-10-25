output "registry_login_server" {
  value = azurerm_container_registry.images.login_server
}

output "registry_name" {
  value = azurerm_container_registry.images.name
}

output "resource_group_name" {
  value = azurerm_resource_group.platform.name
}

output "migration_job_name" {
  value = try(azurerm_container_app_job.migrate[0].name, null)
}

output "service_inputs" {
  description = "Nonsecret inputs for the separately managed API stack."
  value = {
    resource_group_name          = azurerm_resource_group.platform.name
    location                     = var.location
    container_app_environment_id = azurerm_container_app_environment.platform.id
    runtime_identity_id          = azurerm_user_assigned_identity.runtime.id
    database_secret_id           = azurerm_key_vault_secret.runtime.versionless_id
    registry_login_server        = azurerm_container_registry.images.login_server
    app_name                     = "app-${local.name}"
    tags                         = var.tags
  }
}
