# Starts the Lost & Found ML service.
# Creates the virtual environment and installs dependencies on first run.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path ".\.venv\Scripts\python.exe")) {
    Write-Host "First run - creating virtual environment..." -ForegroundColor Cyan
    python -m venv .venv
    Write-Host "Installing dependencies (this downloads ~500 MB, one time only)..." -ForegroundColor Cyan
    .\.venv\Scripts\python.exe -m pip install --upgrade pip --quiet
    .\.venv\Scripts\python.exe -m pip install -r requirements.txt
}

Write-Host ""
Write-Host "Starting ML service on http://localhost:8000" -ForegroundColor Green
Write-Host "Health check: http://localhost:8000/health" -ForegroundColor DarkGray
Write-Host "Start this BEFORE the .NET API." -ForegroundColor DarkGray
Write-Host ""

.\.venv\Scripts\python.exe main.py
