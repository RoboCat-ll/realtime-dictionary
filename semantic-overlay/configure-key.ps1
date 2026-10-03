$ErrorActionPreference = "Stop"
# Credential writes belong to the provider-scoped, DPAPI-protected tray UI.
Write-Host "密钥配置已移入系统托盘：右键实时字典，选择‘配置解释模型’。"
Write-Host "此入口不读取或保存密钥；新保存的凭证使用 Windows 当前用户加密。"
& (Join-Path $PSScriptRoot "start.ps1")
