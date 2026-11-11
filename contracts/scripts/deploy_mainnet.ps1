# Mainnet deploy of CMBGPassCard + HashChainRandomSource (2026-11-08 snapshot).
# The owner key is typed HERE by the user (hidden input), kept only in this shell process and removed at the end.
# Run from the Contracts folder:  & .\scripts\deploy_mainnet.ps1
$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

$env:MAINNET_CONFIRM    = "DEPLOY CMBG MAINNET AFTER AUDIT"
$env:OWNER_PLAIN_WALLET = "I ACCEPT A SINGLE-KEY OWNER"
$env:OPERATOR = "0xA5d27062FEf609627249D8F203d44A467cd45834"
$env:ROOT     = "0x934a87a0c35217806081e51b88ea35191beda727e54f9734fe13cba6df48b522"
$env:BASE_URI = "ipfs://bafybeibm6sfsow3ubaywazeshqiplb6j5zemxx4lhrxut74ldhprzoiktu/"
$env:ANCHOR   = "0x414fcc72c89a3d2968934514ed936a8b0e8b7eea52d7827f2357c866bd63392e"

Write-Host ""
Write-Host "Paste the PRIVATE KEY of the owner wallet 0x3a9D...f18f (input is hidden), then Enter:" -ForegroundColor Yellow
$secure = Read-Host -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $env:RH_MAINNET_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
}

try {
    npx hardhat run scripts/deploy.js --network robinhoodMainnet
} finally {
    Remove-Item Env:RH_MAINNET_KEY -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "Owner key removed from this shell. Close this tab when done." -ForegroundColor Green
}
