namespace InvoiceMesh.Core.Validation;

/// <summary>The code lists EN 16931 refers to, as far as the rules of InvoiceMesh need them.</summary>
public static class CodeLists
{
    /// <summary>ISO 4217 currency codes.</summary>
    public static readonly IReadOnlySet<string> Currencies = Set(
        "AED AFN ALL AMD ANG AOA ARS AUD AWG AZN BAM BBD BDT BGN BHD BIF BMD BND BOB BRL BSD BTN BWP BYN BZD CAD CDF CHF CLP CNY COP CRC CUP CVE CZK DJF DKK DOP DZD " +
        "EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GNF GTQ GYD HKD HNL HTG HUF IDR ILS INR IQD IRR ISK JMD JOD JPY KES KGS KHR KMF KPW KRW KWD KYD KZT LAK LBP LKR " +
        "LRD LSL LYD MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MYR MZN NAD NGN NIO NOK NPR NZD OMR PAB PEN PGK PHP PKR PLN PYG QAR RON RSD RUB RWF SAR SBD SCR " +
        "SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB TJS TMT TND TOP TRY TTD TWD TZS UAH UGX USD UYU UZS VES VND VUV WST XAF XCD XOF XPF YER ZAR ZMW ZWL");

    /// <summary>ISO 3166-1 alpha-2 country codes (plus 1A for Kosovo and XI for Northern Ireland, which EN 16931 allows).</summary>
    public static readonly IReadOnlySet<string> Countries = Set(
        "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV " +
        "CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM " +
        "IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW " +
        "MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS " +
        "ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW 1A XI");

    /// <summary>The invoice type codes (UNCL 1001) EN 16931 allows.</summary>
    public static readonly IReadOnlySet<string> InvoiceTypes = Set(
        "71 80 81 82 83 84 102 130 202 203 204 211 218 219 261 262 295 296 308 325 326 331 380 381 382 383 384 385 386 387 388 389 390 393 394 395 396 420 456 457 " +
        "471 472 473 500 501 502 503 527 532 553 575 623 633 751 780 817 870 875 876 877 935");

    /// <summary>The invoice type codes XRechnung allows (BR-DE-17).</summary>
    public static readonly IReadOnlySet<string> XRechnungInvoiceTypes = Set("326 380 384 389 381 875 876 877");

    /// <summary>The VAT categories (UNCL 5305, subset of EN 16931).</summary>
    public static readonly IReadOnlySet<string> VatCategories = Set("S Z E AE K G O L M");

    /// <summary>Payment means (UNCL 4461), the codes that occur on invoices.</summary>
    public static readonly IReadOnlySet<string> PaymentMeans = Set(
        "1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30 31 32 33 34 35 36 37 38 39 40 41 42 43 44 45 46 47 48 49 50 51 52 53 54 55 56 " +
        "57 58 59 60 61 62 63 64 65 66 67 68 70 74 75 76 77 78 91 92 93 94 95 96 97 98 ZZZ");

    /// <summary>
    /// The unit codes (UN/ECE Recommendation 20 and 21) that occur on almost every invoice. The full list has more than 2000 entries; a code not in here is
    /// reported as a hint, not as an error.
    /// </summary>
    public static readonly IReadOnlySet<string> CommonUnits = Set(
        "C62 H87 EA XPP HUR MIN SEC DAY WEE MON ANN QAN KGM GRM TNE MGM LTR MLT MTQ MTK MTR CMT MMT KMT KWH MWH KWT SET PR LS P1 XBX XPK XCT XPA XPL XBG XRO " +
        "XBO XCA XCR XSA XTU XST NAR NPR E48 E49 DZN TU ZZ");

    /// <summary>The prefixes of VAT identifiers: ISO 3166-1 alpha-2 and EL for Greece (BR-CO-09).</summary>
    public static bool HasCountryPrefix(string vatId)
        => vatId.Length > 2 && (Countries.Contains(vatId[..2].ToUpperInvariant()) || vatId.StartsWith("EL", StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> Set(string codes) => new(codes.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
}
