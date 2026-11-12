using System.Diagnostics;
using System.Security.Cryptography;
namespace Platform;
public sealed class WpsPreviewService(string root,string script)
{
    readonly SemaphoreSlim gate=new(1,1);
    public async Task<string> Render(string input,int index,CancellationToken cancellation=default)
    {
        using(var package=new PptPackage(input))
            if(index<1||index>package.Slides.Length)throw new ArgumentException("页面不存在");
        var hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input,cancellation)));
        var directory=Path.Combine(root,"wps-previews","v1-1920",hash);
        var output=Path.Combine(directory,$"slide-{index}.png");
        if(Valid(output))return output;
        await gate.WaitAsync(cancellation);
        try {
            if(Valid(output))return output;
            Directory.CreateDirectory(directory);
            var temporary=Path.Combine(directory,"job-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try {
                var info=new ProcessStartInfo {
                    FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"),
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true
                };
                foreach(var arg in new[]{"-NoProfile","-Sta","-ExecutionPolicy","Bypass","-File",script,"-InputFile",Path.GetFullPath(input),"-OutputDirectory",temporary,"-SlideIndex",index.ToString()})
                    info.ArgumentList.Add(arg);
                using var process=Process.Start(info)??throw new InvalidOperationException("无法启动WPS预览服务");
                var stdout=process.StandardOutput.ReadToEndAsync();
                var stderr=process.StandardError.ReadToEndAsync();
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                try {await process.WaitForExitAsync(timeout.Token);}
                catch(OperationCanceledException){try{process.Kill(true);}catch{}throw new InvalidOperationException("WPS渲染超时，请确认WPS已完成首次启动且没有弹窗后重试");}
                await stdout;var error=await stderr;
                var generated=Path.Combine(temporary,$"slide-{index}.png");
                if(process.ExitCode!=0||!Valid(generated))throw new InvalidOperationException("WPS高清预览生成失败，请确认WPS可正常打开该文件。"+(error.Length>0?" "+error[..Math.Min(error.Length,300)]:""));
                File.Move(generated,output,true);return output;
            } finally {try{Directory.Delete(temporary,true);}catch{}}
        } finally {gate.Release();}
    }
    static bool Valid(string file){if(!File.Exists(file))return false;using var s=File.OpenRead(file);Span<byte> header=stackalloc byte[24];if(s.Read(header)!=24)return false;return header[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})&&System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..20])==1920;}
}
