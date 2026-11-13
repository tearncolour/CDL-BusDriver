# Parses a script with PowerShell's own parser and reports, as one line of JSON, what the parser made of it: the syntax errors, the commands it would
# call (the first word of each command), and every string constant. Nothing is run. Used by WindowsScriptTests to judge a script of either shape.
param([Parameter(Mandatory)][string]$ScriptFile)
$text = [System.IO.File]::ReadAllText($ScriptFile, [System.Text.Encoding]::UTF8)
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
$commands = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
$strings = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $n.StringConstantType -ne 'BareWord' }, $true) | ForEach-Object { $_.Value })
$expandables = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -or $n -is [System.Management.Automation.Language.SubExpressionAst] }, $true)).Count
[pscustomobject]@{ errors = $errors.Count; commands = $commands; strings = $strings; expandables = $expandables } | ConvertTo-Json -Compress -Depth 4
