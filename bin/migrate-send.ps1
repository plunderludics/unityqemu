# D4.1 test: decompress a saved migration stream and feed it to a QEMU
# started with -incoming tcp:127.0.0.1:<Port>. Retries until QEMU is listening.
# Usage: powershell -ExecutionPolicy Bypass -File bin/migrate-send.ps1 -Port 4556 -InFile state.gz
param(
    [int]$Port = 4556,
    [string]$InFile = "state.gz"
)

$client = $null
for ($i = 0; $i -lt 50 -and -not $client; $i++) {
    try { $client = [System.Net.Sockets.TcpClient]::new("127.0.0.1", $Port) }
    catch { Start-Sleep -Milliseconds 200 }
}
if (-not $client) { throw "Could not connect to 127.0.0.1:$Port — is QEMU running with -incoming?" }

Write-Host "Connected, sending state..."
$net = $client.GetStream()
$file = [System.IO.File]::OpenRead($InFile)
$gzip = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionMode]::Decompress)
$gzip.CopyTo($net)
$net.Flush()
$client.Client.Shutdown([System.Net.Sockets.SocketShutdown]::Send)

# Drain until QEMU closes its side (migration finished loading).
$buf = New-Object byte[] 65536
while ($net.Read($buf, 0, $buf.Length) -gt 0) { }

$gzip.Dispose()
$file.Dispose()
$client.Dispose()
Write-Host "State sent — check the QEMU monitor ('info status'), then 'cont' to resume."
