namespace InvoiceMesh.Core.Semantics;

/// <summary>What kind of value a business term holds; it decides how a reader normalises it and how a rule reads it.</summary>
public enum SemanticType
{
    Text,
    Code,
    Identifier,
    Date,
    Amount,
    UnitPrice,
    Quantity,
    Percent,
}

/// <summary>
/// A business term of EN 16931 and where it is in each syntax. <see cref="Ubl"/> and <see cref="Cii"/> are paths of <see cref="Xml.XPathLite"/>, relative to
/// the root of the document, or to the element of the item when the term belongs to a <see cref="Group"/>.
/// </summary>
/// <param name="Section">The part of the invoice it belongs to, for the pages ("Rechnung", "Verkäufer", "Käufer" ...).</param>
public sealed record SemanticField(string Id, string Label, SemanticType Type, string Section, string? Group, string? Ubl, string? Cii);

/// <summary>A repeating business group: the path of its elements in each syntax.</summary>
public sealed record SemanticGroup(string Id, string Label, string? Ubl, string? Cii);

/// <summary>
/// The business terms InvoiceMesh reads, with their bindings to UBL 2.1 (Invoice and CreditNote) and to UN/CEFACT CII D16B, as EN 16931-3-2 and -3-3 define
/// them. This table is the one place that knows the syntaxes: the readers, the source picker of the mapping and the pages read it. A term that is missing
/// here is still available as a raw field of its XML path.
/// </summary>
public static class SemanticCatalog
{
    public const string Lines = "BG-25";
    public const string VatBreakdown = "BG-23";
    public const string Allowances = "BG-20";
    public const string Charges = "BG-21";

    private const string CiiAgreement = "rsm:SupplyChainTradeTransaction/ram:ApplicableHeaderTradeAgreement";
    private const string CiiDelivery = "rsm:SupplyChainTradeTransaction/ram:ApplicableHeaderTradeDelivery";
    private const string CiiSettlement = "rsm:SupplyChainTradeTransaction/ram:ApplicableHeaderTradeSettlement";
    private const string CiiSeller = CiiAgreement + "/ram:SellerTradeParty";
    private const string CiiBuyer = CiiAgreement + "/ram:BuyerTradeParty";
    private const string CiiTotals = CiiSettlement + "/ram:SpecifiedTradeSettlementHeaderMonetarySummation";
    private const string CiiMeans = CiiSettlement + "/ram:SpecifiedTradeSettlementPaymentMeans";
    private const string CiiTerms = CiiSettlement + "/ram:SpecifiedTradePaymentTerms";
    private const string UblSeller = "cac:AccountingSupplierParty/cac:Party";
    private const string UblBuyer = "cac:AccountingCustomerParty/cac:Party";
    private const string UblMeans = "cac:PaymentMeans";
    private const string UblTotals = "cac:LegalMonetaryTotal";

    public static readonly IReadOnlyList<SemanticGroup> Groups =
    [
        new(Lines, "Rechnungspositionen", "cac:InvoiceLine|cac:CreditNoteLine", "rsm:SupplyChainTradeTransaction/ram:IncludedSupplyChainTradeLineItem"),
        new(VatBreakdown, "Umsatzsteueraufschlüsselung", "cac:TaxTotal/cac:TaxSubtotal", CiiSettlement + "/ram:ApplicableTradeTax"),
        new(Allowances, "Nachlässe auf Dokumentebene", "cac:AllowanceCharge[cbc:ChargeIndicator='false']", CiiSettlement + "/ram:SpecifiedTradeAllowanceCharge[ram:ChargeIndicator/udt:Indicator='false']"),
        new(Charges, "Zuschläge auf Dokumentebene", "cac:AllowanceCharge[cbc:ChargeIndicator='true']", CiiSettlement + "/ram:SpecifiedTradeAllowanceCharge[ram:ChargeIndicator/udt:Indicator='true']"),
    ];

    public static readonly IReadOnlyList<SemanticField> Fields =
    [
        // ---------------------------------------------------------------------------------------------------- the invoice
        new("BT-1", "Rechnungsnummer", SemanticType.Identifier, "Rechnung", null, "cbc:ID", "rsm:ExchangedDocument/ram:ID"),
        new("BT-2", "Rechnungsdatum", SemanticType.Date, "Rechnung", null, "cbc:IssueDate", "rsm:ExchangedDocument/ram:IssueDateTime/udt:DateTimeString"),
        new("BT-3", "Rechnungsart (Code)", SemanticType.Code, "Rechnung", null, "cbc:InvoiceTypeCode|cbc:CreditNoteTypeCode", "rsm:ExchangedDocument/ram:TypeCode"),
        new("BT-5", "Währung", SemanticType.Code, "Rechnung", null, "cbc:DocumentCurrencyCode", CiiSettlement + "/ram:InvoiceCurrencyCode"),
        new("BT-6", "Währung der Umsatzsteuer", SemanticType.Code, "Rechnung", null, "cbc:TaxCurrencyCode", CiiSettlement + "/ram:TaxCurrencyCode"),
        new("BT-7", "Steuerdatum", SemanticType.Date, "Rechnung", null, "cbc:TaxPointDate", CiiSettlement + "/ram:ApplicableTradeTax/ram:TaxPointDate/udt:DateString"),
        new("BT-9", "Fälligkeitsdatum", SemanticType.Date, "Zahlung", null, "cbc:DueDate|cac:PaymentMeans/cbc:PaymentDueDate", CiiTerms + "/ram:DueDateDateTime/udt:DateTimeString"),
        new("BT-10", "Käuferreferenz (Leitweg-ID)", SemanticType.Identifier, "Rechnung", null, "cbc:BuyerReference", CiiAgreement + "/ram:BuyerReference"),
        new("BT-11", "Projektreferenz", SemanticType.Identifier, "Referenzen", null, "cac:ProjectReference/cbc:ID", CiiAgreement + "/ram:SpecifiedProcuringProject/ram:ID"),
        new("BT-12", "Vertragsreferenz", SemanticType.Identifier, "Referenzen", null, "cac:ContractDocumentReference/cbc:ID", CiiAgreement + "/ram:ContractReferencedDocument/ram:IssuerAssignedID"),
        new("BT-13", "Bestellnummer", SemanticType.Identifier, "Referenzen", null, "cac:OrderReference/cbc:ID", CiiAgreement + "/ram:BuyerOrderReferencedDocument/ram:IssuerAssignedID"),
        new("BT-14", "Auftragsnummer des Verkäufers", SemanticType.Identifier, "Referenzen", null, "cac:OrderReference/cbc:SalesOrderID", CiiAgreement + "/ram:SellerOrderReferencedDocument/ram:IssuerAssignedID"),
        new("BT-19", "Buchungsreferenz des Käufers", SemanticType.Identifier, "Referenzen", null, "cbc:AccountingCost", CiiSettlement + "/ram:ReceivableSpecifiedTradeAccountingAccount/ram:ID"),
        new("BT-20", "Zahlungsbedingungen", SemanticType.Text, "Zahlung", null, "cac:PaymentTerms/cbc:Note", CiiTerms + "/ram:Description"),
        new("BT-22", "Bemerkung zur Rechnung", SemanticType.Text, "Rechnung", null, "cbc:Note", "rsm:ExchangedDocument/ram:IncludedNote/ram:Content"),
        new("BT-23", "Geschäftsprozess", SemanticType.Identifier, "Rechnung", null, "cbc:ProfileID", "rsm:ExchangedDocumentContext/ram:BusinessProcessSpecifiedDocumentContextParameter/ram:ID"),
        new("BT-24", "Spezifikationskennung", SemanticType.Identifier, "Rechnung", null, "cbc:CustomizationID", "rsm:ExchangedDocumentContext/ram:GuidelineSpecifiedDocumentContextParameter/ram:ID"),
        new("BT-25", "Vorausgegangene Rechnung", SemanticType.Identifier, "Referenzen", null, "cac:BillingReference/cac:InvoiceDocumentReference/cbc:ID", CiiSettlement + "/ram:InvoiceReferencedDocument/ram:IssuerAssignedID"),
        new("BT-26", "Datum der vorausgegangenen Rechnung", SemanticType.Date, "Referenzen", null, "cac:BillingReference/cac:InvoiceDocumentReference/cbc:IssueDate", CiiSettlement + "/ram:InvoiceReferencedDocument/ram:FormattedIssueDateTime/qdt:DateTimeString"),

        // ---------------------------------------------------------------------------------------------------- the seller
        new("BT-27", "Verkäufer: Name", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:PartyLegalEntity/cbc:RegistrationName", CiiSeller + "/ram:Name"),
        new("BT-28", "Verkäufer: Handelsname", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:PartyName/cbc:Name", CiiSeller + "/ram:SpecifiedLegalOrganization/ram:TradingBusinessName"),
        new("BT-29", "Verkäufer: Kennung", SemanticType.Identifier, "Verkäufer", null, UblSeller + "/cac:PartyIdentification/cbc:ID", CiiSeller + "/ram:ID|" + CiiSeller + "/ram:GlobalID"),
        new("BT-30", "Verkäufer: Registernummer", SemanticType.Identifier, "Verkäufer", null, UblSeller + "/cac:PartyLegalEntity/cbc:CompanyID", CiiSeller + "/ram:SpecifiedLegalOrganization/ram:ID"),
        new("BT-31", "Verkäufer: USt-IdNr.", SemanticType.Identifier, "Verkäufer", null, UblSeller + "/cac:PartyTaxScheme[cac:TaxScheme/cbc:ID='VAT']/cbc:CompanyID", CiiSeller + "/ram:SpecifiedTaxRegistration/ram:ID[@schemeID='VA']"),
        new("BT-32", "Verkäufer: Steuernummer", SemanticType.Identifier, "Verkäufer", null, UblSeller + "/cac:PartyTaxScheme[cac:TaxScheme/cbc:ID!='VAT']/cbc:CompanyID", CiiSeller + "/ram:SpecifiedTaxRegistration/ram:ID[@schemeID='FC']"),
        new("BT-33", "Verkäufer: Rechtliche Informationen", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:PartyLegalEntity/cbc:CompanyLegalForm", CiiSeller + "/ram:Description"),
        new("BT-34", "Verkäufer: Elektronische Adresse", SemanticType.Identifier, "Verkäufer", null, UblSeller + "/cbc:EndpointID", CiiSeller + "/ram:URIUniversalCommunication/ram:URIID"),
        new("BT-34-scheme", "Verkäufer: Schema der elektronischen Adresse", SemanticType.Code, "Verkäufer", null, UblSeller + "/cbc:EndpointID/@schemeID", CiiSeller + "/ram:URIUniversalCommunication/ram:URIID/@schemeID"),
        new("BT-35", "Verkäufer: Straße", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:PostalAddress/cbc:StreetName", CiiSeller + "/ram:PostalTradeAddress/ram:LineOne"),
        new("BT-36", "Verkäufer: Adresszusatz", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:PostalAddress/cbc:AdditionalStreetName", CiiSeller + "/ram:PostalTradeAddress/ram:LineTwo"),
        new("BT-37", "Verkäufer: Ort", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:PostalAddress/cbc:CityName", CiiSeller + "/ram:PostalTradeAddress/ram:CityName"),
        new("BT-38", "Verkäufer: Postleitzahl", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:PostalAddress/cbc:PostalZone", CiiSeller + "/ram:PostalTradeAddress/ram:PostcodeCode"),
        new("BT-39", "Verkäufer: Region", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:PostalAddress/cbc:CountrySubentity", CiiSeller + "/ram:PostalTradeAddress/ram:CountrySubDivisionName"),
        new("BT-40", "Verkäufer: Land (Code)", SemanticType.Code, "Verkäufer", null, UblSeller + "/cac:PostalAddress/cac:Country/cbc:IdentificationCode", CiiSeller + "/ram:PostalTradeAddress/ram:CountryID"),
        new("BT-41", "Verkäufer: Kontaktperson", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:Contact/cbc:Name", CiiSeller + "/ram:DefinedTradeContact/ram:PersonName"),
        new("BT-42", "Verkäufer: Telefon", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:Contact/cbc:Telephone", CiiSeller + "/ram:DefinedTradeContact/ram:TelephoneUniversalCommunication/ram:CompleteNumber"),
        new("BT-43", "Verkäufer: E-Mail", SemanticType.Text, "Verkäufer", null, UblSeller + "/cac:Contact/cbc:ElectronicMail", CiiSeller + "/ram:DefinedTradeContact/ram:EmailURIUniversalCommunication/ram:URIID"),

        // ---------------------------------------------------------------------------------------------------- the buyer
        new("BT-44", "Käufer: Name", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:PartyLegalEntity/cbc:RegistrationName", CiiBuyer + "/ram:Name"),
        new("BT-45", "Käufer: Handelsname", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:PartyName/cbc:Name", CiiBuyer + "/ram:SpecifiedLegalOrganization/ram:TradingBusinessName"),
        new("BT-46", "Käufer: Kennung", SemanticType.Identifier, "Käufer", null, UblBuyer + "/cac:PartyIdentification/cbc:ID", CiiBuyer + "/ram:ID|" + CiiBuyer + "/ram:GlobalID"),
        new("BT-47", "Käufer: Registernummer", SemanticType.Identifier, "Käufer", null, UblBuyer + "/cac:PartyLegalEntity/cbc:CompanyID", CiiBuyer + "/ram:SpecifiedLegalOrganization/ram:ID"),
        new("BT-48", "Käufer: USt-IdNr.", SemanticType.Identifier, "Käufer", null, UblBuyer + "/cac:PartyTaxScheme/cbc:CompanyID", CiiBuyer + "/ram:SpecifiedTaxRegistration/ram:ID[@schemeID='VA']"),
        new("BT-49", "Käufer: Elektronische Adresse", SemanticType.Identifier, "Käufer", null, UblBuyer + "/cbc:EndpointID", CiiBuyer + "/ram:URIUniversalCommunication/ram:URIID"),
        new("BT-49-scheme", "Käufer: Schema der elektronischen Adresse", SemanticType.Code, "Käufer", null, UblBuyer + "/cbc:EndpointID/@schemeID", CiiBuyer + "/ram:URIUniversalCommunication/ram:URIID/@schemeID"),
        new("BT-50", "Käufer: Straße", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:PostalAddress/cbc:StreetName", CiiBuyer + "/ram:PostalTradeAddress/ram:LineOne"),
        new("BT-51", "Käufer: Adresszusatz", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:PostalAddress/cbc:AdditionalStreetName", CiiBuyer + "/ram:PostalTradeAddress/ram:LineTwo"),
        new("BT-52", "Käufer: Ort", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:PostalAddress/cbc:CityName", CiiBuyer + "/ram:PostalTradeAddress/ram:CityName"),
        new("BT-53", "Käufer: Postleitzahl", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:PostalAddress/cbc:PostalZone", CiiBuyer + "/ram:PostalTradeAddress/ram:PostcodeCode"),
        new("BT-54", "Käufer: Region", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:PostalAddress/cbc:CountrySubentity", CiiBuyer + "/ram:PostalTradeAddress/ram:CountrySubDivisionName"),
        new("BT-55", "Käufer: Land (Code)", SemanticType.Code, "Käufer", null, UblBuyer + "/cac:PostalAddress/cac:Country/cbc:IdentificationCode", CiiBuyer + "/ram:PostalTradeAddress/ram:CountryID"),
        new("BT-56", "Käufer: Kontaktperson", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:Contact/cbc:Name", CiiBuyer + "/ram:DefinedTradeContact/ram:PersonName"),
        new("BT-57", "Käufer: Telefon", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:Contact/cbc:Telephone", CiiBuyer + "/ram:DefinedTradeContact/ram:TelephoneUniversalCommunication/ram:CompleteNumber"),
        new("BT-58", "Käufer: E-Mail", SemanticType.Text, "Käufer", null, UblBuyer + "/cac:Contact/cbc:ElectronicMail", CiiBuyer + "/ram:DefinedTradeContact/ram:EmailURIUniversalCommunication/ram:URIID"),

        // ---------------------------------------------------------------------------------------------------- delivery
        new("BT-70", "Lieferung an: Name", SemanticType.Text, "Lieferung", null, "cac:Delivery/cac:DeliveryParty/cac:PartyName/cbc:Name", CiiDelivery + "/ram:ShipToTradeParty/ram:Name"),
        new("BT-72", "Lieferdatum", SemanticType.Date, "Lieferung", null, "cac:Delivery/cbc:ActualDeliveryDate", CiiDelivery + "/ram:ActualDeliverySupplyChainEvent/ram:OccurrenceDateTime/udt:DateTimeString"),
        new("BT-73", "Leistungszeitraum von", SemanticType.Date, "Lieferung", null, "cac:InvoicePeriod/cbc:StartDate", CiiSettlement + "/ram:BillingSpecifiedPeriod/ram:StartDateTime/udt:DateTimeString"),
        new("BT-74", "Leistungszeitraum bis", SemanticType.Date, "Lieferung", null, "cac:InvoicePeriod/cbc:EndDate", CiiSettlement + "/ram:BillingSpecifiedPeriod/ram:EndDateTime/udt:DateTimeString"),

        // ---------------------------------------------------------------------------------------------------- payment
        new("BT-81", "Zahlungsart (Code)", SemanticType.Code, "Zahlung", null, UblMeans + "/cbc:PaymentMeansCode", CiiMeans + "/ram:TypeCode"),
        new("BT-82", "Zahlungsart (Text)", SemanticType.Text, "Zahlung", null, UblMeans + "/cbc:PaymentMeansCode/@name", CiiMeans + "/ram:Information"),
        new("BT-83", "Verwendungszweck", SemanticType.Text, "Zahlung", null, UblMeans + "/cbc:PaymentID", CiiSettlement + "/ram:PaymentReference"),
        new("BT-84", "IBAN / Kontokennung", SemanticType.Identifier, "Zahlung", null, UblMeans + "/cac:PayeeFinancialAccount/cbc:ID", CiiMeans + "/ram:PayeePartyCreditorFinancialAccount/ram:IBANID|" + CiiMeans + "/ram:PayeePartyCreditorFinancialAccount/ram:ProprietaryID"),
        new("BT-85", "Kontoinhaber", SemanticType.Text, "Zahlung", null, UblMeans + "/cac:PayeeFinancialAccount/cbc:Name", CiiMeans + "/ram:PayeePartyCreditorFinancialAccount/ram:AccountName"),
        new("BT-86", "BIC", SemanticType.Identifier, "Zahlung", null, UblMeans + "/cac:PayeeFinancialAccount/cac:FinancialInstitutionBranch/cbc:ID", CiiMeans + "/ram:PayeeSpecifiedCreditorFinancialInstitution/ram:BICID"),
        new("BT-89", "Mandatsreferenz", SemanticType.Identifier, "Zahlung", null, UblMeans + "/cac:PaymentMandate/cbc:ID", CiiTerms + "/ram:DirectDebitMandateID"),
        new("BT-90", "Gläubiger-ID", SemanticType.Identifier, "Zahlung", null, UblSeller + "/cac:PartyIdentification/cbc:ID[@schemeID='SEPA']", CiiSettlement + "/ram:CreditorReferenceID"),
        new("BT-91", "Belastetes Konto (IBAN)", SemanticType.Identifier, "Zahlung", null, UblMeans + "/cac:PaymentMandate/cac:PayerFinancialAccount/cbc:ID", CiiMeans + "/ram:PayerPartyDebtorFinancialAccount/ram:IBANID"),

        // ---------------------------------------------------------------------------------------------------- totals
        new("BT-106", "Summe der Positionen (netto)", SemanticType.Amount, "Summen", null, UblTotals + "/cbc:LineExtensionAmount", CiiTotals + "/ram:LineTotalAmount"),
        new("BT-107", "Summe der Nachlässe", SemanticType.Amount, "Summen", null, UblTotals + "/cbc:AllowanceTotalAmount", CiiTotals + "/ram:AllowanceTotalAmount"),
        new("BT-108", "Summe der Zuschläge", SemanticType.Amount, "Summen", null, UblTotals + "/cbc:ChargeTotalAmount", CiiTotals + "/ram:ChargeTotalAmount"),
        new("BT-109", "Gesamtbetrag ohne USt", SemanticType.Amount, "Summen", null, UblTotals + "/cbc:TaxExclusiveAmount", CiiTotals + "/ram:TaxBasisTotalAmount"),
        new("BT-110", "Summe Umsatzsteuer", SemanticType.Amount, "Summen", null, "cac:TaxTotal[cac:TaxSubtotal]/cbc:TaxAmount|cac:TaxTotal/cbc:TaxAmount", CiiTotals + "/ram:TaxTotalAmount"),
        new("BT-112", "Gesamtbetrag mit USt", SemanticType.Amount, "Summen", null, UblTotals + "/cbc:TaxInclusiveAmount", CiiTotals + "/ram:GrandTotalAmount"),
        new("BT-113", "Bereits gezahlt", SemanticType.Amount, "Summen", null, UblTotals + "/cbc:PrepaidAmount", CiiTotals + "/ram:TotalPrepaidAmount"),
        new("BT-114", "Rundungsbetrag", SemanticType.Amount, "Summen", null, UblTotals + "/cbc:PayableRoundingAmount", CiiTotals + "/ram:RoundingAmount"),
        new("BT-115", "Zahlbetrag", SemanticType.Amount, "Summen", null, UblTotals + "/cbc:PayableAmount", CiiTotals + "/ram:DuePayableAmount"),

        // ---------------------------------------------------------------------------------------------------- allowances and charges of the document
        new("BT-92", "Nachlass: Betrag", SemanticType.Amount, "Nachlässe", Allowances, "cbc:Amount", "ram:ActualAmount"),
        new("BT-93", "Nachlass: Basisbetrag", SemanticType.Amount, "Nachlässe", Allowances, "cbc:BaseAmount", "ram:BasisAmount"),
        new("BT-94", "Nachlass: Prozent", SemanticType.Percent, "Nachlässe", Allowances, "cbc:MultiplierFactorNumeric", "ram:CalculationPercent"),
        new("BT-95", "Nachlass: Steuerkategorie", SemanticType.Code, "Nachlässe", Allowances, "cac:TaxCategory/cbc:ID", "ram:CategoryTradeTax/ram:CategoryCode"),
        new("BT-96", "Nachlass: Steuersatz", SemanticType.Percent, "Nachlässe", Allowances, "cac:TaxCategory/cbc:Percent", "ram:CategoryTradeTax/ram:RateApplicablePercent"),
        new("BT-97", "Nachlass: Grund", SemanticType.Text, "Nachlässe", Allowances, "cbc:AllowanceChargeReason", "ram:Reason"),
        new("BT-98", "Nachlass: Grund (Code)", SemanticType.Code, "Nachlässe", Allowances, "cbc:AllowanceChargeReasonCode", "ram:ReasonCode"),
        new("BT-99", "Zuschlag: Betrag", SemanticType.Amount, "Zuschläge", Charges, "cbc:Amount", "ram:ActualAmount"),
        new("BT-100", "Zuschlag: Basisbetrag", SemanticType.Amount, "Zuschläge", Charges, "cbc:BaseAmount", "ram:BasisAmount"),
        new("BT-101", "Zuschlag: Prozent", SemanticType.Percent, "Zuschläge", Charges, "cbc:MultiplierFactorNumeric", "ram:CalculationPercent"),
        new("BT-102", "Zuschlag: Steuerkategorie", SemanticType.Code, "Zuschläge", Charges, "cac:TaxCategory/cbc:ID", "ram:CategoryTradeTax/ram:CategoryCode"),
        new("BT-103", "Zuschlag: Steuersatz", SemanticType.Percent, "Zuschläge", Charges, "cac:TaxCategory/cbc:Percent", "ram:CategoryTradeTax/ram:RateApplicablePercent"),
        new("BT-104", "Zuschlag: Grund", SemanticType.Text, "Zuschläge", Charges, "cbc:AllowanceChargeReason", "ram:Reason"),
        new("BT-105", "Zuschlag: Grund (Code)", SemanticType.Code, "Zuschläge", Charges, "cbc:AllowanceChargeReasonCode", "ram:ReasonCode"),

        // ---------------------------------------------------------------------------------------------------- the VAT breakdown
        new("BT-116", "Steuer: Bemessungsgrundlage", SemanticType.Amount, "Umsatzsteuer", VatBreakdown, "cbc:TaxableAmount", "ram:BasisAmount"),
        new("BT-117", "Steuer: Betrag", SemanticType.Amount, "Umsatzsteuer", VatBreakdown, "cbc:TaxAmount", "ram:CalculatedAmount"),
        new("BT-118", "Steuer: Kategorie", SemanticType.Code, "Umsatzsteuer", VatBreakdown, "cac:TaxCategory/cbc:ID", "ram:CategoryCode"),
        new("BT-119", "Steuer: Satz", SemanticType.Percent, "Umsatzsteuer", VatBreakdown, "cac:TaxCategory/cbc:Percent", "ram:RateApplicablePercent"),
        new("BT-120", "Steuer: Befreiungsgrund", SemanticType.Text, "Umsatzsteuer", VatBreakdown, "cac:TaxCategory/cbc:TaxExemptionReason", "ram:ExemptionReason"),
        new("BT-121", "Steuer: Befreiungsgrund (Code)", SemanticType.Code, "Umsatzsteuer", VatBreakdown, "cac:TaxCategory/cbc:TaxExemptionReasonCode", "ram:ExemptionReasonCode"),

        // ---------------------------------------------------------------------------------------------------- lines
        new("BT-126", "Position: Nummer", SemanticType.Identifier, "Positionen", Lines, "cbc:ID", "ram:AssociatedDocumentLineDocument/ram:LineID"),
        new("BT-127", "Position: Bemerkung", SemanticType.Text, "Positionen", Lines, "cbc:Note", "ram:AssociatedDocumentLineDocument/ram:IncludedNote/ram:Content"),
        new("BT-129", "Position: Menge", SemanticType.Quantity, "Positionen", Lines, "cbc:InvoicedQuantity|cbc:CreditedQuantity", "ram:SpecifiedLineTradeDelivery/ram:BilledQuantity"),
        new("BT-130", "Position: Einheit (Code)", SemanticType.Code, "Positionen", Lines, "cbc:InvoicedQuantity/@unitCode|cbc:CreditedQuantity/@unitCode", "ram:SpecifiedLineTradeDelivery/ram:BilledQuantity/@unitCode"),
        new("BT-131", "Position: Nettobetrag", SemanticType.Amount, "Positionen", Lines, "cbc:LineExtensionAmount", "ram:SpecifiedLineTradeSettlement/ram:SpecifiedTradeSettlementLineMonetarySummation/ram:LineTotalAmount"),
        new("BT-132", "Position: Bestellposition", SemanticType.Identifier, "Positionen", Lines, "cac:OrderLineReference/cbc:LineID", "ram:SpecifiedLineTradeAgreement/ram:BuyerOrderReferencedDocument/ram:LineID"),
        new("BT-133", "Position: Buchungsreferenz", SemanticType.Identifier, "Positionen", Lines, "cbc:AccountingCost", "ram:SpecifiedLineTradeSettlement/ram:ReceivableSpecifiedTradeAccountingAccount/ram:ID"),
        new("BT-134", "Position: Zeitraum von", SemanticType.Date, "Positionen", Lines, "cac:InvoicePeriod/cbc:StartDate", "ram:SpecifiedLineTradeSettlement/ram:BillingSpecifiedPeriod/ram:StartDateTime/udt:DateTimeString"),
        new("BT-135", "Position: Zeitraum bis", SemanticType.Date, "Positionen", Lines, "cac:InvoicePeriod/cbc:EndDate", "ram:SpecifiedLineTradeSettlement/ram:BillingSpecifiedPeriod/ram:EndDateTime/udt:DateTimeString"),
        new("BT-146", "Position: Nettopreis", SemanticType.UnitPrice, "Positionen", Lines, "cac:Price/cbc:PriceAmount", "ram:SpecifiedLineTradeAgreement/ram:NetPriceProductTradePrice/ram:ChargeAmount"),
        new("BT-147", "Position: Preisnachlass", SemanticType.UnitPrice, "Positionen", Lines, "cac:Price/cac:AllowanceCharge/cbc:Amount", "ram:SpecifiedLineTradeAgreement/ram:GrossPriceProductTradePrice/ram:AppliedTradeAllowanceCharge/ram:ActualAmount"),
        new("BT-148", "Position: Bruttopreis", SemanticType.UnitPrice, "Positionen", Lines, "cac:Price/cac:AllowanceCharge/cbc:BaseAmount", "ram:SpecifiedLineTradeAgreement/ram:GrossPriceProductTradePrice/ram:ChargeAmount"),
        new("BT-149", "Position: Basismenge", SemanticType.Quantity, "Positionen", Lines, "cac:Price/cbc:BaseQuantity", "ram:SpecifiedLineTradeAgreement/ram:NetPriceProductTradePrice/ram:BasisQuantity"),
        new("BT-151", "Position: Steuerkategorie", SemanticType.Code, "Positionen", Lines, "cac:Item/cac:ClassifiedTaxCategory/cbc:ID", "ram:SpecifiedLineTradeSettlement/ram:ApplicableTradeTax/ram:CategoryCode"),
        new("BT-152", "Position: Steuersatz", SemanticType.Percent, "Positionen", Lines, "cac:Item/cac:ClassifiedTaxCategory/cbc:Percent", "ram:SpecifiedLineTradeSettlement/ram:ApplicableTradeTax/ram:RateApplicablePercent"),
        new("BT-153", "Position: Artikelname", SemanticType.Text, "Positionen", Lines, "cac:Item/cbc:Name", "ram:SpecifiedTradeProduct/ram:Name"),
        new("BT-154", "Position: Beschreibung", SemanticType.Text, "Positionen", Lines, "cac:Item/cbc:Description", "ram:SpecifiedTradeProduct/ram:Description"),
        new("BT-155", "Position: Artikelnummer (Verkäufer)", SemanticType.Identifier, "Positionen", Lines, "cac:Item/cac:SellersItemIdentification/cbc:ID", "ram:SpecifiedTradeProduct/ram:SellerAssignedID"),
        new("BT-156", "Position: Artikelnummer (Käufer)", SemanticType.Identifier, "Positionen", Lines, "cac:Item/cac:BuyersItemIdentification/cbc:ID", "ram:SpecifiedTradeProduct/ram:BuyerAssignedID"),
        new("BT-157", "Position: Artikelkennung (GTIN)", SemanticType.Identifier, "Positionen", Lines, "cac:Item/cac:StandardItemIdentification/cbc:ID", "ram:SpecifiedTradeProduct/ram:GlobalID"),
        new("BT-157-scheme", "Position: Schema der Artikelkennung", SemanticType.Code, "Positionen", Lines, "cac:Item/cac:StandardItemIdentification/cbc:ID/@schemeID", "ram:SpecifiedTradeProduct/ram:GlobalID/@schemeID"),
        new("BT-159", "Position: Ursprungsland", SemanticType.Code, "Positionen", Lines, "cac:Item/cac:OriginCountry/cbc:IdentificationCode", "ram:SpecifiedTradeProduct/ram:OriginTradeCountry/ram:ID"),
    ];

    private static readonly Dictionary<string, SemanticField> ById = Fields.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SemanticGroup> GroupById = Groups.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

    public static SemanticField? Field(string id) => ById.GetValueOrDefault(id);

    public static SemanticGroup? Group(string id) => GroupById.GetValueOrDefault(id);

    public static IEnumerable<SemanticField> FieldsOf(string? group) => Fields.Where(f => string.Equals(f.Group, group, StringComparison.OrdinalIgnoreCase));

    /// <summary>"BT-1 Rechnungsnummer", or the id itself for a term that is not in the table.</summary>
    public static string Describe(string id) => ById.TryGetValue(id, out var f) ? $"{f.Id} {f.Label}" : id;

    public static string LabelOf(string id) => ById.TryGetValue(id, out var f) ? f.Label : id;

    /// <summary>The label of a key of the document or of an item ("BG-25[2]/BT-129" → "Position 2: Menge").</summary>
    public static string LabelOfKey(string key)
    {
        if (!SemanticDocument.TryParseItemKey(key, out var group, out var index, out var field))
        {
            return LabelOf(key);
        }

        var label = LabelOf(field);
        var colon = label.IndexOf(':', StringComparison.Ordinal);
        var name = colon > 0 ? label[(colon + 1)..].Trim() : label;
        var prefix = group switch
        {
            Lines => "Position",
            VatBreakdown => "Steuer",
            Allowances => "Nachlass",
            Charges => "Zuschlag",
            _ => group,
        };
        return $"{prefix} {index + 1}: {name}";
    }
}
