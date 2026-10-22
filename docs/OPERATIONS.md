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

## Intake: folders, NAS shares, mailboxes

*Einstellungen › Eingänge*: places documents come from by themselves. Each one is looked at every N minutes (1 to 1440); *Jetzt abholen* runs it at once,
*Prüfen* tries the settings. What is found is taken in exactly like an upload; a ZIP is unpacked (up to 500 files, each within the upload limit, macOS and
Windows leftovers skipped), and every document keeps where it came from (`Source`, `SourceDetail`: shown on the *Original* tab and in the API).

- **Folder**: a path relative to `/data` (default `eingang`) or a folder mounted into the container. The container runs as UID 1654: the folder must be
  writable for it, because taken files are moved away.
- **NAS (SMB)**: a folder on a share, over SMB on port 445, like a backup target. No mount, no extra rights.
- Files are taken once they have not changed for 5 seconds (a scanner may still be writing). Afterwards they are moved to a folder below
  (`verarbeitet`, with the time in front of the name); without one they are deleted (the original stays in InvoiceMesh). A file nothing could be taken from
  (empty, too large, an empty ZIP) goes to `fehler`. At most 200 files per run.
- **Mailbox (IMAP)**: TLS (993), STARTTLS (143) or none. The *unread* mails of a folder (`INBOX`) are read: PDF, XML and ZIP attachments always, pictures only as
  real attachments (a logo in a signature is not). Each mail is marked as read and, when a done folder is set (e.g. `Rechnungen/Verarbeitet`), moved there
  (made when missing). A mail without a usable attachment is reported in the result of the run and handled the same way. At most 100 mails per run.
  Many providers want an app password for IMAP.
- Each run writes its result to the list (*Ergebnis*); runs that took something in or failed go to the activity log.

## Planned exports

*Einstellungen › Geplante Exporte*: an export (*Exporte*: format, schema, fields) that runs on a schedule (as backups: hourly, daily, weekly, monthly) and delivers its file.

- *Which documents*: **new since the last run** (by the time of processing; the first run goes back the given number of days; a document processed again counts
  as new again; documents finished in the last 30 seconds wait for the next run, so none is lost while it is being saved) or **the last N days** every time.
  Optionally only those that are not invalid, and no file when there is nothing.
- *Where*: a **folder** (relative to `/data`, default `exporte`, or mounted), a **NAS share** (SMB), or a **webhook**: an HTTP POST of the file to an address,
  with its name in `Content-Disposition`, the headers `X-InvoiceMesh-Export` and `X-InvoiceMesh-Count`, and one optional header of your own (for example
  `Authorization: Bearer …`, stored encrypted). Any 2xx counts as delivered; redirects are not followed; 60 seconds timeout.
  *Prüfen* sends `{"test":true}` with `X-InvoiceMesh-Test: true`.
- A failed run is tried again after 15, 30 and 45 minutes, then at the next planned time; what it missed stays new until a run delivers it.

## API

*Einstellungen › API*: keys for other systems. A key (`im_` and 43 characters) is shown **once** when it is made; the database keeps its SHA-256 only.
A key may **import**, **read** or both, can expire and be switched off; *Neu erzeugen* replaces it at once.

Every call sends the key as `Authorization: Bearer im_…` or `X-Api-Key: im_…`. Answers are JSON (camelCase, enums as strings, times in UTC).

| Call | Right | What it does |
|---|---|---|
| `POST /api/v1/invoices` | import | Takes documents in: `multipart/form-data` with one or more files (any field name, up to 100), or the bytes of one file as the body with its name in `X-File-Name` or `?fileName=`. ZIPs are unpacked. **202** with `documents` (`id`, `fileName`, `duplicateOf`, `url`) and `problems`; **422** when nothing could be taken; **400** without a file or a name. |
| `GET /api/v1/invoices?since=&status=&limit=` | read | The documents, newest first. `since`: time of receipt (ISO 8601); `status`: `queued`, `processing`, `processed`, `failed`; `limit` 1–500 (100). |
| `GET /api/v1/invoices/{id}` | read | State, format, schema and mapping used, validation counts, invoice number, parties, total, generated files with their addresses. |
| `GET /api/v1/invoices/{id}/normalized` | read | The data in the target schema (409 before the normalization). |
| `GET /api/v1/invoices/{id}/extracted` | read | Format, business terms as read and as corrected, corrections, messages, raw fields. |
| `GET /api/v1/invoices/{id}/report` | read | The validation report: status, findings, checks that ran, whether an XRechnung can be made and what is missing. |
| `GET /api/v1/invoices/{id}/xrechnung` | read | The newest XRechnung of the current data (409 when none was made or none can be). |
| `GET /api/v1/invoices/{id}/original` | read | The original file, unchanged. |
| `GET /api/v1/invoices/{id}/files/{fileId}` | read | A generated file (PDF view, XRechnung). |

Errors: **401** no valid key (`WWW-Authenticate: Bearer`), **403** the key lacks the right, **404** unknown, **409** not that far yet; the body is
`{"error": "…"}` (German). A typical client posts the file, keeps the `id`, polls `GET /invoices/{id}` until `processingStatus` is `processed` or `failed`,
then fetches what it needs.

```bash
curl -H "Authorization: Bearer im_…" -F "file=@rechnung.pdf" https://invoices.example.com/api/v1/invoices
curl -H "Authorization: Bearer im_…" https://invoices.example.com/api/v1/invoices/42/normalized
```

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
- The mailbox test needs a mail server that takes any sign-in (`INVOICEMESH_TEST_IMAP=localhost;imap=3143;smtp=3025`), for example
  `docker run -d -p 3025:3025 -p 3143:3143 -e GREENMAIL_OPTS="-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.auth.disabled" greenmail/standalone:2.1.3`.
- `cd tests/e2e && npm install && node flow.js http://localhost:2308`: the way through the app in Edge (`SHOTS=<folder>`, `MOBILE=1`).
