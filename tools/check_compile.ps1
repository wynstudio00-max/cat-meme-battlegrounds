# Compiles all Assets/**/*.cs (runtime + Editor) with Unity's bundled Roslyn against the last compiled
# Library/ScriptAssemblies, so C# errors show up without opening Unity. Needs Unity to have compiled once.
$ed = "C:\Program Files\Unity\Hub\Editor\6000.5.1f1\Editor\Data"
$csc = "$ed\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll"; $dotnet = "$ed\NetCoreRuntime\dotnet.exe"
$sa = "G:\PROJECT-GAME-WAR-CAT\CatBattleground\Library\ScriptAssemblies"
$q = { param($p) '-r:"' + $p + '"' }
$lines = @("-nologo","-noconfig","-nostdlib+","-langversion:9","-target:library","-out:$env:TEMP\all_check.dll", (& $q "$ed\NetStandard\ref\2.1.0\netstandard.dll"))
$lines += Get-ChildItem "$ed\Managed\UnityEngine" -Filter *.dll | % { & $q $_.FullName }
$lines += Get-ChildItem $sa -Filter *.dll | ? { $_.Name -notlike 'Assembly-CSharp*' -and $_.Name -notlike '*CodeGen*' } | % { & $q $_.FullName }
$lines += Get-ChildItem "G:\PROJECT-GAME-WAR-CAT\CatBattleground\Assets" -Recurse -Filter *.cs | ? { $_.FullName -notlike '*TutorialInfo*' } | % { '"' + $_.FullName + '"' }
[IO.File]::WriteAllLines("$env:TEMP\csc.rsp", $lines)
& $dotnet $csc "@$env:TEMP\csc.rsp" 2>&1 | ? { $_ -match 'error' }
"compile check finished"
