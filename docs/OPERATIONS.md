# Operation

## Ports and volumes

| | Release (`docker-compose.yml`) | Development (`docker-compose.dev.yml`) |
|---|---|---|
| Web | 2308 | 2308 |
| pgAdmin | – | 2318 (`admin@invoicemesh.example.com` / `invoicemesh`, add the server `db`) |
| PostgreSQL | not published | 12308 |
| Volumes | `invoicemesh-data` (`/data`), `invoicemesh-db` | the same, plus `invoicemesh-pgadmin` |

The services are `invoicemesh` and `db` (PostgreSQL 17). The database and its user are made on the first start; the migrations run at start-up.

## Configuration

The settings live in `/data/config/app.json` and are edited on the pages under *Einstellungen*. How the machine is deployed can be set by
environment variables, which win over the file and are never written back:

| Variable | Meaning |
|---|---|
| `INVOICEMESH__Database__Host` / `__Port` / `__Database` / `__Username` / `__Password` | the PostgreSQL (default `db`, 5432, `invoicemesh`, `postgres`, `invoicemesh`) |
| `INVOICEMESH__Server__PublicUrl` | the address people use (links that are handed on) |
| `INVOICEMESH__Server__TimeZone` | default `Europe/Berlin` |
| `INVOICEMESH__Server__ForceSecureCookie` | `true` behind HTTPS |
| `INVOICEMESH_DATA`, `INVOICEMESH_PORT` | the data folder (`/data`) and the port (2308) |
| `INVOICEMESH_DB_PASSWORD` (compose only) | one password for both services of `docker-compose.yml` (in `.env`); set it before the first start, Postgres keeps it in its volume |

## Sign-in, sessions, second factor

- Accounts are local (the e-mail address is the sign-in name). The first administrator is made on the first visit.
- A session is a row in the database; the cookie only names it. The keys that protect the cookie are in `/data/keys`, so a restart of the container keeps everybody signed in.
  **Lose `/data/keys` and everybody has to sign in again; saved passwords (NAS) have to be entered again.**
- TOTP (RFC 6238): *Mein Konto › Sicherheit › Einrichten*, QR code, ten recovery codes. An administrator can reset it. *Einstellungen › Sicherheit* can require it for administrators.
- Five wrong passwords lock an account for 15 minutes.

## Backup

*Einstellungen › Backup*: a **target** (a folder, or a share of a NAS over SMB on port 445), a **plan** (what, where, when) and the retention.

- *What*: *Nur das System* (the database and the settings) or *System und Dateien* (plus every original and every generated file in the volume). The file is a ZIP; with a passphrase it is encrypted (AES-GCM, PBKDF2).
- *When*: hourly (every N hours), daily, weekly (weekday), monthly (day of the month), at a time of day in the time zone of the installation.
  A plan that was missed (the container was off) runs at the next start. A failed run is shown on the overview and tried again (three times, at growing intervals).
- *Retention*: keep the last N, plus one per day, week and month for a number of them. What is neither is deleted from the target after a successful run.
- *Restore*: *Wiederherstellung*: upload or pick a file, the software checks it (a newer version or a damaged file is refused, nothing is touched),
  makes a safety copy of the present state and replaces the database (and, if asked, the files and the settings). The sequences of the ids are set again.
- The database password is **not** part of a backup.
- The NAS: the account needs write access to the share; SMB 2.0.2 to 3.0.2 over TCP 445 (SMBLibrary, there is no port setting).
  *Einstellungen › Speicher (NAS)* can also keep the files (originals, generated files) on a NAS and move them over and back.

## Updating

`docker compose pull && docker compose up -d`. The migrations run at start-up; a backup from a newer version than the running one is refused.
Versions: releases `<major>.<minor>.<build>-<date>` (from `main`), `nightly-<build>-<date>` (from `dev`), `local-<date>` for a local build.

## Processing

- An upload stores the original unchanged (SHA-256 in the database) and queues the document; the worker processes one after the other. After a restart it takes
  up what was waiting or half done.
- Text recognition: Tesseract 5 with `deu eng fra spa ita nld` and Poppler (`pdftoppm`, 300 dpi) are part of the image. *Einstellungen › Verarbeitung* chooses the
  languages, whether an XRechnung (and in which syntax) and a readable PDF are made by themselves, the tolerance of the sum checks and the largest file.
- New standards, CIUS and versions: *Einstellungen › Formate* (stored in `app.json`, no change of the program). They are recognised by the specification identifier (BT-24).

## Checking an XRechnung with the KoSIT validator

InvoiceMesh checks the rules of EN 16931, XRechnung (BR-DE) and Peppol BIS that it implements, and reads every XRechnung it writes back before it keeps it.
The binding check is the validator of the KoSIT; the GitHub action runs it on the XRechnungen of the tests. By hand (Java 17+ or Docker):

```bash
curl -LO https://github.com/itplr-kosit/validator/releases/download/v1.6.3/validator-1.6.3-standalone.jar
curl -LO https://github.com/itplr-kosit/validator-configuration-xrechnung/releases/download/v2026-08-31/xrechnung-3.0.2-validator-configuration-2026-08-31.zip
unzip xrechnung-3.0.2-validator-configuration-2026-08-31.zip -d config
docker run --rm -v "$PWD:/k" -w /k eclipse-temurin:21-jre java -jar validator-1.6.3-standalone.jar -s config/scenarios.xml -r config -h RE-2026-0815.xrechnung-ubl.xml
```

## Tests

- `dotnet test InvoiceMesh.slnx` with `INVOICEMESH_TEST_DB=Host=localhost;Port=12308;Username=postgres;Password=invoicemesh` (the Postgres of the dev stack).
- The NAS tests need Samba on port 445 (`INVOICEMESH_TEST_SMB=host;share=backups;user=backup;password=backup-secret`). On Windows 445 is taken: run Samba and the
  tests as containers in the network of the dev stack (see CLAUDE.md).
- `cd tests/e2e && npm install && node flow.js http://localhost:2308`: the way through the app in Edge (`SHOTS=<folder>`, `MOBILE=1`).
