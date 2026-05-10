# Start Nezumi Radio Bots (6 Units, 3 Groups)
# Make sure to fill in BOT_VCID_0 to BOT_VCID_5 in .env before running!

Write-Host "Starting 6 Nezumi Radio Bots..." -ForegroundColor Green

for ($i = 0; $i -le 5; $i++) {
    Write-Host "Starting Bot Unit $i..." -ForegroundColor Cyan
    # Use environment variable BOT_INDEX to tell each process which config to load
    $env:BOT_INDEX = $i
    Start-Process dotnet -ArgumentList "run" -NoNewWindow -Environment @{ "BOT_INDEX" = $i }
}

Write-Host "All 6 bots have been launched. Check task manager or individual logs to verify." -ForegroundColor Yellow
