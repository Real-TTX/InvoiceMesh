# InvoiceMesh: notes for working in this repository

Reads international invoices (UBL, CII, XRechnung, ZUGFeRD/Factur-X, Peppol, PDFs, scans), normalizes them into a schema the user defines through versioned
mappings, validates them (EN 16931, XRechnung, Peppol) and writes XRechnung, PDF views and JSON/CSV/XML exports. ASP.NET Core 10 Razor Pages, EF Core 10 on
PostgreSQL 17, one container (port **2308**) plus a database. User-facing text is **German**; code, comments and docs (except the README) are English.
The infrastructure (setup, sign-in, TOTP, sessions, backup to a NAS, list controls) came from `../MatBill` (which has it from `../NextProject`); the look is
MatDesign (`../MatDesign`, MatUi copied into `src/InvoiceMesh/wwwroot/ui`). Before changing UI read MatDesign's `CLAUDE.md` and `docs/ADOPTING.md`.

## Commands

```powershell
./scripts/redeploy.ps1            # always: rebuild the image and redeploy the dev stack (web 2308, pgAdmin 2318, Postgres 12308)
./scripts/redeploy.ps1 -Fresh     # the same with empty volumes (setup page again; Einstellungen > System makes the sample documents)
dotnet build InvoiceMesh.slnx     # build locally BEFORE a redeploy: a compile error otherwise shows up as "docker compose failed"
$env:INVOICEMESH_TEST_DB="Host=localhost;Port=12308;Username=postgres;Password=invoicemesh"; dotnet test InvoiceMesh.slnx
cd tests/e2e; npm install; node flow.js http://localhost:2308    # the way through the app in Edge; SHOTS=<dir> keeps screenshots, MOBILE=1 for 390 px
NODE_PATH=tests/e2e/node_modules node tools/icons.js              # PNG icons of the manifest from the SVGs
dotnet ef migrations add <Name> -p src/InvoiceMesh -s src/InvoiceMesh -o Data/Migrations
./scripts/sync-matui.ps1          # take the toolkit from the running MatDesign (http://localhost:8060/ui/matui.zip)
```

Test changes by **rebuilding the container and redeploying the stack**. Wait for `http://localhost:2308/health`. The app shell scrolls inside `.mat-main`: a
full-page screenshot needs a viewport as tall as the content (flow.js does it).

The NAS tests need Samba on 445 (`INVOICEMESH_TEST_SMB`, the CI starts one). On Windows 445 is taken: run `servercontainers/samba` with a volume on
`/shares/backups` in the network `invoicemesh-dev_default` and `dotnet test` in `mcr.microsoft.com/dotnet/sdk:10.0` in the same network (copy the source in without
`bin`/`obj`; `INVOICEMESH_TEST_DB=Host=db;...`, `INVOICEMESH_TEST_SMB=<samba container>;share=backups;user=backup;password=backup-secret`). `MSYS_NO_PATHCONV=1` in Git Bash.

## Layout

- `src/InvoiceMesh.Core` (no web types, all unit tested):
  - `Semantics/` the semantic model of EN 16931 (`SemanticDocument`: business terms `BT-n` and items of `BG-25/23/20/21`, every value with confidence and source)
    and **`SemanticCatalog`, the one table of the bindings to UBL and CII** (paths of `Xml/XPathLite`).
    `Xml/XmlSchemas` checks UBL 2.1 / CII D16B against the official XSDs in `Xsd/` (embedded, unchanged from the KoSIT configuration): the reader writes
    the result as messages `XSD` / `XSD-VALID`, the validation shows them as scope `Structure`; errors only where the specification requires that schema.
  - `Formats/` `DetectedFormat` (kind, syntax, standard, profile, version, evidence), `SpecificationRegistry` (BT-24 identifiers; custom ones come from config).
  - `Extraction/` `InvoiceReader` (order: XML, XML inside a PDF, PDF text, OCR through `IOcrEngine`), `XmlInvoiceReader` (raw fields with indexes + semantic),
    `PdfContent` (attachments, text lines by position), `TextInvoiceExtractor` (multilingual labels, `TextValues` for amounts/dates/IBAN/VAT ids).
  - `Schemas/` the user's schema (`SchemaDefinition`, `FieldValues` typing); `Mapping/` rules, `Transforms`, `MappingEngine` (outcome per field: source, status,
    candidates), `Defaults` (standard schema + mapping); `Validation/` `SemanticValidator` (rule ids as in the standards), `CodeLists`; `Output/` `XRechnungWriter`
    (UBL Invoice/CreditNote, CII, element order of the schemas), `Exporter` (JSON/CSV/XML).
- `src/InvoiceMesh`: `Services/InvoiceService` (the pipeline: add → process → normalize → validate → outputs, corrections, renormalize), `DefinitionService`
  (versioned schemas and mappings: draft → active → retired, mapping selection by specificity and priority, diffs), `Processing/` (queue + worker, Tesseract),
  `InvoicePdf`, `ExportService`, `DemoData` (`Demo/*` embedded samples), `Backup/`, `Auth/`; `Pages/` (Invoices with tabs, Schemas, Mappings, Exports, Formats, Admin,
  Account, Share); `Data/` (ImDbContext, entities, migrations); `Controls/` + `UiKit/` (shell, lists, `im-*` tag helpers).
- `tests/InvoiceMesh.Tests`: `Core/` (with `Samples/`), `Web/`, `Integration/` (real app + PostgreSQL / Samba); `tests/e2e/flow.js`.

## Rules of the domain

- **The original never changes.** It is stored as uploaded (SHA-256 kept); the XML of a hybrid PDF is stored beside it. Deleting a document is a soft delete.
- **Nothing is made up.** Readers write only what the document says (the counted line number BT-126 of a text invoice is the one exception, marked).
  Defaults are marked as defaults. An XRechnung is only written when `SemanticValidator.XRechnungReadiness` finds nothing; it is read back and validated after writing.
- **Processing ≠ validation**: `ProcessingStatus` and `ValidationStatus` are separate columns and separate pills.
- **Corrections are laid over the extraction** (`InvoiceCorrection`, newest per key counts, `Reverted` takes back). Source corrections (`BT-n`, `BG-25[2]/BT-129`)
  feed everything; target corrections (`group.field`, `group[2].field`) only that schema's data.
- **Versions**: only a draft changes; activating retires the old version. A revision stores schema version + mapping version + extraction; documents on an
  older schema version are shown as such and renormalized on request. Renaming a schema key carries the rename into the mapping drafts.
- A document is claimed for processing by one `UPDATE ... WHERE Status = Queued`; the worker resets `Processing` to `Queued` at start.
- The XRechnung rules implemented are a subset; the CI runs the **KoSIT validator** on the XRechnungen the tests write (`INVOICEMESH_XRECHNUNG_OUT`).
- Database: tables PascalCase (the document entity is `InvoiceDocument`, table `Invoice`), `Id` bigint, audit columns filled by `ImDbContext`, `UpdateState` soft
  delete (User, InvoiceDocument, TargetSchema, MappingSet, ExportProfile), enums as text, properties ending in `Json` are `jsonb`, `Token` uuid for secrets.
  Configuration (processing, custom formats, storage, security, backup) is JSON in `/data/config/app.json`; logic and versioned definitions in the database.

## UI conventions

- MatUi only, flat look; app classes `im-*` in `app.css`; no inline styles or scripts (CSP `default-src 'self'`): data becomes a custom property in `app.js`
  (`data-bar`). Icons from the sprite (`Ui.Icon`; own icons and the logo glyph in `ui/im-icons.svg`).
- Lists: toolbar above (search, filter, sort), table, pager directly below, actions below on the left. CRUD = list + separate edit page. Buttons in one row from
  positive to negative, delete spaced apart (`im-buttons`). Dependent fields with `show-if` (also on `im-card`). Several controls of one kind per page: ids derive from the control id.
- A document has link tabs (`InvoicePage`): Übersicht, Original, Daten, Validierung, Dateien, Verlauf. Pages that show waiting documents poll `/api/invoices/status`.
- The accent is `#1a73c8` (white on it 4.85 : 1); the logo is `wwwroot/logo.svg` (tile) and `im-logo` (glyph).
- Formatting: request culture invariant; text a person reads goes through `Fmt` (de-DE). Razor encodes umlauts as entities: tests compare decoded text.

## Gotchas

- Shell strings with backticks, `$`, apostrophes get mangled by the Bash tool: write files with the editor tools.
- In Razor, `@foreach`/`@{` directly inside a code block is an error; a variable named `section` clashes with the directive.
- `RedirectToPage` to a page that does not exist is a 500, not a 404.
- Tesseract reads a column of amounts as a block of its own: `TesseractOcr.ReadTsv` rebuilds lines by position.
- The dev compose has an explicit subnet (`10.253.38.0/24`).

## Git

Branches: `main` (release), `dev` (development). Commit and push **under the user's name** only: no `Co-Authored-By` trailer, no "Generated with" line.
Versions: release `<major>.<minor>.<build>-<date>`, dev `nightly-<build>-<date>`, local `local-<date>` (`Directory.Build.props`, set by the GitHub action).
