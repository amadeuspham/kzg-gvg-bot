# No defaults for anything account-specific: the repo is public. CI sets these from GitHub
# secrets (TF_VAR_*); locally they come from a git-ignored terraform.tfvars.

variable "tenancy_ocid" {
  type = string
}

# The resources live in the root compartment, so this is the tenancy OCID today.
variable "compartment_ocid" {
  type = string
}

# Pinned image: source_id forces a new instance, so it must equal the image the instance was built from.
variable "instance_image_ocid" {
  type = string
}

# Must match the instance's current key line exactly, comment included, or the plan shows a change.
variable "ssh_authorized_keys" {
  type = string
}

variable "fault_domain" {
  type    = string
  default = "FAULT-DOMAIN-3"
}

variable "region" {
  type    = string
  default = "eu-frankfurt-1"
}
