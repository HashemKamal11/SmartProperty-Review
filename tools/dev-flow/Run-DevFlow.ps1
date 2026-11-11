<#
.SYNOPSIS
  Runs the marketplace walkthrough end to end against the Development demo container, with isolated demo records.

.DESCRIPTION
  DEVELOPMENT ONLY. The API must be running with docker-compose.dev-demo.yml (demo mode), and `provision` must have been run.
  Every check prints PASS or FAIL with the expected and actual status; a summary log (no tokens, passwords or codes) is
  written to $LogPath.

  By default a new demo administrator is created for the run. With -RealAdmin the script signs in as
  admin@smartproperty.local instead and asks for its password (hidden prompt; set it first with the DevTools
  `reset-admin-password` command). Demo owners and customers get random passwords that live only in this process.

.EXAMPLE
  powershell -File tools\dev-flow\Run-DevFlow.ps1 -RealAdmin
#>
[CmdletBinding()]
param(
    [switch]$RealAdmin,
    [string]$BaseUrl = "http://localhost:5000",
    [string]$ApiContainer = "smartpropertyback-api-1",
    [string]$DbContainer = "smartpropertyback-postgres-1",
    [string]$LogPath = (Join-Path $env:TEMP ("smartproperty-dev-flow-{0}.txt" -f (Get-Date -Format "yyyyMMdd-HHmmss")))
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$base = $BaseUrl
$log = $LogPath
"" | Set-Content $log

function Invoke-Api {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [string]$Token,
        [string]$Json,
        [switch]$Quiet
    )
    $headers = @{}
    if ($Token) { $headers["Authorization"] = "Bearer $Token" }
    $p = @{ Method = $Method; Uri = "$base$Path"; Headers = $headers; UseBasicParsing = $true }
    if ($Json) { $p.ContentType = "application/json"; $p.Body = $Json }
    if ($PSVersionTable.PSVersion.Major -ge 7) { $p.SkipHttpErrorCheck = $true }

    try {
        $r = Invoke-WebRequest @p
        $status = [int]$r.StatusCode; $text = $r.Content
    } catch {
        $resp = $_.Exception.Response
        $status = [int]$resp.StatusCode
        $text = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd()
    }
    $body = $null
    if ($text) { try { $body = $text | ConvertFrom-Json } catch { $body = $text } }

    if (-not $Quiet) {
        Write-Host "HTTP $status" -ForegroundColor Cyan
        if ($body -is [string]) { Write-Host $body } elseif ($body) {
            $shown = ($text -replace '"(accessToken|refreshToken)"\s*:\s*"[^"]*"', '"$1":"<hidden>"')
            try { ($shown | ConvertFrom-Json) | ConvertTo-Json -Depth 12 } catch { Write-Host $shown }
        }
    }
    [pscustomobject]@{ Status = $status; Body = $body }
}

$script:results = @()
function Check($label, $res, [int]$expected, $codeExpected = $null) {
    $ok = ($res.Status -eq $expected)
    $actualCode = if ($res.Body -and $res.Body.PSObject.Properties["code"]) { $res.Body.code } else { "" }
    if ($ok -and $codeExpected) { $ok = ($actualCode -eq $codeExpected) }
    $line = "{0}  {1}  expected {2}{3}  actual {4}{5}" -f ($(if ($ok) { "PASS" } else { "FAIL" })), $label, $expected, $(if ($codeExpected) { " ($codeExpected)" } else { "" }), $res.Status, $(if ($actualCode) { " ($actualCode)" } else { "" })
    Write-Host $line -ForegroundColor $(if ($ok) { "Green" } else { "Red" })
    Add-Content $log $line
    $script:results += [pscustomobject]@{ Label = $label; Ok = $ok }
}

# A check on a value rather than on a response.
function Assert-That($label, [bool]$condition, $detail = "") {
    $line = "{0}  {1}{2}" -f ($(if ($condition) { "PASS" } else { "FAIL" })), $label, $(if ($detail) { "  ($detail)" } else { "" })
    Write-Host $line -ForegroundColor $(if ($condition) { "Green" } else { "Red" })
    Add-Content $log $line
    $script:results += [pscustomobject]@{ Label = $label; Ok = $condition }
}

# Development only: the verification email is written to the container log instead of being sent.
function Get-EmailCode([string]$email) {
    $text = (cmd /c "docker logs $ApiContainer 2>&1") | Out-String
    $m = [regex]::Matches($text, "(?s)Email to " + [regex]::Escape($email) + ".*?verification code is (\d{6})")
    if ($m.Count -eq 0) { throw "No verification email logged for $email" }
    $m[$m.Count - 1].Groups[1].Value
}

# Random passwords live only in this process.
function New-DemoPassword {
    $chars = [char[]]((48..57) + (65..90) + (97..122))
    (-join (1..20 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })) + "Aa1"
}

# Reads the database credentials from the container's own environment; nothing is printed.
function Invoke-Db([string]$sql) {
    $u = (docker exec $DbContainer printenv POSTGRES_USER).Trim()
    $d = (docker exec $DbContainer printenv POSTGRES_DB).Trim()
    (docker exec $DbContainer psql -U $u -d $d -Atc $sql) -join "`n"
}

function Invoke-DevTool([string[]]$ToolArgs) {
    Push-Location $repo
    try { dotnet run --project tools/SmartProperty.DevTools -- @ToolArgs --i-understand-this-is-the-development-database 2>&1 | Out-String }
    finally { Pop-Location }
}

$stamp = Get-Date -Format "MMddHHmmss"
$ownerEmail = "demo.owner.$stamp@demo.local";         $ownerPassword = New-DemoPassword
$customerEmail = "demo.customer.$stamp@demo.local";    $customerPassword = New-DemoPassword
$customer2Email = "demo.customer2.$stamp@demo.local";  $customer2Password = New-DemoPassword

if ($RealAdmin) {
    $adminEmail = "admin@smartproperty.local"
    $secure = Read-Host "Password for $adminEmail (hidden; set it with the DevTools reset-admin-password command)" -AsSecureString
    $adminPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    "Administrator: admin@smartproperty.local (real account)" | Add-Content $log
} else {
    $adminEmail = "demo.admin.$stamp@demo.local"; $adminPassword = New-DemoPassword
    $env:SP_DEMO_ACCOUNT_PASSWORD = $adminPassword
    try {
        $made = Invoke-DevTool @("create-demo-user", "--email", $adminEmail, "--first", "Demo", "--last", "Admin", "--phone", "+2019$stamp", "--role", "Platform Admin,Development Marketplace Administrator", "--password-env", "SP_DEMO_ACCOUNT_PASSWORD")
    } finally { Remove-Item Env:SP_DEMO_ACCOUNT_PASSWORD -ErrorAction SilentlyContinue }
    if ($made -notmatch "Done") { throw "Could not create the demo administrator: $made" }
    "Administrator: $adminEmail (demo account with the Platform Admin and Development Marketplace Administrator roles, the same two roles admin@smartproperty.local holds)" | Add-Content $log
}

# ---- 1. register
foreach ($a in @(@($ownerEmail, $ownerPassword, "Olivia", "Owner", "+2010$stamp"), @($customerEmail, $customerPassword, "Carla", "Customer", "+2011$stamp"), @($customer2Email, $customer2Password, "Caleb", "Customer", "+2012$stamp"))) {
    $body = @{ email = $a[0]; password = $a[1]; firstName = $a[2]; lastName = $a[3]; phoneNumber = $a[4] } | ConvertTo-Json
    $r = Invoke-Api POST "/api/auth/register" -Json $body -Quiet
    Check "1 register $($a[2])" $r 201
    if ($a[2] -eq "Olivia") { $ownerUserId = $r.Body.userId; "owner register: status=$($r.Body.status) phoneVerificationExpiresAt=$($r.Body.phoneVerificationExpiresAt)" | Add-Content $log }
    if ($a[2] -eq "Carla") { $customerUserId = $r.Body.userId }
}

# ---- 2. verify email (code from the container log), log in
foreach ($a in @(@($ownerEmail, "owner"), @($customerEmail, "customer"), @($customer2Email, "customer2"))) {
    $code = Get-EmailCode $a[0]
    $r = Invoke-Api POST "/api/auth/verify-email" -Json (@{ email = $a[0]; code = $code } | ConvertTo-Json) -Quiet
    Check "2 verify-email $($a[1])" $r 200
    "  verify-email $($a[1]): status=$($r.Body.status) emailVerified=$($r.Body.emailVerified)" | Add-Content $log
}
$ownerToken     = (Invoke-Api POST "/api/auth/login" -Json (@{ email = $ownerEmail; password = $ownerPassword } | ConvertTo-Json) -Quiet).Body.accessToken
$customerToken  = (Invoke-Api POST "/api/auth/login" -Json (@{ email = $customerEmail; password = $customerPassword } | ConvertTo-Json) -Quiet).Body.accessToken
$customer2Token = (Invoke-Api POST "/api/auth/login" -Json (@{ email = $customer2Email; password = $customer2Password } | ConvertTo-Json) -Quiet).Body.accessToken
$adminLogin = Invoke-Api POST "/api/auth/login" -Json (@{ email = $adminEmail; password = $adminPassword } | ConvertTo-Json) -Quiet
Check "2 login administrator ($adminEmail)" $adminLogin 200
$adminToken = $adminLogin.Body.accessToken
Check "2 /me owner is Active" (Invoke-Api GET "/api/auth/me" -Token $ownerToken -Quiet) 200

# ---- 3. owner profile
$fixtures = Join-Path $env:TEMP "smartproperty-fixtures"
New-Item -ItemType Directory -Force $fixtures | Out-Null
[IO.File]::WriteAllBytes("$fixtures\address-proof.png", [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="))
$nid = (Get-Random -Minimum 1000000000 -Maximum 1999999999).ToString()
$r = Invoke-Api PUT "/api/owner-profile/identity" -Token $ownerToken -Quiet -Json (@{ nationalId = $nid; nationality = "Egyptian"; birthDate = "1990-01-01"; idExpiryDate = "2031-01-01"; confirmed = $true } | ConvertTo-Json)
Check "3a identity" $r 200
$r = Invoke-Api PUT "/api/owner-profile/address" -Token $ownerToken -Quiet -Json (@{ buildingNumber = "1234"; streetName = "Example Street"; district = "Maadi"; city = "Cairo"; postalCode = "11728"; additionalNumber = "5678"; confirmed = $true } | ConvertTo-Json)
Check "3b address" $r 200
$out = curl.exe -s -w "`n%{http_code}" -X PUT "$base/api/owner-profile/address-proof" -H "Authorization: Bearer $ownerToken" -F "file=@$fixtures\address-proof.png;type=image/png"
$lines = $out -split "`n"
Check "3c address proof (multipart)" ([pscustomobject]@{ Status = [int]$lines[-1]; Body = $null }) 200
$r = Invoke-Api POST "/api/owner-profile/submit" -Token $ownerToken -Quiet
Check "3d submit" $r 200
"  profile status after submit: $($r.Body.status)" | Add-Content $log
$profileId = (Invoke-Api GET "/api/owner-profile" -Token $ownerToken -Quiet).Body.profileId

# ---- 4. administrator reviews
Check "V1 customer cannot approve owner profile" (Invoke-Api POST "/api/admin/owner-verifications/$profileId/approve" -Token $customerToken -Quiet) 403
Check "V1 owner cannot approve own profile" (Invoke-Api POST "/api/admin/owner-verifications/$profileId/approve" -Token $ownerToken -Quiet) 403
$q = Invoke-Api GET "/api/admin/owner-verifications?status=UnderReview" -Token $adminToken -Quiet
Check "4 queue" $q 200
$r = Invoke-Api POST "/api/admin/owner-verifications/$profileId/approve" -Token $adminToken -Quiet
Check "4 approve owner profile" $r 200
"  profile status: $($r.Body.status) canAddProperties=$($r.Body.canAddProperties)" | Add-Content $log

# ---- 5. property
$areaId = "61cfc4ca-ad5f-7ea2-673c-72af05ae17c1"
$property = @{ type = "Villa"; areaId = $areaId; addressLine = "12 Demo Street"; postalCode = "11728"; bedrooms = 4; bathrooms = 3; areaSqm = 320.5; furnished = $false; amenityCodes = @("AirConditioning", "Balcony"); viewCodes = @("Garden") } | ConvertTo-Json
Check "5 customer cannot create a property" (Invoke-Api POST "/api/properties" -Token $customerToken -Json $property -Quiet) 403
$r = Invoke-Api POST "/api/properties" -Token $ownerToken -Json $property -Quiet
Check "5 create property" $r 201
$propertyId = $r.Body.id
"  propertyId=$propertyId timeZone=$($r.Body.physical.timeZoneId)" | Add-Content $log

# ---- 7 (before grants): a draft is refused without a grant
$saleContent = @{ title = "Demo villa for sale"; description = "Four bedrooms, garden."; amount = 5000000; currency = "EGP"; evidenceIds = @() }
$r = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Sale"; content = $saleContent } | ConvertTo-Json -Depth 6)
Check "7 draft without a grant is refused" $r 403 "listing_submissions.not_eligible"

# ---- 6. grants through the Development tool
Push-Location $repo
$grantOutput = dotnet run --project tools/SmartProperty.DevTools -- issue-grants --i-understand-this-is-the-development-database --property $propertyId --owner $ownerEmail --purposes Sale,Rent 2>&1 | Out-String
Pop-Location
Write-Host $grantOutput
$grantOutput | Add-Content $log
if ($grantOutput -notmatch "Issued a Sale grant" -or $grantOutput -notmatch "Issued a Rent grant") { throw "Grant issuing failed" }

# ---- 8. evidence fixtures
function Register-Fixture($token, $propertyId, $path, $type) {
    $out = curl.exe -s -w "`n%{http_code}" -X POST "$base/api/development/evidence" -H "Authorization: Bearer $token" -F "propertyId=$propertyId" -F "file=@$path;type=$type"
    $parts = $out -split "`n"
    [pscustomobject]@{ Status = [int]$parts[-1]; Body = ($parts[0..($parts.Length - 2)] -join "`n" | ConvertFrom-Json) }
}
Set-Content "$fixtures\title-deed.txt" "Demo title deed. Simulated fixture, not a real document."
Set-Content "$fixtures\bad-scan.txt" "Demo document that carries the marker SMARTPROPERTY-DEMO-REJECT-ME so the simulated scan rejects it."
$fx = Register-Fixture $ownerToken $propertyId "$fixtures\title-deed.txt" "text/plain"
Check "8 register evidence fixture" $fx 201
$evidenceId = $fx.Body.evidenceId
"  fixture label: $($fx.Body.label)" | Add-Content $log
$bad = Register-Fixture $ownerToken $propertyId "$fixtures\bad-scan.txt" "text/plain"
Check "8 register the reject-marker fixture" $bad 201
$badEvidenceId = $bad.Body.evidenceId
$fxOther = Register-Fixture $customerToken $propertyId "$fixtures\title-deed.txt" "text/plain"
$customerEvidenceId = $fxOther.Body.evidenceId

$rentContent = @{
    title = "Demo villa for rent"; description = "Furnished, garden."; evidenceIds = @($evidenceId)
    pricingOptions = @(
        @{ period = "Daily"; amount = 1500; currency = "EGP" }, @{ period = "Weekly"; amount = 9000; currency = "EGP" },
        @{ period = "Monthly"; amount = 30000; currency = "EGP" }, @{ period = "Yearly"; amount = 320000; currency = "EGP" })
    rentalTerms = @{ checkInTime = "14:00:00"; checkOutTime = "11:00:00"; minimumStayDays = 1; windows = @(@{ startDate = "2027-06-01"; endDate = "2027-09-01" }) }
}
$saleContent.evidenceIds = @($evidenceId)

# unknown / foreign evidence must fail
$unknownSale = $saleContent.Clone(); $unknownSale.evidenceIds = @([guid]::NewGuid().ToString())
Check "8 unknown evidence id fails" (Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Sale"; content = $unknownSale } | ConvertTo-Json -Depth 6)) 422
$foreignSale = $saleContent.Clone(); $foreignSale.evidenceIds = @($customerEvidenceId)
Check "8 another user's evidence id fails" (Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Sale"; content = $foreignSale } | ConvertTo-Json -Depth 6)) 422

# ---- 7. Both
$both = Invoke-Api POST "/api/marketplace/me/properties/$propertyId/listing-drafts" -Token $ownerToken -Quiet -Json (@{ sale = $saleContent; rent = $rentContent } | ConvertTo-Json -Depth 8)
Check "7 Both drafts" $both 201
$saleSubmissionId = $both.Body.sale.submissionId; $saleListingId = $both.Body.sale.listingId
$rentSubmissionId = $both.Body.rent.submissionId; $rentListingId = $both.Body.rent.listingId
$noEvidenceSale = $saleContent.Clone(); $noEvidenceSale.evidenceIds = @()
Check "7 customer cannot draft (no grant)" (Invoke-Api POST "/api/marketplace/me/properties/$propertyId/listing-drafts" -Token $customerToken -Quiet -Json (@{ sale = $noEvidenceSale } | ConvertTo-Json -Depth 6)) 403 "listing_submissions.not_eligible"

# reject-marker fixture: submitted, then the review refuses it
$badRent = $rentContent.Clone(); $badRent.evidenceIds = @($badEvidenceId)

foreach ($s in @(@($saleSubmissionId, "Sale"), @($rentSubmissionId, "Rent"))) {
    $r = Invoke-Api POST "/api/marketplace/me/listing-submissions/$($s[0])/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = 1 } | ConvertTo-Json)
    Check "8 submit $($s[1])" $r 200
    "  $($s[1]) status: $($r.Body.status)" | Add-Content $log
}

# ---- 9. review
Check "V2 customer cannot publish" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$rentSubmissionId/approve" -Token $customerToken -Quiet) 403 "authorization.forbidden"
Check "V4 owner cannot review own listing" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$rentSubmissionId/approve" -Token $ownerToken -Quiet) 403 "authorization.forbidden"
Check "9 queue" (Invoke-Api GET "/api/admin/marketplace/listing-submissions" -Token $adminToken -Quiet) 200
$r = Invoke-Api POST "/api/admin/marketplace/listing-submissions/$rentSubmissionId/approve" -Token $adminToken -Quiet
Check "9 approve Rent" $r 200
$r = Invoke-Api POST "/api/admin/marketplace/listing-submissions/$saleSubmissionId/approve" -Token $adminToken -Quiet
Check "9 approve Sale" $r 200
Check "9 decided twice" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$saleSubmissionId/approve" -Token $adminToken -Quiet) 409 "listing_submissions.not_pending_review"

# ---- 10. customer browses and requests
$d = Invoke-Api GET "/api/public/marketplace/listings/$rentListingId" -Quiet
Check "10 public rent detail" $d 200
"  options: " + (($d.Body.pricingOptions | ForEach-Object { "$($_.rentPeriod)=$($_.amount) $($_.currency)" }) -join ", ") + " | terms tz=$($d.Body.rentalTerms.timeZoneId) in=$($d.Body.rentalTerms.checkInTime)" | Add-Content $log
Check "10 search by Daily price" (Invoke-Api GET "/api/public/marketplace/listings?purpose=Rent&rentPeriod=Daily&minPrice=1000&maxPrice=2000&currency=EGP" -Quiet) 200
Check "10 search by availability" (Invoke-Api GET "/api/public/marketplace/listings?purpose=Rent&availableFrom=2027-06-10&availableTo=2027-06-15" -Quiet) 200
Check "10 own listing request refused" (Invoke-Api POST "/api/marketplace/rental-requests" -Token $ownerToken -Quiet -Json (@{ listingId = $rentListingId; pricingPeriod = "Daily"; startDate = "2027-06-10"; endDate = "2027-06-15" } | ConvertTo-Json)) 403 "marketplace_requests.own_listing"
$rr = Invoke-Api POST "/api/marketplace/rental-requests" -Token $customerToken -Quiet -Json (@{ listingId = $rentListingId; pricingPeriod = "Daily"; startDate = "2027-06-10"; endDate = "2027-06-15"; message = "Five nights in June." } | ConvertTo-Json)
Check "10 rental request" $rr 201
$rentalRequestId = $rr.Body.requestId
$rv = (Invoke-Api GET "/api/marketplace/me/rental-requests/$rentalRequestId" -Token $customerToken -Quiet).Body
"  rental request: $($rv.amount) $($rv.currency) per $($rv.pricingPeriod), $($rv.startDate) to $($rv.endDate), check-in $($rv.checkInTime) $($rv.timeZoneId)" | Add-Content $log
$rr2 = Invoke-Api POST "/api/marketplace/rental-requests" -Token $customer2Token -Quiet -Json (@{ listingId = $rentListingId; pricingPeriod = "Daily"; startDate = "2027-06-12"; endDate = "2027-06-18" } | ConvertTo-Json)
Check "10 competing overlapping rental request (pending)" $rr2 201
$competingRentalId = $rr2.Body.requestId
$pr = Invoke-Api POST "/api/marketplace/purchase-requests" -Token $customerToken -Quiet -Json (@{ listingId = $saleListingId; offeredAmount = 4900000; message = "Cash buyer." } | ConvertTo-Json)
Check "10 purchase request" $pr 201
$purchaseRequestId = $pr.Body.requestId
Check "10 customer cannot read another's request" (Invoke-Api GET "/api/marketplace/me/rental-requests/$competingRentalId" -Token $customerToken -Quiet) 404

# ---- 11. owner accepts
$rec = Invoke-Api GET "/api/marketplace/owner/requests?status=Submitted" -Token $ownerToken -Quiet
Check "11 received requests" $rec 200
"  received open requests: $($rec.Body.totalCount) (requesterName present: $([bool]$rec.Body.items[0].requesterName))" | Add-Content $log
Check "V3 customer cannot accept" (Invoke-Api POST "/api/marketplace/owner/rental-requests/$rentalRequestId/accept" -Token $customerToken -Quiet) 404
$acc = Invoke-Api POST "/api/marketplace/owner/rental-requests/$rentalRequestId/accept" -Token $ownerToken -Quiet
Check "11 accept rental" $acc 200
$commitmentId = $acc.Body.commitmentId
"  signingDeadline - acceptedAt: " + ([datetimeoffset]$acc.Body.signingDeadline - [datetimeoffset]$acc.Body.acceptedAt).TotalHours + " hours" | Add-Content $log
Check "V6 rent ad closed" (Invoke-Api GET "/api/public/marketplace/listings/$rentListingId" -Quiet) 404
Check "V6 sale ad closed" (Invoke-Api GET "/api/public/marketplace/listings/$saleListingId" -Quiet) 404
$c1 = (Invoke-Api GET "/api/marketplace/me/rental-requests/$competingRentalId" -Token $customer2Token -Quiet).Body.status
$c2 = (Invoke-Api GET "/api/marketplace/me/purchase-requests/$purchaseRequestId" -Token $customerToken -Quiet).Body.status
"  competing rental: $c1 | purchase: $c2" | Add-Content $log
Check "V6 competing rental closed" ([pscustomobject]@{ Status = $(if ($c1 -eq "ClosedListingUnavailable") { 200 } else { 0 }); Body = $null }) 200
Check "V6 purchase request closed" ([pscustomobject]@{ Status = $(if ($c2 -eq "ClosedListingUnavailable") { 200 } else { 0 }); Body = $null }) 200

# ---- 12. signing, activation
Check "12 customer cannot sign" (Invoke-Api POST "/api/marketplace/me/commitments/$commitmentId/confirm-signed" -Token $customerToken -Quiet) 404
Check "12 handover before signature is refused" (Invoke-Api POST "/api/marketplace/me/commitments/$commitmentId/confirm-handover" -Token $ownerToken -Quiet) 409 "marketplace_commitments.step_not_allowed"
Check "12 owner confirms signed" (Invoke-Api POST "/api/marketplace/me/commitments/$commitmentId/confirm-signed" -Token $ownerToken -Quiet) 200
$h = Invoke-Api POST "/api/marketplace/me/commitments/$commitmentId/confirm-handover" -Token $ownerToken -Quiet
Check "12 owner confirms handover (activation)" $h 200
"  commitment status after handover: $($h.Body.status)" | Add-Content $log
Check "12 customer reads the commitment" (Invoke-Api GET "/api/marketplace/me/commitments/$commitmentId" -Token $customerToken -Quiet) 200

# ---- V7a: a Daily rental in force blocks a sale
$saleContent2 = $saleContent.Clone()
$sd = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Sale"; content = $saleContent2 } | ConvertTo-Json -Depth 6)
Check "V7a re-advertise Sale: new draft" $sd 201
"  sale listing reused: $($sd.Body.listingId -eq $saleListingId) version=$($sd.Body.version)" | Add-Content $log
$saleV2 = $sd.Body.submissionId
Check "V7a submit Sale v2" (Invoke-Api POST "/api/marketplace/me/listing-submissions/$saleV2/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = 1 } | ConvertTo-Json)) 200
Check "V7a approve Sale blocked by the Daily occupancy" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$saleV2/approve" -Token $adminToken -Quiet) 409 "listing_submissions.short_stay_occupancy"

# ---- 13. return
Check "13 customer cannot confirm return" (Invoke-Api POST "/api/marketplace/me/commitments/$commitmentId/confirm-return" -Token $customerToken -Quiet) 404
$ret = Invoke-Api POST "/api/marketplace/me/commitments/$commitmentId/confirm-return" -Token $ownerToken -Quiet
Check "13 owner confirms return" $ret 200
"  commitment status after return: $($ret.Body.status)" | Add-Content $log
Check "V7a approve Sale after the return" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$saleV2/approve" -Token $adminToken -Quiet) 200

# ---- 14. re-advertise the Rent listing for the same property
$rd = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Rent"; content = $rentContent } | ConvertTo-Json -Depth 8)
Check "14 re-advertise Rent: new draft" $rd 201
"  rent listing reused: $($rd.Body.listingId -eq $rentListingId) version=$($rd.Body.version)" | Add-Content $log
$rentV2 = $rd.Body.submissionId
Check "14 submit Rent v2" (Invoke-Api POST "/api/marketplace/me/listing-submissions/$rentV2/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = 1 } | ConvertTo-Json)) 200
Check "14 approve Rent v2" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$rentV2/approve" -Token $adminToken -Quiet) 200
$prop = (Invoke-Api GET "/api/properties/$propertyId" -Quiet).Body
"  property after re-advertising: id=$($prop.id) status=$($prop.status) bedrooms=$($prop.bedrooms)" | Add-Content $log
$hist = (Invoke-Api GET "/api/marketplace/me/listing-submissions?purpose=Rent" -Token $ownerToken -Quiet).Body
"  rent history: " + (($hist.items | ForEach-Object { "v$($_.version)=$($_.status)" }) -join ", ") | Add-Content $log

# V-E: the reject-marker fixture is saved with a draft, but the submission is refused; the draft is then discarded
$badSale = $saleContent.Clone(); $badSale.evidenceIds = @($badEvidenceId)
$bs = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Sale"; content = $badSale } | ConvertTo-Json -Depth 6)
Check "V-E draft with the reject-marker fixture is saved" $bs 201
Check "V-E submitting it is refused (the simulated scan rejected the fixture)" (Invoke-Api POST "/api/marketplace/me/listing-submissions/$($bs.Body.submissionId)/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = 1 } | ConvertTo-Json)) 409 "listing_submissions.prerequisites_unverified"
Check "V-E the refused draft can be discarded" (Invoke-Api DELETE "/api/marketplace/me/listing-submissions/$($bs.Body.submissionId)?expectedRevision=1" -Token $ownerToken -Quiet) 200

# ---- V7b / V5: a Monthly rental, overlap, back-to-back, sale preserved
$m = Invoke-Api POST "/api/marketplace/rental-requests" -Token $customerToken -Quiet -Json (@{ listingId = $rentListingId; pricingPeriod = "Monthly"; startDate = "2027-07-01"; endDate = "2027-08-01" } | ConvertTo-Json)
Check "V7b Monthly rental request" $m 201
$accM = Invoke-Api POST "/api/marketplace/owner/rental-requests/$($m.Body.requestId)/accept" -Token $ownerToken -Quiet
Check "V7b accept Monthly rental" $accM 200
$monthlyCommitment = $accM.Body.commitmentId
Check "V7b sign Monthly commitment" (Invoke-Api POST "/api/marketplace/me/commitments/$monthlyCommitment/confirm-signed" -Token $ownerToken -Quiet) 200

# re-advertise Rent (v3) and test overlap / back-to-back
$rd3 = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Rent"; content = $rentContent } | ConvertTo-Json -Depth 8)
Check "V5 re-advertise Rent v3" $rd3 201
Check "V5 submit Rent v3" (Invoke-Api POST "/api/marketplace/me/listing-submissions/$($rd3.Body.submissionId)/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = 1 } | ConvertTo-Json)) 200
Check "V5 approve Rent v3" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$($rd3.Body.submissionId)/approve" -Token $adminToken -Quiet) 200
Check "V5 overlapping request rejected" (Invoke-Api POST "/api/marketplace/rental-requests" -Token $customer2Token -Quiet -Json (@{ listingId = $rentListingId; pricingPeriod = "Daily"; startDate = "2027-07-15"; endDate = "2027-07-20" } | ConvertTo-Json)) 409 "marketplace_requests.dates_unavailable"
$b2b = Invoke-Api POST "/api/marketplace/rental-requests" -Token $customer2Token -Quiet -Json (@{ listingId = $rentListingId; pricingPeriod = "Daily"; startDate = "2027-08-01"; endDate = "2027-08-05" } | ConvertTo-Json)
Check "V5 back-to-back request allowed" $b2b 201
$accB = Invoke-Api POST "/api/marketplace/owner/rental-requests/$($b2b.Body.requestId)/accept" -Token $ownerToken -Quiet
Check "V5 accept the non-overlapping rental" $accB 200
# The accepted Daily rental is occupancy, so a sale could not be published while it stands. Run it through to the return
# so the sale rules for the Monthly rental alone can be shown next.
$bCommitment = $accB.Body.commitmentId
Check "V5 sign the back-to-back commitment" (Invoke-Api POST "/api/marketplace/me/commitments/$bCommitment/confirm-signed" -Token $ownerToken -Quiet) 200
Check "V5 hand over the back-to-back rental" (Invoke-Api POST "/api/marketplace/me/commitments/$bCommitment/confirm-handover" -Token $ownerToken -Quiet) 200
Check "V5 return the back-to-back rental" (Invoke-Api POST "/api/marketplace/me/commitments/$bCommitment/confirm-return" -Token $ownerToken -Quiet) 200
$mc = (Invoke-Api GET "/api/marketplace/me/commitments/$monthlyCommitment" -Token $customerToken -Quiet).Body
"  Monthly commitment after the later acceptance: status=$($mc.status) dates=$($mc.startDate)..$($mc.endDate)" | Add-Content $log
Check "V6 earlier signed commitment preserved" ([pscustomobject]@{ Status = $(if ($mc.status -eq "Signed") { 200 } else { 0 }); Body = $null }) 200

# Sale with a Monthly rental in force
$sd3 = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Sale"; content = $saleContent } | ConvertTo-Json -Depth 6)
Check "V7b re-advertise Sale v3" $sd3 201
Check "V7b submit Sale v3" (Invoke-Api POST "/api/marketplace/me/listing-submissions/$($sd3.Body.submissionId)/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = 1 } | ConvertTo-Json)) 200
Check "V7b approve Sale despite a Monthly rental" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$($sd3.Body.submissionId)/approve" -Token $adminToken -Quiet) 200
$pr2 = Invoke-Api POST "/api/marketplace/purchase-requests" -Token $customerToken -Quiet -Json (@{ listingId = $saleListingId; offeredAmount = 4800000 } | ConvertTo-Json)
Check "V7b purchase request" $pr2 201
Check "V7b accept the sale beside the rentals" (Invoke-Api POST "/api/marketplace/owner/purchase-requests/$($pr2.Body.requestId)/accept" -Token $ownerToken -Quiet) 200
$mc2 = (Invoke-Api GET "/api/marketplace/me/commitments/$monthlyCommitment" -Token $customerToken -Quiet).Body
Check "V7b Monthly rental preserved by the sale" ([pscustomobject]@{ Status = $(if ($mc2.status -eq "Signed") { 200 } else { 0 }); Body = $null }) 200

# V7c: a sale in force blocks publishing
$rd4 = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propertyId; purpose = "Rent"; content = $rentContent } | ConvertTo-Json -Depth 8)
Check "V7c re-advertise Rent v4" $rd4 201
Check "V7c submit Rent v4" (Invoke-Api POST "/api/marketplace/me/listing-submissions/$($rd4.Body.submissionId)/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = 1 } | ConvertTo-Json)) 200
Check "V7c a sale in force blocks publishing" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$($rd4.Body.submissionId)/approve" -Token $adminToken -Quiet) 409 "listing_submissions.property_committed"


# =====================================================================================================================
# PART B. A second property for the paths the main flow does not take: draft editing, listing rejection, withdrawal,
# request withdrawal/rejection, the signing window and the deadlines already given to commitments.
# =====================================================================================================================
function Get-Revision($r) { if ($r.Body.PSObject.Properties["revision"]) { [int]$r.Body.revision } else { 1 } }

function Publish-Rent($propId, $content, $label) {
    $d = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $propId; purpose = "Rent"; content = $content } | ConvertTo-Json -Depth 8)
    Check "$label draft" $d 201
    Check "$label submit" (Invoke-Api POST "/api/marketplace/me/listing-submissions/$($d.Body.submissionId)/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = (Get-Revision $d) } | ConvertTo-Json)) 200
    Check "$label approve" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$($d.Body.submissionId)/approve" -Token $adminToken -Quiet) 200
    $d
}

$p2 = Invoke-Api POST "/api/properties" -Token $ownerToken -Json $property -Quiet
Check "B create the second property" $p2 201
$property2Id = $p2.Body.id
$grants2 = Invoke-DevTool @("issue-grants", "--property", $property2Id, "--owner", $ownerEmail, "--purposes", "Sale,Rent")
Add-Content $log $grants2
Assert-That "B grants issued for the second property" (($grants2 -match "Issued a Sale grant") -and ($grants2 -match "Issued a Rent grant"))
$fx2 = Register-Fixture $ownerToken $property2Id "$fixtures\title-deed.txt" "text/plain"
Check "B register evidence for the second property" $fx2 201
$rent2 = $rentContent.Clone(); $rent2.evidenceIds = @($fx2.Body.evidenceId)

# ---- draft editing
$d1 = Invoke-Api POST "/api/marketplace/me/listing-submissions" -Token $ownerToken -Quiet -Json (@{ propertyId = $property2Id; purpose = "Rent"; content = $rent2 } | ConvertTo-Json -Depth 8)
Check "E create a Rent draft" $d1 201
$d1Id = $d1.Body.submissionId; $d1Listing = $d1.Body.listingId; $rev1 = Get-Revision $d1
$edited = $rent2.Clone()
$edited.title = "Edited title for the demo rental"; $edited.description = "Edited description."
$edited.pricingOptions = @(@{ period = "Daily"; amount = 1800; currency = "EGP" }, @{ period = "Monthly"; amount = 32000; currency = "EGP" })
$u = Invoke-Api PUT "/api/marketplace/me/listing-submissions/$d1Id" -Token $ownerToken -Quiet -Json (@{ expectedRevision = $rev1; content = $edited } | ConvertTo-Json -Depth 8)
Check "E edit the draft" $u 200
$rev2 = Get-Revision $u
Assert-That "E the revision moved on" ($rev2 -gt $rev1) "revision $rev1 -> $rev2"
Check "E editing again with the stale revision is refused" (Invoke-Api PUT "/api/marketplace/me/listing-submissions/$d1Id" -Token $ownerToken -Quiet -Json (@{ expectedRevision = $rev1; content = $edited } | ConvertTo-Json -Depth 8)) 409 "listing_submissions.stale_revision"
$read = Invoke-Api GET "/api/marketplace/me/listing-submissions/$d1Id" -Token $ownerToken -Quiet
Check "E read the edited draft" $read 200
$readText = $read.Body | ConvertTo-Json -Depth 12
Assert-That "E the edit is stored (title and description)" (($readText -match "Edited title for the demo rental") -and ($readText -match "Edited description"))
Check "E another user cannot edit it" (Invoke-Api PUT "/api/marketplace/me/listing-submissions/$d1Id" -Token $customerToken -Quiet -Json (@{ expectedRevision = $rev2; content = $edited } | ConvertTo-Json -Depth 8)) 404
Check "E submit the edited draft" (Invoke-Api POST "/api/marketplace/me/listing-submissions/$d1Id/submit" -Token $ownerToken -Quiet -Json (@{ expectedRevision = $rev2 } | ConvertTo-Json)) 200
$afterSubmit = Invoke-Api PUT "/api/marketplace/me/listing-submissions/$d1Id" -Token $ownerToken -Quiet -Json (@{ expectedRevision = $rev2; content = $edited } | ConvertTo-Json -Depth 8)
Assert-That "E a submitted version can no longer be edited" ($afterSubmit.Status -ge 400 -and $afterSubmit.Status -lt 500) "status $($afterSubmit.Status) $($afterSubmit.Body.code)"

# ---- listing rejection
Check "R reject without a reason is refused" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$d1Id/reject" -Token $adminToken -Quiet -Json (@{ reason = "" } | ConvertTo-Json)) 422
Check "R the owner cannot reject" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$d1Id/reject" -Token $ownerToken -Quiet -Json (@{ reason = "no" } | ConvertTo-Json)) 403 "authorization.forbidden"
$rej = Invoke-Api POST "/api/admin/marketplace/listing-submissions/$d1Id/reject" -Token $adminToken -Quiet -Json (@{ reason = "Photos do not match the property." } | ConvertTo-Json)
Check "R reject with a reason" $rej 200
"  listing rejection: status=$($rej.Body.status)" | Add-Content $log
Assert-That "R the decision reads Rejected" ("$($rej.Body.status)" -eq "Rejected")
Check "R a rejected listing is not public" (Invoke-Api GET "/api/public/marketplace/listings/$d1Listing" -Quiet) 404
Check "R it cannot be decided again" (Invoke-Api POST "/api/admin/marketplace/listing-submissions/$d1Id/approve" -Token $adminToken -Quiet) 409 "listing_submissions.not_pending_review"
$rejRead = Invoke-Api GET "/api/marketplace/me/listing-submissions/$d1Id" -Token $ownerToken -Quiet
Assert-That "R the owner sees the rejection and its reason" (($rejRead.Body | ConvertTo-Json -Depth 12) -match "Rejected" -and ($rejRead.Body | ConvertTo-Json -Depth 12) -match "Photos do not match")

# after a rejection a new version can be drafted for the same listing and published
$v2 = Publish-Rent $property2Id $edited "R after rejection: new version (carrying the edited content)"
Assert-That "R the new version is the same listing" ($v2.Body.listingId -eq $d1Listing)
$pub = Invoke-Api GET "/api/public/marketplace/listings/$d1Listing" -Quiet
Check "R the listing is now public" $pub 200
$opts = ($pub.Body.pricingOptions | Sort-Object rentPeriod | ForEach-Object { "$($_.rentPeriod)=$([decimal]$_.amount)" }) -join ","
"  published price options after the edit: $opts" | Add-Content $log
Assert-That "R only the two edited price options are published (Daily 1800, Monthly 32000)" ($opts -eq "Daily=1800,Monthly=32000")
$hist2 = (Invoke-Api GET "/api/marketplace/me/listing-submissions?purpose=Rent" -Token $ownerToken -Quiet).Body
"  second property history: " + (($hist2.items | Where-Object { $_.listingId -eq $d1Listing } | ForEach-Object { "v$($_.version)=$($_.status)" }) -join ", ") | Add-Content $log

# ---- withdrawal of a published listing
Check "W a customer cannot withdraw it" (Invoke-Api POST "/api/marketplace/me/listings/$d1Listing/withdraw" -Token $customerToken -Quiet) 404
Check "W the owner withdraws it" (Invoke-Api POST "/api/marketplace/me/listings/$d1Listing/withdraw" -Token $ownerToken -Quiet) 200
Check "W a withdrawn listing is not public" (Invoke-Api GET "/api/public/marketplace/listings/$d1Listing" -Quiet) 404
Check "W withdrawing again is refused" (Invoke-Api POST "/api/marketplace/me/listings/$d1Listing/withdraw" -Token $ownerToken -Quiet) 409
Check "W a request on a withdrawn listing is refused" (Invoke-Api POST "/api/marketplace/rental-requests" -Token $customerToken -Quiet -Json (@{ listingId = $d1Listing; pricingPeriod = "Daily"; startDate = "2027-06-02"; endDate = "2027-06-04" } | ConvertTo-Json)) 404 "marketplace_requests.listing_unavailable"
$v3 = Publish-Rent $property2Id $rent2 "W republish from a version submitted after the withdrawal"
Check "W public again after republishing" (Invoke-Api GET "/api/public/marketplace/listings/$d1Listing" -Quiet) 200

# ---- request withdrawal and rejection
$q1 = Invoke-Api POST "/api/marketplace/rental-requests" -Token $customerToken -Quiet -Json (@{ listingId = $d1Listing; pricingPeriod = "Daily"; startDate = "2027-06-02"; endDate = "2027-06-04" } | ConvertTo-Json)
Check "Q customer requests a rental" $q1 201
Check "Q customer withdraws it" (Invoke-Api POST "/api/marketplace/me/rental-requests/$($q1.Body.requestId)/withdraw" -Token $customerToken -Quiet) 200
Check "Q withdrawing again is refused" (Invoke-Api POST "/api/marketplace/me/rental-requests/$($q1.Body.requestId)/withdraw" -Token $customerToken -Quiet) 409 "marketplace_requests.not_open"
$q2 = Invoke-Api POST "/api/marketplace/rental-requests" -Token $customerToken -Quiet -Json (@{ listingId = $d1Listing; pricingPeriod = "Daily"; startDate = "2027-06-02"; endDate = "2027-06-04" } | ConvertTo-Json)
Check "Q customer can request again after withdrawing" $q2 201
Check "Q a customer cannot reject a request" (Invoke-Api POST "/api/marketplace/owner/rental-requests/$($q2.Body.requestId)/reject" -Token $customerToken -Quiet) 404
Check "Q the owner rejects it" (Invoke-Api POST "/api/marketplace/owner/rental-requests/$($q2.Body.requestId)/reject" -Token $ownerToken -Quiet) 200
$q2Read = (Invoke-Api GET "/api/marketplace/me/rental-requests/$($q2.Body.requestId)" -Token $customerToken -Quiet).Body
"  rejected request status: $($q2Read.status)" | Add-Content $log
Assert-That "Q the customer sees it as rejected" ("$($q2Read.status)" -eq "Rejected")
Check "Q a rejection does not close the listing" (Invoke-Api GET "/api/public/marketplace/listings/$d1Listing" -Quiet) 200

# ---- the signing window, and deadlines already given
$w0 = Invoke-Api GET "/api/admin/marketplace/settings/signing-window" -Token $adminToken -Quiet
Check "S read the signing window" $w0 200
$hoursBefore = [int]$w0.Body.hours
"  signing window before: hours=$hoursBefore default=$($w0.Body.isDefault)" | Add-Content $log

$reqA = Invoke-Api POST "/api/marketplace/rental-requests" -Token $customerToken -Quiet -Json (@{ listingId = $d1Listing; pricingPeriod = "Daily"; startDate = "2027-06-10"; endDate = "2027-06-12" } | ConvertTo-Json)
Check "S request A" $reqA 201
$accA = Invoke-Api POST "/api/marketplace/owner/rental-requests/$($reqA.Body.requestId)/accept" -Token $ownerToken -Quiet
Check "S accept A (deadline given under the old window)" $accA 200
$commitA = $accA.Body.commitmentId
$deadlineA = [datetimeoffset]$accA.Body.signingDeadline
$hoursA = ($deadlineA - [datetimeoffset]$accA.Body.acceptedAt).TotalHours
# The accept response carries 100 ns precision; PostgreSQL keeps microseconds. So "did not move" is judged against what is stored.
$persistedA = [datetimeoffset](Invoke-Api GET "/api/marketplace/me/commitments/$commitA" -Token $customerToken -Quiet).Body.signingDeadline
Assert-That "S the stored deadline equals the one given at acceptance (to the microsecond)" ([math]::Abs(($persistedA - $deadlineA).TotalMilliseconds) -lt 1)
Assert-That "S A was given the window in force when it was accepted" ([math]::Abs($hoursA - $hoursBefore) -lt 0.01) "$hoursA hours"

# a snapshot of every commitment and reservation that exists right now
$ids = Invoke-Db "select string_agg(quote_literal(id::text), ',') from marketplace.property_commitments"
$snapSql = "select md5(coalesce(string_agg(t::text, ',' order by t.id::text), '')) from marketplace.property_commitments t where t.id::text in ($ids)"
$resSql = "select md5(coalesce(string_agg(t::text, ',' order by t.property_id::text), '')) from marketplace.property_reservations t"
$commitCount = [int](Invoke-Db "select count(*) from marketplace.property_commitments")
$snapBefore = Invoke-Db $snapSql
$resBefore = Invoke-Db $resSql
$pendingBefore = Invoke-Db "select count(*) from marketplace.property_commitments where status = 'PendingSignature'"
"  snapshot: $commitCount commitments ($pendingBefore pending signature), legacy reservations hashed" | Add-Content $log

Check "S owner cannot change the window" (Invoke-Api PUT "/api/admin/marketplace/settings/signing-window" -Token $ownerToken -Quiet -Json (@{ hours = 24 } | ConvertTo-Json)) 403
Check "S customer cannot change the window" (Invoke-Api PUT "/api/admin/marketplace/settings/signing-window" -Token $customerToken -Quiet -Json (@{ hours = 24 } | ConvertTo-Json)) 403
Check "S zero hours is refused" (Invoke-Api PUT "/api/admin/marketplace/settings/signing-window" -Token $adminToken -Quiet -Json (@{ hours = 0 } | ConvertTo-Json)) 422
Check "S 721 hours is refused" (Invoke-Api PUT "/api/admin/marketplace/settings/signing-window" -Token $adminToken -Quiet -Json (@{ hours = 721 } | ConvertTo-Json)) 422
Check "S a missing value is refused" (Invoke-Api PUT "/api/admin/marketplace/settings/signing-window" -Token $adminToken -Quiet -Json "{}") 422
Assert-That "S refused changes left the window alone" ([int](Invoke-Api GET "/api/admin/marketplace/settings/signing-window" -Token $adminToken -Quiet).Body.hours -eq $hoursBefore)

$set24 = Invoke-Api PUT "/api/admin/marketplace/settings/signing-window" -Token $adminToken -Quiet -Json (@{ hours = 24 } | ConvertTo-Json)
Check "S change the window to 24 hours" $set24 200
Assert-That "S the window reads 24 and is no longer the default" (([int]$set24.Body.hours -eq 24) -and ($set24.Body.isDefault -eq $false))
$cA1 = (Invoke-Api GET "/api/marketplace/me/commitments/$commitA" -Token $customerToken -Quiet).Body
Assert-That "S A's deadline did not move" ([datetimeoffset]$cA1.signingDeadline -eq $persistedA) "$($cA1.signingDeadline)"
Assert-That "S every existing commitment row is byte-for-byte unchanged" ((Invoke-Db $snapSql) -eq $snapBefore)
Assert-That "S the legacy reservations are unchanged" ((Invoke-Db $resSql) -eq $resBefore)
Assert-That "S the pending commitments are still pending" ((Invoke-Db "select count(*) from marketplace.property_commitments where status = 'PendingSignature'") -eq $pendingBefore)

# a new acceptance after the change uses the new window
$ad = Publish-Rent $property2Id $rent2 "S re-advertise after A closed the ads"
$reqB = Invoke-Api POST "/api/marketplace/rental-requests" -Token $customer2Token -Quiet -Json (@{ listingId = $d1Listing; pricingPeriod = "Daily"; startDate = "2027-06-20"; endDate = "2027-06-22" } | ConvertTo-Json)
Check "S request B" $reqB 201
$accB2 = Invoke-Api POST "/api/marketplace/owner/rental-requests/$($reqB.Body.requestId)/accept" -Token $ownerToken -Quiet
Check "S accept B (after the change)" $accB2 200
$deadlineB = [datetimeoffset]$accB2.Body.signingDeadline
$hoursB = ($deadlineB - [datetimeoffset]$accB2.Body.acceptedAt).TotalHours
$persistedB = [datetimeoffset](Invoke-Api GET "/api/marketplace/me/commitments/$($accB2.Body.commitmentId)" -Token $customer2Token -Quiet).Body.signingDeadline
Assert-That "S B was given the new 24-hour window" ([math]::Abs($hoursB - 24) -lt 0.01) "$hoursB hours"

$set168 = Invoke-Api PUT "/api/admin/marketplace/settings/signing-window" -Token $adminToken -Quiet -Json (@{ hours = 168 } | ConvertTo-Json)
Check "S change the window to 168 hours" $set168 200
$cA2 = (Invoke-Api GET "/api/marketplace/me/commitments/$commitA" -Token $customerToken -Quiet).Body
$cB2 = (Invoke-Api GET "/api/marketplace/me/commitments/$($accB2.Body.commitmentId)" -Token $customer2Token -Quiet).Body
Assert-That "S A's deadline still did not move" ([datetimeoffset]$cA2.signingDeadline -eq $persistedA)
Assert-That "S B's deadline did not move" ([datetimeoffset]$cB2.signingDeadline -eq $persistedB)
Assert-That "S every commitment that existed before the first change is unchanged" ((Invoke-Db $snapSql) -eq $snapBefore)
Assert-That "S the legacy reservations are still unchanged" ((Invoke-Db $resSql) -eq $resBefore)

$restore = Invoke-Api PUT "/api/admin/marketplace/settings/signing-window" -Token $adminToken -Quiet -Json (@{ hours = $hoursBefore } | ConvertTo-Json)
Check "S restore the window to $hoursBefore hours" $restore 200
"  signing window restored to $hoursBefore hours (the settings row now exists with that value; it did not before the first change)" | Add-Content $log
"  deadlines: A=$deadlineA (window $hoursA h)  B=$deadlineB (window $hoursB h)" | Add-Content $log

# =====================================================================================================================
# PART C. Owner profile rejection and resubmission
# =====================================================================================================================
$owner2Email = "demo.owner2.$stamp@demo.local"; $owner2Password = New-DemoPassword
Check "P register a second owner" (Invoke-Api POST "/api/auth/register" -Quiet -Json (@{ email = $owner2Email; password = $owner2Password; firstName = "Omar"; lastName = "Owner"; phoneNumber = "+2013$stamp" } | ConvertTo-Json)) 201
Check "P verify the second owner's email" (Invoke-Api POST "/api/auth/verify-email" -Quiet -Json (@{ email = $owner2Email; code = (Get-EmailCode $owner2Email) } | ConvertTo-Json)) 200
$owner2Token = (Invoke-Api POST "/api/auth/login" -Quiet -Json (@{ email = $owner2Email; password = $owner2Password } | ConvertTo-Json)).Body.accessToken
$nid2 = (Get-Random -Minimum 2000000000 -Maximum 2999999999).ToString()
Check "P identity" (Invoke-Api PUT "/api/owner-profile/identity" -Token $owner2Token -Quiet -Json (@{ nationalId = $nid2; nationality = "Egyptian"; birthDate = "1991-02-02"; idExpiryDate = "2031-01-01"; confirmed = $true } | ConvertTo-Json)) 200
Check "P address" (Invoke-Api PUT "/api/owner-profile/address" -Token $owner2Token -Quiet -Json (@{ buildingNumber = "4321"; streetName = "Another Street"; district = "Maadi"; city = "Cairo"; postalCode = "11728"; additionalNumber = "8765"; confirmed = $true } | ConvertTo-Json)) 200
$o = curl.exe -s -w "`n%{http_code}" -X PUT "$base/api/owner-profile/address-proof" -H "Authorization: Bearer $owner2Token" -F "file=@$fixtures\address-proof.png;type=image/png"
Check "P address proof" ([pscustomobject]@{ Status = [int](($o -split "`n")[-1]); Body = $null }) 200
Check "P submit" (Invoke-Api POST "/api/owner-profile/submit" -Token $owner2Token -Quiet) 200
$profile2Id = (Invoke-Api GET "/api/owner-profile" -Token $owner2Token -Quiet).Body.profileId
Check "P reject without a reason is refused" (Invoke-Api POST "/api/admin/owner-verifications/$profile2Id/reject" -Token $adminToken -Quiet -Json (@{ reason = "" } | ConvertTo-Json)) 422
Check "P the owner cannot reject their own profile" (Invoke-Api POST "/api/admin/owner-verifications/$profile2Id/reject" -Token $owner2Token -Quiet -Json (@{ reason = "no" } | ConvertTo-Json)) 403
$pr = Invoke-Api POST "/api/admin/owner-verifications/$profile2Id/reject" -Token $adminToken -Quiet -Json (@{ reason = "Address proof unreadable." } | ConvertTo-Json)
Check "P reject with a reason" $pr 200
"  profile rejection: status=$($pr.Body.status) canAddProperties=$($pr.Body.canAddProperties)" | Add-Content $log
$mine = (Invoke-Api GET "/api/owner-profile" -Token $owner2Token -Quiet).Body
Assert-That "P the owner sees Rejected and the reason" (("$($mine.status)" -eq "Rejected") -and (($mine | ConvertTo-Json -Depth 8) -match "Address proof unreadable"))
Check "P a rejected owner cannot create a property" (Invoke-Api POST "/api/properties" -Token $owner2Token -Quiet -Json $property) 403
Check "P edit after rejection" (Invoke-Api PUT "/api/owner-profile/address" -Token $owner2Token -Quiet -Json (@{ buildingNumber = "4321"; streetName = "Another Street"; district = "Maadi"; city = "Cairo"; postalCode = "11728"; additionalNumber = "8765"; confirmed = $true } | ConvertTo-Json)) 200
$o = curl.exe -s -w "`n%{http_code}" -X PUT "$base/api/owner-profile/address-proof" -H "Authorization: Bearer $owner2Token" -F "file=@$fixtures\address-proof.png;type=image/png"
Check "P new address proof" ([pscustomobject]@{ Status = [int](($o -split "`n")[-1]); Body = $null }) 200
Check "P resubmit" (Invoke-Api POST "/api/owner-profile/submit" -Token $owner2Token -Quiet) 200
Check "P approve the resubmitted profile" (Invoke-Api POST "/api/admin/owner-verifications/$profile2Id/approve" -Token $adminToken -Quiet) 200
Check "P the approved owner can now create a property" (Invoke-Api POST "/api/properties" -Token $owner2Token -Quiet -Json $property) 201

$failed = @($script:results | Where-Object { -not $_.Ok })
Write-Host ""
Write-Host ("{0} checks, {1} failed" -f $script:results.Count, $failed.Count) -ForegroundColor $(if ($failed.Count) { "Red" } else { "Green" })
Add-Content $log ("{0} checks, {1} failed" -f $script:results.Count, $failed.Count)
foreach ($f in $failed) { Add-Content $log ("FAILED: " + $f.Label) }
"administrator=$adminEmail propertyId=$propertyId property2Id=$property2Id" | Add-Content $log
Write-Host "Log: $log"
if ($failed.Count) { exit 1 }
