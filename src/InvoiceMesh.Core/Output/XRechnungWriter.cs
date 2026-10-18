using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Core.Xml;

namespace InvoiceMesh.Core.Output;

/// <summary>
/// Writes an XRechnung 3.0 from the semantic model, in UBL 2.1 (Invoice or CreditNote) or in UN/CEFACT CII D16B, in the order of elements the schemas
/// prescribe. It writes only what the model holds: nothing is filled in. Whether the model is complete enough is decided before
/// (<see cref="Validation.SemanticValidator.XRechnungReadiness"/>); the user's own schema plays no part in it.
/// </summary>
public static class XRechnungWriter
{
    private static readonly XNamespace Cbc = XmlNames.Cbc;
    private static readonly XNamespace Cac = XmlNames.Cac;
    private static readonly XNamespace Rsm = XmlNames.Rsm;
    private static readonly XNamespace Ram = XmlNames.Ram;
    private static readonly XNamespace Udt = XmlNames.Udt;
    private static readonly XNamespace Qdt = XmlNames.Qdt;

    public static byte[] Write(SemanticDocument d, InvoiceSyntax syntax)
    {
        var xml = syntax == InvoiceSyntax.Cii ? Cii(d) : Ubl(d);
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  " };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            xml.Save(writer);
        }

        return stream.ToArray();
    }

    // ------------------------------------------------------------------------------------------------------------ helpers

    private static string Amount(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? Math.Round(d, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture) : value ?? "";

    private static string Number(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d.ToString("0.############", CultureInfo.InvariantCulture) : value ?? "";

    private static string Date102(string? iso) => iso?.Replace("-", "", StringComparison.Ordinal) ?? "";

    /// <summary>An element with text, or nothing when there is no value (XElement leaves a null child out).</summary>
    private static XElement? E(XName name, string? value, params object?[] attributes)
        => string.IsNullOrWhiteSpace(value) ? null : new XElement(name, attributes.Where(a => a is not null), value);

    /// <summary>An element with children, or nothing when none of them has a value.</summary>
    private static XElement? G(XName name, params object?[] content)
    {
        var children = content.Where(c => c is not null).ToList();
        return children.OfType<XElement>().Any() ? new XElement(name, children) : null;
    }

    private static string Scheme(SemanticDocument d, string bt)
        => d.Get(bt + "-scheme") ?? (d.Get(bt)?.Contains('@', StringComparison.Ordinal) == true ? "EM" : "");

    private static string Process(SemanticDocument d) => d.Get("BT-23") ?? SpecificationRegistry.PeppolProcess;

    // ------------------------------------------------------------------------------------------------------------ UBL

    private static XDocument Ubl(SemanticDocument d)
    {
        var credit = d.Get("BT-3") == "381";
        XNamespace root = credit ? XmlNames.UblCreditNote : XmlNames.UblInvoice;
        var currency = d.Get("BT-5") ?? "EUR";
        XAttribute Cur() => new("currencyID", currency);
        XElement? Money(XName name, string bt) => E(name, d.Get(bt) is null ? null : Amount(d.Get(bt)), Cur());

        var lines = d.Items(SemanticCatalog.Lines).Select((l, i) => UblLine(l, i, credit, currency));
        var body = new XElement(root + (credit ? "CreditNote" : "Invoice"),
            new XAttribute(XNamespace.Xmlns + "cac", XmlNames.Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", XmlNames.Cbc),
            E(Cbc + "CustomizationID", SpecificationRegistry.XRechnung30),
            E(Cbc + "ProfileID", Process(d)),
            E(Cbc + "ID", d.Get("BT-1")),
            E(Cbc + "IssueDate", d.Get("BT-2")),
            credit ? null : E(Cbc + "DueDate", d.Get("BT-9")),
            credit ? E(Cbc + "TaxPointDate", d.Get("BT-7")) : null,
            E(Cbc + (credit ? "CreditNoteTypeCode" : "InvoiceTypeCode"), d.Get("BT-3")),
            E(Cbc + "Note", d.Get("BT-22")),
            credit ? null : E(Cbc + "TaxPointDate", d.Get("BT-7")),
            E(Cbc + "DocumentCurrencyCode", currency),
            E(Cbc + "TaxCurrencyCode", d.Get("BT-6")),
            E(Cbc + "AccountingCost", d.Get("BT-19")),
            E(Cbc + "BuyerReference", d.Get("BT-10")),
            G(Cac + "InvoicePeriod", E(Cbc + "StartDate", d.Get("BT-73")), E(Cbc + "EndDate", d.Get("BT-74"))),
            d.Get("BT-13") is null && d.Get("BT-14") is null ? null : new XElement(Cac + "OrderReference", new XElement(Cbc + "ID", d.Get("BT-13") ?? "NA"), E(Cbc + "SalesOrderID", d.Get("BT-14"))),
            G(Cac + "BillingReference", G(Cac + "InvoiceDocumentReference", E(Cbc + "ID", d.Get("BT-25")), E(Cbc + "IssueDate", d.Get("BT-26")))),
            G(Cac + "ContractDocumentReference", E(Cbc + "ID", d.Get("BT-12"))),
            G(Cac + "ProjectReference", E(Cbc + "ID", d.Get("BT-11"))),
            new XElement(Cac + "AccountingSupplierParty", UblParty(d, seller: true)),
            new XElement(Cac + "AccountingCustomerParty", UblParty(d, seller: false)),
            G(Cac + "Delivery", E(Cbc + "ActualDeliveryDate", d.Get("BT-72")), G(Cac + "DeliveryParty", G(Cac + "PartyName", E(Cbc + "Name", d.Get("BT-70"))))),
            d.Get("BT-81") is null ? null : new XElement(Cac + "PaymentMeans",
                E(Cbc + "PaymentMeansCode", d.Get("BT-81"), d.Get("BT-82") is { } n ? new XAttribute("name", n) : null),
                credit ? E(Cbc + "PaymentDueDate", d.Get("BT-9")) : null,
                E(Cbc + "PaymentID", d.Get("BT-83")),
                G(Cac + "PayeeFinancialAccount", E(Cbc + "ID", d.Get("BT-84")), E(Cbc + "Name", d.Get("BT-85")), G(Cac + "FinancialInstitutionBranch", E(Cbc + "ID", d.Get("BT-86")))),
                G(Cac + "PaymentMandate", E(Cbc + "ID", d.Get("BT-89")), G(Cac + "PayerFinancialAccount", E(Cbc + "ID", d.Get("BT-91"))))),
            G(Cac + "PaymentTerms", E(Cbc + "Note", d.Get("BT-20"))),
            d.Items(SemanticCatalog.Allowances).Select(a => UblAllowance(a, false, currency)),
            d.Items(SemanticCatalog.Charges).Select(c => UblAllowance(c, true, currency)),
            new XElement(Cac + "TaxTotal",
                Money(Cbc + "TaxAmount", "BT-110") ?? new XElement(Cbc + "TaxAmount", Cur(), "0.00"),
                d.Items(SemanticCatalog.VatBreakdown).Select(v => new XElement(Cac + "TaxSubtotal",
                    E(Cbc + "TaxableAmount", Amount(v.Get("BT-116")), Cur()),
                    E(Cbc + "TaxAmount", Amount(v.Get("BT-117")), Cur()),
                    UblCategory(Cac + "TaxCategory", v.Get("BT-118"), v.Get("BT-119"), v.Get("BT-121"), v.Get("BT-120"))))),
            new XElement(Cac + "LegalMonetaryTotal",
                Money(Cbc + "LineExtensionAmount", "BT-106"),
                Money(Cbc + "TaxExclusiveAmount", "BT-109"),
                Money(Cbc + "TaxInclusiveAmount", "BT-112"),
                Money(Cbc + "AllowanceTotalAmount", "BT-107"),
                Money(Cbc + "ChargeTotalAmount", "BT-108"),
                Money(Cbc + "PrepaidAmount", "BT-113"),
                Money(Cbc + "PayableRoundingAmount", "BT-114"),
                Money(Cbc + "PayableAmount", "BT-115")),
            lines);
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), body);
    }

    private static XElement UblParty(SemanticDocument d, bool seller)
    {
        string? V(int sellerBt, int buyerBt) => d.Get("BT-" + (seller ? sellerBt : buyerBt));
        var endpoint = seller ? "BT-34" : "BT-49";
        var scheme = Scheme(d, endpoint);
        return new XElement(Cac + "Party",
            E(Cbc + "EndpointID", d.Get(endpoint), scheme.Length > 0 ? new XAttribute("schemeID", scheme) : null),
            G(Cac + "PartyIdentification", E(Cbc + "ID", V(29, 46))),
            G(Cac + "PartyName", E(Cbc + "Name", V(28, 45))),
            new XElement(Cac + "PostalAddress",
                E(Cbc + "StreetName", V(35, 50)),
                E(Cbc + "AdditionalStreetName", V(36, 51)),
                E(Cbc + "CityName", V(37, 52)),
                E(Cbc + "PostalZone", V(38, 53)),
                E(Cbc + "CountrySubentity", V(39, 54)),
                G(Cac + "Country", E(Cbc + "IdentificationCode", V(40, 55)))),
            G(Cac + "PartyTaxScheme", E(Cbc + "CompanyID", V(31, 48)), V(31, 48) is null ? null : new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT"))),
            seller ? G(Cac + "PartyTaxScheme", E(Cbc + "CompanyID", d.Get("BT-32")), d.Get("BT-32") is null ? null : new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "FC"))) : null,
            new XElement(Cac + "PartyLegalEntity",
                E(Cbc + "RegistrationName", V(27, 44)),
                E(Cbc + "CompanyID", V(30, 47)),
                seller ? E(Cbc + "CompanyLegalForm", d.Get("BT-33")) : null),
            G(Cac + "Contact", E(Cbc + "Name", V(41, 56)), E(Cbc + "Telephone", V(42, 57)), E(Cbc + "ElectronicMail", V(43, 58))));
    }

    private static XElement UblAllowance(SemanticItem a, bool charge, string currency)
    {
        string? V(int allowanceBt, int chargeBt) => a.Get("BT-" + (charge ? chargeBt : allowanceBt));
        return new XElement(Cac + "AllowanceCharge",
            new XElement(Cbc + "ChargeIndicator", charge ? "true" : "false"),
            E(Cbc + "AllowanceChargeReasonCode", V(98, 105)),
            E(Cbc + "AllowanceChargeReason", V(97, 104)),
            E(Cbc + "MultiplierFactorNumeric", V(94, 101) is { } p ? Number(p) : null),
            new XElement(Cbc + "Amount", new XAttribute("currencyID", currency), Amount(V(92, 99))),
            E(Cbc + "BaseAmount", V(93, 100) is { } b ? Amount(b) : null, new XAttribute("currencyID", currency)),
            UblCategory(Cac + "TaxCategory", V(95, 102), V(96, 103), null, null));
    }

    private static XElement UblCategory(XName name, string? category, string? rate, string? exemptionCode, string? exemptionReason)
        => new(name,
            E(Cbc + "ID", category),
            E(Cbc + "Percent", rate is null ? null : Number(rate)),
            E(Cbc + "TaxExemptionReasonCode", exemptionCode),
            E(Cbc + "TaxExemptionReason", exemptionReason),
            new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")));

    private static XElement UblLine(SemanticItem l, int index, bool credit, string currency)
    {
        var unit = l.Get("BT-130");
        return new XElement(Cac + (credit ? "CreditNoteLine" : "InvoiceLine"),
            E(Cbc + "ID", l.Get("BT-126") ?? (index + 1).ToString(CultureInfo.InvariantCulture)),
            E(Cbc + "Note", l.Get("BT-127")),
            E(Cbc + (credit ? "CreditedQuantity" : "InvoicedQuantity"), Number(l.Get("BT-129")), unit is null ? null : new XAttribute("unitCode", unit)),
            E(Cbc + "LineExtensionAmount", Amount(l.Get("BT-131")), new XAttribute("currencyID", currency)),
            E(Cbc + "AccountingCost", l.Get("BT-133")),
            G(Cac + "InvoicePeriod", E(Cbc + "StartDate", l.Get("BT-134")), E(Cbc + "EndDate", l.Get("BT-135"))),
            G(Cac + "OrderLineReference", E(Cbc + "LineID", l.Get("BT-132"))),
            new XElement(Cac + "Item",
                E(Cbc + "Description", l.Get("BT-154")),
                E(Cbc + "Name", l.Get("BT-153")),
                G(Cac + "BuyersItemIdentification", E(Cbc + "ID", l.Get("BT-156"))),
                G(Cac + "SellersItemIdentification", E(Cbc + "ID", l.Get("BT-155"))),
                G(Cac + "StandardItemIdentification", E(Cbc + "ID", l.Get("BT-157"), l.Get("BT-157-scheme") is { } us ? new XAttribute("schemeID", us) : null)),
                G(Cac + "OriginCountry", E(Cbc + "IdentificationCode", l.Get("BT-159"))),
                UblCategory(Cac + "ClassifiedTaxCategory", l.Get("BT-151"), l.Get("BT-152"), null, null)),
            new XElement(Cac + "Price",
                E(Cbc + "PriceAmount", Number(l.Get("BT-146")), new XAttribute("currencyID", currency)),
                E(Cbc + "BaseQuantity", l.Get("BT-149") is { } bq ? Number(bq) : null, unit is null ? null : new XAttribute("unitCode", unit)),
                l.Get("BT-147") is null ? null : new XElement(Cac + "AllowanceCharge",
                    new XElement(Cbc + "ChargeIndicator", "false"),
                    new XElement(Cbc + "Amount", new XAttribute("currencyID", currency), Number(l.Get("BT-147"))),
                    E(Cbc + "BaseAmount", l.Get("BT-148") is { } gp ? Number(gp) : null, new XAttribute("currencyID", currency)))));
    }

    // ------------------------------------------------------------------------------------------------------------ CII

    private static XDocument Cii(SemanticDocument d)
    {
        var currency = d.Get("BT-5") ?? "EUR";
        XElement? Money(XName name, string bt, bool withCurrency = false)
            => E(name, d.Get(bt) is null ? null : Amount(d.Get(bt)), withCurrency ? new XAttribute("currencyID", currency) : null);
        XElement? DateTime(XName name, string? iso) => iso is null ? null : new XElement(name, new XElement(Udt + "DateTimeString", new XAttribute("format", "102"), Date102(iso)));

        var body = new XElement(Rsm + "CrossIndustryInvoice",
            new XAttribute(XNamespace.Xmlns + "rsm", XmlNames.Rsm),
            new XAttribute(XNamespace.Xmlns + "ram", XmlNames.Ram),
            new XAttribute(XNamespace.Xmlns + "qdt", XmlNames.Qdt),
            new XAttribute(XNamespace.Xmlns + "udt", XmlNames.Udt),
            new XElement(Rsm + "ExchangedDocumentContext",
                new XElement(Ram + "BusinessProcessSpecifiedDocumentContextParameter", new XElement(Ram + "ID", Process(d))),
                new XElement(Ram + "GuidelineSpecifiedDocumentContextParameter", new XElement(Ram + "ID", SpecificationRegistry.XRechnung30))),
            new XElement(Rsm + "ExchangedDocument",
                E(Ram + "ID", d.Get("BT-1")),
                E(Ram + "TypeCode", d.Get("BT-3")),
                DateTime(Ram + "IssueDateTime", d.Get("BT-2")),
                (d.Get("BT-22") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => new XElement(Ram + "IncludedNote", new XElement(Ram + "Content", n)))),
            new XElement(Rsm + "SupplyChainTradeTransaction",
                d.Items(SemanticCatalog.Lines).Select((l, i) => CiiLine(l, i)),
                new XElement(Ram + "ApplicableHeaderTradeAgreement",
                    E(Ram + "BuyerReference", d.Get("BT-10")),
                    CiiParty(d, seller: true),
                    CiiParty(d, seller: false),
                    G(Ram + "SellerOrderReferencedDocument", E(Ram + "IssuerAssignedID", d.Get("BT-14"))),
                    G(Ram + "BuyerOrderReferencedDocument", E(Ram + "IssuerAssignedID", d.Get("BT-13"))),
                    G(Ram + "ContractReferencedDocument", E(Ram + "IssuerAssignedID", d.Get("BT-12"))),
                    d.Get("BT-11") is null ? null : new XElement(Ram + "SpecifiedProcuringProject", new XElement(Ram + "ID", d.Get("BT-11")), new XElement(Ram + "Name", "Project reference"))),
                new XElement(Ram + "ApplicableHeaderTradeDelivery",
                    G(Ram + "ShipToTradeParty", E(Ram + "Name", d.Get("BT-70"))),
                    d.Get("BT-72") is null ? null : new XElement(Ram + "ActualDeliverySupplyChainEvent", DateTime(Ram + "OccurrenceDateTime", d.Get("BT-72")))),
                new XElement(Ram + "ApplicableHeaderTradeSettlement",
                    E(Ram + "CreditorReferenceID", d.Get("BT-90")),
                    E(Ram + "PaymentReference", d.Get("BT-83")),
                    E(Ram + "TaxCurrencyCode", d.Get("BT-6")),
                    E(Ram + "InvoiceCurrencyCode", currency),
                    d.Get("BT-81") is null ? null : new XElement(Ram + "SpecifiedTradeSettlementPaymentMeans",
                        E(Ram + "TypeCode", d.Get("BT-81")),
                        E(Ram + "Information", d.Get("BT-82")),
                        G(Ram + "PayerPartyDebtorFinancialAccount", E(Ram + "IBANID", d.Get("BT-91"))),
                        G(Ram + "PayeePartyCreditorFinancialAccount", E(Ram + "IBANID", d.Get("BT-84")), E(Ram + "AccountName", d.Get("BT-85"))),
                        G(Ram + "PayeeSpecifiedCreditorFinancialInstitution", E(Ram + "BICID", d.Get("BT-86")))),
                    d.Items(SemanticCatalog.VatBreakdown).Select(v => new XElement(Ram + "ApplicableTradeTax",
                        E(Ram + "CalculatedAmount", Amount(v.Get("BT-117"))),
                        new XElement(Ram + "TypeCode", "VAT"),
                        E(Ram + "ExemptionReason", v.Get("BT-120")),
                        E(Ram + "BasisAmount", Amount(v.Get("BT-116"))),
                        E(Ram + "CategoryCode", v.Get("BT-118")),
                        E(Ram + "ExemptionReasonCode", v.Get("BT-121")),
                        E(Ram + "RateApplicablePercent", v.Get("BT-119") is { } r ? Number(r) : null))),
                    G(Ram + "BillingSpecifiedPeriod", DateTime(Ram + "StartDateTime", d.Get("BT-73")), DateTime(Ram + "EndDateTime", d.Get("BT-74"))),
                    d.Items(SemanticCatalog.Allowances).Select(a => CiiAllowance(a, false)),
                    d.Items(SemanticCatalog.Charges).Select(c => CiiAllowance(c, true)),
                    G(Ram + "SpecifiedTradePaymentTerms", E(Ram + "Description", d.Get("BT-20")), DateTime(Ram + "DueDateDateTime", d.Get("BT-9")), E(Ram + "DirectDebitMandateID", d.Get("BT-89"))),
                    new XElement(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation",
                        Money(Ram + "LineTotalAmount", "BT-106"),
                        Money(Ram + "ChargeTotalAmount", "BT-108"),
                        Money(Ram + "AllowanceTotalAmount", "BT-107"),
                        Money(Ram + "TaxBasisTotalAmount", "BT-109"),
                        Money(Ram + "TaxTotalAmount", "BT-110", withCurrency: true),
                        Money(Ram + "RoundingAmount", "BT-114"),
                        Money(Ram + "GrandTotalAmount", "BT-112"),
                        Money(Ram + "TotalPrepaidAmount", "BT-113"),
                        Money(Ram + "DuePayableAmount", "BT-115")),
                    d.Get("BT-25") is null ? null : new XElement(Ram + "InvoiceReferencedDocument",
                        new XElement(Ram + "IssuerAssignedID", d.Get("BT-25")),
                        d.Get("BT-26") is null ? null : new XElement(Ram + "FormattedIssueDateTime", new XElement(Qdt + "DateTimeString", new XAttribute("format", "102"), Date102(d.Get("BT-26"))))),
                    G(Ram + "ReceivableSpecifiedTradeAccountingAccount", E(Ram + "ID", d.Get("BT-19"))))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), body);
    }

    private static XElement CiiParty(SemanticDocument d, bool seller)
    {
        string? V(int sellerBt, int buyerBt) => d.Get("BT-" + (seller ? sellerBt : buyerBt));
        var endpoint = seller ? "BT-34" : "BT-49";
        var scheme = Scheme(d, endpoint);
        return new XElement(Ram + (seller ? "SellerTradeParty" : "BuyerTradeParty"),
            E(Ram + "ID", V(29, 46)),
            E(Ram + "Name", V(27, 44)),
            seller ? E(Ram + "Description", d.Get("BT-33")) : null,
            G(Ram + "SpecifiedLegalOrganization", E(Ram + "ID", V(30, 47)), E(Ram + "TradingBusinessName", V(28, 45))),
            G(Ram + "DefinedTradeContact",
                E(Ram + "PersonName", V(41, 56)),
                G(Ram + "TelephoneUniversalCommunication", E(Ram + "CompleteNumber", V(42, 57))),
                G(Ram + "EmailURIUniversalCommunication", E(Ram + "URIID", V(43, 58)))),
            new XElement(Ram + "PostalTradeAddress",
                E(Ram + "PostcodeCode", V(38, 53)),
                E(Ram + "LineOne", V(35, 50)),
                E(Ram + "LineTwo", V(36, 51)),
                E(Ram + "CityName", V(37, 52)),
                E(Ram + "CountryID", V(40, 55)),
                E(Ram + "CountrySubDivisionName", V(39, 54))),
            G(Ram + "URIUniversalCommunication", E(Ram + "URIID", d.Get(endpoint), scheme.Length > 0 ? new XAttribute("schemeID", scheme) : null)),
            G(Ram + "SpecifiedTaxRegistration", E(Ram + "ID", V(31, 48), new XAttribute("schemeID", "VA"))),
            seller ? G(Ram + "SpecifiedTaxRegistration", E(Ram + "ID", d.Get("BT-32"), new XAttribute("schemeID", "FC"))) : null);
    }

    private static XElement CiiAllowance(SemanticItem a, bool charge)
    {
        string? V(int allowanceBt, int chargeBt) => a.Get("BT-" + (charge ? chargeBt : allowanceBt));
        return new XElement(Ram + "SpecifiedTradeAllowanceCharge",
            new XElement(Ram + "ChargeIndicator", new XElement(Udt + "Indicator", charge ? "true" : "false")),
            E(Ram + "CalculationPercent", V(94, 101) is { } p ? Number(p) : null),
            E(Ram + "BasisAmount", V(93, 100) is { } b ? Amount(b) : null),
            E(Ram + "ActualAmount", Amount(V(92, 99))),
            E(Ram + "ReasonCode", V(98, 105)),
            E(Ram + "Reason", V(97, 104)),
            new XElement(Ram + "CategoryTradeTax",
                new XElement(Ram + "TypeCode", "VAT"),
                E(Ram + "CategoryCode", V(95, 102)),
                E(Ram + "RateApplicablePercent", V(96, 103) is { } r ? Number(r) : null)));
    }

    private static XElement CiiLine(SemanticItem l, int index)
    {
        var unit = l.Get("BT-130");
        return new XElement(Ram + "IncludedSupplyChainTradeLineItem",
            new XElement(Ram + "AssociatedDocumentLineDocument",
                E(Ram + "LineID", l.Get("BT-126") ?? (index + 1).ToString(CultureInfo.InvariantCulture)),
                G(Ram + "IncludedNote", E(Ram + "Content", l.Get("BT-127")))),
            new XElement(Ram + "SpecifiedTradeProduct",
                E(Ram + "GlobalID", l.Get("BT-157"), l.Get("BT-157-scheme") is { } cs ? new XAttribute("schemeID", cs) : null),
                E(Ram + "SellerAssignedID", l.Get("BT-155")),
                E(Ram + "BuyerAssignedID", l.Get("BT-156")),
                E(Ram + "Name", l.Get("BT-153")),
                E(Ram + "Description", l.Get("BT-154")),
                G(Ram + "OriginTradeCountry", E(Ram + "ID", l.Get("BT-159")))),
            new XElement(Ram + "SpecifiedLineTradeAgreement",
                G(Ram + "BuyerOrderReferencedDocument", E(Ram + "LineID", l.Get("BT-132"))),
                l.Get("BT-148") is null ? null : new XElement(Ram + "GrossPriceProductTradePrice",
                    new XElement(Ram + "ChargeAmount", Number(l.Get("BT-148"))),
                    l.Get("BT-147") is null ? null : new XElement(Ram + "AppliedTradeAllowanceCharge",
                        new XElement(Ram + "ChargeIndicator", new XElement(Udt + "Indicator", "false")),
                        new XElement(Ram + "ActualAmount", Number(l.Get("BT-147"))))),
                new XElement(Ram + "NetPriceProductTradePrice",
                    E(Ram + "ChargeAmount", Number(l.Get("BT-146"))),
                    E(Ram + "BasisQuantity", l.Get("BT-149") is { } bq ? Number(bq) : null, unit is null ? null : new XAttribute("unitCode", unit)))),
            new XElement(Ram + "SpecifiedLineTradeDelivery",
                E(Ram + "BilledQuantity", Number(l.Get("BT-129")), unit is null ? null : new XAttribute("unitCode", unit))),
            new XElement(Ram + "SpecifiedLineTradeSettlement",
                new XElement(Ram + "ApplicableTradeTax",
                    new XElement(Ram + "TypeCode", "VAT"),
                    E(Ram + "CategoryCode", l.Get("BT-151")),
                    E(Ram + "RateApplicablePercent", l.Get("BT-152") is { } r ? Number(r) : null)),
                G(Ram + "BillingSpecifiedPeriod",
                    l.Get("BT-134") is null ? null : new XElement(Ram + "StartDateTime", new XElement(Udt + "DateTimeString", new XAttribute("format", "102"), Date102(l.Get("BT-134")))),
                    l.Get("BT-135") is null ? null : new XElement(Ram + "EndDateTime", new XElement(Udt + "DateTimeString", new XAttribute("format", "102"), Date102(l.Get("BT-135"))))),
                new XElement(Ram + "SpecifiedTradeSettlementLineMonetarySummation", E(Ram + "LineTotalAmount", Amount(l.Get("BT-131")))),
                G(Ram + "ReceivableSpecifiedTradeAccountingAccount", E(Ram + "ID", l.Get("BT-133")))));
    }
}
