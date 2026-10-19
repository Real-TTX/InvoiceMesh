# XML schemas

The official schemas the structure of an incoming UBL or CII invoice is checked against (`Xml/XmlSchemas.cs`), embedded into the assembly.
They are taken unchanged from the validator configuration of the KoSIT for XRechnung 3.0.2 (`resources/ubl/2.1/xsd`, `resources/cii/16b/xsd`),
the same configuration the CI runs the KoSIT validator with.

| Folder | Schema | Copyright |
|---|---|---|
| `ubl/` | OASIS UBL 2.1 (Invoice, CreditNote and the common modules they import) | © OASIS Open 2013 |
| `cii/` | UN/CEFACT Cross Industry Invoice D16B | © UN/CEFACT 2016 |

Both may be copied and distributed provided their copyright notices stay; the files are not modified (the notices are in each file's header).
