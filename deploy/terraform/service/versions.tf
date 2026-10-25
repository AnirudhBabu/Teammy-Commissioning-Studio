terraform {
  required_version = "~> 1.17.0"
  backend "azurerm" {}
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "5.11.0"
    }
  }
}
provider "azurerm" {
  features {}
}
