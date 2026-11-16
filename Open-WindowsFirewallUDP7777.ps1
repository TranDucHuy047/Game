<#
.SYNOPSIS
Opens UDP port 7777 in Windows Defender Firewall and prints your Wi-Fi LAN IPv4 address
so your iPhone can connect directly to your Windows PC Unity Host over Wi-Fi.
#>

$ruleName = "Coral Archipelago Server UDP 7777"

Write-Host "=== Coral Archipelago Wi-Fi Co-op Network Setup ===" -ForegroundColor Cyan

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Warning "Please run PowerShell as Administrator to add the Windows Firewall rule automatically."
} else {
    $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($existing) {
        Remove-NetFirewallRule -DisplayName $ruleName
    }

    New-NetFirewallRule `
        -DisplayName $ruleName `
        -Direction Inbound `
        -Protocol UDP `
        -LocalPort 7777 `
        -Action Allow `
        -Profile Private,Public | Out-Null

    Write-Host "[OK] Opened Inbound UDP Port 7777 on Windows Firewall." -ForegroundColor Green
}

Write-Host "`nAvailable IPv4 addresses on this PC (enter the Wi-Fi IP on your iPhone):" -ForegroundColor Yellow
Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -ne "127.0.0.1" -and $_.IPAddress -notlike "169.254.*" } |
    Select-Object InterfaceAlias, IPAddress |
    Format-Table -AutoSize
