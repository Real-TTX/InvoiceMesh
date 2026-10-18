using System.Text.Json.Nodes;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Core.Semantics;

namespace InvoiceMesh.Tests.Core;

public class MappingTests
{
    private static async Task<MappingInput> InputAsync(string sample)
    {
        var r = await new InvoiceReader(new SpecificationRegistry()).ReadAsync(Samples.Bytes(sample), sample);
        return new MappingInput { Semantic = r.Semantic, Raw = r.SourceFields, Meta = new Dictionary<string, string> { ["fileName"] = sample, ["syntax"] = r.Format.Syntax.ToString() } };
    }

    [Fact]
    public async Task The_default_mapping_fills_the_default_schema_from_UBL_and_CII_alike()
    {
        foreach (var sample in new[] { "xrechnung-ubl.xml", "facturx-en16931.xml" })
        {
            var result = MappingEngine.Run(Defaults.Schema(), Defaults.Mapping(), await InputAsync(sample));
            Assert.Empty(result.Report.DefinitionProblems);
            Assert.Empty(result.Report.MissingRequired);
            Assert.Equal(2, ((JsonArray)result.Data["lines"]!).Count);
            Assert.NotNull(result.Data["totals"]!["grossTotal"]);
        }
    }

    [Fact]
    public async Task Typed_values_and_provenance()
    {
        var result = MappingEngine.Run(Defaults.Schema(), Defaults.Mapping(), await InputAsync("xrechnung-ubl.xml"));

        Assert.Equal("2026-10-01", (string)result.Data["invoice"]!["issueDate"]!);
        Assert.Equal(1243.5m, (decimal)result.Data["totals"]!["grossTotal"]!);
        Assert.Equal(10m, (decimal)result.Data["lines"]![0]!["quantity"]!);
        var name = result.Report.Fields.Single(f => f.Target == "seller.name");
        Assert.Equal("bt:BT-27", name.Source);
        Assert.Equal(FieldStatus.Mapped, name.Status);
        Assert.Equal(2, name.Candidates.Count);
        Assert.Contains(result.Report.Fields, f => f is { Target: "lines.name", Item: 2, Value: "Fachbuch" });
    }

    private static SchemaDefinition SmallSchema() => new()
    {
        Groups =
        [
            new SchemaGroup
            {
                Key = "head", Label = "Kopf",
                Fields =
                [
                    new SchemaField { Key = "number", Label = "Nummer", Required = true },
                    new SchemaField { Key = "date", Label = "Datum", Type = FieldType.Date },
                    new SchemaField { Key = "who", Label = "Wer" },
                    new SchemaField { Key = "costCenter", Label = "Kostenstelle", Required = true, DefaultValue = "4711" },
                    new SchemaField { Key = "kind", Label = "Art" },
                ],
            },
            new SchemaGroup { Key = "items", Label = "Positionen", Repeating = true, Fields = [new SchemaField { Key = "text", Label = "Text" }, new SchemaField { Key = "no", Label = "Nr", Type = FieldType.Integer }] },
        ],
    };

    [Fact]
    public async Task Priorities_conflicts_defaults_conditions_transforms_and_raw_lists()
    {
        var mapping = new MappingDefinition
        {
            Collections = [new CollectionBinding { Group = "items", Source = "xml:/Invoice/cac:InvoiceLine" }],
            Rules =
            [
                new MappingRule { Target = "head.number", Sources = ["bt:BT-999", "xml:/Invoice/cbc:ID"], Transforms = [new TransformStep { Op = "split", Arg = "-", Arg2 = "-1" }] },
                new MappingRule { Target = "head.date", Sources = ["const:01.10.2026"], Transforms = [new TransformStep { Op = "date", Arg = "dd.MM.yyyy" }] },
                new MappingRule { Target = "head.who", Sources = ["bt:BT-27", "bt:BT-28"] },
                new MappingRule { Target = "head.kind", Sources = ["const:Behörde"], Condition = new RuleCondition { Source = "bt:BT-10", Operator = ConditionOperator.Matches, Value = @"^\d{8}-" } },
                new MappingRule { Target = "items.text", Sources = ["xml:cac:Item/cbc:Name", "xml:cac:Item/cbc:Description"], Combine = CombineMode.Concat, Separator = " – " },
                new MappingRule { Target = "items.no", Sources = ["meta:index"] },
                new MappingRule { Target = "head.nothing", Sources = ["bt:BT-1"] },
            ],
        };

        var result = MappingEngine.Run(SmallSchema(), mapping, await InputAsync("xrechnung-ubl.xml"));
        var fields = result.Report.Fields;

        Assert.Equal("0815", (string)result.Data["head"]!["number"]!);
        Assert.Equal("xml:/Invoice/cbc:ID", fields.Single(f => f.Target == "head.number").Source);
        Assert.Equal("2026-10-01", (string)result.Data["head"]!["date"]!);
        Assert.Equal(FieldStatus.Conflict, fields.Single(f => f.Target == "head.who").Status);
        Assert.Equal("4711", (string)result.Data["head"]!["costCenter"]!);
        Assert.Equal(FieldStatus.Default, fields.Single(f => f.Target == "head.costCenter").Status);
        Assert.Equal("Behörde", (string)result.Data["head"]!["kind"]!);
        Assert.Equal("Beratung – IT-Beratung im September", (string)result.Data["items"]![0]!["text"]!);
        Assert.Equal(2L, (long)result.Data["items"]![1]!["no"]!);
        Assert.Contains(result.Report.DefinitionProblems, p => p.Contains("head.nothing", StringComparison.Ordinal));
        Assert.Contains(result.Report.Unused, u => u.Reference == "bt:BT-112");
    }

    [Fact]
    public async Task A_manual_override_wins_and_is_marked()
    {
        var mapping = new MappingDefinition { Rules = [new MappingRule { Target = "head.number", Sources = ["bt:BT-1"] }] };
        var overrides = new Dictionary<string, string> { ["head.number"] = "KORR-1", ["items[1].text"] = "Hand" };

        var result = MappingEngine.Run(SmallSchema(), mapping, await InputAsync("xrechnung-ubl.xml"), overrides);

        Assert.Equal("KORR-1", (string)result.Data["head"]!["number"]!);
        Assert.Equal(FieldStatus.Manual, result.Report.Fields.Single(f => f.Target == "head.number").Status);
        Assert.Equal("Hand", (string)result.Data["items"]![0]!["text"]!);
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_invalid_and_not_written()
    {
        var input = new MappingInput { Semantic = new SemanticDocument() };
        input.Semantic.Set("BT-2", "gestern");
        var mapping = new MappingDefinition { Rules = [new MappingRule { Target = "head.date", Sources = ["bt:BT-2"] }] };

        var result = MappingEngine.Run(SmallSchema(), mapping, input);

        var outcome = result.Report.Fields.Single(f => f.Target == "head.date");
        Assert.Equal(FieldStatus.Invalid, outcome.Status);
        Assert.Null(result.Data["head"]!["date"]);
        Assert.Equal(FieldStatus.NotMapped, result.Report.Fields.Single(f => f.Target == "head.who").Status);
        Assert.Contains(result.Report.MissingRequired, f => f.Target == "head.number");
    }

    [Theory]
    [InlineData("trim", "  a  ", null, null, "a")]
    [InlineData("upper", "de", null, null, "DE")]
    [InlineData("compact", "de 811 907.980", null, null, "DE811907980")]
    [InlineData("replace", "RE-2026", "^RE-", "", "2026")]
    [InlineData("substring", "ABCDEF", "1", "3", "BCD")]
    [InlineData("split", "Max Muster", " ", "0", "Max")]
    [InlineData("number", "1.234,50", ",", null, "1234.5")]
    [InlineData("number", "1,234.50", null, null, "1234.5")]
    [InlineData("round", "2.345", "2", null, "2.35")]
    [InlineData("multiply", "100", "-1", null, "-100")]
    [InlineData("map", "381", "380=Rechnung;381=Gutschrift", null, "Gutschrift")]
    [InlineData("map", "999", "380=Rechnung", "Sonstiges", "Sonstiges")]
    [InlineData("date", "5.3.26", "d.M.yy", null, "2026-03-05")]
    [InlineData("digits", "+49 (40) 123", null, null, "4940123")]
    public void Transformations(string op, string input, string? arg, string? arg2, string expected)
    {
        Assert.Equal(expected, Transforms.Apply(input, new TransformStep { Op = op, Arg = arg, Arg2 = arg2 }, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_bad_regular_expression_is_an_error_not_an_exception()
    {
        Assert.Equal("x", Transforms.Apply("x", new TransformStep { Op = "replace", Arg = "(" }, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Schema_problems_are_found()
    {
        var schema = SmallSchema();
        schema.Groups[0].Fields.Add(new SchemaField { Key = "number", Label = "Doppelt" });
        schema.Groups[0].Fields.Add(new SchemaField { Key = "1bad", Label = "Schlecht" });
        schema.Groups[0].Fields.Add(new SchemaField { Key = "amount", Label = "Betrag", Type = FieldType.Money, DefaultValue = "viel" });

        var problems = schema.Problems();

        Assert.Contains(problems, p => p.Contains("mehrfach", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("1bad", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Standardwert", StringComparison.Ordinal));
        Assert.Empty(Defaults.Schema().Problems());
    }
}
