# Audius Genre Check Script

$genres = @(
    "Rock", "Metal", "Alternative", "Hip-Hop/Rap", "Punk", "Folk", "Pop", "Ambient", 
    "World", "Jazz", "Acoustic", "Funk", "R&B/Soul", "Devotional", "Classical", 
    "Reggae", "Country", "Blues", "Latin", "Lo-Fi", "Hyperpop", "Dancehall", 
    "Techno", "Trap", "House", "Tech House", "Deep House", "Disco", "Electro", 
    "Jungle", "Progressive House", "Hardstyle", "Glitch Hop", "Trance", 
    "Future Bass", "Future House", "Tropical House", "Downtempo", "Drum & Bass", 
    "Dubstep", "Jersey Club", "Vaporwave", "Moombahton"
)

Write-Host "Searching for Audius Discovery Node..." -ForegroundColor Cyan
try {
    $nodes = Invoke-RestMethod "https://api.audius.co"
    $node = $nodes.data[0]
    Write-Host "Using Node: $node"
} catch {
    $node = "https://discoveryprovider.audius.co"
    Write-Host "Using Fallback Node: $node"
}

Write-Host "`n--- Genre Scan Start ---"

foreach ($g in $genres) {
    $encoded = [System.Uri]::EscapeDataString($g)
    $url = "$node/v1/tracks/trending?genre=$encoded&limit=5&time=month"
    
    try {
        $res = Invoke-RestMethod $url
        $count = $res.data.Count
        if ($count -gt 0) {
            Write-Host "[OK] " -NoNewline -ForegroundColor Green
            Write-Host "$($g.PadRight(20)) : $count tracks"
        } else {
            Write-Host "[NG] " -NoNewline -ForegroundColor Red
            Write-Host "$($g.PadRight(20)) : 0 tracks"
        }
    } catch {
        Write-Host "[ERR]" -NoNewline -ForegroundColor Yellow
        Write-Host "$($g.PadRight(20)) : API Error"
    }
}

Write-Host "--- Scan Complete ---"
