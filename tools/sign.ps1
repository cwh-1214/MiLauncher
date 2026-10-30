<#
.SYNOPSIS
    给 MiLauncher 的发布产物做 Authenticode 代码签名。

.DESCRIPTION
    证书来源三选一（优先级从高到低）：
      -PfxPath / -PfxPassword   本地 PFX 文件，最省事
      -Thumbprint               证书指纹，从「当前用户\个人」证书库找
                                （SimplySign 云证书、UKey、手动导入的 PFX 都出现在这里）
      -Subject                  按主题名模糊匹配，例如 "MiLauncher"

    不传参数时自动读取脚本目录下的 signing.local.json。
    既没有配置也没有可用证书时，打印说明后以 0 退出——不会阻断构建。

    依赖：Windows 自带 Set-AuthenticodeSignature，不需要安装 Windows SDK / signtool。

.EXAMPLE
    .\sign.ps1 -Path ..\publish\MiLauncher.exe -Thumbprint 8A3F19C2...

.EXAMPLE
    .\sign.ps1 -Path ..\publish\MiLauncher.exe -PfxPath D:\cert\milauncher.pfx -PfxPassword "***"
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$Thumbprint,
    [string]$Subject,
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

# 时间戳服务按顺序试，某个不可达就换下一个
$TimestampFallbacks = @(
    $TimestampServer,
    "http://timestamp.sectigo.com",
    "http://timestamp.acs.microsoft.com"
)

function Write-Step($text) { Write-Host "  $text" }
function Write-Warn2($text) { Write-Host "  ! $text" -ForegroundColor Yellow }

# ---------- 1. 定位目标文件 ----------
if (-not $Path) {
    $Path = Join-Path $PSScriptRoot "..\publish\MiLauncher.exe"
}
$Path = [System.IO.Path]::GetFullPath($Path)

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Warn2 "找不到文件：$Path"
    Write-Host "  先运行 publish.cmd 生成发布产物。"
    exit 1
}

$sizeMb = [Math]::Round((Get-Item -LiteralPath $Path).Length / 1MB, 1)
Write-Host "签名目标：$Path  ($sizeMb MB)"

# ---------- 2. 读取配置 ----------
$configPath = Join-Path $PSScriptRoot "signing.local.json"
$config = $null
if (Test-Path -LiteralPath $configPath) {
    try {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Step "读取配置：$configPath"
    }
    catch {
        Write-Warn2 "signing.local.json 解析失败，忽略：$($_.Exception.Message)"
        $config = $null
    }
}

if ($config) {
    if (-not $PfxPath -and $config.PfxPath) {
        $PfxPath = $config.PfxPath
        if (-not $PfxPassword -and $config.PfxPassword) { $PfxPassword = $config.PfxPassword }
    }
    if (-not $Thumbprint -and $config.Thumbprint) { $Thumbprint = $config.Thumbprint }
    if (-not $Subject -and $config.Subject) { $Subject = $config.Subject }
}

# PFX 相对路径按配置所在目录解析
if ($PfxPath -and -not [System.IO.Path]::IsPathRooted($PfxPath)) {
    $PfxPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PfxPath))
}

# ---------- 3. 找证书 ----------
$cert = $null

if ($PfxPath) {
    if (-not (Test-Path -LiteralPath $PfxPath)) {
        Write-Warn2 "找不到证书文件：$PfxPath"
        exit 1
    }

    if (-not $PfxPassword) {
        $secure = Read-Host "  请输入 PFX 密码" -AsSecureString
        $PfxPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }

    # 先看证书库里有没有（重复构建时避免反复导入）
    try {
        $probe = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $PfxPassword)
        $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $probe.Thumbprint }
        if ($existing) {
            $cert = $existing[0]
            Write-Step "证书已在证书库中：$($cert.Thumbprint)"
        }
        else {
            $imported = Import-PfxCertificate -FilePath $PfxPath `
                -CertStoreLocation Cert:\CurrentUser\My `
                -Password (ConvertTo-SecureString $PfxPassword -AsPlainText -Force)
            $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $imported.Thumbprint } | Select-Object -First 1
            Write-Step "已导入证书到 当前用户\个人：$($cert.Thumbprint)"
        }
    }
    catch {
        Write-Warn2 "读取 PFX 失败：$($_.Exception.Message)"
        exit 1
    }
}

if (-not $cert -and $Thumbprint) {
    $clean = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $clean } | Select-Object -First 1
    if (-not $cert) {
        Write-Warn2 "当前用户证书库里找不到指纹 $clean"
        exit 1
    }
    Write-Step "按指纹找到证书：$($cert.Subject)"
}

if (-not $cert -and $Subject) {
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -like "*$Subject*" -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if (-not $cert) {
        Write-Warn2 "证书库里没有匹配「$Subject」的带私钥证书"
        exit 1
    }
    Write-Step "按主题名找到证书：$($cert.Subject)"
}

if (-not $cert) {
    Write-Host ""
    Write-Host "  没有可用的签名证书，跳过签名。" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  三种拿到证书的方式（详见 签名与分发说明.md）："
    Write-Host "    · Certum 个人 OV 代码签名（国内可办，约 800 元/年，仅需身份证）"
    Write-Host "    · Certum 开源代码签名（GitHub 1k+ star 的开源项目可免费申请）"
    Write-Host "    · Azure 工件签名（9.99 美元/月，但仅限美加个人 / 美加欧英组织）"
    Write-Host ""
    Write-Host "  拿到之后：把证书指纹或 PFX 路径写进 tools\signing.local.json，"
    Write-Host "  再运行一次 publish.cmd 就会自动签名。"
    Write-Host ""
    exit 0
}

if (-not $cert.HasPrivateKey) {
    Write-Warn2 "证书没有私钥，无法签名：$($cert.Subject)"
    exit 1
}

# ---------- 4. 签名 ----------
Write-Host ""
Write-Step "签名者：$($cert.Subject)"
Write-Step "颁发者：$($cert.Issuer)"
Write-Step "有效期：$($cert.NotBefore.ToString('yyyy-MM-dd')) → $($cert.NotAfter.ToString('yyyy-MM-dd'))"

$signed = $null
foreach ($ts in $TimestampFallbacks) {
    try {
        Write-Step "签名中（SHA-256，时间戳 $ts）…"
        $signed = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert `
            -HashAlgorithm SHA256 -TimestampServer $ts
        if ($signed.Status -eq 'Valid' -or $signed.Status -eq 'UnknownError') { break }
        Write-Warn2 "返回状态 $($signed.Status)，换一个时间戳服务重试"
        $signed = $null
    }
    catch {
        Write-Warn2 "签名失败：$($_.Exception.Message)"
        $signed = $null
    }
}

if (-not $signed) {
    Write-Warn2 "所有时间戳服务都失败了。可以去掉时间戳重签（证书过期后签名会失效，不推荐）："
    Write-Host "    .\sign.ps1 -Path `"$Path`" -Thumbprint $($cert.Thumbprint) -TimestampServer `"`""
    exit 1
}

# ---------- 5. 校验 ----------
$check = Get-AuthenticodeSignature -FilePath $Path
$signer = "—"
if ($check.SignerCertificate) {
    $signer = $check.SignerCertificate.Subject
}

Write-Host ""
Write-Step "签名状态：$($check.Status)"
Write-Step "签名者：$signer"
if ($check.TimeStamperCertificate) {
    Write-Step "时间戳：$($check.TimeStamperCertificate.Subject)"
}
else {
    Write-Warn2 "没有时间戳，证书到期后签名会失效"
}

$sha = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
Write-Step "SHA-256：$sha"

if ($check.Status -eq 'Valid') {
    Write-Host ""
    Write-Host "  签名完成，证书链校验通过。" -ForegroundColor Green
    exit 0
}

if ($check.Status -eq 'UnknownError') {
    Write-Host ""
    Write-Warn2 "签名已写入，但根证书不受信任（自签名证书的正常表现）。"
    Write-Host "  这只适合自测，对外分发必须换成 CA 签发的证书。"
    exit 0
}

Write-Host ""
Write-Warn2 "签名校验未通过：$($check.StatusMessage)"
exit 1
