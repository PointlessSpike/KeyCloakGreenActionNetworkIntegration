# KeyCloak Green Action Network Integration

This repository contains the Linux-first prototype for synchronising **Action Network** people into **Keycloak** users, with optional tag-to-group synchronisation for parties that want it.

## What is here

1. `GreenParty.IdentitySync/` — a `.NET 8` worker that:
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
- imported realm `greenparty-local`

The checked-in compose file only contains safe local defaults. Override the local admin or database credentials in `LocalDevelopment/keycloak/.env` if you do not want to use the development defaults.

## Worker configuration

Use `.NET user-secrets` locally:

```bash
cd GreenParty.IdentitySync
dotnet user-secrets set "ActionNetwork:ApiToken" "<action-network-api-key>"
dotnet user-secrets set "Keycloak:AdminUsername" "admin"
dotnet user-secrets set "Keycloak:AdminPassword" "admin"
```

By default, the worker only provisions and updates **users** from Action Network. It does **not** change Keycloak groups unless you explicitly configure `Sync:TagMappings`.

If you want Action Network tags to drive Keycloak groups, add `Sync:TagMappings` entries in `GreenParty.IdentitySync/appsettings.json` or user secrets with the real Action Network tag IDs and target Keycloak group paths.

## Running the worker

Run one sync pass:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project GreenParty.IdentitySync/GreenParty.IdentitySync.csproj -- --once
```

Run it as a timed background worker:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project GreenParty.IdentitySync/GreenParty.IdentitySync.csproj
```

The worker writes local sync state to `GreenParty.IdentitySync/data/`.

## Linux server deployment shape

Starter `systemd` unit files live in:

```text
GreenParty.IdentitySync/deploy/systemd/
```

They assume the published worker is deployed to:

```text
/opt/greenparty/identity-sync/
```

For a Linux server deployment, the repository now also includes:

```text
deploy/server/
```

That folder contains:

1. a production-oriented `docker compose` file for **Keycloak + PostgreSQL**
2. a `.env.example` for server-side Keycloak configuration
3. a realm import file for first boot
4. an example environment file for the sync worker service

### Suggested server layout

Use a split like this:

1. **Keycloak + PostgreSQL**
   - run with Docker Compose from `deploy/server/`
   - bind Keycloak to `127.0.0.1:8080`
   - keep PostgreSQL private inside Docker only
2. **Reverse proxy**
   - terminate TLS with Nginx, Caddy, or another front-end proxy
   - forward traffic to `http://127.0.0.1:8080`
3. **Sync worker**
   - publish `GreenParty.IdentitySync` to `/opt/greenparty/identity-sync/`
   - install the provided `systemd` service/timer
   - store runtime settings in `/etc/greenparty-identity-sync/greenparty-identity-sync.env`

### Suggested deployment steps

1. Prepare Keycloak server files:

```bash
mkdir -p /opt/greenparty/keycloak
cp -r deploy/server/* /opt/greenparty/keycloak/
cd /opt/greenparty/keycloak
cp .env.example .env
```

2. Edit `.env` with real values:
   - `KEYCLOAK_DB_PASSWORD`
   - `KEYCLOAK_BOOTSTRAP_ADMIN_PASSWORD`
   - `KEYCLOAK_HOSTNAME`

3. Start Keycloak:

```bash
docker compose up -d
```

4. Publish the worker:

```bash
dotnet publish GreenParty.IdentitySync/GreenParty.IdentitySync.csproj -c Release -o /opt/greenparty/identity-sync
```

5. Create the worker runtime config:

```bash
sudo mkdir -p /etc/greenparty-identity-sync
sudo cp GreenParty.IdentitySync/deploy/systemd/greenparty-identity-sync.env.example /etc/greenparty-identity-sync/greenparty-identity-sync.env
```

6. Edit `/etc/greenparty-identity-sync/greenparty-identity-sync.env` with real values:
   - `ActionNetwork__ApiToken`
   - `Keycloak__AdminPassword` or switch to a dedicated client later
   - optional sync settings

7. Install and enable the timer:

```bash
sudo cp GreenParty.IdentitySync/deploy/systemd/greenparty-identity-sync.service /etc/systemd/system/
sudo cp GreenParty.IdentitySync/deploy/systemd/greenparty-identity-sync.timer /etc/systemd/system/
sudo useradd --system --home /opt/greenparty/identity-sync --shell /usr/sbin/nologin greenparty-sync || true
sudo systemctl daemon-reload
sudo systemctl enable --now greenparty-identity-sync.timer
```

### Reverse proxy note

The production compose file expects a reverse proxy in front of Keycloak. The proxy should forward the external hostname you set in `KEYCLOAK_HOSTNAME` to:

```text
http://127.0.0.1:8080
```
