using InvoiceMesh.Configuration;
using InvoiceMesh.UiKit;

namespace InvoiceMesh.Pages.Admin.Formats;

/// <summary>
/// The specification identifiers the administrator added: national formats, CIUS and new versions, recognised without a change of the program. They are
/// configuration (app.json) and come before the built-in ones.
/// </summary>
public sealed class IndexModel(ConfigStore config) : PageModelBase
{
    public TableModel Table { get; private set; } = null!;

    public void OnGet()
    {
        Head("Formate", "Eigene Spezifikationskennungen: weitere nationale Standards, Profile und Versionen.", ("Einstellungen", null), ("Formate", null));
        var custom = config.Current.Formats.Custom.Select((c, i) => (c, i)).ToList();
        Table = TableModel.Of("format-table", "Eigene Formate", custom,
        [
            (new TableColumn("Standard und Profil"), x => Ui.NameWithSub($"{x.c.Standard} · {x.c.Profile}", $"/Admin/Formats/Edit?index={x.i}", x.c.Identifier + (x.c.IsPrefix ? " …" : ""))),
            (new TableColumn("Version"), x => Ui.Text(x.c.Version)),
            (new TableColumn("Regeln", HideOnPhone: true), x => Ui.Text(InvoiceMesh.Pages.Formats.IndexModel.Rules(x.c.ToSpecification().Rules))),
        ],
        x => [new RowAction("edit", "Bearbeiten", $"/Admin/Formats/Edit?index={x.i}")])
        with { EmptyTitle = "Noch keine eigenen Formate", EmptyText = "Die eingebauten Standards siehst du unter „Formate“. Hier ergänzt du weitere.", EmptyIcon = "file-code" };
    }
}
