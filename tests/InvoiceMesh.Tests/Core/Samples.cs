using System.Text;

namespace InvoiceMesh.Tests.Core;

/// <summary>The sample invoices of the tests (copied next to the test assembly).</summary>
public static class Samples
{
    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    public static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));
}
