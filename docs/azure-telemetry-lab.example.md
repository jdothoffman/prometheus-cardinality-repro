# Azure telemetry lab VM (template)

Copy to `docs/azure-telemetry-lab.md` (gitignored) and fill in your values. That file stays local — this repo is public.

Always-on-capable Linux VM for hosting a metrics stack you can send telemetry to from your own apps (and optionally run this repo’s k3d repro). **Not Spot** — Spot eviction would drop the receiver.

Local cluster layout: [infrastructure-and-tests.md](infrastructure-and-tests.md).

## Provisioned resources

| Item | Value |
|---|---|
| Subscription | `<name>` (`<subscription-id>`) |
| Resource group | `rg-telemetry-lab` |
| Region | `eastus` |
| VM name | `vm-telemetry-lab` |
| Size | `Standard_B4ms` (4 vCPU / 16 GiB) — or `Standard_D4s_v5` if you have DSv5 quota |
| Priority | Regular (not Spot) |
| OS | Ubuntu 24.04 LTS |
| Admin user | `azureuser` |
| Auth | SSH public key |
| Public IP | `<public-ip>` (prefer Static) |
| NSG | SSH **22** only until OTLP/Grafana are secured |

## Cost (ballpark, East US Linux)

| State | Approx. |
|---|---|
| B4ms running | ~$0.166/hr |
| Deallocated | $0 compute; OS disk still bills (~$8–10/mo for 128 GB Standard SSD) |

## Deploy

```sh
# Local (gitignored): VM_HOST=azureuser@<public-ip>
cp .lab.env.example .lab.env   # edit VM_HOST

./scripts/remote-deploy.sh all
```

On the VM: `./scripts/deploy-lab.sh all`  
UI via SSH tunnels — see script `ui-hint` output. Full private notes: your local `azure-telemetry-lab.md`.

## Recreate

```sh
az group create --name rg-telemetry-lab --location eastus

az vm create \
  --resource-group rg-telemetry-lab \
  --name vm-telemetry-lab \
  --location eastus \
  --image Canonical:ubuntu-24_04-lts:server:latest \
  --size Standard_B4ms \
  --admin-username azureuser \
  --ssh-key-values ~/.ssh/id_ed25519.pub \
  --os-disk-size-gb 128 \
  --storage-sku StandardSSD_LRS \
  --public-ip-sku Standard \
  --nsg-rule SSH \
  --priority Regular \
  --tags purpose=telemetry-lab project=grafanPromPoc
```

## Tear down

```sh
az group delete --name rg-telemetry-lab --yes --no-wait
```
