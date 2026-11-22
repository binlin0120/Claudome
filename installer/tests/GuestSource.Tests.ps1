#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$rows=[Collections.Generic.List[object]]::new()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\ClaudeSetup\Guest') -Filter '*.ps1'){
    $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    $rows.Add(@{name=$file.Name;passed=$errors.Count -eq 0;errors=@($errors | ForEach-Object Message)})
    $reserved=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and $node.Left.VariablePath.UserPath -in @('home','HOME','CODEX_HOME')},$true))
    $rows.Add(@{name=($file.Name+'-no-reserved-variable-assignment');passed=$reserved.Count -eq 0;errors=@($reserved | ForEach-Object {$_.Extent.StartLineNumber})})
    # The task runner is a source string, so parse it separately as well.
    if($file.Name -eq 'InteractiveRun.ps1'){
        foreach($value in $ast.FindAll({param($a)$a -is [Management.Automation.Language.StringConstantExpressionAst] -and $a.StringConstantType -eq 'SingleQuotedHereString'},$true)){
            $tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseInput($value.Value,[ref]$tokens,[ref]$errors)
            $rows.Add(@{name='embedded-interactive-task-runner';passed=$errors.Count -eq 0;errors=@($errors | ForEach-Object Message)})
        }
    }
}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$failed=@($rows | Where-Object {-not $_.passed}).Count
@{type='source parser only; no script execution';passed=$rows.Count-$failed;failed=$failed;tests=@($rows)} | ConvertTo-Json -Depth 6 | ForEach-Object {[IO.File]::WriteAllText((Join-Path $OutputDirectory 'guest-source-tests.json'),$_,[Text.UTF8Encoding]::new($false))}
Write-Output ('Guest source parse passed='+($rows.Count-$failed)+' failed='+$failed)
if($failed){exit 1}
