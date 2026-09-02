<#
Shared TLS setup for scripts that talk to the stack over HTTPS.

Two problems to solve on Windows:

  1. .NET Framework (Windows PowerShell 5.1) negotiates below TLS 1.2 by
     default; Traefik is configured with minVersion TLS 1.2 and refuses.

  2. The stack uses a private CA with no CRL or OCSP responder. Windows treats
     "revocation status unknown" as a hard failure, which is why curl needs
     --ssl-no-revoke. .NET needs the equivalent: a chain policy with
     RevocationMode = NoCheck.

Dot-source this before making requests:

    . "$PSScriptRoot\lib\tls-trust.ps1"
    Enable-LLMServiceTls -CaPath (Join-Path $root 'config/traefik/certs/ca.crt')
#>

function Enable-LLMServiceTls {
    param([Parameter(Mandatory = $true)][string]$CaPath)

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    if (-not (Test-Path $CaPath)) {
        Write-Host "  TLS: CA not found at $CaPath" -ForegroundColor Yellow
        return
    }

    $script:LLMServiceCa =
        New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CaPath)

    # Accept a certificate only if it actually chains to OUR CA. This is not
    # "trust everything": an unrelated bad certificate still fails.
    [Net.ServicePointManager]::ServerCertificateValidationCallback = {
        param($senderObj, $cert, $chain, $sslPolicyErrors)

        if ($sslPolicyErrors -eq [Net.Security.SslPolicyErrors]::None) { return $true }

        try {
            $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cert)
            $ch = New-Object System.Security.Cryptography.X509Certificates.X509Chain
            $ch.ChainPolicy.RevocationMode = 'NoCheck'
            $ch.ChainPolicy.VerificationFlags = 'AllowUnknownCertificateAuthority'
            $ch.ChainPolicy.ExtraStore.Add($script:LLMServiceCa) | Out-Null
            $null = $ch.Build($c)

            # The root of the built chain must be our CA, by thumbprint.
            $root = $ch.ChainElements[$ch.ChainElements.Count - 1].Certificate
            return ($root.Thumbprint -eq $script:LLMServiceCa.Thumbprint)
        } catch {
            return $false
        }
    }
}
