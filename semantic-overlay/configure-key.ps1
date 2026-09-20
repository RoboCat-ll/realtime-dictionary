$ErrorActionPreference = "Stop"

$configDir = Join-Path ([Environment]::GetFolderPath("ApplicationData")) "RealtimeDictionary"
$configPath = Join-Path $configDir "config.json"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null

Write-Host "实时字典 - 硅基流动 API Key 配置"
Write-Host "密钥只保存在当前 Windows 用户目录：$configPath"
$secureKey = Read-Host "请输入硅基流动 API Key（输入内容会隐藏）" -AsSecureString
$key = ""
$bstr = [IntPtr]::Zero
try {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
}
finally {
    if ($bstr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

$config = [ordered]@{
    base_url = "https://api.siliconflow.cn/v1"
    model = "deepseek-ai/DeepSeek-V4-Flash"
    api_key = $key
}
$json = $config | ConvertTo-Json
Set-Content -LiteralPath $configPath -Value $json -Encoding utf8
Write-Host "已保存。请退出并重新启动实时字典，使配置生效。"
