terraform {
  # 1.12 added the native OCI Object Storage backend.
  required_version = ">= 1.12.0"

  required_providers {
    oci = {
      source  = "oracle/oci"
      version = ">= 6.0.0"
    }
  }

  # Partial config: bucket, namespace and region come from -backend-config (see backend.hcl.example),
  # so account details stay out of the public repo. Credentials come from ~/.oci/config.
  backend "oci" {
    key = "kzg/oci-core.tfstate"
  }
}

# Credentials come from the DEFAULT profile in ~/.oci/config, locally and in CI.
provider "oci" {
  region = var.region
}
