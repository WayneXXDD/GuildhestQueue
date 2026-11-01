[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceCheckout,[Parameter(Mandatory)][string]$FeedDirectory)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path -LiteralPath $SourceCheckout).ProviderPath
$workspace = (Resolve-Path -LiteralPath $env:GITHUB_WORKSPACE).ProviderPath
if ($root -ne $workspace -or (Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Publisher must run in its disposable GitHub workspace.' }
$feed = (Resolve-Path -LiteralPath $FeedDirectory).ProviderPath
if ($feed -eq $root -or $feed.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Feed must be outside the source checkout.' }
$entries = @(Get-Content -LiteralPath (Join-Path $feed 'build-manifest.json') -Raw | ConvertFrom-Json)
if ($entries.Count -ne 1 -or $entries[0].InternalName -ne 'GuildhestQueue' -or -not $entries[0].IsTestingExclusive) { throw 'Expected one testing plugin.' }
function RepoGit([string[]]$Arguments) {
    & git -C $root @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments[0])" }
}
$origin = & git -C $root remote get-url origin
if ($LASTEXITCODE -ne 0 -or $origin -notin @('https://github.com/WayneXXDD/GuildhestQueue','https://github.com/WayneXXDD/GuildhestQueue.git')) { throw 'Unexpected publication repository.' }
$branch = 'plugin-repo'
$existing = & git -C $root ls-remote --heads origin "refs/heads/$branch"
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect distribution branch.' }
if ($existing) {
    RepoGit @('fetch','--depth','1','origin',"refs/heads/${branch}:refs/remotes/origin/$branch")
    RepoGit @('checkout','-B',$branch,"refs/remotes/origin/$branch")
} else {
    RepoGit @('checkout','--orphan',$branch)
    # The resolved, disposable runner workspace is checked above.
    RepoGit @('rm','-r','-f','--ignore-unmatch','--','.')
}
if ((& git -C $root branch --show-current) -ne 'plugin-repo') { throw 'Refusing to publish to a source branch.' }
$previous = Join-Path $root 'build-manifest.json'
if (Test-Path -LiteralPath $previous) {
    $old = @(Get-Content -LiteralPath $previous -Raw | ConvertFrom-Json)
    if ([version]$entries[0].AssemblyVersion -lt [version]$old[0].AssemblyVersion) { throw 'Refusing a version downgrade.' }
}
foreach ($item in Get-ChildItem -LiteralPath $feed -Force) { Copy-Item -LiteralPath $item.FullName -Destination $root -Recurse -Force }
RepoGit @('config','user.name','github-actions[bot]')
RepoGit @('config','user.email','41898282+github-actions[bot]@users.noreply.github.com')
RepoGit @('add','--all')
& git -C $root diff --cached --quiet
if ($LASTEXITCODE -eq 0) { Write-Output 'Distribution is already current.'; exit 0 }
if ($LASTEXITCODE -ne 1) { throw 'Cannot inspect staged changes.' }
RepoGit @('commit','-m','Publish Guildhest Queue testing build')
RepoGit @('push','origin','HEAD:refs/heads/plugin-repo')
