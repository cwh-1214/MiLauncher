<#
.SYNOPSIS
    生成一个自签名代码签名证书，仅用于验证签名流水线是否跑通。

.DESCRIPTION
    自签名证书不会被任何其他电脑信任，对外分发时：
      · SmartScreen 依然会拦，依然显示「未知发布者」
      · 文件属性里能看到签名，但证书链校验失败
    所以它只适合两种情况：
      1. 现在就想确认 tools\sign.ps1 能用（拿到真证书后直接换指纹即可）
      2. 给公司内网分发，把证书导入到「受信任的根证书颁发机构」后就没警告了

    生成的证书放在「当前用户\个人」，带私钥，一年有效，可重复运行（同名会覆盖）。

.EXAMPLE
    .\make-dev-cert.ps1
    # 之后用打印出来的指纹签名
    .\sign.ps1 -Path ..\publish\MiLauncher.exe -Thumbprint <指纹>

.EXAMPLE
    .\make-dev-cert.ps1 -Remove
    # 测试完把证书删掉
#>
[CmdletBinding()]
param(
    [string]$SubjectName = "CN=MiLauncher Dev, O=MiLauncher, C=CN",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $SubjectName } |
    Sort-Object NotAfter -Descending

if ($Remove) {
    if (-not $existing) {
        Write-Host "没有找到开发证书，无需清理。"
        exit 0
    }
    foreach ($c in $existing) {
        Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $c.Thumbprint) -Force
        Write-Host "已删除：$($c.Thumbprint)"
    }
    exit 0
}

if ($existing) {
    $cert = $existing[0]
    Write-Host "开发证书已存在，直接复用。"
}
else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $SubjectName `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(1) `
        -TextExtension @(
            "2.5.29.37={text}1.3.6.1.5.5.7.3.3",   # EKU: 代码签名
            "2.5.29.19={text}"                      # 不是 CA
        )
    Write-Host "已生成开发证书。"
}

Write-Host ""
Write-Host "  主题：$($cert.Subject)"
Write-Host "  指纹：$($cert.Thumbprint)"
Write-Host "  到期：$($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host ""
Write-Host "  用它签名："
Write-Host "    .\sign.ps1 -Path ..\publish\MiLauncher.exe -Thumbprint $($cert.Thumbprint)"
Write-Host ""
Write-Host "  提醒：自签名证书对外分发没有任何作用，别把它当成发布版。" -ForegroundColor Yellow
