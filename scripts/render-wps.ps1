param([string]$InputFile,[string]$OutputDirectory,[int]$SlideIndex=0)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.Encoding]::UTF8
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$application=$null;$presentation=$null
try {
 $application=New-Object -ComObject KWPP.Application
 $application.AutomationSecurity=3
 $presentation=$application.Presentations.Open($InputFile,-1,0,0)
 $width=1920
 $height=[int][Math]::Round($width*$presentation.PageSetup.SlideHeight/$presentation.PageSetup.SlideWidth)
 for($i=1;$i -le $presentation.Slides.Count;$i++){
  if($SlideIndex -gt 0 -and $SlideIndex -ne $i){continue}
  $slide=$presentation.Slides.Item($i)
  try {$slide.Export((Join-Path $OutputDirectory ("slide-"+$i+".png")),"PNG",$width,$height)} finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($slide)}
 }
 Write-Output ('Rendered '+$presentation.Slides.Count+' slides')
} finally {
 if($null -ne $presentation){$presentation.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($presentation)}
 if($null -ne $application){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($application)}
 [GC]::Collect();[GC]::WaitForPendingFinalizers()
}
