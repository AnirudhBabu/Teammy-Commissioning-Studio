locals {
  environment = {
    ASPNETCORE_ENVIRONMENT                      = "Production"
    Database__Provider                          = "PostgreSql"
    Database__Initialize                        = "false"
    Authentication__DemoEnabled                 = "false"
    Authentication__Authority                   = var.oidc_authority
    Authentication__Audience                    = var.oidc_audience
    Authentication__RoleClaimType               = "roles"
    Authentication__MemberMappings__0__Subject  = var.member_subjects.engineer
    Authentication__MemberMappings__0__MemberId = "engineer-anirudh"
    Authentication__MemberMappings__1__Subject  = var.member_subjects.approver
    Authentication__MemberMappings__1__MemberId = "approver-elena"
    Authentication__MemberMappings__2__Subject  = var.member_subjects.viewer
    Authentication__MemberMappings__2__MemberId = "viewer-guest"
    OTEL_SERVICE_NAME                           = "teammy-api"
    OTEL_EXPORTER_OTLP_PROTOCOL                 = "http/protobuf"
  }
}

resource "azurerm_container_app" "api" {
  name                         = var.app_name
  resource_group_name          = var.resource_group_name
  container_app_environment_id = var.container_app_environment_id
  revision_mode                = "Multiple"
  max_inactive_revisions       = 5
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.runtime_identity_id]
  }
  registry {
    server   = var.registry_login_server
    identity = var.runtime_identity_id
  }
  secret {
    name                = "database"
    identity            = var.runtime_identity_id
    key_vault_secret_id = var.database_secret_id
  }

  ingress {
    external_enabled           = true
    allow_insecure_connections = false
    target_port                = 8080
    transport                  = "http"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    revision_suffix = "bootstrap"
    min_replicas    = 1
    max_replicas    = 3
    http_scale_rule {
      name                = "http-concurrency"
      concurrent_requests = "20"
    }
    container {
      name   = "api"
      image  = var.api_image
      cpu    = 0.5
      memory = "1Gi"
      dynamic "env" {
        for_each = nonsensitive(toset(keys(local.environment)))
        content {
          name  = env.key
          value = local.environment[env.key]
        }
      }
      env {
        name        = "ConnectionStrings__Workspace"
        secret_name = "database"
      }
      dynamic "env" {
        for_each = var.otel_endpoint == "" ? [] : [var.otel_endpoint]
        content {
          name  = "OTEL_EXPORTER_OTLP_ENDPOINT"
          value = env.value
        }
      }
      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 2
        timeout                 = 2
        failure_count_threshold = 30
      }
      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 15
        timeout                 = 2
        failure_count_threshold = 3
      }
      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/ready"
        interval_seconds        = 5
        timeout                 = 2
        failure_count_threshold = 3
      }
    }
  }

  # Terraform owns infrastructure; the protected deployment workflow owns rollout state.
  lifecycle {
    ignore_changes = [
      template[0].container[0].image,
      template[0].revision_suffix,
      ingress[0].traffic_weight
    ]
  }
}
