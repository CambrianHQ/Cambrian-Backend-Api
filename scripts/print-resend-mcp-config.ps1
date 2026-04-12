$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherPath = Join-Path $repoRoot "scripts/start-resend-mcp.ps1"

$config = [ordered]@{
    mcpServers = [ordered]@{
        resend = [ordered]@{
            command = "powershell"
            args = @(
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                $launcherPath
            )
            env = [ordered]@{
                RESEND_API_KEY = "re_xxxxxxxxx"
                SENDER_EMAIL_ADDRESS = "noreply@yourdomain.com"
            }
        }
    }
}

$config | ConvertTo-Json -Depth 6
