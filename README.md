# KeyCloak Green Action Network Integration

This repository contains the Linux-first prototype for synchronising **Action Network** people and selected tags into **Keycloak** users and groups.

## What is here

1. `MkGreens.IdentitySync/` — a `.NET 8` worker that:
   - fetches people from Action Network
   - optionally maps Action Network tags to Keycloak groups
   - creates or updates Keycloak users
   - reconciles managed group membership
   - stores sync run history and person links in SQLite
2. `LocalDevelopment/keycloak/` — a local Keycloak + PostgreSQL Docker Compose stack for development

## Local Keycloak stack

Start Keycloak and PostgreSQL:

```bash
cp LocalDevelopment/keycloak/.env.example LocalDevelopment/keycloak/.env
docker compose -f LocalDevelopment/keycloak/compose.yaml up -d
```

This exposes:

- Keycloak admin UI at `http://localhost:8080/admin/`
- local bootstrap admin user `admin` / `admin`
- imported realm `mkgreens-local`

The checked-in compose file only contains safe local defaults. Override the local admin or database credentials in `LocalDevelopment/keycloak/.env` if you do not want to use the development defaults.

## Worker configuration

Use `.NET user-secrets` locally:

```bash
cd MkGreens.IdentitySync
dotnet user-secrets set "ActionNetwork:ApiToken" "<action-network-api-key>"
dotnet user-secrets set "Keycloak:AdminUsername" "admin"
dotnet user-secrets set "Keycloak:AdminPassword" "admin"
```

If you want Action Network tags to drive Keycloak groups, update `Sync:TagMappings` in `MkGreens.IdentitySync/appsettings.json` or user secrets with the real Action Network tag IDs and target Keycloak group paths.

## Running the worker

Run one sync pass:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project MkGreens.IdentitySync/MkGreens.IdentitySync.csproj -- --once
```

Run it as a timed background worker:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project MkGreens.IdentitySync/MkGreens.IdentitySync.csproj
```

The worker writes local sync state to `MkGreens.IdentitySync/data/`.

## Linux server deployment shape

Starter `systemd` unit files live in:

```text
MkGreens.IdentitySync/deploy/systemd/
```

They assume the published worker is deployed to:

```text
/opt/mkgreens/identity-sync/
```
