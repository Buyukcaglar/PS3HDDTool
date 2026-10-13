using System.Diagnostics;
using System.Text.Json;
using PS3HddTool.Core.Migration;

static class LegacyVerificationHandoffTests
{
    internal static void Run(string dir,string legacyCli,
        Func<string,Fixture,Fixture,string,byte[]?,byte[]?,MigrationRequest> setup,byte[] registry)
    {
        if(RegularImage.HashFile(legacyCli)!="40941A36CCA59760F6808C792784CAAFDD79C0AA71159E29B3CE79D386CE2615" ||
            RegularImage.HashFile(Path.Combine(Path.GetDirectoryName(legacyCli)!,"PS3HddTool.Core.dll"))!="D9636E83F5DA88B1A0FE5C123E2ACFF61FD67838A0E88F34D7915C5235CB7079")
            throw new Exception("Unexpected legacy CLI.");
        var source=new Fixture(112);source.Add("home",4);source.Add("home/00000001",4);
        source.Add("home/00000001/savedata",4);source.Add("home/00000001/savedata/generated",payload:Fixture.Payload(8192,43));
        source.Add("home/00000001/trophy",4);source.Add("home/00000001/trophy/generated",payload:Fixture.Payload(17003,44));
        source.Add("game",4);source.Add("game/generated.bin",payload:Fixture.Payload(64*1024*1024+731,45));source.Finish();
        var old=new Fixture(160);old.Add("old",payload:Fixture.Payload(17003,46));old.Finish(true);
        var r=setup(dir,source,old,"cbc192-bswap16",registry,registry);
        string cache=Path.Combine(dir,"source-hashes.json"),cacheHash;
        using(var disk=RegularImage.Open(r.Source)){var v=new UfsVolume(disk,32);v.Inventory(true);cacheHash=SourceHashSnapshot.Save(v,cache);}
        var request=new DirectRequest(new(r.Source.Path,32,r.Source.Crypto,r.Source.KeyFile),new(r.DestinationTemplate.Path,32,r.DestinationTemplate.Crypto,r.DestinationTemplate.KeyFile),
            Path.Combine(dir,"journal"),r.SourceRegistry,r.TargetRegistry,SourceHashCache:cache,SourceHashCacheSha256:cacheHash);
        if(request.Source.Kind!="image"||request.Destination.Kind!="image")throw new Exception("Synthetic images only.");
        string requestFile=Path.Combine(dir,"request.json");File.WriteAllBytes(requestFile,JsonSerializer.SerializeToUtf8Bytes(request,ImageMigrator.Json));
        var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=dir};
        foreach(string arg in new[]{legacyCli,"direct-run","--request",requestFile,"--json","--progress-interval-ms","0"})start.ArgumentList.Add(arg);
        using var child=Process.Start(start)!;
        var stderr=child.StandardError.ReadToEndAsync();var stopped=new TaskCompletionSource<bool>();
        var stdout=Task.Run(async()=>{
            using var log=new StreamWriter(Path.Combine(dir,"legacy.ndjson"));string? line;
            while((line=await child.StandardOutput.ReadLineAsync())!=null){await log.WriteLineAsync(line);await log.FlushAsync();
                var e=JsonDocument.Parse(line).RootElement;
                if(!stopped.Task.IsCompleted&&e.TryGetProperty("data",out var data)&&data.TryGetProperty("progress",out var p)&&p.GetProperty("Phase").GetString()=="verify-destination-hash"){
                    using var frozen=VerificationHandoffNative.Freeze(child.Id,child.StartTime.ToUniversalTime().ToFileTimeUtc(),child.MainModule!.FileName);
                    var state=DirectMigrator.Status(request.JournalDirectory);
                    if(state.Phase!="verifying"||state.VerificationSourceHashSha256!=null||state.Request.SourceHashCacheSha256!=cacheHash)
                        throw new Exception("Legacy checkpoint is not eligible.");
                    frozen.StopForVerifiedHandoff();stopped.TrySetResult(true);
                }
            }
        });
        try{
            if(!child.WaitForExit(60000))throw new Exception("Synthetic legacy handoff timed out.");
            stdout.GetAwaiter().GetResult();File.WriteAllText(Path.Combine(dir,"legacy.stderr.txt"),stderr.GetAwaiter().GetResult());
            if(!stopped.Task.IsCompletedSuccessfully||child.ExitCode!=76||DirectMigrator.Status(request.JournalDirectory).Phase!="verifying")
                throw new Exception("Legacy process did not stop without rollback.");
            var watch=Stopwatch.StartNew();bool sourceHashed=false,copied=false;
            var resumed=DirectMigrator.ResumeVerification(request.JournalDirectory,telemetry:p=>{
                if(p.Phase is "source-hash" or "verify-source-hash")sourceHashed=true;
                if(p.Phase=="copy-tree")copied=true;
            });watch.Stop();
            if(resumed.Phase!="complete"||sourceHashed||copied||RegularImage.HashFile(cache)!=cacheHash)throw new Exception("Resume copied or rehashed source.");
            using var result=RegularImage.Open(request.Destination.Image);
            Oracle.Check(source,result.ReadBytes(0,old.Image.Length),new MigrationManifest(1,r,source.Items.Count,resumed.Mapping,"",null,false));
            File.WriteAllText(Path.Combine(dir,"handoff.json"),JsonSerializer.Serialize(new{legacyExit=child.ExitCode,checkpoint="verifying",copied,sourceHashed,resumed=resumed.Phase,
                resumedSeconds=watch.Elapsed.TotalSeconds,generatedPayloadBytes=64*1024*1024+731,cacheHash,syntheticOnly=true},ImageMigrator.Json));
        }finally{if(!child.HasExited)child.Kill();}
    }
}
