output "api_url" {
  value = "https://${azurerm_container_app.api.ingress[0].fqdn}"
}
output "container_app_name" {
  value = azurerm_container_app.api.name
}
