# D4.1 test: receive a QEMU migration stream on loopback TCP, gzip it to a file.
# Usage: powershell -ExecutionPolicy Bypass -File bin/migrate-recv.ps1 -Port 4555 -OutFile state.gz
param(
    [int]$Port = 4555,
    [string]$OutFile = "state.gz"
)

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
Write-Host "Listening on 127.0.0.1:$Port — now run 'migrate tcp:127.0.0.1:$Port' in the QEMU monitor..."

$client = $listener.AcceptTcpClient()
Write-Host "QEMU connected, receiving stream..."
$net = $client.GetStream()
$file = [System.IO.File]::Create($OutFile)
$gzip = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionMode]::Compress)
$net.CopyTo($gzip)
$gzip.Dispose()
$file.Dispose()
$client.Dispose()
$listener.Stop()

Write-Host "Saved $OutFile ($([math]::Round((Get-Item $OutFile).Length / 1MB, 1)) MB compressed)"
