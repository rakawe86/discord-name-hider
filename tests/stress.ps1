# Tries to break DiscordNameHider.exe. Runs against a throwaway sandbox: fake Discord folder, fake state file.
# Run: powershell -STA -ExecutionPolicy Bypass -File tests\stress.ps1 [path\to\exe]
param([string]$Exe = (Join-Path $PSScriptRoot "..\out\DiscordNameHider.exe"))
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Web.Extensions
Add-Type -Namespace W -Name U -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr SendMessage(System.IntPtr h,int m,System.IntPtr w,System.IntPtr l);'
$asm = [Reflection.Assembly]::LoadFile((Resolve-Path $Exe).Path)
$all = [Reflection.BindingFlags]"NonPublic,Public,Instance,Static"
$hider = $asm.GetType("Hider"); $formT = $asm.GetType("MainForm")
$box = Join-Path ([IO.Path]::GetTempPath()) ("dnh-stress-" + [Guid]::NewGuid().ToString("N"))
$local = "$box\local"; $roam = "$box\roam"; $tmp = "$roam\DiscordNameHider\state.json"; $vs = "$roam\Vencord\settings\settings.json"
New-Item -ItemType Directory -Force (Split-Path $tmp), (Split-Path $vs), $local | Out-Null

# Redirect every path. The fake flavor name also means real Discord processes are never matched or killed.
foreach ($kv in @{ LocalApp = $local; RoamingApp = $roam; DistDir = "$local\DiscordNameHider\dist"; StateFile = $tmp; VencordSettings = $vs }.GetEnumerator()) {
    $f = $hider.GetField($kv.Key, $all); $f.SetValue($null, $kv.Value)
    if ($f.GetValue($null) -ne $kv.Value) { throw "sandbox failed for $($kv.Key)" }
}
$fl = $hider.GetField("Flavors", $all).GetValue($null); for ($i = 0; $i -lt $fl.Length; $i++) { $fl[$i] = "FakeDiscord$i" }

$script:fails = 0; $script:errs = New-Object Collections.ArrayList
[Windows.Forms.Application]::add_ThreadException({ param($s, $e) [void]$script:errs.Add($e.Exception.Message) })
function Check($name, $ok, $detail = "") {
    $n = $script:errs.Count
    if ($ok -and $n -eq 0) { "PASS  $name" } else { $script:fails++; "FAIL  $name  $detail" + $(if ($n) { " [$n crash(es): $($script:errs[0])]" }) }
    $script:errs.Clear()
}
function Inner($e) { while ($e.InnerException) { $e = $e.InnerException }; $e.Message }
function WriteState($json) { [IO.File]::WriteAllText($tmp, $json) }
function People($n) { '{"enabled":true,"users":[' + ((1..$n | ForEach-Object { '{"value":"u' + $_ + '","label":"","hidden":true}' }) -join ",") + ']}' }
function ReadState { $j = New-Object Web.Script.Serialization.JavaScriptSerializer; $j.MaxJsonLength = [int]::MaxValue; $j.DeserializeObject([IO.File]::ReadAllText($tmp)) }
function Pump($ms = 0) { $end = [DateTime]::Now.AddMilliseconds($ms); do { [Windows.Forms.Application]::DoEvents() } while ([DateTime]::Now -lt $end) }
function Open { $o = [Activator]::CreateInstance($formT); $o.StartPosition = "Manual"; $o.Location = New-Object Drawing.Point(-3000, -3000); $o.Show(); Pump; $o }
function Field($o, $n) { $formT.GetField($n, $all).GetValue($o) }
function Call($o, $n, $a = $null) { [void]$formT.GetMethod($n, $all).Invoke($o, $a) }
function ClickAt($list, $r) {
    $lp = [IntPtr](($r.Y + [int]($r.Height / 2)) * 65536 + ($r.X + [int]($r.Width / 2)))
    [void][W.U]::SendMessage($list.Handle, 0x0201, [IntPtr]1, $lp); [void][W.U]::SendMessage($list.Handle, 0x0202, [IntPtr]0, $lp); Pump
}
function ClickRow($list, $i) { ClickAt $list $list.ToggleRect($i) }
function Key($list, $k) { [void][W.U]::SendMessage($list.Handle, 0x0100, [IntPtr]$k, [IntPtr]0); [void][W.U]::SendMessage($list.Handle, 0x0101, [IntPtr]$k, [IntPtr]0); Pump }
function Rows { @((ReadState).users).Count }
function Hidden { @((ReadState).users | ForEach-Object { $_.hidden }) }

# --- list ---
WriteState (People 3); $before = [IO.File]::ReadAllText($tmp); $f = Open
Check "open: does not crash or rewrite the list" ([IO.File]::ReadAllText($tmp) -eq $before)
$l = Field $f "people"
foreach ($r in 1..20) { foreach ($i in 2, 1, 0) { ClickRow $l $i } }
Check "60 rapid ticks: no crash, even clicks restore every box" (@(Hidden | Where-Object { -not $_ }).Count -eq 0) (Hidden -join ",")
WriteState (People 3); Pump 1100
ClickRow $l 2
Check "single tick saves that person only" ((Hidden -join ",") -eq "True,True,False") (Hidden -join ",")

WriteState (People 3); Pump 1100
foreach ($r in 1..10) { Key $l 0x20 }
Check "keyboard: Space 10 times returns to start" ((Hidden -join ",") -eq "True,True,True") (Hidden -join ",")
Key $l 0x28; Key $l 0x28; Key $l 0x28; Key $l 0x26; Key $l 0x20
Check "keyboard: arrows then Space change the right person" ((Hidden -join ",") -eq "True,False,True") (Hidden -join ",")
ClickAt $l $l.RemoveRect(0)
Check "remove button removes that person" ((Rows) -eq 2 -and (ReadState).users[0].value -eq "u2")
Key $l 0x2E; Key $l 0x2E; Key $l 0x2E; Key $l 0x20; Key $l 0x26
Check "keyboard: Delete past the end of the list is safe" ((Rows) -eq 0)

$m = Field $f "master"; $flip = $m.GetType().GetMethod("OnClick", $all)
foreach ($r in 1..100) { [void]$flip.Invoke($m, @([EventArgs]::Empty)); Pump }
Check "100 main-switch flips" ((ReadState).enabled -eq $true)

# --- changes from Discord (Ctrl+Shift+H) arriving during use ---
$j = 0
foreach ($r in 1..6) {
    WriteState ('{"enabled":' + $(if ($r % 2) { "false" } else { "true" }) + ',"users":[{"value":"a","label":"","hidden":true},{"value":"b","label":"","hidden":false}]}')
    Pump 1100
    if ((Field $f "state").users.Count -eq 2) { ClickRow $l 1; ClickRow $l 1; $j++ }
}
Check "external file changes picked up while clicking" ($j -eq 6) "synced $j/6"
WriteState (People 3); Pump 1100; WriteState '{"enabled":tr'; Pump 1100
Check "half-written file from Discord does not empty the list" ((Field $f "state").users.Count -eq 3)

# --- input ---
$add = Field $f "addValue"
function AddP($v) { $add.Text = $v; Call $f "AddPerson"; Pump }
WriteState '{"enabled":true,"users":[]}'; Pump 1100
AddP "   "; AddP "@bob"; AddP "BOB"; AddP "bob"; AddP "<@123456789012345678>"; AddP "x, y"; AddP ("z" * 5000); AddP ("name" + [char]0x540D + [char]0xD83D + [char]0xDE00); AddP '"quote\back'
$v = @((ReadState).users | ForEach-Object { $_.value })
Check "blank and duplicates rejected" (@($v | Where-Object { $_ -eq "bob" }).Count -eq 1 -and $v -notcontains "" -and $v -notcontains "   ")
Check "pasted mention stored as a usable ID" ($v -contains "123456789012345678") ($v -join " | ").Substring(0, 40)
Check "comma list becomes separate people" ($v -contains "x" -and $v -contains "y")
Check "unicode, quotes and very long names survive a save" ($v -contains '"quote\back' -and @($v | Where-Object { $_.Length -eq 5000 }).Count -eq 1)
while ((Field $f "state").users.Count) { ClickAt $l $l.RemoveRect(0) }
Check "remove everyone" (@((ReadState).users).Count -eq 0)
Call $f "SetAll" @($true); Call $f "SetAll" @($false); ClickAt $l (New-Object Drawing.Rectangle 20, 20, 10, 10); Key $l 0x20; Key $l 0x2E; Pump
Check "buttons, clicks and keys on an empty list" $true
$f.Close()

# --- bad or huge files on open ---
foreach ($bad in @{ garbage = "not json {{{"; empty = ""; nullusers = '{"enabled":true,"users":null}'; nullentry = '{"enabled":true,"users":[null,{"value":null},{"value":"ok","hidden":true}]}'; wrongtype = '{"enabled":"yes","users":"nope"}'; array = '[1,2]' }.GetEnumerator()) {
    WriteState $bad.Value; Remove-Item "$tmp.bad" -ErrorAction SilentlyContinue
    $msg = ""; try { $f = Open; Call $f "SetAll" @($true); Pump; $f.Close() } catch { $msg = Inner $_.Exception }
    $kept = (Test-Path "$tmp.bad") -or ($bad.Key -in "nullentry", "nullusers")
    Check "bad file ($($bad.Key)): opens, usable, old content not silently lost" ($msg -eq "" -and $kept) $msg
}
WriteState (People 60000); $msg = ""; $n = -1
try { $f = Open; $n = (Field $f "state").users.Count; $f.Refresh(); $f.Close() } catch { $msg = Inner $_.Exception }
Check "huge list (60,000 people, over 2 MB) loads" ($n -eq 60000) "rows=$n $msg"
WriteState (People 5000); $sw = [Diagnostics.Stopwatch]::StartNew(); $f = Open; $l = Field $f "people"; ClickRow $l 0; $t = $sw.ElapsedMilliseconds; $f.Close()
Check "5,000 people: open + tick under 5s" ($t -lt 5000) "${t}ms"

# --- save while something else holds the file (Discord polling, antivirus) ---
WriteState (People 3); $save = $hider.GetMethod("SaveState", $all); $st = [Activator]::CreateInstance($asm.GetType("HiderState"))
function SaveWhileHeld($gapMs, $saves) {
    $flag = [hashtable]::Synchronized(@{ Stop = $false })
    $rs = [PowerShell]::Create(); [void]$rs.AddScript({ param($p, $flag, $gap) while (-not $flag.Stop) { try { $h = [IO.File]::Open($p, 'Open', 'Read', 'Read'); [Threading.Thread]::Sleep(3); $h.Close() } catch {}; if ($gap) { [Threading.Thread]::Sleep($gap) } } }).AddArgument($tmp).AddArgument($flag).AddArgument($gapMs)
    $ar = $rs.BeginInvoke(); $bad = 0; $why = ""; $lost = 0
    foreach ($r in 1..$saves) { try { [void]$save.Invoke($null, @($st)) } catch { $bad++; $why = Inner $_.Exception }; if (-not (Test-Path $tmp)) { $lost++ } }
    $flag.Stop = $true; [void]$rs.EndInvoke($ar); $rs.Dispose()
    @{ Bad = $bad; Lost = $lost; Why = $why }
}
$x = SaveWhileHeld 0 100
Check "file held almost nonstop: a save may be refused, the file is never lost" ($x.Lost -eq 0) "missing $($x.Lost) times"
$x = SaveWhileHeld 20 300
Check "300 saves while the file is read repeatedly: none fail, file never lost" ($x.Bad -eq 0 -and $x.Lost -eq 0) "$($x.Bad) failed, missing $($x.Lost) times: $($x.Why)"

# --- update check ---
$newer = $hider.GetMethod("IsNewer", $all)
Check "version compare: newer, same, older, 1.10 vs 1.4, junk" ($newer.Invoke($null, @("1.4.2", "1.4.1")) -and -not $newer.Invoke($null, @("1.4.1", "1.4.1")) -and -not $newer.Invoke($null, @("1.3.9", "1.4.1")) -and $newer.Invoke($null, @("1.10.0", "1.4.1")) -and -not $newer.Invoke($null, @("<x>", "1.4.1")))

# --- install / uninstall on a fake Discord ---
function FakeDiscord($ver) { $res = "$local\FakeDiscord0\app-$ver\resources"; New-Item -ItemType Directory -Force $res | Out-Null; [IO.File]::WriteAllText("$res\app.asar", "ORIGINAL-$ver"); $res }
$res = FakeDiscord "1.0.1"
$esc = @($hider.GetMethod("ResourceDirs", $all).Invoke($null, @("FakeDiscord0")) | Where-Object { -not $_.StartsWith($box) })
if ($esc.Count) { throw "sandbox escape: $esc" }
[IO.File]::WriteAllText($vs, '{"themeLinks":["keep-me"],"plugins":{"Other":{"enabled":true}}}')
$inst = $hider.GetMethod("Install", $all); $uninst = $hider.GetMethod("Uninstall", $all); $isInst = $hider.GetMethod("IsInstalled", $all)
[void]$inst.Invoke($null, $null); [void]$inst.Invoke($null, $null)
Check "install records the version for the update check" ((Get-Content "$local\DiscordNameHider\dist\version.txt") -eq $hider.GetField("Version", $all).GetValue($null))
Check "install twice keeps the original Discord file" ([IO.File]::ReadAllText("$res\_app.asar") -eq "ORIGINAL-1.0.1" -and $isInst.Invoke($null, $null))
$s = [IO.File]::ReadAllText($vs); Check "install keeps other settings" ($s -match "keep-me" -and $s -match "Other" -and $s -match "NoTrack")
$res2 = FakeDiscord "1.0.2"
Check "Discord update not patched yet: not reported as active" (-not $isInst.Invoke($null, $null))
[void]$inst.Invoke($null, $null)
Check "re-install patches the new version" ($isInst.Invoke($null, $null) -and (Test-Path "$res2\_app.asar"))
[void]$uninst.Invoke($null, $null)
Check "uninstall restores every version exactly" ([IO.File]::ReadAllText("$res\app.asar") -eq "ORIGINAL-1.0.1" -and [IO.File]::ReadAllText("$res2\app.asar") -eq "ORIGINAL-1.0.2" -and -not (Test-Path "$res\_app.asar"))
Check "uninstall keeps the list" (Test-Path $tmp)
[IO.File]::WriteAllText($vs, "{corrupt"); [void]$inst.Invoke($null, $null); [void]$uninst.Invoke($null, $null)
Check "corrupt Vencord settings are backed up, not lost" ((Test-Path "$vs.bad") -and [IO.File]::ReadAllText("$vs.bad") -eq "{corrupt")

# Antivirus/lock: the original app.asar can't be moved. Discord must still be intact afterwards.
$res3 = FakeDiscord "1.0.3"; $lock = [IO.File]::Open("$res3\app.asar", 'Open', 'Read', 'Read'); $threw = $false
try { [void]$inst.Invoke($null, $null) } catch { $threw = $true }; $lock.Close()
Check "locked Discord file: clean error, Discord still has its file" ($threw -and [IO.File]::ReadAllText("$res3\app.asar") -eq "ORIGINAL-1.0.3" -and -not (Test-Path "$res3\_app.asar"))
Remove-Item -Recurse -Force $box -ErrorAction SilentlyContinue
""
if ($script:fails) { "$script:fails test(s) FAILED"; exit 1 } else { "ALL PASSED" }
