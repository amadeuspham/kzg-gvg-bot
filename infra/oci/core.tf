# Imported from an OCI Resource Manager discovery export of resources that already exist.
# Resource names keep the export's labels so the state addresses match; renaming one needs a
# moved block. Oracle-Tags (creator email, timestamps) and hardcoded OCIDs are left out on
# purpose: the repo is public, and those attributes are computed, so omitting them is no diff.

data "oci_identity_availability_domain" "ad1" {
  compartment_id = var.tenancy_ocid
  ad_number      = 1
}

data "oci_core_services" "all_oracle_services" {
  filter {
    name   = "name"
    values = ["All .* Services In Oracle Services Network"]
    regex  = true
  }
}

data "oci_core_vnic_attachments" "broadcaster" {
  compartment_id = var.compartment_ocid
  instance_id    = oci_core_instance.export_kzg-broadcaster-instance_1.id
}

resource "oci_core_nat_gateway" "export_NAT-gateway-keg-vcn" {
  block_traffic  = "false"
  compartment_id = var.compartment_ocid
  display_name   = "NAT gateway-keg-vcn"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  vcn_id = oci_core_vcn.export_keg-vcn.id
}

resource "oci_core_internet_gateway" "export_Internet-gateway-keg-vcn" {
  compartment_id = var.compartment_ocid
  display_name   = "Internet gateway-keg-vcn"
  enabled        = "true"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  vcn_id = oci_core_vcn.export_keg-vcn.id
}

resource "oci_core_default_dhcp_options" "export_Default-DHCP-Options-for-keg-vcn" {
  compartment_id   = var.compartment_ocid
  display_name     = "Default DHCP Options for keg-vcn"
  domain_name_type = "CUSTOM_DOMAIN"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  manage_default_resource_id = oci_core_vcn.export_keg-vcn.default_dhcp_options_id
  options {
    custom_dns_servers = []
    server_type        = "VcnLocalPlusInternet"
    type               = "DomainNameServer"
  }
  options {
    search_domain_names = [
      "kegvcn.oraclevcn.com",
    ]
    type = "SearchDomain"
  }
}

resource "oci_core_subnet" "export_public-subnet-keg-vcn" {
  cidr_block      = "10.0.0.0/24"
  compartment_id  = var.compartment_ocid
  dhcp_options_id = oci_core_default_dhcp_options.export_Default-DHCP-Options-for-keg-vcn.id
  display_name    = "public subnet-keg-vcn"
  dns_label       = "sub11242100180"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  ipv4cidr_blocks = [
    "10.0.0.0/24",
  ]
  ipv6cidr_blocks            = []
  prohibit_internet_ingress  = "false"
  prohibit_public_ip_on_vnic = "false"
  route_table_id             = oci_core_vcn.export_keg-vcn.default_route_table_id
  security_list_ids = [
    oci_core_vcn.export_keg-vcn.default_security_list_id,
  ]
  vcn_id = oci_core_vcn.export_keg-vcn.id
}

resource "oci_core_subnet" "export_private-subnet-keg-vcn" {
  cidr_block      = "10.0.1.0/24"
  compartment_id  = var.compartment_ocid
  dhcp_options_id = oci_core_default_dhcp_options.export_Default-DHCP-Options-for-keg-vcn.id
  display_name    = "private subnet-keg-vcn"
  dns_label       = "sub11242100181"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  ipv4cidr_blocks = [
    "10.0.1.0/24",
  ]
  ipv6cidr_blocks            = []
  prohibit_internet_ingress  = "true"
  prohibit_public_ip_on_vnic = "true"
  route_table_id             = oci_core_route_table.export_route-table-for-private-private-subnet-keg-vcn.id
  security_list_ids = [
    oci_core_security_list.export_security-list-for-private-subnet-keg-vcn.id,
  ]
  vcn_id = oci_core_vcn.export_keg-vcn.id
}

resource "oci_core_vcn" "export_keg-vcn" {
  cidr_blocks = [
    "10.0.0.0/16",
  ]
  compartment_id = var.compartment_ocid
  display_name   = "keg-vcn"
  dns_label      = "kegvcn"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  ipv6private_cidr_blocks = []
  security_attributes     = {}
}

resource "oci_core_route_table" "export_route-table-for-private-private-subnet-keg-vcn" {
  compartment_id = var.compartment_ocid
  display_name   = "route table for private private subnet-keg-vcn"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  route_rules {
    destination       = "0.0.0.0/0"
    destination_type  = "CIDR_BLOCK"
    network_entity_id = oci_core_nat_gateway.export_NAT-gateway-keg-vcn.id
    route_type        = "STATIC"
  }
  route_rules {
    destination       = "all-fra-services-in-oracle-services-network"
    destination_type  = "SERVICE_CIDR_BLOCK"
    network_entity_id = oci_core_service_gateway.export_Service-gateway-keg-vcn.id
    route_type        = "STATIC"
  }
  vcn_id = oci_core_vcn.export_keg-vcn.id
}

resource "oci_core_default_route_table" "export_default-route-table-for-keg-vcn" {
  compartment_id = var.compartment_ocid
  display_name   = "default route table for keg-vcn"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  manage_default_resource_id = oci_core_vcn.export_keg-vcn.default_route_table_id
  route_rules {
    destination       = "0.0.0.0/0"
    destination_type  = "CIDR_BLOCK"
    network_entity_id = oci_core_internet_gateway.export_Internet-gateway-keg-vcn.id
    route_type        = "STATIC"
  }
}

resource "oci_core_security_list" "export_security-list-for-private-subnet-keg-vcn" {
  compartment_id = var.compartment_ocid
  display_name   = "security list for private subnet-keg-vcn"
  egress_security_rules {
    destination      = "0.0.0.0/0"
    destination_type = "CIDR_BLOCK"
    protocol         = "all"
    stateless        = "false"
  }
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  ingress_security_rules {
    protocol    = "6"
    source      = "10.0.0.0/16"
    source_type = "CIDR_BLOCK"
    stateless   = "false"
    tcp_options {
      max = "22"
      min = "22"
    }
  }
  ingress_security_rules {
    icmp_options {
      code = "4"
      type = "3"
    }
    protocol    = "1"
    source      = "0.0.0.0/0"
    source_type = "CIDR_BLOCK"
    stateless   = "false"
  }
  ingress_security_rules {
    icmp_options {
      code = "-1"
      type = "3"
    }
    protocol    = "1"
    source      = "10.0.0.0/16"
    source_type = "CIDR_BLOCK"
    stateless   = "false"
  }
  vcn_id = oci_core_vcn.export_keg-vcn.id
}

resource "oci_core_default_security_list" "export_Default-Security-List-for-keg-vcn" {
  compartment_id = var.compartment_ocid
  display_name   = "Default Security List for keg-vcn"
  egress_security_rules {
    destination      = "0.0.0.0/0"
    destination_type = "CIDR_BLOCK"
    protocol         = "all"
    stateless        = "false"
  }
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  ingress_security_rules {
    protocol    = "6"
    source      = "0.0.0.0/0"
    source_type = "CIDR_BLOCK"
    stateless   = "false"
    tcp_options {
      max = "22"
      min = "22"
    }
  }
  ingress_security_rules {
    icmp_options {
      code = "4"
      type = "3"
    }
    protocol    = "1"
    source      = "0.0.0.0/0"
    source_type = "CIDR_BLOCK"
    stateless   = "false"
  }
  ingress_security_rules {
    icmp_options {
      code = "-1"
      type = "3"
    }
    protocol    = "1"
    source      = "10.0.0.0/16"
    source_type = "CIDR_BLOCK"
    stateless   = "false"
  }
  ingress_security_rules {
    protocol    = "6"
    source      = "0.0.0.0/0"
    source_type = "CIDR_BLOCK"
    stateless   = "false"
    tcp_options {
      max = "80"
      min = "80"
    }
  }
  ingress_security_rules {
    protocol    = "6"
    source      = "0.0.0.0/0"
    source_type = "CIDR_BLOCK"
    stateless   = "false"
    tcp_options {
      max = "443"
      min = "443"
    }
  }
  manage_default_resource_id = oci_core_vcn.export_keg-vcn.default_security_list_id
}

resource "oci_core_private_ip" "export_kzg-broadcaster-instance" {
  cidr_prefix_length = "32"
  display_name       = "kzg-broadcaster-instance"
  freeform_tags      = {}
  hostname_label     = "kzg-broadcaster-instance"
  ip_address         = "10.0.0.224"
  lifetime           = "EPHEMERAL"
  subnet_id          = oci_core_subnet.export_public-subnet-keg-vcn.id
  # The instance's primary VNIC, looked up so its OCID stays out of the repo.
  vnic_id = data.oci_core_vnic_attachments.broadcaster.vnic_attachments[0].vnic_id

  lifecycle {
    # The lookup reads after apply whenever the instance has a pending change, and vnic_id
    # forces replacement, so without this any instance edit would also replace this IP.
    ignore_changes = [vnic_id]
  }
}

resource "oci_core_instance" "export_kzg-broadcaster-instance_1" {
  agent_config {
    are_all_plugins_disabled = "false"
    is_management_disabled   = "false"
    is_monitoring_disabled   = "false"
    plugins_config {
      desired_state = "DISABLED"
      name          = "Vulnerability Scanning"
    }
    plugins_config {
      desired_state = "DISABLED"
      name          = "OS Management Hub Agent"
    }
    plugins_config {
      desired_state = "DISABLED"
      name          = "Management Agent"
    }
    plugins_config {
      desired_state = "ENABLED"
      name          = "Custom Logs Monitoring"
    }
    plugins_config {
      desired_state = "DISABLED"
      name          = "Compute RDMA GPU Monitoring"
    }
    plugins_config {
      desired_state = "ENABLED"
      name          = "Compute Instance Run Command"
    }
    plugins_config {
      desired_state = "ENABLED"
      name          = "Compute Instance Monitoring"
    }
    plugins_config {
      desired_state = "DISABLED"
      name          = "Compute HPC RDMA Auto-Configuration"
    }
    plugins_config {
      desired_state = "DISABLED"
      name          = "Compute HPC RDMA Authentication"
    }
    plugins_config {
      desired_state = "ENABLED"
      name          = "Cloud Guard Workload Protection"
    }
    plugins_config {
      desired_state = "DISABLED"
      name          = "Block Volume Management"
    }
    plugins_config {
      desired_state = "DISABLED"
      name          = "Bastion"
    }
  }
  availability_config {
    recovery_action = "RESTORE_INSTANCE"
  }
  availability_domain = data.oci_identity_availability_domain.ad1.name
  compartment_id      = var.compartment_ocid
  create_vnic_details {
    assign_public_ip       = "true"
    display_name           = "kzg-broadcaster-instance"
    freeform_tags          = {}
    hostname_label         = "kzg-broadcaster-instance"
    nsg_ids                = []
    private_ip             = "10.0.0.224"
    security_attributes    = {}
    skip_source_dest_check = "false"
    subnet_id              = oci_core_subnet.export_public-subnet-keg-vcn.id
  }
  display_name      = "kzg-broadcaster-instance"
  extended_metadata = {}
  fault_domain      = var.fault_domain
  freeform_tags     = {}
  instance_options {
    are_legacy_imds_endpoints_disabled = "true"
  }
  launch_options {
    boot_volume_type                    = "PARAVIRTUALIZED"
    firmware                            = "UEFI_64"
    is_consistent_volume_naming_enabled = "true"
    is_pv_encryption_in_transit_enabled = "true"
    network_type                        = "PARAVIRTUALIZED"
    remote_data_volume_type             = "PARAVIRTUALIZED"
  }
  metadata = {
    "ssh_authorized_keys" = var.ssh_authorized_keys
  }
  security_attributes = {}
  shape               = "VM.Standard.A1.Flex"
  shape_config {
    baseline_ocpu_utilization = ""
    memory_in_gbs             = "6"
    nvmes                     = "0"
    ocpus                     = "1"
    resource_management       = ""
    vcpus                     = "1"
  }
  source_details {
    boot_volume_vpus_per_gb = "10"
    source_id               = var.instance_image_ocid
    source_type             = "image"
  }
  state = "RUNNING"
}

resource "oci_core_service_gateway" "export_Service-gateway-keg-vcn" {
  compartment_id = var.compartment_ocid
  display_name   = "Service gateway-keg-vcn"
  freeform_tags = {
    "VCN" = "2026-11-24T20:59:57.809Z"
  }
  services {
    service_id = data.oci_core_services.all_oracle_services.services[0].id
  }
  vcn_id = oci_core_vcn.export_keg-vcn.id
}
