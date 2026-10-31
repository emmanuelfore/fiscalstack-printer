namespace PrintInterceptor.Core.Fiscalisation;

/// <summary>
/// Tenant identity returned by GET /api/v1/company. Receipts must carry the
/// same name, TIN and VAT number that FiscalStack registered with ZIMRA.
/// </summary>
public sealed class CompanyProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Tin { get; set; } = "";
    public string VatNumber { get; set; } = "";
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string LogoUrl { get; set; } = "";
}
