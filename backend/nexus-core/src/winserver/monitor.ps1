$ProgressPreference='SilentlyContinue';$ErrorActionPreference='SilentlyContinue';$i=[Globalization.CultureInfo]::InvariantCulture
$q=[Console]::OpenStandardOutput()
function E($l){$b=[Text.Encoding]::UTF8.GetBytes(($l-join"`n")+"`n");try{$q.Write($b,0,$b.Length);$q.Flush()}catch{exit}}
E @("@pid $PID $((Get-Process -Id $PID).StartTime.ToFileTimeUtc())")
try{$w=Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop}catch{E @('@nocim');exit}
$w=Get-CimInstance Win32_Process -Filter "ProcessId=$($w.ParentProcessId)";$g=Get-Process -Id $w.ParentProcessId
function N($s){$s -replace '[^A-Za-z0-9._-]','_'}
$o=Get-CimInstance Win32_OperatingSystem;$c=Get-CimInstance Win32_ComputerSystem;$p=@(Get-CimInstance Win32_Processor)[0]
E @('@os';'PRETTY_NAME='+$o.Caption;'@kernel';'Windows '+$o.Version;'@info';'cpu='+$p.Name;'cores='+$c.NumberOfLogicalProcessors;'vendor='+$c.Manufacturer;'product='+$c.Model;'virt='+$(if($c.HypervisorPresent){'vm'}else{'none'}))
$n=0;while(1){E @(&{'@sample'
foreach($t in Get-CimInstance Win32_PerfRawData_PerfOS_Processor){$k=if($t.Name-eq'_Total'){'cpu'}else{'cpu'+$t.Name};'{0} {1} 0 {2} {3} 0 0 0 0'-f$k,[uint64]($t.PercentUserTime/1e5),[uint64]($t.PercentPrivilegedTime/1e5),[uint64]($t.PercentIdleTime/1e5)}
$o=Get-CimInstance Win32_OperatingSystem;'MemTotal: {0} kB'-f$o.TotalVisibleMemorySize;'MemAvailable: {0} kB'-f$o.FreePhysicalMemory
$a=0;$u=0;foreach($f in Get-CimInstance Win32_PageFileUsage){$a+=$f.AllocatedBaseSize;$u+=$f.CurrentUsage};'SwapTotal: {0} kB'-f($a*1024);'SwapFree: {0} kB'-f(($a-$u)*1024)
'@uptime';([Diagnostics.Stopwatch]::GetTimestamp()/[Diagnostics.Stopwatch]::Frequency).ToString('F2',$i)
$d=Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3';'@disk';foreach($x in $d|?{$_.DeviceID-eq$o.SystemDrive}){'{0} {1} {2} {3} 0% {0}\'-f$x.DeviceID,[uint64]($x.Size/1kb),[uint64](($x.Size-$x.FreeSpace)/1kb),[uint64]($x.FreeSpace/1kb)}
'@disks';foreach($x in $d){'{0} {1} {2} {3} 0% {0}\'-f$x.DeviceID,[uint64]($x.Size/1kb),[uint64](($x.Size-$x.FreeSpace)/1kb),[uint64]($x.FreeSpace/1kb)}
'@net';foreach($s in Get-NetAdapterStatistics){'{0}: {1} 0 0 0 0 0 0 0 {2} 0'-f(N $s.Name),$s.ReceivedBytes,$s.SentBytes}
'@addr';foreach($x in Get-NetIPAddress){if($x.IPAddress-notmatch'^(127\.|::1$)'){'1: {0} {1} {2}/{3}'-f(N $x.InterfaceAlias),$(if($x.AddressFamily-eq2){'inet'}else{'inet6'}),($x.IPAddress-replace'%.*'),$x.PrefixLength}}
'@io';foreach($x in Get-CimInstance Win32_PerfRawData_PerfDisk_PhysicalDisk){if($x.Name-ne'_Total'){'0 0 disk{0} 0 0 {1} 0 0 0 {2} 0 0 {3}'-f($x.Name-split' ')[0],[uint64]($x.DiskReadBytesPersec/512),[uint64]($x.DiskWriteBytesPersec/512),[uint64]($x.PercentDiskTime/1e4)}}
$z=[DateTimeOffset]::Now;'@time';'{0} {1}{2:hhmm}'-f$z.ToUnixTimeSeconds(),$(if($z.Offset-lt0){'-'}else{'+'}),$z.Offset;'@end'})
if($n-eq0){$n=1;sleep 1}else{sleep 3};if($g-and$g.HasExited){exit}}
