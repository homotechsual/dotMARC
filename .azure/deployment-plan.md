# dotMARC container refresh

Status: Deployed

## 1. Scope and approval

Refresh the existing Container App from the published `latest` image.
User approved the image switch and this plan on 2026-11-02.
Do not build local code, provision resources, or modify application configuration.

## 2. Azure context

- Subscription: Microsoft Partner Network (`069d8525-b483-42c6-bf55-5a92d1b46375`)
- Resource group: `dotMARC`
- Container App: `dotmarc`
- Region: UK South

## 3. Recipe

recipe.type: azcli

## 4. Current and target images

- Current: `ghcr.io/homotechsual/dotmarc:0.9.3`
- Target: `ghcr.io/homotechsual/dotmarc:latest`
- Target manifest observed: `sha256:d0e2ccec08d76c871546b752941f7a6f9c749d2255771e647fbf99740527aa0e`

## 5. Execution

Inspect revision mode, health, ingress, and target image availability.
Create a fresh revision with the target image, preserving existing settings.
Verify revision provisioning, replica health, and HTTPS availability.

## 6. Recovery

Retain the previous revision and its name for rollback if startup fails.
New images may apply database migrations; automatic downgrade is not assumed safe.
Previous healthy revision: `dotmarc--v093`. Single revision mode currently routes 100% to the latest revision.

## 7. Validation Proof

Validation performed 2026-11-02:

- `az account show`: authenticated in the approved subscription.
- `az containerapp show` and `az containerapp revision list`: existing app Succeeded,
  previous revision Provisioned and Healthy, Single mode with 100% latest-revision traffic.
- GHCR token and manifest GET: public `latest` resolves to the digest above, supporting Linux amd64 and arm64.
- `az policy assignment list --scope <resource group> --disable-scope-strict-match`: no applicable assignments returned.
- `az rest .../Microsoft.Authorization/permissions`: current caller has `*` actions with no exclusions on the app.
- Build verification uses the existing published registry manifest; local rebuild is intentionally outside scope.
- Static infrastructure-role review is not applicable: no infrastructure or role changes; effective permissions verified live.

### All validation checks pass

- [x] Core validation: CLI and authentication, target app health, current revision and traffic.
  Bicep build, deployment validate and what-if are not applicable: no infrastructure deployment.
- [x] Docker build: not applicable; use the published image only. Check registry manifest and Linux amd64 support.
- [x] Azure policy validation: verify applicable assignments for the existing resource group; no new resources.
- [x] Role verification: confirm current principal can update the existing Container App.

## 8. Deployment results

- Updated only the existing container image and revision suffix using `az containerapp update`.
- New revision: `dotmarc--latest-20261102182316`.
- Image: `ghcr.io/homotechsual/dotmarc:latest`.
- Latest ready revision matches the new revision; provisioning Succeeded.
- Revision Healthy, RunningAtMaxScale, receiving 100% traffic.
- Replica ready and started, with zero restarts.
- HTTPS checks return the expected 302 Microsoft Entra sign-in redirect on both
  `https://dotmarc.mjco.dev` and the Azure ingress hostname.
- No infrastructure, database credentials, environment variables, domains or roles changed.
