# Fails unless the file carries an Authenticode signature. With release signing the signature must also be
# trusted on a stock Windows; the test-signing certificate is not, so that run only needs a signature to be there.
param([Parameter(Mandatory)][string]$Path)

$sig = Get-AuthenticodeSignature -LiteralPath $Path
if (-not $sig.SignerCertificate) { throw "$Path is not signed" }
'{0}: {1}, {2}, timestamp {3}' -f (Split-Path $Path -Leaf), $sig.Status, $sig.SignerCertificate.Subject,
    ($(if ($sig.TimeStamperCertificate) { 'yes' } else { 'no' }))
if ($env:SIGNPATH_POLICY -ne 'test-signing' -and $sig.Status -ne 'Valid') {
    throw "$Path has signature status $($sig.Status): $($sig.StatusMessage)"
}
