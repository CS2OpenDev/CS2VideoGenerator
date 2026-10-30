# Security Policy

## Reporting a vulnerability

Please report security issues privately through GitHub: open the repository's **Security** tab and
choose **Report a vulnerability**. Do not open a public issue for anything that could put users at
risk.

Include what you found, how to reproduce it, and which version of `Cs2VideoGenerator.Cli` /
`Cs2VideoGenerator.Core` (or which commit) is affected.

## Supported versions

Fixes land on `main` and ship in the next release. Only the latest published version is supported.

## Scope

CSVG modifies a local Counter-Strike 2 installation while it runs and loads a native plugin into
the game (see the README). Issues in that machinery, such as file backup/restore, plugin
installation, or the local gRPC endpoint, are in scope. Valve Anti-Cheat behaviour is not: CSVG
always launches CS2 with `-insecure`.
