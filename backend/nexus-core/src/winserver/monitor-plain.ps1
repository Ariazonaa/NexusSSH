$ProgressPreference='SilentlyContinue';$ErrorActionPreference='SilentlyContinue';$i=[Globalization.CultureInfo]::InvariantCulture
$q=[Console]::OpenStandardOutput()
function E($l){$b=[Text.Encoding]::UTF8.GetBytes(($l-join"`n")+"`n");try{$q.Write($b,0,$b.Length);$q.Flush()}catch{exit}}
E @("@pid $PID $((Get-Process -Id $PID).StartTime.ToFileTimeUtc())")
function N($s){$s -replace '[^A-Za-z0-9._-]','_'}
Add-Type -TypeDefinition '__NXW__';Add-Type -AssemblyName Microsoft.VisualBasic;$m=New-Object Microsoft.VisualBasic.Devices.ComputerInfo
$d=[NxW]::Procs();$g=$d[[long]$d[[long]$PID][1]][1];$h=$d[[long]$g][4]
$r=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion';$p=Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0';$s=Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS';$v=[Environment]::OSVersion.Version
E @('@os';'PRETTY_NAME='+$r.ProductName;'@kernel';'Windows {0}.{1}.{2}'-f$v.Major,$v.Minor,$v.Build;'@info';'cpu='+"$($p.ProcessorNameString)".Trim();'cores='+[Environment]::ProcessorCount;'vendor='+$s.SystemManufacturer;'product='+$s.SystemProductName)
$n=0;while(1){E @(&{'@sample'
$c=[NxW]::Cores();$a=@(0,0,0);foreach($t in $c){$a[0]+=$t[0];$a[1]+=$t[1];$a[2]+=$t[2]}
'cpu {0} 0 {1} {2} 0 0 0 0'-f[uint64]($a[2]/1e5),[uint64](($a[1]-$a[0])/1e5),[uint64]($a[0]/1e5)
$k=0;foreach($t in $c){'cpu{0} {1} 0 {2} {3} 0 0 0 0'-f$k,[uint64]($t[2]/1e5),[uint64](($t[1]-$t[0])/1e5),[uint64]($t[0]/1e5);$k++}
'MemTotal: {0} kB'-f[uint64]($m.TotalPhysicalMemory/1kb);'MemAvailable: {0} kB'-f[uint64]($m.AvailablePhysicalMemory/1kb)
'@uptime';([Diagnostics.Stopwatch]::GetTimestamp()/[Diagnostics.Stopwatch]::Frequency).ToString('F2',$i)
$d=[IO.DriveInfo]::GetDrives()|?{$_.DriveType-eq'Fixed'-and$_.IsReady}
'@disk';foreach($x in $d|?{$_.Name-eq"$env:SystemDrive\"}){'{0} {1} {2} {3} 0% {0}\'-f$x.Name.TrimEnd('\'),[uint64]($x.TotalSize/1kb),[uint64](($x.TotalSize-$x.TotalFreeSpace)/1kb),[uint64]($x.AvailableFreeSpace/1kb)}
'@disks';foreach($x in $d){'{0} {1} {2} {3} 0% {0}\'-f$x.Name.TrimEnd('\'),[uint64]($x.TotalSize/1kb),[uint64](($x.TotalSize-$x.TotalFreeSpace)/1kb),[uint64]($x.AvailableFreeSpace/1kb)}
$u=[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()|?{$_.NetworkInterfaceType-ne'Loopback'}
'@net';foreach($x in $u){$t=$x.GetIPStatistics();'{0}: {1} 0 0 0 0 0 0 0 {2} 0'-f(N $x.Name),$t.BytesReceived,$t.BytesSent}
'@addr';foreach($x in $u){foreach($t in $x.GetIPProperties().UnicastAddresses){'1: {0} {1} {2}/{3}'-f(N $x.Name),$(if($t.Address.AddressFamily-eq'InterNetwork'){'inet'}else{'inet6'}),("$($t.Address)"-replace'%.*'),$t.PrefixLength}}
$z=[DateTimeOffset]::Now;'@time';'{0} {1}{2:hhmm}'-f$z.ToUnixTimeSeconds(),$(if($z.Offset-lt0){'-'}else{'+'}),$z.Offset;'@end'})
if($n-eq0){$n=1;sleep 1}else{sleep 3};$y=[NxW]::Procs()[[long]$g];if(-not$y-or$y[4]-ne$h){exit}}
