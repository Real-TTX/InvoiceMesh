using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Schemas;

/// <summary>The target schemas: what fields the normalized data have. Each is versioned; the documents keep the version they were made with.</summary>
public sealed class IndexModel(ImDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Zielschemas", "Welche Felder und Spalten deine normalisierten Rechnungsdaten haben. Änderungen entstehen als Entwurf und gelten erst nach dem Aktivieren.", ("Zielschemas", null));
        var spec = new ListSpec<TargetSchema>
        {
            Id = "schemas",
            Action = "/Schemas",
            Label = "Zielschemas",
            Noun = "Schemas",
            SearchPlaceholder = "Name oder Beschreibung ...",
            Search = (q, text) => q.Where(s => EF.Functions.ILike(s.Name, Like.Contains(text)) || EF.Functions.ILike(s.Description ?? "", Like.Contains(text))),
            Filters =
            [
                new FilterSpec<TargetSchema>("entwurf", "Entwurf", [new("ja", "Mit offenem Entwurf"), new("nein", "Ohne Entwurf")], (q, v) => v == "ja"
                    ? q.Where(s => s.Versions.Any(x => x.State == VersionState.Draft)) : q.Where(s => !s.Versions.Any(x => x.State == VersionState.Draft))),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<TargetSchema>, bool, IOrderedQueryable<TargetSchema>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(s => s.Name) : q.OrderBy(s => s.Name)),
                ["neu"] = ("Angelegt", (q, d) => d ? q.OrderBy(s => s.CreateDate) : q.OrderByDescending(s => s.CreateDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.TargetSchema.AsNoTracking().Include(s => s.Versions), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var ids = result.Items.Select(s => s.Id).ToList();
        var mappings = await db.MappingSet.Where(m => ids.Contains(m.TargetSchemaId)).GroupBy(m => m.TargetSchemaId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var documents = await db.Invoice.Where(i => i.SchemaVersionId != null).Join(db.TargetSchemaVersion, i => i.SchemaVersionId, v => v.Id, (i, v) => v.TargetSchemaId)
            .Where(sid => ids.Contains(sid)).GroupBy(sid => sid).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

        Table = TableModel.Of("schema-table", "Zielschemas", result.Items,
        [
            (new TableColumn("Schema"), s => Ui.NameWithSub(s.Name + (s.IsDefault ? " (Standard)" : ""), $"/Schemas/Edit?id={s.Id}", s.Description)),
            (new TableColumn("Aktive Version"), s => s.Versions.Where(v => v.State == VersionState.Active).Select(v => (int?)v.Number).FirstOrDefault() is { } n ? Ui.Text("v" + n) : Ui.Pill("keine", "warn")),
            (new TableColumn("Entwurf", HideOnPhone: true), s => s.Versions.Any(v => v.State == VersionState.Draft) ? Labels.Pill(VersionState.Draft) : Ui.None()),
            (new TableColumn("Felder", Numeric: true, HideOnPhone: true), s => Ui.Text(FieldCount(s).ToString())),
            (new TableColumn("Mappings", Numeric: true, HideOnPhone: true), s => Ui.Text(mappings.GetValueOrDefault(s.Id).ToString())),
            (new TableColumn("Belege", Numeric: true), s => Ui.Text(documents.GetValueOrDefault(s.Id).ToString())),
        ],
        s => [new RowAction("edit", "Bearbeiten", $"/Schemas/Edit?id={s.Id}"), new RowAction("history", "Versionen", $"/Schemas/Versions?id={s.Id}")]);
    }

    private static int FieldCount(TargetSchema s)
    {
        var version = s.Versions.FirstOrDefault(v => v.State == VersionState.Draft) ?? s.Versions.FirstOrDefault(v => v.State == VersionState.Active);
        return version is null ? 0 : DefinitionService.Definition(version).AllFields().Count();
    }
}
