<#
用法（在仓库根目录执行）：
  powershell -ExecutionPolicy Bypass -File scripts\secrets.ps1 lock   -Source .env-save.example -Output secrets.enc
  powershell -ExecutionPolicy Bypass -File scripts\secrets.ps1 unlock -Input secrets.enc -ToFile .env
Git Bash 下可用 ./scripts/secrets.sh lock|unlock。
#>
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('lock', 'unlock')]
    [string]$Command,

    [string]$Source,
    [string]$Output,
    [string]$Input,
    [string]$ToFile,
    [switch]$Force,
    [switch]$RemoveSource,
    [switch]$NoClipboard
)

$ErrorActionPreference = 'Stop'

if ([Console]::IsOutputRedirected) {
    try {
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    }
    catch {
    }
}

$script:FormatVersion = 'MSG2DC-SECRETS v1'
$script:DefaultIterations = 600000
$script:ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:InputArg = if ($PSBoundParameters.ContainsKey('Input')) { [string]$PSBoundParameters['Input'] } else { 'secrets.enc' }

function Get-ProjectPath {
    param([string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $script:ProjectRoot $Path))
}

function Stop-WithError {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Red
    exit 1
}

function Clear-Bytes {
    param([byte[]]$Bytes)
    if ($null -ne $Bytes -and $Bytes.Length -gt 0) {
        [System.Array]::Clear($Bytes, 0, $Bytes.Length)
    }
}

function New-RandomBytes {
    param([int]$Length)
    $bytes = New-Object byte[] $Length
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }
    return ,$bytes
}

function Test-SecureStringEqual {
    param(
        [System.Security.SecureString]$Left,
        [System.Security.SecureString]$Right
    )
    $leftBstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Left)
    $rightBstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Right)
    try {
        $difference = [int]($Left.Length -bxor $Right.Length)
        $count = [Math]::Min($Left.Length, $Right.Length)
        for ($i = 0; $i -lt $count; $i++) {
            $leftChar = [System.Runtime.InteropServices.Marshal]::ReadInt16($leftBstr, $i * 2)
            $rightChar = [System.Runtime.InteropServices.Marshal]::ReadInt16($rightBstr, $i * 2)
            $difference = $difference -bor ($leftChar -bxor $rightChar)
        }
        return ($difference -eq 0)
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($leftBstr)
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($rightBstr)
    }
}

function New-EncryptionKeys {
    param(
        [System.Security.SecureString]$Secure,
        [byte[]]$Salt,
        [int]$Iterations
    )
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    $passwordBytes = $null
    $material = $null
    $kdf = $null
    try {
        $byteLength = [System.Runtime.InteropServices.Marshal]::ReadInt32($bstr, -4)
        $utf16 = New-Object byte[] $byteLength
        try {
            [System.Runtime.InteropServices.Marshal]::Copy($bstr, $utf16, 0, $byteLength)
            $passwordBytes = [System.Text.Encoding]::Convert(
                [System.Text.Encoding]::Unicode,
                [System.Text.Encoding]::UTF8,
                $utf16)
        }
        finally {
            Clear-Bytes $utf16
        }

        $kdf = [System.Security.Cryptography.Rfc2898DeriveBytes]::new(
            $passwordBytes,
            $Salt,
            $Iterations,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        $material = $kdf.GetBytes(64)

        $aesKey = New-Object byte[] 32
        $macKey = New-Object byte[] 32
        [System.Array]::Copy($material, 0, $aesKey, 0, 32)
        [System.Array]::Copy($material, 32, $macKey, 0, 32)

        return [pscustomobject]@{ Aes = $aesKey; Mac = $macKey }
    }
    finally {
        Clear-Bytes $material
        Clear-Bytes $passwordBytes
        if ($null -ne $kdf) { $kdf.Dispose() }
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Join-Bytes {
    param([byte[]]$First, [byte[]]$Second)
    $joined = New-Object byte[] ($First.Length + $Second.Length)
    [System.Array]::Copy($First, 0, $joined, 0, $First.Length)
    [System.Array]::Copy($Second, 0, $joined, $First.Length, $Second.Length)
    return ,$joined
}

function Test-MacEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($Left.Length -ne $Right.Length) {
        return $false
    }
    $difference = 0
    for ($i = 0; $i -lt $Left.Length; $i++) {
        $difference += ($Left[$i] -bxor $Right[$i])
    }
    return ($difference -eq 0)
}

function Get-HmacSha256 {
    param([byte[]]$Key, [byte[]]$Data)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($Key)
    try {
        return ,$hmac.ComputeHash($Data)
    }
    finally {
        $hmac.Dispose()
    }
}

function Invoke-AesEncrypt {
    param([byte[]]$Plaintext, [byte[]]$Key, [byte[]]$IV)
    $aes = [System.Security.Cryptography.Aes]::Create()
    $transform = $null
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $Key
        $aes.IV = $IV
        $transform = $aes.CreateEncryptor()
        $result = $transform.TransformFinalBlock($Plaintext, 0, $Plaintext.Length)
        return ,$result
    }
    finally {
        if ($null -ne $transform) { $transform.Dispose() }
        $aes.Dispose()
    }
}

function Invoke-AesDecrypt {
    param([byte[]]$Ciphertext, [byte[]]$Key, [byte[]]$IV)
    $aes = [System.Security.Cryptography.Aes]::Create()
    $transform = $null
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $Key
        $aes.IV = $IV
        $transform = $aes.CreateDecryptor()
        $result = $transform.TransformFinalBlock($Ciphertext, 0, $Ciphertext.Length)
        return ,$result
    }
    finally {
        if ($null -ne $transform) { $transform.Dispose() }
        $aes.Dispose()
    }
}

function ConvertFrom-Base64Field {
    param([string]$Name, [string]$Value)
    try {
        return ,[System.Convert]::FromBase64String($Value)
    }
    catch {
        Stop-WithError "密文文件字段 $Name 不是有效的 Base64。"
    }
}

function Read-Envelope {
    param([string]$Path)
    $raw = [System.IO.File]::ReadAllBytes($Path)
    $text = (New-Object System.Text.UTF8Encoding($false)).GetString($raw).TrimStart([char]0xFEFF)
    $lines = $text -split "`r?`n"

    $header = $lines[0].Trim()
    if ($header -ne $script:FormatVersion) {
        if ($header.StartsWith('MSG2DC-SECRETS')) {
            Stop-WithError "不支持的密文格式版本：$header（本脚本支持 $script:FormatVersion）"
        }
        Stop-WithError "不是有效的密文文件：$Path"
    }

    $fields = @{}
    for ($i = 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        if ($line.Length -eq 0) { continue }
        $separator = $line.IndexOf('=')
        if ($separator -lt 1) {
            Stop-WithError "密文文件第 $($i + 1) 行格式无效。"
        }
        $key = $line.Substring(0, $separator).Trim().ToLowerInvariant()
        $value = $line.Substring($separator + 1).Trim()
        $fields[$key] = $value
    }

    foreach ($required in @('iterations', 'salt', 'iv', 'tag', 'ciphertext')) {
        if (-not $fields.ContainsKey($required)) {
            Stop-WithError "密文文件缺少字段：$required"
        }
    }

    $iterations = 0
    if (-not [int]::TryParse($fields['iterations'], [ref]$iterations)) {
        Stop-WithError '密文文件中的 iterations 不是有效整数。'
    }
    if ($iterations -lt 10000 -or $iterations -gt 10000000) {
        Stop-WithError "密文文件中的 iterations 超出允许范围：$iterations"
    }

    $salt = ConvertFrom-Base64Field 'salt' $fields['salt']
    $iv = ConvertFrom-Base64Field 'iv' $fields['iv']
    $tag = ConvertFrom-Base64Field 'tag' $fields['tag']
    $ciphertext = ConvertFrom-Base64Field 'ciphertext' $fields['ciphertext']

    if ($salt.Length -ne 16) { Stop-WithError '密文文件中的 salt 长度不正确。' }
    if ($iv.Length -ne 16) { Stop-WithError '密文文件中的 iv 长度不正确。' }
    if ($tag.Length -ne 32) { Stop-WithError '密文文件中的 tag 长度不正确。' }
    if ($ciphertext.Length -eq 0 -or ($ciphertext.Length % 16) -ne 0) {
        Stop-WithError '密文文件中的 ciphertext 长度不正确。'
    }

    return [pscustomobject]@{
        Iterations = $iterations
        Salt       = $salt
        IV         = $iv
        Tag        = $tag
        Ciphertext = $ciphertext
    }
}

function Invoke-Lock {
    if ([string]::IsNullOrEmpty($Source)) { $Source = '.env-save.example' }
    if ([string]::IsNullOrEmpty($Output)) { $Output = 'secrets.enc' }

    $sourcePath = Get-ProjectPath $Source
    $outputPath = Get-ProjectPath $Output

    if (-not [System.IO.File]::Exists($sourcePath)) {
        Stop-WithError "找不到明文文件：$sourcePath"
    }
    if ($sourcePath -eq $outputPath) {
        Stop-WithError '明文路径与输出路径相同，已中止。'
    }
    if ([System.IO.File]::Exists($outputPath) -and -not $Force) {
        Stop-WithError "目标文件已存在：$outputPath（如需覆盖请加 -Force）"
    }
    $outputDirectory = [System.IO.Path]::GetDirectoryName($outputPath)
    if (-not [System.IO.Directory]::Exists($outputDirectory)) {
        Stop-WithError "输出目录不存在：$outputDirectory"
    }

    $password = Read-Host -AsSecureString -Prompt '设置加密口令（至少 12 个字符，用于长期保管）'
    try {
        if ($password.Length -lt 12) {
            Stop-WithError '口令太短：至少需要 12 个字符。'
        }

        $confirm = Read-Host -AsSecureString -Prompt '请再次输入同一口令'
        try {
            if (-not (Test-SecureStringEqual $password $confirm)) {
                Stop-WithError '两次输入的口令不一致，已中止。'
            }
        }
        finally {
            $confirm.Dispose()
        }

        $salt = New-RandomBytes 16
        $iv = New-RandomBytes 16
        $keys = New-EncryptionKeys -Secure $password -Salt $salt -Iterations $script:DefaultIterations

        $plaintext = $null
        try {
            $plaintext = [System.IO.File]::ReadAllBytes($sourcePath)
        }
        catch {
            Stop-WithError "无法读取明文文件：$sourcePath"
        }

        try {
            $ciphertext = Invoke-AesEncrypt -Plaintext $plaintext -Key $keys.Aes -IV $iv
            $macInput = Join-Bytes $iv $ciphertext
            try {
                $tag = Get-HmacSha256 -Key $keys.Mac -Data $macInput
            }
            finally {
                Clear-Bytes $macInput
            }

            $envelopeText = @(
                $script:FormatVersion
                "iterations=$($script:DefaultIterations)"
                "salt=$([System.Convert]::ToBase64String($salt))"
                "iv=$([System.Convert]::ToBase64String($iv))"
                "tag=$([System.Convert]::ToBase64String($tag))"
                "ciphertext=$([System.Convert]::ToBase64String($ciphertext))"
            ) -join "`r`n"

            $envelopeBytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($envelopeText + "`r`n")
            try {
                [System.IO.File]::WriteAllBytes($outputPath, $envelopeBytes)
            }
            catch {
                Stop-WithError "无法写入密文文件：$outputPath"
            }
        }
        finally {
            Clear-Bytes $plaintext
            Clear-Bytes $keys.Aes
            Clear-Bytes $keys.Mac
        }
    }
    finally {
        $password.Dispose()
    }

    $size = (New-Object System.IO.FileInfo($outputPath)).Length
    Write-Host "已加密到 $outputPath（$size 字节）"

    if ($RemoveSource) {
        try {
            [System.IO.File]::Delete($sourcePath)
        }
        catch {
            Stop-WithError "密文已生成，但删除明文失败：$sourcePath"
        }
        Write-Host "已删除明文：$sourcePath"
    }
    else {
        Write-Host "注意：明文仍在 $sourcePath" -ForegroundColor Yellow
    }
}

function Invoke-Unlock {
    $inputArgument = $script:InputArg
    if ([string]::IsNullOrEmpty($inputArgument)) { $inputArgument = 'secrets.enc' }
    if ([string]::IsNullOrEmpty($ToFile)) { $ToFile = '.env' }

    $inputPath = Get-ProjectPath $inputArgument
    $targetPath = Get-ProjectPath $ToFile

    if (-not [System.IO.File]::Exists($inputPath)) {
        Stop-WithError "找不到密文文件：$inputPath"
    }
    if ($inputPath -eq $targetPath) {
        Stop-WithError '密文路径与输出路径相同，已中止。'
    }
    if ([System.IO.File]::Exists($targetPath) -and -not $Force) {
        Stop-WithError "目标文件已存在：$targetPath（如需覆盖请加 -Force）"
    }
    $targetDirectory = [System.IO.Path]::GetDirectoryName($targetPath)
    if (-not [System.IO.Directory]::Exists($targetDirectory)) {
        Stop-WithError "目标目录不存在：$targetDirectory"
    }

    $envelope = Read-Envelope $inputPath

    $password = Read-Host -AsSecureString -Prompt '输入解密口令'
    $plaintext = $null
    try {
        if ($password.Length -eq 0) {
            Stop-WithError '口令不能为空。'
        }

        $keys = New-EncryptionKeys -Secure $password -Salt $envelope.Salt -Iterations $envelope.Iterations
        try {
            $macInput = Join-Bytes $envelope.IV $envelope.Ciphertext
            try {
                $computedTag = Get-HmacSha256 -Key $keys.Mac -Data $macInput
            }
            finally {
                Clear-Bytes $macInput
            }

            try {
                if (-not (Test-MacEqual $envelope.Tag $computedTag)) {
                    Stop-WithError '密码错误或文件已被篡改'
                }
            }
            finally {
                Clear-Bytes $computedTag
            }

            try {
                $plaintext = Invoke-AesDecrypt -Ciphertext $envelope.Ciphertext -Key $keys.Aes -IV $envelope.IV
            }
            catch {
                Stop-WithError '解密失败：密文已损坏'
            }
        }
        finally {
            Clear-Bytes $keys.Aes
            Clear-Bytes $keys.Mac
        }
    }
    finally {
        $password.Dispose()
    }

    try {
        [System.IO.File]::WriteAllBytes($targetPath, $plaintext)
    }
    catch {
        Clear-Bytes $plaintext
        Stop-WithError "无法写入目标文件：$targetPath"
    }

    $size = $plaintext.Length
    try {
        if ($NoClipboard) {
            Write-Host "已解密到 $targetPath（$size 字节）"
        }
        else {
            $copied = $false
            $clipboardError = $null
            $text = (New-Object System.Text.UTF8Encoding($false)).GetString($plaintext)
            if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
                $text = $text.Substring(1)
            }
            for ($attempt = 0; $attempt -lt 5 -and -not $copied; $attempt++) {
                try {
                    Set-Clipboard -Value $text
                    $copied = $true
                }
                catch {
                    $clipboardError = $_.Exception.Message
                    Start-Sleep -Milliseconds 200
                }
            }
            if ($copied) {
                Write-Host "已解密到 $targetPath（$size 字节），并已复制到剪贴板"
            }
            else {
                Write-Host "已解密到 $targetPath（$size 字节），但复制到剪贴板失败：$clipboardError" -ForegroundColor Yellow
            }
        }
    }
    finally {
        Clear-Bytes $plaintext
    }
}

try {
    if ($Command -eq 'lock') {
        Invoke-Lock
    }
    else {
        Invoke-Unlock
    }
}
catch {
    Write-Host ("错误：" + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
