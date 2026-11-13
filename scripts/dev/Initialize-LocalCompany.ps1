[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CompanyName,
    [ValidatePattern('^[A-Z]{3}$')]
    [string]$Currency = "IRR",
    [ValidatePattern('^[A-Z0-9_-]{2,50}$')]
    [string]$CompanyCode = "MAIN"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$envPath = Join-Path $repoRoot ".env"
if (-not (Test-Path $envPath)) {
    throw "فایل .env پیدا نشد. ابتدا از .env.example یک فایل .env محلی بسازید."
}

$settings = @{}
Get-Content $envPath | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*?)\s*$' -and $matches[1] -notmatch '^#') {
        $value = $matches[2].Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $settings[$matches[1]] = $value
    }
}
$signingKey = $settings["AUTHENTICATION_SIGNING_KEY"]
if ([string]::IsNullOrWhiteSpace($signingKey) -or [System.Text.Encoding]::UTF8.GetByteCount($signingKey) -lt 32) {
    throw "AUTHENTICATION_SIGNING_KEY باید حداقل ۳۲ بایت UTF-8 باشد."
}
if ([string]::IsNullOrWhiteSpace($settings["POSTGRES_PASSWORD"])) {
    throw "POSTGRES_PASSWORD در .env تنظیم نشده است."
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
function ConvertTo-JsonBase64Url($Value) {
    $json = ConvertTo-Json -InputObject $Value -Compress
    return ConvertTo-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($json))
}

$tenantId = [Guid]::NewGuid().ToString()
$companyId = [Guid]::NewGuid().ToString()
$userId = [Guid]::NewGuid().ToString()
$tenantCode = "LOCAL-" + $tenantId.Replace("-", "").Substring(0, 12)
$safeTenantCode = $tenantCode.Replace("'", "''")
$safeCompanyCode = $CompanyCode.Replace("'", "''")
$safeCompanyName = $CompanyName.Replace("'", "''")
$actor = $userId

$sql = @"
BEGIN;
SELECT set_config('app.tenant_id', '$tenantId', true);
INSERT INTO tenants(id, tenant_code, display_name)
VALUES ('$tenantId', '$safeTenantCode', '$safeCompanyName');
INSERT INTO companies(id, tenant_id, company_code, legal_name, functional_currency)
VALUES ('$companyId', '$tenantId', '$safeCompanyCode', '$safeCompanyName', '$Currency');
INSERT INTO company_memberships(tenant_id, company_id, user_id, role_code, is_active)
VALUES ('$tenantId', '$companyId', '$userId', 'FINANCE_ADMIN', true);
INSERT INTO audit_events(tenant_id, company_id, actor_id, event_type, entity_type, entity_id, payload)
VALUES ('$tenantId', '$companyId', '$actor', 'LOCAL_DEVELOPMENT_BOOTSTRAP', 'COMPANY', '$companyId',
        jsonb_build_object('role', 'FINANCE_ADMIN', 'environment', 'local-development'));
COMMIT;
"@

Push-Location $repoRoot
try {
    & docker compose exec -T postgres psql -U samaneh_app -d samaneh -v ON_ERROR_STOP=1 -c $sql
    if ($LASTEXITCODE -ne 0) { throw "ایجاد شرکت و عضویت محلی در PostgreSQL ناموفق بود." }
}
finally {
    Pop-Location
}

$header = ConvertTo-JsonBase64Url @{ alg = "HS256"; typ = "JWT" }
$payload = ConvertTo-JsonBase64Url @{
    iss = "samaneh-local"
    aud = "samaneh-local-web"
    exp = [DateTimeOffset]::UtcNow.AddHours(8).ToUnixTimeSeconds()
    tenant_id = $tenantId
    sub = $userId
}
$unsigned = "$header.$payload"
$hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($signingKey))
try {
    $signature = ConvertTo-Base64Url ($hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($unsigned)))
}
finally {
    $hmac.Dispose()
}
$token = "$unsigned.$signature"

Write-Host ""
Write-Host "شرکت و عضویت مدیر محلی با موفقیت ایجاد شد." -ForegroundColor Green
Write-Host "Company ID: $companyId"
Write-Host "Tenant ID:  $tenantId"
Write-Host "User ID:    $userId"
Write-Host "Role:       FINANCE_ADMIN (local development only)"
Write-Host "Token expiry: 8 hours"
Write-Host ""
Write-Host "توکن را فقط در رابط محلی http://localhost:8081 وارد کنید:"
Write-Host $token
Write-Warning "این توکن و نقش مدیر فقط برای محیط توسعه محلی است. از این اسکریپت یا HS256 محلی در محیط واقعی استفاده نکنید."
